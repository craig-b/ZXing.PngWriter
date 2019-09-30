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
using System.Runtime.CompilerServices;
using ZXing.Common;

namespace ZXing.PngWriter
{
    internal static class Extensions
    {
        public static void WriteInt(this Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        public static void WriteUInt(this Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        public static void WriteLong(this Stream stream, long value)
        {
            stream.WriteByte((byte)(value >> 56));
            stream.WriteByte((byte)(value >> 48));
            stream.WriteByte((byte)(value >> 40));
            stream.WriteByte((byte)(value >> 32));
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        public static void SetUInt(this Span<byte> array, uint value, int position)
        {
            array[position] = (byte)(value >> 24);
            array[++position] = (byte)(value >> 16);
            array[++position] = (byte)(value >> 8);
            array[++position] = (byte)value;
        }

        private static byte ReverseBits(this byte b)
        {
            b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
            b = (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
            return b;
        }

        public static bool SequenceEqualTo(this ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        public static bool SequenceEqualTo(this Span<byte> left, Span<byte> right) => ((ReadOnlySpan<byte>)left).SequenceEqualTo(right);

        public static Span<byte> GetBytes(this BitArray bitArray)
        {
            unsafe
            {
                return new Span<byte>(Unsafe.AsPointer(ref bitArray.Array[0]), bitArray.SizeInBytes);
            }
        }

        public static void Negate(this Span<byte> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (byte)~span[i];
            }
        }

        public static void Negate(this int[] span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = ~span[i];
            }
        }

        public static void ReverseBits(this Span<byte> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = span[i].ReverseBits();
            }
        }
    }
}
