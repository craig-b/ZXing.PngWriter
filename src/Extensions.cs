﻿/*
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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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

        public static Span<byte> GetBytes(this BitArray bitArray) => MemoryMarshal.AsBytes<int>(bitArray.Array).Slice(0, bitArray.SizeInBytes);

        public static unsafe void Negate(this Span<byte> span)
        {
            var bytesNegated = 0;
            if (Avx2.IsSupported)
            {
                var vectorCount = span.Length / 32;
                fixed (byte* ptr = span)
                {
                    for (int i = 0; i < vectorCount; i++)
                    {
                        var currentPtr = ptr + (i * 32);
                        Avx.Store(currentPtr, Avx2.AndNot(Avx.LoadVector256(currentPtr), Vector256.Create(byte.MaxValue)));
                        bytesNegated += 32;
                    }
                }
            }
            else if (Sse2.IsSupported)
            {
                var vectorCount = span.Length / 16;
                fixed (byte* ptr = span)
                {
                    for (int i = 0; i < vectorCount; i++)
                    {
                        var currentPtr = ptr + (i * 16);
                        Sse2.Store(currentPtr, Sse2.AndNot(Sse2.LoadVector128(currentPtr), Vector128.Create(byte.MaxValue)));
                        bytesNegated += 16;
                    }
                }
            }
            for (int i = bytesNegated; i < span.Length; i++)
            {
                span[i] = (byte)~span[i];
            }
        }

        public static unsafe void Negate(this Span<int> span)
        {
            var intsNegated = 0;
            if (Avx2.IsSupported)
            {
                var vectorCount = span.Length / 8;
                fixed (int* ptr = &span[0])
                {
                    for (int i = 0; i < vectorCount; i++)
                    {
                        var currentPtr = ptr + (i * 8);
                        Avx.Store(currentPtr, Avx2.AndNot(Avx.LoadVector256(currentPtr), Vector256.Create(-1)));
                        intsNegated += 8;
                    }
                }
            }
            else if (Sse2.IsSupported)
            {
                var vectorCount = span.Length / 4;
                fixed (int* ptr = &span[0])
                {
                    for (int i = 0; i < vectorCount; i++)
                    {
                        var currentPtr = ptr + (i * 4);
                        Sse2.Store(currentPtr, Sse2.AndNot(Sse2.LoadVector128(currentPtr), Vector128.Create(-1)));
                        intsNegated += 4;
                    }
                }
            }
            for (int i = intsNegated; i < span.Length; i++)
            {
                span[i] = ~span[i];
            }
        }

        public static unsafe void ReverseBits(this Span<int> span)
        {
            var intsReversed = 0;

            if (Avx2.IsSupported)
            {
                fixed (int* ptr = span)
                {
                    var vectorCount = span.Length / 8;
                    for (int i = 0; i < vectorCount; i++)
                    {

                        var vector = Avx.LoadVector256((ptr + intsReversed));
                        var vector2 = Avx2.And(Avx2.And(vector, Vector256.Create(0xFF00FF)), Vector256.Create(-16711936));
                        vector =
                            Avx2.Add(
                                Avx2.Or(
                                    Avx2.ShiftRightLogical(vector, 8),
                                    Avx2.ShiftLeftLogical(vector, 24)
                                ),
                                Avx2.Or(
                                    Avx2.ShiftLeftLogical(vector2, 8),
                                    Avx2.ShiftRightLogical(vector2, 24)
                                 )
                            );

                        Avx.Store(ptr + intsReversed, vector);
                        intsReversed += 8;
                    }
                }
            }

            for (int i = intsReversed; i < span.Length; i++)
            {
                span[i] = BinaryPrimitives.ReverseEndianness(span[i]);
            }

            fixed (void* ptr = span)
            {
                new Span<byte>(ptr, span.Length * 4).ReverseBits();
            }
        }

        static readonly Vector256<ushort> BitShiftMask_F0F0 = Vector256.Create((ushort)0xF0F0);
        static readonly Vector256<ushort> BitShiftMask_0F0F = Vector256.Create((ushort)0x0F0F);
        static readonly Vector256<ushort> BitShiftMask_CCCC = Vector256.Create((ushort)0xCCCC);
        static readonly Vector256<ushort> BitShiftMask_3333 = Vector256.Create((ushort)0x3333);
        static readonly Vector256<ushort> BitShiftMask_AAAA = Vector256.Create((ushort)0xAAAA);
        static readonly Vector256<ushort> BitShiftMask_5555 = Vector256.Create((ushort)0x5555);

        public static unsafe void ReverseBits(this Span<byte> span)
        {
            var bytesReversed = 0;

            if (Avx2.IsSupported)
            {
                fixed (byte* ptr = span)
                {
                    var vectorCount = span.Length / 32;
                    for (int i = 0; i < vectorCount; i++)
                    {
                        var vector = Avx.LoadVector256((ushort*)(ptr + bytesReversed));
                        vector = Avx2.Or(Avx2.ShiftRightLogical(Avx2.And(vector, BitShiftMask_F0F0), 4), Avx2.ShiftLeftLogical(Avx2.And(vector, BitShiftMask_0F0F), 4));
                        vector = Avx2.Or(Avx2.ShiftRightLogical(Avx2.And(vector, BitShiftMask_CCCC), 2), Avx2.ShiftLeftLogical(Avx2.And(vector, BitShiftMask_3333), 2));
                        vector = Avx2.Or(Avx2.ShiftRightLogical(Avx2.And(vector, BitShiftMask_AAAA), 1), Avx2.ShiftLeftLogical(Avx2.And(vector, BitShiftMask_5555), 1));
                        Avx.Store((ushort*)(ptr + bytesReversed), vector);
                        bytesReversed += 32;
                    }
                }
            }

            for (int i = bytesReversed; i < span.Length; i++)
            {
                span[i] = span[i].ReverseBits();
            }
        }
    }
}
