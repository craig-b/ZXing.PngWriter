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
using System.Collections.Generic;

namespace ZXing.PngWriter
{
    internal static class PngImageTextWriter
    {
        /// <summary>
        /// Calculates the width needed (in bits) for <paramref name="text"/>
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public static int GetRequiredWidth(string text) => text.Length << 3;

        /// <summary>
        /// Writes <paramref name="text"/> to the supplied <paramref name="pngImageWriter"/>
        /// Will write over white space if available
        /// </summary>
        /// <param name="pngImageWriter"></param>
        /// <param name="text"></param>
        public static void Write(PngImageWriter pngImageWriter, string text)
        {
            pngImageWriter.EnsureBlankScanLines(13);
            pngImageWriter.WriteLine();
            var textLen = text.Length;
            Span<byte> row = stackalloc byte[textLen * 11];
            row.Fill(byte.MaxValue);
            for (int c = 0; c < text.Length; c++)
            {
                if (characterBitmaps.TryGetValue(text[c], out var charBitmap))
                {
                    for (int y = 0; y < 11; y++)
                    {
                        row[(y * textLen) + c] = charBitmap[y];
                    }
                }
            }
            for (int i = 0; i < 11; i++)
            {
                pngImageWriter.WriteLine(row.Slice(textLen * i, textLen));
            }
            pngImageWriter.WriteLine();
        }

