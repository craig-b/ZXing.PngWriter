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
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ZXing.Common;

namespace ZXing.PngWriter
{
    internal static class Extensions
    {
        public static void WriteInt(this Stream stream, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        public static void WriteUInt(this Stream stream, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, value);
            stream.Write(buf);
        }

        public static void SetUInt(this Span<byte> span, uint value, int position)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(position), value);
        }

        private static byte ReverseBits(this byte b)
        {
            b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
            b = (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
            return b;
        }

        public static Span<byte> GetBytes(this BitArray bitArray) => MemoryMarshal.AsBytes<int>(bitArray.Array).Slice(0, bitArray.SizeInBytes);

        public static void Negate(this Span<byte> span)
        {
            var vectors = MemoryMarshal.Cast<byte, Vector<byte>>(span);
            var allOnes = Vector<byte>.AllBitsSet;
            for (int i = 0; i < vectors.Length; i++)
                vectors[i] = Vector.Xor(vectors[i], allOnes);
            for (int i = vectors.Length * Vector<byte>.Count; i < span.Length; i++)
                span[i] = (byte)~span[i];
        }

        public static void Negate(this Span<int> span)
        {
            var vectors = MemoryMarshal.Cast<int, Vector<int>>(span);
            var allOnes = Vector<int>.AllBitsSet;
            for (int i = 0; i < vectors.Length; i++)
                vectors[i] = Vector.Xor(vectors[i], allOnes);
            for (int i = vectors.Length * Vector<int>.Count; i < span.Length; i++)
                span[i] = ~span[i];
        }

        public static void ReverseBits(this Span<int> span)
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = BinaryPrimitives.ReverseEndianness(span[i]);
            MemoryMarshal.AsBytes(span).ReverseBits();
        }

        public static void ReverseBits(this Span<byte> span)
        {
            var vectors = MemoryMarshal.Cast<byte, Vector<byte>>(span);
            var mask_F0 = new Vector<byte>(0xF0);
            var mask_0F = new Vector<byte>(0x0F);
            var mask_CC = new Vector<byte>(0xCC);
            var mask_33 = new Vector<byte>(0x33);
            var mask_AA = new Vector<byte>(0xAA);
            var mask_55 = new Vector<byte>(0x55);
            for (int i = 0; i < vectors.Length; i++)
            {
                var v = vectors[i];
                v = Vector.BitwiseOr(
                    Vector.BitwiseAnd(Vector.ShiftRightLogical(v, 4), mask_0F),
                    Vector.ShiftLeft(Vector.BitwiseAnd(v, mask_0F), 4));
                v = Vector.BitwiseOr(
                    Vector.BitwiseAnd(Vector.ShiftRightLogical(v, 2), mask_33),
                    Vector.ShiftLeft(Vector.BitwiseAnd(v, mask_33), 2));
                v = Vector.BitwiseOr(
                    Vector.BitwiseAnd(Vector.ShiftRightLogical(v, 1), mask_55),
                    Vector.ShiftLeft(Vector.BitwiseAnd(v, mask_55), 1));
                vectors[i] = v;
            }
            for (int i = vectors.Length * Vector<byte>.Count; i < span.Length; i++)
                span[i] = span[i].ReverseBits();
        }
    }
}
