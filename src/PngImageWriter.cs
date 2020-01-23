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

using Ionic.Zlib;
using Soft160.Data.Cryptography;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace ZXing.PngWriter
{
    internal sealed class PngImageWriter : IDisposable
    {
        public PngImageWriter(int width, int height, TextualInformation? textualInformation = null)
        {
            _widthInBytes = width + 7 >> 3;
            _height = height;

            WritePngHeader();
            WriteHdr(width, height);

            if (textualInformation != null)
            {
                foreach (var item in textualInformation.GetDataBlocks())
                {
                    WriteChunk(item.Type, item.Data);
                }
            }
            _toDeflateStream = new MemoryStream((_widthInBytes + 1) * height);

            _emptyScanLine = new Memory<byte>(new byte[_widthInBytes]);
            _blankScanLine = new Memory<byte>(new byte[_widthInBytes]);
            _blankScanLine.Span.Fill(byte.MaxValue);
        }

        private readonly int _widthInBytes;
        private readonly Memory<byte> _emptyScanLine;
        private readonly Memory<byte> _blankScanLine;
        private int _linesWritten;
        private int _height;
        private int _blankLines = -1;

        private MemoryStream? _toDeflateStream;

        public MemoryStream Stream { get; } = new MemoryStream();

        public void WriteLine() => WriteLine(_blankScanLine.Span);

        public void WriteLine(Span<byte> currentScanLine) => WriteLine(currentScanLine, Span<byte>.Empty);

        public void WriteLine(Span<byte> currentScanLine, Span<byte> previousScanLine)
        {
            if (_toDeflateStream == null) ThrowDisposedException();
            if (_linesWritten >= _height) throw new InvalidOperationException($"Already written {_linesWritten} lines");

            var totalMargin = _widthInBytes - currentScanLine.Length;
            var leftMargin = totalMargin / 2;
            var rightMargin = totalMargin - leftMargin;
            if (previousScanLine.Length != 0 && currentScanLine.SequenceEqual(previousScanLine))
            {
                _toDeflateStream.WriteByte(2);
                _toDeflateStream.Write(_emptyScanLine.Span);
                if (_blankLines > 0) _blankLines++;
            }
            else
            {
                // first line or different to previous
                _toDeflateStream.WriteByte(0);
                if (leftMargin > 0)
                    _toDeflateStream.Write(_blankScanLine.Span.Slice(0, leftMargin));
                _toDeflateStream.Write(currentScanLine);
                if (rightMargin > 0)
                    _toDeflateStream.Write(_blankScanLine.Span.Slice(0, rightMargin));
                if (currentScanLine.SequenceEqual(_blankScanLine.Span)) _blankLines = 1;
                else _blankLines = 0;
            }
            _linesWritten++;
        }

        public void EnsureBlankScanLines(int scanLineCount)
        {
            if (_toDeflateStream == null) ThrowDisposedException();

            var linesToMoveBack = _blankLines > 0 ? Math.Min(scanLineCount, _blankLines) : 0;
            var linesToAdd = scanLineCount - linesToMoveBack;
            UpdateHeight(linesToAdd + _height);
            _linesWritten -= linesToMoveBack;
            _toDeflateStream.Position -= ((_widthInBytes + 1) * linesToMoveBack);
        }

        public void Finish()
        {
            if (_toDeflateStream == null) ThrowDisposedException();
            if (_linesWritten < _height) throw new InvalidOperationException($"Written {_linesWritten:n0} lines, expected {_height:n0}");

            using (var deflateBackingStream = new MemoryStream())
            {
                using (var deflateStream = new ZlibStream(deflateBackingStream, CompressionMode.Compress, true))
                {
                    _toDeflateStream.Position = 0;
                    _toDeflateStream.CopyTo(deflateStream);
                }
                var deflatedBuffer = deflateBackingStream.GetBuffer().AsSpan().Slice(0, (int)deflateBackingStream.Length);
                WriteChunk(datType, deflatedBuffer);
            }
            _toDeflateStream.Dispose();
            _toDeflateStream = null;

            WriteEnd();
            Stream.Position = 0;
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
            stream.Position = position;
        }

        public void Dispose() => _toDeflateStream?.Dispose();

        private static readonly byte[] pngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

        private void WritePngHeader() =>
            // write png signature
            Stream.Write(pngSignature);

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

            WriteChunk(hdrType, hdrData);
        }

        private static readonly byte[] datType = new[] { (byte)'I', (byte)'D', (byte)'A', (byte)'T' };
        private static readonly byte[] hdrType = new[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        private static readonly byte[] endType = new[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' };

        private void WriteEnd() =>
            //var endTypeAndData = new[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' };
            WriteChunk(endType);

        private void WriteChunk(Span<byte> chunkType) => WriteChunk(chunkType, Span<byte>.Empty);

        private void WriteChunk(Span<byte> chunkType, Span<byte> chunkData)
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
            var crcHash = CRC.Crc32(chunkType);
            if (chunkData.Length > 0)
                crcHash = CRC.Crc32(chunkData, crcHash);
            stream.WriteUInt(crcHash);
        }
    }
}