        private static readonly IReadOnlyDictionary<char, byte[]> characterBitmaps = new Dictionary<char, byte[]> {
            { 'a', new byte[] { 255, 255, 255, 199, 251, 195, 187, 187, 195, 255, 255 } },
            { 'b', new byte[] { 191, 191, 191, 135, 187, 187, 187, 187, 135, 255, 255 } },
            { 'c', new byte[] { 255, 255, 255, 199, 187, 191, 191, 187, 199, 255, 255 } },
            { 'd', new byte[] { 251, 251, 251, 195, 187, 187, 187, 187, 195, 255, 255 } },
            { 'e', new byte[] { 255, 255, 255, 199, 187, 131, 191, 187, 199, 255, 255 } },
            { 'f', new byte[] { 231, 219, 223, 223, 135, 223, 223, 223, 223, 255, 255 } },
            { 'g', new byte[] { 255, 255, 255, 199, 187, 187, 187, 195, 251, 187, 199 } },
            { 'h', new byte[] { 191, 191, 191, 167, 155, 187, 187, 187, 187, 255, 255 } },
            { 'i', new byte[] { 255, 239, 255, 207, 239, 239, 239, 239, 199, 255, 255 } },
            { 'j', new byte[] { 255, 247, 255, 231, 247, 247, 247, 247, 183, 183, 207 } },
            { 'k', new byte[] { 191, 191, 191, 183, 175, 159, 175, 183, 187, 255, 255 } },
            { 'l', new byte[] { 207, 239, 239, 239, 239, 239, 239, 239, 199, 255, 255 } },
            { 'm', new byte[] { 255, 255, 255, 151, 171, 171, 171, 171, 187, 255, 255 } },
            { 'n', new byte[] { 255, 255, 255, 167, 155, 187, 187, 187, 187, 255, 255 } },
            { 'o', new byte[] { 255, 255, 255, 199, 187, 187, 187, 187, 199, 255, 255 } },
            { 'p', new byte[] { 255, 255, 255, 135, 187, 187, 187, 135, 191, 191, 191 } },
            { 'q', new byte[] { 255, 255, 255, 195, 187, 187, 187, 195, 251, 251, 251 } },
            { 'r', new byte[] { 255, 255, 255, 167, 155, 191, 191, 191, 191, 255, 255 } },
            { 's', new byte[] { 255, 255, 255, 199, 187, 207, 247, 187, 199, 255, 255 } },
            { 't', new byte[] { 255, 223, 223, 135, 223, 223, 223, 219, 231, 255, 255 } },
            { 'u', new byte[] { 255, 255, 255, 187, 187, 187, 187, 179, 203, 255, 255 } },
            { 'v', new byte[] { 255, 255, 255, 187, 187, 187, 215, 215, 239, 255, 255 } },
            { 'w', new byte[] { 255, 255, 255, 187, 187, 171, 171, 171, 215, 255, 255 } },
            { 'x', new byte[] { 255, 255, 255, 187, 215, 239, 239, 215, 187, 255, 255 } },
            { 'y', new byte[] { 255, 255, 255, 187, 187, 187, 179, 203, 251, 187, 199 } },
            { 'z', new byte[] { 255, 255, 255, 131, 247, 239, 223, 191, 131, 255, 255 } },
            { 'A', new byte[] { 239, 215, 187, 187, 187, 131, 187, 187, 187, 255, 255 } },
            { 'B', new byte[] { 135, 219, 219, 219, 199, 219, 219, 219, 135, 255, 255 } },
            { 'C', new byte[] { 199, 187, 191, 191, 191, 191, 191, 187, 199, 255, 255 } },
            { 'D', new byte[] { 135, 219, 219, 219, 219, 219, 219, 219, 135, 255, 255 } },
            { 'E', new byte[] { 131, 191, 191, 191, 135, 191, 191, 191, 131, 255, 255 } },
            { 'F', new byte[] { 131, 191, 191, 191, 135, 191, 191, 191, 191, 255, 255 } },
            { 'G', new byte[] { 199, 187, 191, 191, 191, 179, 187, 187, 199, 255, 255 } },
            { 'H', new byte[] { 187, 187, 187, 187, 131, 187, 187, 187, 187, 255, 255 } },
            { 'I', new byte[] { 199, 239, 239, 239, 239, 239, 239, 239, 199, 255, 255 } },
            { 'J', new byte[] { 227, 247, 247, 247, 247, 247, 247, 183, 207, 255, 255 } },
            { 'K', new byte[] { 187, 187, 183, 175, 159, 175, 183, 187, 187, 255, 255 } },
            { 'L', new byte[] { 191, 191, 191, 191, 191, 191, 191, 191, 131, 255, 255 } },
            { 'M', new byte[] { 187, 187, 147, 171, 171, 187, 187, 187, 187, 255, 255 } },
            { 'N', new byte[] { 187, 155, 155, 171, 171, 179, 179, 187, 187, 255, 255 } },
            { 'O', new byte[] { 199, 187, 187, 187, 187, 187, 187, 187, 199, 255, 255 } },
            { 'P', new byte[] { 135, 187, 187, 187, 135, 191, 191, 191, 191, 255, 255 } },
            { 'Q', new byte[] { 199, 187, 187, 187, 187, 187, 187, 171, 199, 251, 255 } },
            { 'R', new byte[] { 135, 187, 187, 187, 135, 175, 183, 187, 187, 255, 255 } },
            { 'S', new byte[] { 199, 187, 191, 191, 199, 251, 251, 187, 199, 255, 255 } },
            { 'T', new byte[] { 131, 239, 239, 239, 239, 239, 239, 239, 239, 255, 255 } },
            { 'U', new byte[] { 187, 187, 187, 187, 187, 187, 187, 187, 199, 255, 255 } },
            { 'V', new byte[] { 187, 187, 187, 187, 215, 215, 215, 239, 239, 255, 255 } },
            { 'W', new byte[] { 187, 187, 187, 187, 171, 171, 171, 147, 187, 255, 255 } },
            { 'X', new byte[] { 187, 187, 215, 215, 239, 215, 215, 187, 187, 255, 255 } },
            { 'Y', new byte[] { 187, 187, 215, 215, 239, 239, 239, 239, 239, 255, 255 } },
            { 'Z', new byte[] { 131, 251, 247, 247, 239, 223, 223, 191, 131, 255, 255 } },
            { '1', new byte[] { 239, 207, 175, 239, 239, 239, 239, 239, 131, 255, 255 } },
            { '2', new byte[] { 199, 187, 187, 251, 247, 239, 223, 191, 131, 255, 255 } },
            { '3', new byte[] { 131, 251, 247, 239, 199, 251, 251, 187, 199, 255, 255 } },
            { '4', new byte[] { 247, 247, 231, 215, 215, 183, 131, 247, 247, 255, 255 } },
            { '5', new byte[] { 131, 191, 191, 167, 155, 251, 251, 187, 199, 255, 255 } },
            { '6', new byte[] { 199, 187, 191, 191, 135, 187, 187, 187, 199, 255, 255 } },
            { '7', new byte[] { 131, 251, 247, 247, 239, 239, 223, 223, 223, 255, 255 } },
            { '8', new byte[] { 199, 187, 187, 187, 199, 187, 187, 187, 199, 255, 255 } },
            { '9', new byte[] { 199, 187, 187, 187, 195, 251, 251, 187, 199, 255, 255 } },
            { '0', new byte[] { 239, 215, 187, 187, 187, 187, 187, 215, 239, 255, 255 } },
            { '.', new byte[] { 255, 255, 255, 255, 255, 255, 255, 239, 199, 239, 255 } },
            { ':', new byte[] { 255, 255, 239, 199, 239, 255, 255, 239, 199, 239, 255 } },
            { ',', new byte[] { 255, 255, 255, 255, 255, 255, 255, 231, 239, 223, 255 } },
            { ';', new byte[] { 255, 255, 239, 199, 239, 255, 255, 231, 239, 223, 255 } },
            { '(', new byte[] { 247, 239, 239, 223, 223, 223, 239, 239, 247, 255, 255 } },
            { '"', new byte[] { 215, 215, 215, 255, 255, 255, 255, 255, 255, 255, 255 } },
            { '*', new byte[] { 255, 239, 171, 131, 199, 131, 171, 239, 255, 255, 255 } },
            { '!', new byte[] { 239, 239, 239, 239, 239, 239, 239, 255, 239, 255, 255 } },
            { '?', new byte[] { 199, 187, 187, 251, 247, 239, 239, 255, 239, 255, 255 } },
            { '\'', new byte[] { 231, 239, 223, 255, 255, 255, 255, 255, 255, 255, 255 } },
            { ')', new byte[] { 223, 239, 239, 247, 247, 247, 239, 239, 223, 255, 255 } }
        };
    }
}
