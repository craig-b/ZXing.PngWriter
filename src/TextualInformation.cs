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
using System;
using System.Collections.Generic;
using System.IO;
//using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ZXing.PngWriter
{
    public class TextualInformation
    {
        /// <summary>
        /// Short (one line) title or caption for image
        /// </summary>
        public TextData? Title { get; set; }
        //static byte[] titleBytes = Latin1Encoder.GetBytes(nameof(Title));

        /// <summary>
        /// Name of image's creator
        /// </summary>
        public TextData? Author { get; set; }
        //static byte[] authorBytes = Latin1Encoder.GetBytes(nameof(Author));

        /// <summary>
        /// Description of image (possibly long)
        /// </summary>
        public TextData? Description { get; set; }
        //static byte[] descriptionBytes = Latin1Encoder.GetBytes(nameof(Description));

        /// <summary>
        /// Copyright notice
        /// </summary>
        public TextData? Copyright { get; set; }
        //static byte[] copyrightBytes = Latin1Encoder.GetBytes(nameof(Copyright));

        /// <summary>
        /// Time of original image creation
        /// </summary>
        public TextData? CreationTime { get; set; }
        //static byte[] creationTimeBytes = Latin1Encoder.GetBytes(nameof(CreationTime));

        /// <summary>
        /// Software used to create the image
        /// </summary>
        public TextData? Software { get; set; }
        //static byte[] softwareBytes = Latin1Encoder.GetBytes(nameof(Software));

        /// <summary>
        /// Legal disclaimer
        /// </summary>
        public TextData? Disclaimer { get; set; }
        //static byte[] disclaimerBytes = Latin1Encoder.GetBytes(nameof(Disclaimer));

        /// <summary>
        /// Warning of nature of content
        /// </summary>
        public TextData? Warning { get; set; }
        //static byte[] warningBytes = Latin1Encoder.GetBytes(nameof(Warning));

        /// <summary>
        /// Device used to create the image
        /// </summary>
        public TextData? Source { get; set; }
        //static byte[] sourceBytes = Latin1Encoder.GetBytes(nameof(Source));

        /// <summary>
        /// Miscellaneous comment; conversion from GIF comment
        /// </summary>
        public TextData? Comment { get; set; }
        //static byte[] commentBytes = Latin1Encoder.GetBytes(nameof(Comment));

        public IEnumerable<(byte[] Type, byte[] Data)> GetDataBlocks()
        {
            if (Title != null) yield return TextData.GetTypeAndData(() => Title);
            if (Author != null) yield return TextData.GetTypeAndData(() => Author);
            if (Description != null) yield return TextData.GetTypeAndData(() => Description);
            if (Copyright != null) yield return TextData.GetTypeAndData(() => Copyright);
            if (CreationTime != null) yield return TextData.GetTypeAndData(() => CreationTime);
            if (Software != null) yield return TextData.GetTypeAndData(() => Software);
            if (Disclaimer != null) yield return TextData.GetTypeAndData(() => Disclaimer);
            if (Warning != null) yield return TextData.GetTypeAndData(() => Warning);
            if (Source != null) yield return TextData.GetTypeAndData(() => Source);
            if (Comment != null) yield return TextData.GetTypeAndData(() => Comment);
        }
    }

    public class TextData
    {
        public TextData(string text) => Text = text;

        public string Text { get; }
        public bool Compress { get; set; }
        public bool UTF8 { get; set; }

        public static implicit operator TextData(string value) => new TextData(value);

        private static readonly byte[] tEXt = new byte[] { (byte)'t', (byte)'E', (byte)'X', (byte)'t' };
        private static readonly byte[] zTXt = new byte[] { (byte)'z', (byte)'T', (byte)'X', (byte)'t' };
        private static readonly byte[] iTXt = new byte[] { (byte)'i', (byte)'T', (byte)'X', (byte)'t' };

        private static readonly Encoding Latin1Encoder = Encoding.GetEncoding("Latin1");

        internal byte[] GetTypeBytes()
        {
            if (UTF8) return iTXt;
            if (Compress) return zTXt;
            return tEXt;
        }

        internal byte[] GetDataBytes(string keyword)
        {
            if (UTF8) return GetiTXtData(keyword);
            if (Compress) return GetzTXtData(keyword);
            return GettEXtData(keyword);
        }

        private byte[] GettEXtData(string keyword)
        {
            /*
             *  ┌────────────────────┬────────────────┬────────────────────┐
             *  │      Keyword       │ Null separator │        Text        │
             *  ├────────────────────┼────────────────┼────────────────────┤
             *  │ 1-79 bytes         │ 1 byte         │ n bytes            │
             *  │ (character string) │                │ (character string) │
             *  └────────────────────┴────────────────┴────────────────────┘
             */

            var keywordBytes = Latin1Encoder.GetBytes(keyword);
            var encoded = Latin1Encoder.GetBytes(Text);
            var data = new byte[keywordBytes.Length + encoded.Length + 1];
            keywordBytes.CopyTo(data, 0);
            encoded.CopyTo(data, keywordBytes.Length + 1);
            return data;
        }

        private static ReadOnlySpan<byte> CompressBytes(byte[] values)
        {
            using var stream = new MemoryStream();
            using (var zlibStream = new ZlibStream(stream, CompressionMode.Compress, true))
            {
                zlibStream.Write(values);
            }
            return stream.GetBuffer().AsSpan().Slice(0, (int)stream.Length);
        }

        private byte[] GetzTXtData(string keyword)
        {
            /*
             *  ┌────────────────────┬────────────────┬────────────────────┬─────────────────┐
             *  │      Keyword       │ Null separator │ Compression method │ Compressed text │
             *  ├────────────────────┼────────────────┼────────────────────┼─────────────────┤
             *  │ 1-79 bytes         │ 1 byte         │ 1 byte             │ n bytes         │
             *  │ (character string) │                │ (0 = zlib)         │                 │
             *  └────────────────────┴────────────────┴────────────────────┴─────────────────┘
             */

            var latin1Text = Latin1Encoder.GetBytes(Text);
            var compressed = CompressBytes(latin1Text);
            var keywordBytes = Latin1Encoder.GetBytes(keyword);
            var data = new byte[keywordBytes.Length + compressed.Length + 2];
            keywordBytes.CopyTo(data, 0);
            compressed.CopyTo(data.AsSpan().Slice(keywordBytes.Length + 2));
            return data;
        }

        private byte[] GetiTXtData(string keyword)
        {
            /*
             *  ┌────────────────────┬────────────────┬────────────────────┬────────────────────┬────────────────────┬────────────────┬────────────────────┬────────────────┬─────────────────┐
             *  │      Keyword       │ Null separator │ Compression flag   │ Compression method │    Language tag    │ Null separator │ Translated keyword │ Null separator │      Text       │
             *  ├────────────────────┼────────────────┼────────────────────┼────────────────────┼────────────────────┼────────────────┼────────────────────┼────────────────┼─────────────────┤
             *  │ 1-79 bytes         │ 1 byte         │ 1 byte             │ 1 byte             │ 0 or more bytes    │ 1 byte         │ 0 or more bytes    │ 1 byte         │ 0 or more bytes │
             *  │ (character string) │                │ (0 = uncompressed) │ (0 = zlib)         │ (character string) │                │                    │                │                 │
             *  │                    │                │ (1 = compressed)   │                    │                    │                │                    │                │                 │
             *  └────────────────────┴────────────────┴────────────────────┴────────────────────┴────────────────────┴────────────────┴────────────────────┴────────────────┴─────────────────┘
             */

            var keywordBytes = Latin1Encoder.GetBytes(keyword);
            var utf8Text = Encoding.UTF8.GetBytes(Text);
            var finalText = Compress ? CompressBytes(utf8Text) : utf8Text;
            var data = new byte[keywordBytes.Length + finalText.Length + 5];
            keywordBytes.CopyTo(data, 0);
            finalText.CopyTo(data.AsSpan().Slice(keywordBytes.Length + 5));
            data[keywordBytes.Length + 1] = (byte)(Compress ? 1 : 0);
            return data;
        }

        internal static (byte[] Type, byte[] Data) GetTypeAndData(Expression<Func<TextData>> property)
        {
            var textData = property.Compile()();
            var name = GetMemberInfo(property).Name;
            return (textData.GetTypeBytes(), textData.GetDataBytes(name));
        }

        private static MemberInfo GetMemberInfo(Expression expression)
        {
            var lambdaExpression = (LambdaExpression)expression;
            var memberExpression = (!(lambdaExpression.Body is UnaryExpression)) ? ((MemberExpression)lambdaExpression.Body) : ((MemberExpression)((UnaryExpression)lambdaExpression.Body).Operand);
            return memberExpression.Member;
        }
    }
}
