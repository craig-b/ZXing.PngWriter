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
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZXing.Common;
using ZXing.Rendering;

namespace ZXing.PngWriter
{
    internal class PngRenderer : IBarcodeRenderer<Stream>
    {
        /// <summary>
        /// How the image data is compressed
        /// </summary>
        internal PngCompression Compression { get; set; } = PngCompression.DynamicHuffman;

        /// <summary>
        /// Renders the specified matrix to its graphically representation
        /// </summary>
        /// <param name="matrix">The matrix.</param>
        /// <param name="format">The format.</param>
        /// <param name="content">The encoded content of the barcode which should be included in the image.
        /// That can be the numbers below a 1D barcode or something other.</param>
        /// <returns>Stream containing a png image</returns>
        public Stream Render(BitMatrix matrix, BarcodeFormat format, string content) => Render(matrix, format, content, new EncodingOptions());

        /// <summary>
        /// Renders the specified matrix to its graphically representation
        /// </summary>
        /// <param name="matrix">The matrix.</param>
        /// <param name="format">The format.</param>
        /// <param name="content">The encoded content of the barcode which should be included in the image.
        /// That can be the numbers below a 1D barcode or something other.</param>
        /// <param name="options">The options.</param>
        /// <returns>Stream containing a png image</returns>
        public Stream Render(BitMatrix matrix, BarcodeFormat format, string content, EncodingOptions options) => Render(matrix, format, content, options, null);

        /// <summary>
        /// Renders the specified matrix to its graphically representation
        /// </summary>
        /// <param name="matrix">The matrix.</param>
        /// <param name="format">The format.</param>
        /// <param name="content">The encoded content of the barcode which should be included in the image.
        /// That can be the numbers below a 1D barcode or something other.</param>
        /// <param name="options">The options.</param>
        /// <param name="textualInformation">Textual information associated with the image</param>
        /// <returns>Stream containing a png image</returns>
        public Stream Render(BitMatrix matrix, BarcodeFormat format, string content, EncodingOptions options, TextualInformation? textualInformation)
        {
            var includeText = !string.IsNullOrEmpty(content) && !(options?.PureBarcode ?? true);
            var width = matrix.Width;
            if (includeText)
            {
                var requiredWidth = PngImageTextWriter.GetRequiredWidth(content);
                width = Math.Max(width, requiredWidth);
            }
            using var pngImageWriter = new PngImageWriter(width, matrix.Height, textualInformation, Compression);
            // one BitArray reused for every row; its bits are transformed in place, so the untransformed bits of the
            // previous row are kept separately to detect repeated rows before doing any work on them
            BitArray? bitArray = null;
            int[]? previousRow = null;
            try
            {
                for (int y = 0; y < matrix.Height; y++)
                {
                    bitArray = matrix.getRow(y, bitArray);
                    var bits = bitArray.Array.AsSpan();
                    if (previousRow != null && bits.SequenceEqual(previousRow.AsSpan(0, bits.Length)))
                    {
                        pngImageWriter.RepeatLine();
                        continue;
                    }
                    previousRow ??= ArrayPool<int>.Shared.Rent(bits.Length);
                    bits.CopyTo(previousRow);

                    var currentScanLine = bitArray.GetBytes();
                    currentScanLine.Negate();
                    currentScanLine.ReverseBits();
                    pngImageWriter.WriteLine(currentScanLine);
                }
            }
            finally
            {
                if (previousRow != null) ArrayPool<int>.Shared.Return(previousRow);
            }
            if (includeText)
            {
                PngImageTextWriter.Write(pngImageWriter, content);
            }
            pngImageWriter.Finish();
            return pngImageWriter.Stream;
        }
    }
}