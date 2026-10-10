/*
 * Copyright 2019 Craig Beaumont
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;

namespace ZXing.PngWriter
{
    internal enum PngCompression
    {
        /// <summary>
        /// Up-filtered scan lines compressed with <see cref="ZLibStream"/>
        /// </summary>
        ZLib,

        /// <summary>
        /// Scan lines written directly as a fixed Huffman deflate block by <see cref="FixedHuffmanDeflater"/>
        /// </summary>
        FixedHuffman,
    }

    internal sealed class PngImageWriter : IDisposable
    {
        public PngImageWriter(int width, int height, TextualInformation? textualInformation = null, PngCompression compression = PngCompression.FixedHuffman)
        {
            _widthInBytes = (width + 7) >> 3;
            _height = height;
            _compression = compression;

            WritePngHeader();
            WriteHdr(width, height);

            if (textualInformation != null)
            {
                foreach (var item in textualInformation.GetDataBlocks())
                {
                    WriteChunk(item.Type, item.Data);
                }
            }

            _rows = ArrayPool<byte>.Shared.Rent(Math.Max(1, _widthInBytes * height));
            _runOffsets = ArrayPool<int>.Shared.Rent(Math.Max(1, height));
            _runCounts = ArrayPool<int>.Shared.Rent(Math.Max(1, height));
        }

        private readonly int _widthInBytes;
        private readonly PngCompression _compression;
        private int _linesWritten;
        private int _height;
        private bool _disposed;

        // each run is a distinct scan line (excluding filter byte) and the number of consecutive times it appears
        private byte[] _rows;
        private int[] _runOffsets;
        private int[] _runCounts;
        private int _runCount;

        public MemoryStream Stream { get; } = new MemoryStream();

        public void WriteLine() => WriteLine(Span<byte>.Empty);

        public void WriteLine(Span<byte> currentScanLine) => WriteLine(currentScanLine, Span<byte>.Empty);

        /// <summary>
        /// Writes a scan line, centred and padded with white if narrower than the image.
        /// </summary>
        /// <param name="currentScanLine">Scan line to write; empty for a blank (white) line</param>
        /// <param name="previousScanLine">Unused; repeated scan lines are detected by comparing with the last line written</param>
        public void WriteLine(Span<byte> currentScanLine, Span<byte> previousScanLine)
        {
            if (_disposed) ThrowDisposedException();
            if (_linesWritten >= _height) throw new InvalidOperationException($"Already written {_linesWritten} lines");
            if (currentScanLine.Length > _widthInBytes) throw new ArgumentException($"Scan line is {currentScanLine.Length} bytes, image is {_widthInBytes} bytes wide", nameof(currentScanLine));

            var offset = _runCount == 0 ? 0 : _runOffsets[_runCount - 1] + _widthInBytes;
            EnsureRowCapacity(offset + _widthInBytes);
            var row = _rows.AsSpan(offset, _widthInBytes);

            var leftMargin = (_widthInBytes - currentScanLine.Length) / 2;
            row.Fill(byte.MaxValue);
            currentScanLine.CopyTo(row.Slice(leftMargin));

            if (_runCount > 0 && row.SequenceEqual(_rows.AsSpan(_runOffsets[_runCount - 1], _widthInBytes)))
            {
                _runCounts[_runCount - 1]++;
            }
            else
            {
                if (_runCount == _runOffsets.Length) GrowRuns();
                _runOffsets[_runCount] = offset;
                _runCounts[_runCount] = 1;
                _runCount++;
            }
            _linesWritten++;
        }

        /// <summary>
        /// Ensures the next <paramref name="scanLineCount"/> lines written will be over blank lines,
        /// reusing trailing blank lines already written and increasing the image height if necessary.
        /// </summary>
        public void EnsureBlankScanLines(int scanLineCount)
        {
            if (_disposed) ThrowDisposedException();

            var trailingBlankLines = 0;
            if (_runCount > 0 && !_rows.AsSpan(_runOffsets[_runCount - 1], _widthInBytes).ContainsAnyExcept(byte.MaxValue))
                trailingBlankLines = _runCounts[_runCount - 1];

            var linesToMoveBack = Math.Min(scanLineCount, trailingBlankLines);
            var linesToAdd = scanLineCount - linesToMoveBack;
            UpdateHeight(linesToAdd + _height);
            _linesWritten -= linesToMoveBack;
            if (linesToMoveBack > 0)
            {
                _runCounts[_runCount - 1] -= linesToMoveBack;
                if (_runCounts[_runCount - 1] == 0) _runCount--;
            }
        }

        public void Finish()
        {
            if (_disposed) ThrowDisposedException();
            if (_linesWritten < _height) throw new InvalidOperationException($"Written {_linesWritten:n0} lines, expected {_height:n0}");

            if (_compression == PngCompression.FixedHuffman)
                WriteFixedHuffmanData();
            else
                WriteZLibData();
            Dispose();

            WriteEnd();
            Stream.Position = 0;
        }

        private void WriteFixedHuffmanData()
        {
            var stream = Stream;
            var lengthPosition = stream.Position;
            stream.WriteInt(0);
            stream.Write(DatType);

            FixedHuffmanDeflater.Write(stream, _rows, _runOffsets.AsSpan(0, _runCount), _runCounts.AsSpan(0, _runCount), _widthInBytes);

            var dataLength = (int)(stream.Position - lengthPosition - 8);
            stream.WriteUInt(Crc32.HashToUInt32(stream.GetBuffer().AsSpan((int)lengthPosition + 4, 4 + dataLength)));
            var endPosition = stream.Position;
            stream.Position = lengthPosition;
            stream.WriteInt(dataLength);
            stream.Position = endPosition;
        }

        private void WriteZLibData()
        {
            var stride = _widthInBytes + 1;
            using var toDeflateStream = new MemoryStream(stride * _height);
            var upFilteredLine = new byte[stride];
            upFilteredLine[0] = 2;
            for (int r = 0; r < _runCount; r++)
            {
                toDeflateStream.WriteByte(0);
                toDeflateStream.Write(_rows, _runOffsets[r], _widthInBytes);
                for (int i = 1; i < _runCounts[r]; i++)
                    toDeflateStream.Write(upFilteredLine);
            }

            var deflateBackingStream = new MemoryStream();
            using (var deflateStream = new ZLibStream(deflateBackingStream, CompressionMode.Compress, true))
            {
                toDeflateStream.Position = 0;
                toDeflateStream.CopyTo(deflateStream);
            }
            var deflatedBuffer = deflateBackingStream.GetBuffer().AsSpan().Slice(0, (int)deflateBackingStream.Length);
            WriteChunk(DatType, deflatedBuffer);
        }

        private void EnsureRowCapacity(int length)
        {
            if (length <= _rows.Length) return;
            var rows = ArrayPool<byte>.Shared.Rent(Math.Max(length, _rows.Length * 2));
            _rows.AsSpan(0, _runOffsets[_runCount - 1] + _widthInBytes).CopyTo(rows);
            ArrayPool<byte>.Shared.Return(_rows);
            _rows = rows;
        }

        private void GrowRuns()
        {
            var runOffsets = ArrayPool<int>.Shared.Rent(_runOffsets.Length * 2);
            var runCounts = ArrayPool<int>.Shared.Rent(_runCounts.Length * 2);
            _runOffsets.AsSpan(0, _runCount).CopyTo(runOffsets);
            _runCounts.AsSpan(0, _runCount).CopyTo(runCounts);
            ArrayPool<int>.Shared.Return(_runOffsets);
            ArrayPool<int>.Shared.Return(_runCounts);
            _runOffsets = runOffsets;
            _runCounts = runCounts;
        }

        [DoesNotReturn]
        private void ThrowDisposedException() => throw new InvalidOperationException($"{nameof(PngImageWriter)} has already been disposed or you've already called {nameof(Finish)}()");

        private int _heightPosition = -1;

        private void UpdateHeight(int height)
        {
            // possibly use this to write text over blank area at bottom of image
            // instead of adding blank space, write all scan lines then check if there is spare space and update height depending on how much space is actually needed
            if (_heightPosition == -1) throw new InvalidOperationException("Height hasn't been set yet");
            if (_height == height) return;
            _height = height;
            var stream = Stream;
            var position = stream.Position;
            stream.Position = _heightPosition;
            stream.WriteUInt((uint)height);

            // Recalculate CRC over chunk type (4 bytes) + chunk data (13 bytes)
            var chunkTypePosition = _heightPosition - 8;
            stream.Position = chunkTypePosition;
            Span<byte> typeAndData = stackalloc byte[4 + 13];
            var totalRead = 0;
            while (totalRead < typeAndData.Length)
            {
                var bytesRead = stream.Read(typeAndData.Slice(totalRead));
                if (bytesRead == 0)
                    throw new EndOfStreamException("Unable to read IHDR chunk data needed to recalculate CRC.");
                totalRead += bytesRead;
            }
            stream.WriteUInt(Crc32.HashToUInt32(typeAndData));

            stream.Position = position;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_rows);
            ArrayPool<int>.Shared.Return(_runOffsets);
            ArrayPool<int>.Shared.Return(_runCounts);
        }

        private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

        private void WritePngHeader() =>
            // write png signature
            Stream.Write(PngSignature);

        private void WriteHdr(int width, int height) => WriteHdr((uint)width, (uint)height);

        private void WriteHdr(uint width, uint height)
        {
            /*
             *  ┌─────────┬─────────┬───────────┬───────────────┬────────────────────┬───────────────┬──────────────────┐
             *  │  Width  │ Height  │ Bit depth │  Color type   │ Compression method │ Filter method │ Interlace method │
             *  ├─────────┼─────────┼───────────┼───────────────┼────────────────────┼───────────────┼──────────────────┤
             *  │ 4 bytes │ 4 bytes │ 1 byte    │ 1 byte        │ 1 byte             │ 1 byte        │ 1 byte           │
             *  │         │         │           │               │                    │               │                  │
             *  │         │         │ 1 - 1 bit │ 0 - grayscale │ 0                  │ 0             │ 0                │
             *  └─────────┴─────────┴───────────┴───────────────┴────────────────────┴───────────────┴──────────────────┘
             *  
             *  ┌───────┬─────────────┬───────────────────────────────────┐
             *  │ Color │  Allowed    │          Interpretation           │
             *  │ Type  │  Bit Depths │                                   │
             *  ├───────┼─────────────┼───────────────────────────────────┤
             *  │ 0     │ 1,2,4,8,16  │ Each pixel is a grayscale sample. │
             *  │ 2     │ 8,16        │ Each pixel is an R,G,B triple.    │
             *  │ 3     │ 1,2,4,8     │ Each pixel is a palette index;    │
             *  │       │             │ a PLTE chunk must appear.         │
             *  │ 4     │ 8,16        │ Each pixel is a grayscale sample, │
             *  │       │             │ followed by an alpha sample.      │
             *  │ 6     │ 8,16        │ Each pixel is an R,G,B triple,    │
             *  │       │             │ followed by an alpha sample.      │
             *  └───────┴─────────────┴───────────────────────────────────┘
             */

            _heightPosition = (int)Stream.Position + 12;

            Span<byte> hdrData = stackalloc byte[13];

            // Chunk data
            // width (4 bytes)
            hdrData.SetUInt(width, 0);
            // height (4 bytes)
            hdrData.SetUInt(height, 4);
            // bit depth (1 byte)
            hdrData[8] = 1;
            // color type (1 byte)
            hdrData[9] = 0;
            // compression method (1 byte)
            hdrData[10] = 0;
            // filter method (1 byte)
            hdrData[11] = 0;
            // interlace method (1 byte)
            hdrData[12] = 0;

            WriteChunk(HdrType, hdrData);
        }

        private static ReadOnlySpan<byte> DatType => "IDAT"u8;
        private static ReadOnlySpan<byte> HdrType => "IHDR"u8;
        private static ReadOnlySpan<byte> EndType => "IEND"u8;

        private void WriteEnd() => WriteChunk(EndType);

        private void WriteChunk(ReadOnlySpan<byte> chunkType) => WriteChunk(chunkType, ReadOnlySpan<byte>.Empty);

        private void WriteChunk(ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> chunkData)
        {
            /*
             *  ┌─────────┬─────────────┬────────────┬────────────────────┐
             *  │ Length  │ Chunk Type  │ Chunk Data │        CRC         │
             *  ├─────────┼─────────────┼────────────┼────────────────────┤
             *  │ 4 bytes │ 4 bytes     │ 0+ bytes   │ 4 bytes            │
             *  │ uint    │ ASCII chars │            │ incl type and data │
             *  │         │             │            │ excl length        │
             *  └─────────┴─────────────┴────────────┴────────────────────┘
             */

            var stream = Stream;
            stream.WriteInt(chunkData.Length);
            stream.Write(chunkType);
            stream.Write(chunkData);
            var crc = new Crc32();
            crc.Append(chunkType);
            if (chunkData.Length > 0)
                crc.Append(chunkData);
            stream.WriteUInt(crc.GetCurrentHashAsUInt32());
        }
    }
}
