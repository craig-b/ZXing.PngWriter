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
using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace ZXing.PngWriter
{
    /// <summary>
    /// Writes PNG scan lines as a zlib stream (RFC 1950) containing a single fixed Huffman deflate block (RFC 1951),
    /// without building the uncompressed image data.
    /// Every scan line uses filter type 0 (None); repetition is expressed with deflate back-references instead.
    /// </summary>
    internal static class FixedHuffmanDeflater
    {
        private const int MaxMatchLength = 258;
        private const int MinMatchLength = 3;
        private const int MaxDistance = 32768;
        private const uint AdlerModulus = 65521;

        /// <summary>
        /// Writes a zlib stream for the image described by runs of identical scan lines.
        /// </summary>
        /// <param name="output">Stream the zlib data is written to</param>
        /// <param name="rows">Scan line data (excluding filter byte), one row per run, each <paramref name="widthInBytes"/> long</param>
        /// <param name="runOffsets">Offset into <paramref name="rows"/> of each run's scan line</param>
        /// <param name="runCounts">Number of consecutive times each run's scan line appears in the image</param>
        /// <param name="widthInBytes">Length of a scan line excluding the filter byte</param>
        public static void Write(Stream output, ReadOnlySpan<byte> rows, ReadOnlySpan<int> runOffsets, ReadOnlySpan<int> runCounts, int widthInBytes)
        {
            var stride = widthInBytes + 1;
            var runCount = runOffsets.Length;

            // one copy of each run's scan line, with filter byte, plus padding so hashing can always read 3 bytes
            var scanLinesLength = runCount * stride;
            var scanLines = ArrayPool<byte>.Shared.Rent(scanLinesLength + 2);
            // position in the uncompressed image data of each run's first scan line
            var runPositions = ArrayPool<long>.Shared.Rent(runCount);
            var hashBits = Math.Clamp(BitOperations.Log2((uint)Math.Max(1, scanLinesLength)) + 1, 8, 15);
            var head = ArrayPool<int>.Shared.Rent(1 << hashBits);
            var chain = ArrayPool<int>.Shared.Rent(Math.Max(1, scanLinesLength));
            try
            {
                long position = 0;
                for (int r = 0; r < runCount; r++)
                {
                    scanLines[r * stride] = 0;
                    rows.Slice(runOffsets[r], widthInBytes).CopyTo(scanLines.AsSpan((r * stride) + 1));
                    runPositions[r] = position;
                    position += (long)runCounts[r] * stride;
                }
                scanLines[scanLinesLength] = 0;
                scanLines[scanLinesLength + 1] = 0;
                head.AsSpan(0, 1 << hashBits).Fill(-1);

                var matcher = new Matcher(scanLines, runPositions, runCounts, stride, hashBits, head, chain);
                var writer = new TokenWriter(output, stackalloc byte[512]);
                writer.WriteHeader();

                for (int r = 0; r < runCount;)
                {
                    // a segment is consecutive runs that are contiguous in scanLines and in the image data:
                    // runs appearing once followed by one run that may repeat
                    var last = r;
                    while (last < runCount - 1 && runCounts[last] == 1) last++;
                    WriteSegment(ref writer, in matcher, r, last);

                    // remaining copies of the last scan line are an overlapping back-reference to the copy just written
                    var repeatedLength = (runCounts[last] - 1) * stride;
                    if (repeatedLength >= MinMatchLength)
                        writer.WriteMatch(repeatedLength, stride);
                    else
                        for (int i = 0; i < repeatedLength; i++)
                            writer.WriteLiteral(scanLines[(last * stride) + (i % stride)]);

                    r = last + 1;
                }

                uint s1 = 1, s2 = 0;
                for (int r = 0; r < runCount; r++)
                    UpdateAdler(ref s1, ref s2, scanLines.AsSpan(r * stride, stride), runCounts[r]);
                writer.WriteTrailer((s2 << 16) | s1);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scanLines);
                ArrayPool<long>.Shared.Return(runPositions);
                ArrayPool<int>.Shared.Return(head);
                ArrayPool<int>.Shared.Return(chain);
            }
        }

        /// <summary>
        /// Writes the first copy of the scan lines of runs <paramref name="firstRun"/> to <paramref name="lastRun"/>
        /// using literals and back-references found by <paramref name="matcher"/>, with one step of lazy matching.
        /// </summary>
        private static void WriteSegment(ref TokenWriter writer, in Matcher matcher, int firstRun, int lastRun)
        {
            var p = firstRun * matcher.Stride;
            var end = (lastRun + 1) * matcher.Stride;
            var (length, distance) = matcher.Find(p, end, firstRun);
            while (p < end)
            {
                if (length >= MinMatchLength)
                {
                    matcher.Insert(p);
                    if (p + 1 < end)
                    {
                        var next = matcher.Find(p + 1, end, firstRun);
                        if (next.Length > length)
                        {
                            // a longer match starts at the next byte, so write this one as a literal
                            writer.WriteLiteral(matcher.ScanLines[p]);
                            p++;
                            (length, distance) = next;
                            continue;
                        }
                    }
                    writer.WriteMatch(length, distance);
                    for (int i = p + 1; i < p + length; i++) matcher.Insert(i);
                    p += length;
                }
                else
                {
                    writer.WriteLiteral(matcher.ScanLines[p]);
                    matcher.Insert(p);
                    p++;
                }
                if (p < end) (length, distance) = matcher.Find(p, end, firstRun);
            }
        }

        /// <summary>
        /// Finds back-references using hash chains over one copy of each run's scan line,
        /// converting positions to distances in the full image data.
        /// </summary>
        private readonly ref struct Matcher
        {
            private const int MaxChainLength = 64;

            public Matcher(byte[] scanLines, long[] runPositions, ReadOnlySpan<int> runCounts, int stride, int hashBits, int[] head, int[] chain)
            {
                ScanLines = scanLines;
                Stride = stride;
                _runPositions = runPositions;
                _runCounts = runCounts;
                _hashShift = 32 - hashBits;
                _head = head;
                _chain = chain;
            }

            public readonly byte[] ScanLines;
            public readonly int Stride;
            private readonly long[] _runPositions;
            private readonly ReadOnlySpan<int> _runCounts;
            private readonly int _hashShift;
            private readonly int[] _head;
            private readonly int[] _chain;

            private int Hash(int p) => (int)(((uint)ScanLines[p] | ((uint)ScanLines[p + 1] << 8) | ((uint)ScanLines[p + 2] << 16)) * 2654435761u >> _hashShift);

            public void Insert(int p)
            {
                var h = Hash(p);
                _chain[p] = _head[h];
                _head[h] = p;
            }

            /// <summary>
            /// Position in the full image data of <paramref name="p"/>, using the last copy of its run
            /// unless it is in the segment starting at <paramref name="segmentRun"/>, whose first copies are being written.
            /// </summary>
            private long ImagePosition(int p, int segmentRun)
            {
                var run = p / Stride;
                var offset = p - (run * Stride);
                var copies = run >= segmentRun ? 0 : _runCounts[run] - 1;
                return _runPositions[run] + ((long)copies * Stride) + offset;
            }

            /// <summary>
            /// Finds the longest earlier match for the bytes at <paramref name="p"/>, not extending past <paramref name="end"/>.
            /// </summary>
            public (int Length, int Distance) Find(int p, int end, int segmentRun)
            {
                var maxLength = end - p;
                if (maxLength < MinMatchLength) return (0, 0);

                var target = ImagePosition(p, segmentRun);
                var bytes = ScanLines.AsSpan(p, maxLength);
                int bestLength = 0, bestDistance = 0;
                var candidate = _head[Hash(p)];
                for (int tries = 0; candidate >= 0 && tries < MaxChainLength; tries++, candidate = _chain[candidate])
                {
                    var distance = target - ImagePosition(candidate, segmentRun);
                    if (distance > MaxDistance) break;
                    var length = ScanLines.AsSpan(candidate, maxLength).CommonPrefixLength(bytes);
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestDistance = (int)distance;
                        if (length == maxLength) break;
                    }
                }
                return bestLength >= MinMatchLength ? (bestLength, bestDistance) : (0, 0);
            }
        }

        private static uint ReverseBits(uint value, int bits) => ReverseBitsFull(value) >> (32 - bits);

        private static uint ReverseBitsFull(uint v)
        {
            v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
            v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
            v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
            return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
        }

        /// <summary>
        /// Appends <paramref name="count"/> copies of a scan line (including filter byte) to an Adler-32 checksum.
        /// </summary>
        private static void UpdateAdler(ref uint s1, ref uint s2, ReadOnlySpan<byte> scanLine, int count)
        {
            // for a block, s1 grows by the sum of its bytes and s2 by length * s1 + the sum of the running byte sums
            ulong sum = 0, prefixSum = 0;
            foreach (var b in scanLine)
            {
                sum += b;
                prefixSum += sum;
            }
            var length = (ulong)scanLine.Length % AdlerModulus;
            var blockSum = (uint)(sum % AdlerModulus);
            var blockPrefixSum = (uint)(prefixSum % AdlerModulus);
            for (int i = 0; i < count; i++)
            {
                s2 = (uint)((s2 + (length * s1) + blockPrefixSum) % AdlerModulus);
                s1 = (s1 + blockSum) % AdlerModulus;
            }
        }

        /// <summary>
        /// Writes literals and back-references as fixed Huffman codes in a zlib stream,
        /// merging consecutive back-references with the same distance.
        /// </summary>
        private ref struct TokenWriter
        {
            public TokenWriter(Stream output, Span<byte> buffer)
            {
                _output = output;
                _buffer = buffer;
            }

            private readonly Stream _output;
            private readonly Span<byte> _buffer;
            private int _position;
            private ulong _bits;
            private int _bitCount;
            private int _pendingLength;
            private int _pendingDistance;

            public void WriteHeader()
            {
                // zlib header: deflate, 32K window, no preset dictionary, check bits making it a multiple of 31
                WriteByteAligned(0x78);
                WriteByteAligned(0x01);

                // BFINAL = 1, BTYPE = 01 (fixed Huffman)
                WriteBits(0b011, 3);
            }

            public void WriteLiteral(byte value)
            {
                WritePendingMatch();
                WriteLiteralLengthCode(value);
            }

            /// <summary>
            /// Writes a back-reference of any length of at least <see cref="MinMatchLength"/>.
            /// </summary>
            public void WriteMatch(int length, int distance)
            {
                Debug.Assert(length >= MinMatchLength && distance >= 1 && distance <= MaxDistance);
                // copying a then b bytes from the same distance is the same as copying a + b bytes
                if (distance != _pendingDistance) WritePendingMatch();
                _pendingDistance = distance;
                _pendingLength += length;
            }

            public void WriteTrailer(uint adler)
            {
                WritePendingMatch();

                // end of block
                WriteLiteralLengthCode(256);
                if (_bitCount > 0) WriteBits(0, 8 - _bitCount);

                WriteByteAligned((byte)(adler >> 24));
                WriteByteAligned((byte)(adler >> 16));
                WriteByteAligned((byte)(adler >> 8));
                WriteByteAligned((byte)adler);
                _output.Write(_buffer.Slice(0, _position));
                _position = 0;
            }

            /// <summary>
            /// Writes the pending back-reference, split into codes of at most <see cref="MaxMatchLength"/>.
            /// </summary>
            private void WritePendingMatch()
            {
                var length = _pendingLength;
                while (length > 0)
                {
                    var take = length;
                    if (take > MaxMatchLength)
                        // don't leave a remainder too short to be a match
                        take = length - MaxMatchLength >= MinMatchLength ? MaxMatchLength : length - MinMatchLength;
                    WriteLengthAndDistance(take, _pendingDistance);
                    length -= take;
                }
                _pendingLength = 0;
                _pendingDistance = 0;
            }

            private void WriteLengthAndDistance(int length, int distance)
            {
                // length: codes 257-285 with 0-5 extra bits
                int code, extraBits, extra;
                var x = length - MinMatchLength;
                if (length == MaxMatchLength)
                {
                    code = 285; extraBits = 0; extra = 0;
                }
                else if (x < 8)
                {
                    code = 257 + x; extraBits = 0; extra = 0;
                }
                else
                {
                    var log = BitOperations.Log2((uint)x);
                    extraBits = log - 2;
                    code = 257 + (4 * (log - 1)) + ((x >> extraBits) & 3);
                    extra = x & ((1 << extraBits) - 1);
                }
                WriteLiteralLengthCode(code);
                if (extraBits > 0) WriteBits((uint)extra, extraBits);

                // distance: codes 0-29 (fixed 5 bits) with 0-13 extra bits
                var d = distance - 1;
                if (d < 4)
                {
                    WriteBits(ReverseBits((uint)d, 5), 5);
                }
                else
                {
                    var log = BitOperations.Log2((uint)d);
                    var distanceExtraBits = log - 1;
                    var distanceCode = (2 * log) + ((d >> distanceExtraBits) & 1);
                    WriteBits(ReverseBits((uint)distanceCode, 5), 5);
                    WriteBits((uint)(d & ((1 << distanceExtraBits) - 1)), distanceExtraBits);
                }
            }

            private void WriteLiteralLengthCode(int symbol)
            {
                /*
                 *  ┌─────────┬──────┬───────────────────────────┐
                 *  │ Symbol  │ Bits │ Codes                     │
                 *  ├─────────┼──────┼───────────────────────────┤
                 *  │   0-143 │ 8    │  00110000 -  10111111     │
                 *  │ 144-255 │ 9    │ 110010000 - 111111111     │
                 *  │ 256-279 │ 7    │   0000000 -   0010111     │
                 *  │ 280-287 │ 8    │  11000000 -  11000111     │
                 *  └─────────┴──────┴───────────────────────────┘
                 */
                var (code, bits) = symbol switch
                {
                    < 144 => (0b00110000 + symbol, 8),
                    < 256 => (0b110010000 + (symbol - 144), 9),
                    < 280 => (symbol - 256, 7),
                    _ => (0b11000000 + (symbol - 280), 8),
                };
                // Huffman codes are packed most significant bit first
                WriteBits(ReverseBits((uint)code, bits), bits);
            }

            /// <summary>
            /// Writes the low <paramref name="count"/> bits of <paramref name="value"/>, least significant first (at most 32 bits).
            /// </summary>
            private void WriteBits(uint value, int count)
            {
                _bits |= (ulong)value << _bitCount;
                _bitCount += count;
                while (_bitCount >= 8)
                {
                    WriteByte((byte)_bits);
                    _bits >>= 8;
                    _bitCount -= 8;
                }
            }

            private void WriteByteAligned(byte value)
            {
                if (_bitCount != 0) throw new InvalidOperationException("Bit stream is not byte aligned");
                WriteByte(value);
            }

            private void WriteByte(byte value)
            {
                if (_position == _buffer.Length)
                {
                    _output.Write(_buffer);
                    _position = 0;
                }
                _buffer[_position++] = value;
            }
        }
    }
}
