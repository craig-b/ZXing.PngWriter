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
using System.Linq;
using System.Numerics;

namespace ZXing.PngWriter
{
    /// <summary>
    /// Writes PNG scan lines as a zlib stream (RFC 1950) containing a single deflate block (RFC 1951),
    /// without building the uncompressed image data.
    /// Every scan line uses filter type 0 (None); repetition is expressed with deflate back-references instead.
    /// </summary>
    internal static class PngDeflater
    {
        private const int MaxMatchLength = 258;
        private const int MinMatchLength = 3;
        private const int MaxDistance = 32768;
        private const uint AdlerModulus = 65521;

        private const int EndOfBlock = 256;
        private const int LiteralLengthSymbols = 286;
        private const int DistanceSymbols = 30;
        private const int CodeLengthSymbols = 19;
        private const int MaxCodeLength = 15;
        private const int MaxCodeLengthCodeLength = 7;

        // order code length code lengths are written in the dynamic block header
        private static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

        /*
         *  Fixed Huffman code lengths
         *  ┌─────────┬──────┐
         *  │ Symbol  │ Bits │
         *  ├─────────┼──────┤
         *  │   0-143 │ 8    │
         *  │ 144-255 │ 9    │
         *  │ 256-279 │ 7    │
         *  │ 280-287 │ 8    │
         *  └─────────┴──────┘
         *  all 30 distance codes are 5 bits
         */
        private static readonly byte[] FixedLiteralLengthLengths = CreateFixedLiteralLengthLengths();
        private static readonly byte[] FixedDistanceLengths = [.. Enumerable.Repeat((byte)5, DistanceSymbols)];
        private static readonly ushort[] FixedLiteralLengthCodes = CreateCodes(FixedLiteralLengthLengths);
        private static readonly ushort[] FixedDistanceCodes = CreateCodes(FixedDistanceLengths);

        private static byte[] CreateFixedLiteralLengthLengths()
        {
            var lengths = new byte[288];
            lengths.AsSpan(0, 144).Fill(8);
            lengths.AsSpan(144, 112).Fill(9);
            lengths.AsSpan(256, 24).Fill(7);
            lengths.AsSpan(280, 8).Fill(8);
            return lengths;
        }

        private static ushort[] CreateCodes(byte[] lengths)
        {
            var codes = new ushort[lengths.Length];
            BuildCodes(lengths, codes);
            return codes;
        }

        /// <summary>
        /// Writes a zlib stream for the image described by runs of identical scan lines.
        /// </summary>
        /// <param name="output">Stream the zlib data is written to</param>
        /// <param name="rows">Scan line data (excluding filter byte), one row per run, each <paramref name="widthInBytes"/> long</param>
        /// <param name="runOffsets">Offset into <paramref name="rows"/> of each run's scan line</param>
        /// <param name="runCounts">Number of consecutive times each run's scan line appears in the image</param>
        /// <param name="widthInBytes">Length of a scan line excluding the filter byte</param>
        /// <param name="dynamicHuffman">Whether to use Huffman codes built for this image when they're smaller than the fixed codes</param>
        /// <param name="reserveAfter">Bytes the caller will write after the zlib stream; a <see cref="MemoryStream"/> output is grown once to fit them</param>
        public static void Write(Stream output, ReadOnlySpan<byte> rows, ReadOnlySpan<int> runOffsets, ReadOnlySpan<int> runCounts, int widthInBytes, bool dynamicHuffman, int reserveAfter = 0)
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
            var tokens = new TokenList(Math.Max(16, scanLinesLength / 4));
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
                for (int r = 0; r < runCount;)
                {
                    // a segment is consecutive runs that are contiguous in scanLines and in the image data:
                    // runs appearing once followed by one run that may repeat
                    var last = r;
                    while (last < runCount - 1 && runCounts[last] == 1) last++;
                    AddSegment(tokens, in matcher, r, last);

                    // remaining copies of the last scan line are an overlapping back-reference to the copy just written
                    var repeatedLength = (runCounts[last] - 1) * stride;
                    if (repeatedLength >= MinMatchLength)
                        tokens.AddMatch(repeatedLength, stride);
                    else
                        for (int i = 0; i < repeatedLength; i++)
                            tokens.AddLiteral(scanLines[(last * stride) + (i % stride)]);

                    r = last + 1;
                }

