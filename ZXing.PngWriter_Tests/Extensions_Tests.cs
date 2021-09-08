using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using ZXing.Common;
using ZXing.PngWriter;

namespace ZXing.PngWriter_Tests
{
    public class Tests
    {
        [SetUp]
        public void Setup()
        {
        }

        //public static void WriteInt(this Stream stream, int value)
        [Test]
        public void WriteInt_Test()
        {
            var stream = new MemoryStream();
            stream.WriteInt(1);
            Assert.True(stream.Length == 4, "Stream length should be 4 bytes (1 int) - actual: {0} bytes", stream.Length);
            stream.Position = 0;
            var buffer = new byte[4];
            stream.Read(buffer);
            Assert.IsTrue(buffer.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 1 }), "Byte sequence should be 0,0,0,1 - actual: {0},{1},{2},{3}", buffer[0], buffer[1], buffer[2], buffer[3]);
        }

        //public static void WriteUInt(this Stream stream, uint value)
        [Test]
        public void WriteUint_Test()
        {
            var stream = new MemoryStream();
            stream.WriteUInt(1);
            Assert.True(stream.Length == 4, "Stream length should be 4 bytes (1 uint) - actual: {0} bytes", stream.Length);
            stream.Position = 0;
            var buffer = new byte[4];
            stream.Read(buffer);
            Assert.IsTrue(buffer.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 1 }), "Byte sequence should be 0,0,0,1 - actual: {0},{1},{2},{3}", buffer[0], buffer[1], buffer[2], buffer[3]);
        }

        //public static void WriteLong(this Stream stream, long value)
        [Test]
        public void WriteLong_Test()
        {
            var stream = new MemoryStream();
            stream.WriteLong(1);
            Assert.True(stream.Length == 8, "Stream length should be 8 bytes (1 long) - actual: {0} bytes", stream.Length);
            stream.Position = 0;
            var buffer = new byte[8];
            stream.Read(buffer);
            Assert.IsTrue(buffer.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }), "Byte sequence should be 0,0,0,0,0,0,0,1 - actual: {0},{1},{2},{3},{4},{5},{6},{7}", buffer[0], buffer[1], buffer[2], buffer[3], buffer[4], buffer[5], buffer[6], buffer[7]);
        }

        //public static void SetUInt(this Span<byte> array, uint value, int position)
        [Test]
        public void SetUInt_Test()
        {
            Span<byte> span = stackalloc byte[6];
            span.SetUInt(16909060, 1);
            Assert.IsTrue(span.SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 0 }));
        }

        //private static byte ReverseBits(this byte b)
        [Test]
        public void ReverseBits_Byte_Test()
        {
            byte byte1 = 1;
            byte1.ReverseBitsByRef();
            Assert.AreEqual(byte1, 128);
            byte1 >>= 1;
            byte1.ReverseBitsByRef();
            Assert.AreEqual(byte1, 2);
            var bytes = new byte[] { 1, 0b0100_0000 };
            bytes[0].ReverseBitsByRef();
            bytes[1].ReverseBitsByRef();
            Assert.IsTrue(bytes.SequenceEqual(new byte[] { 128, 2 }));
        }

        //public static Span<byte> GetBytes(this BitArray bitArray) => MemoryMarshal.AsBytes<int>(bitArray.Array).Slice(0, bitArray.SizeInBytes);
        [Test]
        public void GetBytes_Test()
        {
            var bitArray = new BitArray();
            bitArray.appendBit(true);
            bitArray.appendBit(false);
            bitArray.appendBit(true);
            var bytes = bitArray.GetBytes();
            Assert.AreEqual(bytes.Length, 4);
            Assert.IsTrue(bytes.SequenceEqual(new byte[] { 5, 0, 0, 0 }));
        }

        //public static unsafe void Negate(this Span<byte> span)
        [Test]
        public void NegateBytes_Test()
        {
            var bytes = new byte[2] { 1, 128 };
            bytes.AsSpan().Negate();
            Assert.IsTrue(bytes.SequenceEqual(new byte[] { 254, 127 }));
        }

        //public static unsafe void Negate(this Span<int> span)
        [Test]
        public void NegateInts_Test()
        {
            var ints = new int[3] { 1, int.MinValue, 2 };
            ints.AsSpan().Negate();
            Assert.IsTrue(ints.SequenceEqual(new int[] { -2, int.MaxValue, -3 }));
        }

        //public static unsafe void ReverseBits(this Span<int> span)
        [Test]
        public void ReverseBits_Ints_Test()
        {
            var ints = new int[] { 1, int.MinValue + 2, 1, int.MinValue + 2, 1, int.MinValue + 2, 1, int.MinValue + 2, 1, int.MinValue + 2, 0 };
            ints.AsSpan().ReverseBits();
            Assert.IsTrue(ints.SequenceEqual(new int[] { int.MinValue, 0b0100_0000_0000_0000_0000_0000_0000_0001, int.MinValue, 0b0100_0000_0000_0000_0000_0000_0000_0001, int.MinValue, 0b0100_0000_0000_0000_0000_0000_0000_0001, int.MinValue, 0b0100_0000_0000_0000_0000_0000_0000_0001, int.MinValue, 0b0100_0000_0000_0000_0000_0000_0000_0001, 0 }));
        }

        //private static unsafe void ReverseEndianness(this Span<int> span)
        [Test]
        public void ReverseEndianness_Test()
        {
            var ints = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, int.MinValue };
            var intsCopy = ints.Clone() as int[];
            ints.AsSpan().ReverseEndianness();
            for (int i = 0; i < intsCopy.Length; i++)
            {
                intsCopy[i] = BinaryPrimitives.ReverseEndianness(intsCopy[i]);
            }
            Assert.IsTrue(ints.AsSpan().SequenceEqual(intsCopy));
        }

        //public static unsafe void ReverseBits(this Span<byte> span)
        [Test]
        public void ReverseBits_Bytes_Test()
        {
            var bytes = new byte[] { 1, 0b0100_0000 };
            bytes.AsSpan().ReverseBits();
            Assert.IsTrue(bytes.SequenceEqual(new byte[] { 128, 2 }));
        }
    }
}