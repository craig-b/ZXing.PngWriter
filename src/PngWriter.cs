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
using System.IO;

namespace ZXing.PngWriter
{
    public class PngWriter : BarcodeWriter<Stream>
    {
        public PngWriter() => Renderer = new PngRenderer();
        
        public Stream Write(string contents, TextualInformation? textualInformation)
        {
            if (!(Renderer is PngRenderer pngRenderer))
            {
                throw new InvalidOperationException("You have to set a renderer instance.");
            }
            var matrix = Encode(contents);
            return pngRenderer.Render(matrix, Format, contents, Options, textualInformation);
        }
    }
}
