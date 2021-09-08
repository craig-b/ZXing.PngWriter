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
            var pngImageWriter = new PngImageWriter(width, matrix.Height, textualInformation);
            try
            {
                Span<byte> previousScanLine = stackalloc byte[(matrix.Width + 31) >> 3]; //Span<byte>.Empty;
                Span<byte> vectorSizedArray = stackalloc byte[((matrix.Width + 255) & ~0b1111_1111) >> 3];
                for (int y = 0; y < matrix.Height; y++)
                {
                    var bitArray = matrix.getRow(y, null);
                    bitArray.GetBytes().CopyTo(vectorSizedArray);
                    vectorSizedArray.Negate();
                    vectorSizedArray.ReverseBits();
                    var currentScanLine = vectorSizedArray.Slice(0, bitArray.SizeInBytes);
                    pngImageWriter.WriteLine(currentScanLine, previousScanLine);
                    currentScanLine.CopyTo(previousScanLine);
                    //previousScanLine = currentScanLine;
                }
                if (includeText)
                {
                    PngImageTextWriter.Write(ref pngImageWriter, content);
                }
                pngImageWriter.Finish();
                return pngImageWriter.Stream;
            }
            finally
            {
                pngImageWriter.Dispose();
            }
        }
    }
}