                uint s1 = 1, s2 = 0;
                for (int r = 0; r < runCount; r++)
                    UpdateAdler(ref s1, ref s2, scanLines.AsSpan(r * stride, stride), runCounts[r]);

                var writer = new BitWriter(output, stackalloc byte[512]);
                // zlib header: deflate, 32K window, no preset dictionary, check bits making it a multiple of 31
                writer.WriteByteAligned(0x78);
                writer.WriteByteAligned(0x01);
                WriteBlock(ref writer, tokens, dynamicHuffman, output, reserveAfter);
                writer.AlignToByte();
                var adler = (s2 << 16) | s1;
                writer.WriteByteAligned((byte)(adler >> 24));
                writer.WriteByteAligned((byte)(adler >> 16));
                writer.WriteByteAligned((byte)(adler >> 8));
                writer.WriteByteAligned((byte)adler);
                writer.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scanLines);
                ArrayPool<long>.Shared.Return(runPositions);
                ArrayPool<int>.Shared.Return(head);
                ArrayPool<int>.Shared.Return(chain);
                tokens.Dispose();
            }
        }

        /// <summary>
        /// Adds the first copy of the scan lines of runs <paramref name="firstRun"/> to <paramref name="lastRun"/>
        /// as literals and back-references found by <paramref name="matcher"/>, with one step of lazy matching.
        /// </summary>
        private static void AddSegment(TokenList tokens, in Matcher matcher, int firstRun, int lastRun)
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
                            // a longer match starts at the next byte, so add this one as a literal
                            tokens.AddLiteral(matcher.ScanLines[p]);
                            p++;
                            (length, distance) = next;
                            continue;
                        }
                    }
                    tokens.AddMatch(length, distance);
                    for (int i = p + 1; i < p + length; i++) matcher.Insert(i);
                    p += length;
                }
                else
                {
                    tokens.AddLiteral(matcher.ScanLines[p]);
                    matcher.Insert(p);
                    p++;
                }
                if (p < end) (length, distance) = matcher.Find(p, end, firstRun);
            }
        }

        /// <summary>
        /// Writes <paramref name="tokens"/> as the final deflate block, with dynamic Huffman codes if allowed and smaller, otherwise fixed codes.
        /// Before writing, grows <paramref name="output"/> (if a <see cref="MemoryStream"/>) to fit the block, the zlib trailer and <paramref name="reserveAfter"/> more bytes.
        /// </summary>
        private static void WriteBlock(ref BitWriter writer, TokenList tokens, bool dynamicHuffman, Stream output, int reserveAfter)
        {
            Span<int> literalLengthFrequencies = stackalloc int[LiteralLengthSymbols];
            Span<int> distanceFrequencies = stackalloc int[DistanceSymbols];
            literalLengthFrequencies.Clear();
            distanceFrequencies.Clear();
            // extra bits are the same whichever codes are used
            long extraBits = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var (length, value) = tokens[i];
                if (length == 0)
                {
                    literalLengthFrequencies[value]++;
                    continue;
                }
                var distanceSymbol = GetDistanceSymbol(value);
                for (var remaining = length; remaining > 0;)
                {
                    var take = NextMatchLength(remaining);
                    var lengthSymbol = GetLengthSymbol(take);
                    literalLengthFrequencies[lengthSymbol.Symbol]++;
                    distanceFrequencies[distanceSymbol.Symbol]++;
                    extraBits += lengthSymbol.ExtraBits + distanceSymbol.ExtraBits;
                    remaining -= take;
                }
            }
            literalLengthFrequencies[EndOfBlock]++;

            var blockBits = 3 + extraBits + CodeBits(literalLengthFrequencies, FixedLiteralLengthLengths) + CodeBits(distanceFrequencies, FixedDistanceLengths);

            // no prefix code can use fewer bits than the entropy of the symbols, and the dynamic header is at least 29 bits,
            // so only build dynamic codes when they could be smaller
            if (dynamicHuffman)
                dynamicHuffman = 3 + 5 + 5 + 4 + (3 * 4) + extraBits + EntropyBits(literalLengthFrequencies) + EntropyBits(distanceFrequencies) < blockBits;

            Span<byte> dynamicLiteralLengthLengths = stackalloc byte[LiteralLengthSymbols];
            Span<byte> dynamicDistanceLengths = stackalloc byte[DistanceSymbols];
            Span<int> headerSymbols = stackalloc int[LiteralLengthSymbols + DistanceSymbols];
            Span<byte> codeLengthLengths = stackalloc byte[CodeLengthSymbols];
            int headerSymbolCount = 0, literalLengthCount = 0, distanceCount = 0, codeLengthCount = 0;
            if (dynamicHuffman)
            {
                // inflaters may reject a distance code with fewer than two symbols, so give unused symbols a code if needed
                Span<int> distanceCodeFrequencies = stackalloc int[DistanceSymbols];
                distanceFrequencies.CopyTo(distanceCodeFrequencies);
                var distancesUsed = 0;
                foreach (var f in distanceCodeFrequencies) if (f > 0) distancesUsed++;
                for (int i = 0; distancesUsed < 2; i++)
                {
                    if (distanceCodeFrequencies[i] != 0) continue;
                    distanceCodeFrequencies[i] = 1;
                    distancesUsed++;
                }

                BuildCodeLengths(literalLengthFrequencies, dynamicLiteralLengthLengths, MaxCodeLength);
                BuildCodeLengths(distanceCodeFrequencies, dynamicDistanceLengths, MaxCodeLength);

                literalLengthCount = LiteralLengthSymbols;
                while (literalLengthCount > 257 && dynamicLiteralLengthLengths[literalLengthCount - 1] == 0) literalLengthCount--;
                distanceCount = DistanceSymbols;
                while (distanceCount > 1 && dynamicDistanceLengths[distanceCount - 1] == 0) distanceCount--;

                // code lengths of both codes form one sequence, run length encoded with code length symbols 16-18
                Span<byte> allLengths = stackalloc byte[LiteralLengthSymbols + DistanceSymbols];
                dynamicLiteralLengthLengths.Slice(0, literalLengthCount).CopyTo(allLengths);
                dynamicDistanceLengths.Slice(0, distanceCount).CopyTo(allLengths.Slice(literalLengthCount));
                headerSymbolCount = RunLengthEncode(allLengths.Slice(0, literalLengthCount + distanceCount), headerSymbols);

                Span<int> codeLengthFrequencies = stackalloc int[CodeLengthSymbols];
                codeLengthFrequencies.Clear();
                for (int i = 0; i < headerSymbolCount; i++) codeLengthFrequencies[headerSymbols[i] & 0xFF]++;
                BuildCodeLengths(codeLengthFrequencies, codeLengthLengths, MaxCodeLengthCodeLength);

                codeLengthCount = CodeLengthSymbols;
                while (codeLengthCount > 4 && codeLengthLengths[CodeLengthOrder[codeLengthCount - 1]] == 0) codeLengthCount--;

                long dynamicBits = 3 + 5 + 5 + 4 + (3 * codeLengthCount) + extraBits;
                for (int i = 0; i < headerSymbolCount; i++)
                    dynamicBits += codeLengthLengths[headerSymbols[i] & 0xFF] + CodeLengthExtraBits(headerSymbols[i] & 0xFF);
                dynamicBits += CodeBits(literalLengthFrequencies, dynamicLiteralLengthLengths) + CodeBits(distanceFrequencies, dynamicDistanceLengths);

                dynamicHuffman = dynamicBits < blockBits;
                if (dynamicHuffman) blockBits = dynamicBits;
            }

            // the block starts byte aligned after the zlib header, and is followed by padding and the 4 byte Adler-32
            if (output is MemoryStream memoryStream)
            {
                var required = memoryStream.Position + writer.BufferedBytes + ((blockBits + 7) >> 3) + 4 + reserveAfter;
                if (required > memoryStream.Capacity) memoryStream.Capacity = (int)required;
            }

            Span<ushort> dynamicLiteralLengthCodes = stackalloc ushort[LiteralLengthSymbols];
            Span<ushort> dynamicDistanceCodes = stackalloc ushort[DistanceSymbols];
            scoped ReadOnlySpan<byte> literalLengthLengths = FixedLiteralLengthLengths;
            scoped ReadOnlySpan<byte> distanceLengths = FixedDistanceLengths;
            scoped ReadOnlySpan<ushort> literalLengthCodes = FixedLiteralLengthCodes;
            scoped ReadOnlySpan<ushort> distanceCodes = FixedDistanceCodes;
            if (dynamicHuffman)
            {
                // BFINAL = 1, BTYPE = 10 (dynamic Huffman)
                writer.WriteBits(0b101, 3);
                writer.WriteBits((uint)(literalLengthCount - 257), 5);
                writer.WriteBits((uint)(distanceCount - 1), 5);
                writer.WriteBits((uint)(codeLengthCount - 4), 4);
                for (int i = 0; i < codeLengthCount; i++) writer.WriteBits(codeLengthLengths[CodeLengthOrder[i]], 3);

                Span<ushort> codeLengthCodes = stackalloc ushort[CodeLengthSymbols];
                BuildCodes(codeLengthLengths, codeLengthCodes);
                for (int i = 0; i < headerSymbolCount; i++)
                {
                    var symbol = headerSymbols[i] & 0xFF;
                    writer.WriteBits(codeLengthCodes[symbol], codeLengthLengths[symbol]);
                    var symbolExtraBits = CodeLengthExtraBits(symbol);
                    if (symbolExtraBits > 0) writer.WriteBits((uint)(headerSymbols[i] >> 8), symbolExtraBits);
                }

                BuildCodes(dynamicLiteralLengthLengths, dynamicLiteralLengthCodes);
                BuildCodes(dynamicDistanceLengths, dynamicDistanceCodes);
                literalLengthLengths = dynamicLiteralLengthLengths;
                distanceLengths = dynamicDistanceLengths;
                literalLengthCodes = dynamicLiteralLengthCodes;
                distanceCodes = dynamicDistanceCodes;
            }
            else
            {
                // BFINAL = 1, BTYPE = 01 (fixed Huffman)
                writer.WriteBits(0b011, 3);
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                var (length, value) = tokens[i];
                if (length == 0)
                {
                    writer.WriteBits(literalLengthCodes[value], literalLengthLengths[value]);
                    continue;
                }
                var distance = GetDistanceSymbol(value);
                for (var remaining = length; remaining > 0;)
                {
                    var take = NextMatchLength(remaining);
                    var lengthSymbol = GetLengthSymbol(take);
                    writer.WriteBits(literalLengthCodes[lengthSymbol.Symbol], literalLengthLengths[lengthSymbol.Symbol]);
                    if (lengthSymbol.ExtraBits > 0) writer.WriteBits((uint)lengthSymbol.Extra, lengthSymbol.ExtraBits);
                    writer.WriteBits(distanceCodes[distance.Symbol], distanceLengths[distance.Symbol]);
                    if (distance.ExtraBits > 0) writer.WriteBits((uint)distance.Extra, distance.ExtraBits);
                    remaining -= take;
                }
            }
            writer.WriteBits(literalLengthCodes[EndOfBlock], literalLengthLengths[EndOfBlock]);
        }

        /// <summary>
        /// Number of bits used by the Huffman codes (excluding extra bits) for symbols with <paramref name="frequencies"/>.
        /// </summary>
        private static long CodeBits(ReadOnlySpan<int> frequencies, ReadOnlySpan<byte> lengths)
        {
            long bits = 0;
            for (int i = 0; i < frequencies.Length; i++) bits += (long)frequencies[i] * lengths[i];
            return bits;
        }

        /// <summary>
        /// Lower bound on the bits any prefix code can use for symbols with <paramref name="frequencies"/>.
        /// </summary>
        private static long EntropyBits(ReadOnlySpan<int> frequencies)
        {
            long total = 0;
            foreach (var f in frequencies) total += f;
            double bits = 0;
            foreach (var f in frequencies)
                if (f > 0) bits += f * Math.Log2((double)total / f);
            // round down, allowing for floating point error
            return Math.Max(0, (long)bits - 1);
        }

        /// <summary>
        /// Length of the next back-reference code when splitting a back-reference of <paramref name="remaining"/> bytes,
        /// never leaving a remainder too short to be a back-reference.
        /// </summary>
        private static int NextMatchLength(int remaining) =>
            remaining <= MaxMatchLength ? remaining
            : remaining - MaxMatchLength >= MinMatchLength ? MaxMatchLength
            : remaining - MinMatchLength;

        /// <summary>
        /// Length symbol (257-285) and extra bits for a back-reference of 3-258 bytes.
        /// </summary>
        private static (int Symbol, int ExtraBits, int Extra) GetLengthSymbol(int length)
        {
            var x = length - MinMatchLength;
            if (length == MaxMatchLength) return (285, 0, 0);
            if (x < 8) return (257 + x, 0, 0);
            var log = BitOperations.Log2((uint)x);
            var extraBits = log - 2;
            return (257 + (4 * (log - 1)) + ((x >> extraBits) & 3), extraBits, x & ((1 << extraBits) - 1));
        }

        /// <summary>
        /// Distance symbol (0-29) and extra bits for a back-reference distance of 1-32768.
        /// </summary>
        private static (int Symbol, int ExtraBits, int Extra) GetDistanceSymbol(int distance)
        {
            var d = distance - 1;
            if (d < 4) return (d, 0, 0);
            var log = BitOperations.Log2((uint)d);
            var extraBits = log - 1;
            return ((2 * log) + ((d >> extraBits) & 1), extraBits, d & ((1 << extraBits) - 1));
        }

        private static int CodeLengthExtraBits(int symbol) => symbol switch { 16 => 2, 17 => 3, 18 => 7, _ => 0 };

        /// <summary>
        /// Run length encodes code lengths with code length symbols 0-18, each stored as symbol | extra bits value &lt;&lt; 8.
        /// </summary>
        /// <returns>Number of symbols written to <paramref name="symbols"/></returns>
        private static int RunLengthEncode(ReadOnlySpan<byte> lengths, Span<int> symbols)
        {
            var count = 0;
            for (int i = 0; i < lengths.Length;)
            {
                var value = lengths[i];
                var run = 1;
                while (i + run < lengths.Length && lengths[i + run] == value) run++;
                i += run;

                if (value == 0)
                {
                    while (run >= 11)
                    {
                        // 18: 11-138 zeros
                        var take = Math.Min(run, 138);
                        symbols[count++] = 18 | ((take - 11) << 8);
                        run -= take;
                    }
                    if (run >= 3)
                    {
                        // 17: 3-10 zeros
                        symbols[count++] = 17 | ((run - 3) << 8);
                        run = 0;
                    }
                }
                else
                {
                    symbols[count++] = value;
                    run--;
                    while (run >= 3)
                    {
                        // 16: repeat previous length 3-6 times
                        var take = Math.Min(run, 6);
                        symbols[count++] = 16 | ((take - 3) << 8);
                        run -= take;
                    }
                }
                for (; run > 0; run--) symbols[count++] = value;
            }
            return count;
        }

        /// <summary>
        /// Builds Huffman code lengths of at most <paramref name="maxLength"/> bits for symbols with <paramref name="frequencies"/>.
        /// Symbols with a frequency of 0 get length 0.
        /// </summary>
        internal static void BuildCodeLengths(ReadOnlySpan<int> frequencies, Span<byte> lengths, int maxLength)
        {
            lengths.Clear();

            // used symbols sorted by ascending frequency, packed as frequency << 16 | symbol
            Span<long> sorted = stackalloc long[frequencies.Length];
            var n = 0;
            for (int i = 0; i < frequencies.Length; i++)
                if (frequencies[i] > 0) sorted[n++] = ((long)frequencies[i] << 16) | (uint)i;
            if (n == 0) return;
            if (n == 1)
            {
                lengths[(int)(sorted[0] & 0xFFFF)] = 1;
                return;
            }
            sorted = sorted.Slice(0, n);
            sorted.Sort();

            // Moffat & Katajainen in-place minimum redundancy code lengths, from frequencies in ascending order
            Span<int> a = stackalloc int[n];
            for (int i = 0; i < n; i++) a[i] = (int)(sorted[i] >> 16);
            a[0] += a[1];
            int root = 0, leaf = 2, next;
            for (next = 1; next < n - 1; next++)
            {
                if (leaf >= n || a[root] < a[leaf]) { a[next] = a[root]; a[root++] = next; }
                else a[next] = a[leaf++];
                if (leaf >= n || (root < next && a[root] < a[leaf])) { a[next] += a[root]; a[root++] = next; }
                else a[next] += a[leaf++];
            }
            a[n - 2] = 0;
            for (next = n - 3; next >= 0; next--) a[next] = a[a[next]] + 1;
            int available = 1, used = 0, depth = 0;
            root = n - 2;
            next = n - 1;
            while (available > 0)
            {
                while (root >= 0 && a[root] == depth) { used++; root--; }
                while (available > used) { a[next--] = depth; available--; }
                available = 2 * used;
                depth++;
                used = 0;
            }

            // count codes of each length, limiting to maxLength while keeping the code complete
            Span<int> lengthCounts = stackalloc int[Math.Max(maxLength, a[0]) + 1];
            lengthCounts.Clear();
            for (int i = 0; i < n; i++) lengthCounts[Math.Min(a[i], maxLength)]++;
            long kraft = 0;
            for (int length = 1; length <= maxLength; length++) kraft += (long)lengthCounts[length] << (maxLength - length);
            while (kraft > 1L << maxLength)
            {
                // move a code at maxLength down a level by splitting the deepest shorter code into two
                lengthCounts[maxLength]--;
                for (int length = maxLength - 1; length > 0; length--)
                {
                    if (lengthCounts[length] == 0) continue;
                    lengthCounts[length]--;
                    lengthCounts[length + 1] += 2;
                    break;
                }
                kraft--;
            }

            // least frequent symbols get the longest codes
            var s = 0;
            for (int length = maxLength; length > 0; length--)
                for (int c = lengthCounts[length]; c > 0; c--)
                    lengths[(int)(sorted[s++] & 0xFFFF)] = (byte)length;
        }

        /// <summary>
        /// Builds canonical Huffman codes (RFC 1951 3.2.2) from code lengths, bit reversed for writing least significant bit first.
        /// </summary>
        private static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
        {
            Span<int> lengthCounts = stackalloc int[MaxCodeLength + 1];
            lengthCounts.Clear();
            foreach (var length in lengths) if (length != 0) lengthCounts[length]++;

            Span<int> nextCode = stackalloc int[MaxCodeLength + 1];
            var code = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                code = (code + lengthCounts[length - 1]) << 1;
                nextCode[length] = code;
            }

            for (int i = 0; i < lengths.Length; i++)
            {
                var length = lengths[i];
                if (length != 0) codes[i] = (ushort)ReverseBits((uint)nextCode[length]++, length);
            }
        }

        private static uint ReverseBits(uint value, int bits)
        {
            value = ((value >> 1) & 0x55555555) | ((value & 0x55555555) << 1);
            value = ((value >> 2) & 0x33333333) | ((value & 0x33333333) << 2);
            value = ((value >> 4) & 0x0F0F0F0F) | ((value & 0x0F0F0F0F) << 4);
            return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value) >> (32 - bits);
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

        /// <summary>
        /// Literals and back-references in order, with consecutive back-references of the same distance merged.
        /// </summary>
        private sealed class TokenList : IDisposable
        {
            public TokenList(int capacity)
            {
                _lengths = ArrayPool<int>.Shared.Rent(capacity);
                _values = ArrayPool<int>.Shared.Rent(capacity);
            }

            // length 0 is a literal with value as the byte, otherwise a back-reference with value as the distance
            private int[] _lengths;
            private int[] _values;

            public int Count { get; private set; }

            public (int Length, int Value) this[int index] => (_lengths[index], _values[index]);

            public void AddLiteral(byte value) => Add(0, value);

            /// <summary>
            /// Adds a back-reference of any length of at least <see cref="MinMatchLength"/>.
            /// </summary>
            public void AddMatch(int length, int distance)
            {
                Debug.Assert(length >= MinMatchLength && distance >= 1 && distance <= MaxDistance);
                // copying a then b bytes from the same distance is the same as copying a + b bytes
                if (Count > 0 && _lengths[Count - 1] != 0 && _values[Count - 1] == distance)
                    _lengths[Count - 1] += length;
                else
                    Add(length, distance);
            }

            private void Add(int length, int value)
            {
                if (Count == _lengths.Length) Grow();
                _lengths[Count] = length;
                _values[Count] = value;
                Count++;
            }

            private void Grow()
            {
                var lengths = ArrayPool<int>.Shared.Rent(_lengths.Length * 2);
                var values = ArrayPool<int>.Shared.Rent(_values.Length * 2);
                _lengths.AsSpan(0, Count).CopyTo(lengths);
                _values.AsSpan(0, Count).CopyTo(values);
                ArrayPool<int>.Shared.Return(_lengths);
                ArrayPool<int>.Shared.Return(_values);
                _lengths = lengths;
                _values = values;
            }

            public void Dispose()
            {
                ArrayPool<int>.Shared.Return(_lengths);
                ArrayPool<int>.Shared.Return(_values);
            }
        }

        private ref struct BitWriter
        {
            public BitWriter(Stream output, Span<byte> buffer)
            {
                _output = output;
                _buffer = buffer;
            }

            private readonly Stream _output;
            private readonly Span<byte> _buffer;
            private int _position;
            private ulong _bits;
            private int _bitCount;

            /// <summary>
            /// Whole bytes written but not yet flushed to the output
            /// </summary>
            public readonly int BufferedBytes => _position;

            /// <summary>
            /// Writes the low <paramref name="count"/> bits of <paramref name="value"/>, least significant first (at most 32 bits).
            /// </summary>
            public void WriteBits(uint value, int count)
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

            public void AlignToByte()
            {
                if (_bitCount > 0) WriteBits(0, 8 - _bitCount);
            }

            public void WriteByteAligned(byte value)
            {
                if (_bitCount != 0) throw new InvalidOperationException("Bit stream is not byte aligned");
                WriteByte(value);
            }

            private void WriteByte(byte value)
            {
                if (_position == _buffer.Length) Flush();
                _buffer[_position++] = value;
            }

            public void Flush()
            {
                _output.Write(_buffer.Slice(0, _position));
                _position = 0;
            }
        }
    }
}
