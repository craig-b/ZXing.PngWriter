using ZXing.PngWriter;

namespace ZXing.PngWriter.Tests;

public class ExtensionsTests
{
    [Fact]
    public void Negate_Bytes_InvertsAllBits()
    {
        byte[] data = [0x00, 0xFF, 0xAA, 0x55];
        data.AsSpan().Negate();
        Assert.Equal([0xFF, 0x00, 0x55, 0xAA], data);
    }

    [Fact]
    public void Negate_Bytes_HandlesLargeSpan()
    {
        // Large enough to hit SIMD paths (>32 bytes)
        var data = new byte[64];
        Array.Fill(data, (byte)0xAA);
        data.AsSpan().Negate();
        Assert.All(data, b => Assert.Equal(0x55, b));
    }

    [Fact]
    public void Negate_Ints_InvertsAllBits()
    {
        int[] data = [0, -1, 0x0F0F0F0F];
        data.AsSpan().Negate();
        Assert.Equal(-1, data[0]);
        Assert.Equal(0, data[1]);
        Assert.Equal(unchecked((int)0xF0F0F0F0), data[2]);
    }

    [Fact]
    public void Negate_Ints_HandlesLargeSpan()
    {
        var data = new int[32];
        Array.Fill(data, 0x0F0F0F0F);
        data.AsSpan().Negate();
        Assert.All(data, i => Assert.Equal(unchecked((int)0xF0F0F0F0), i));
    }

    [Fact]
    public void ReverseBits_Byte_ReversesCorrectly()
    {
        // 0b10000000 -> 0b00000001
        byte[] data = [0x80];
        data.AsSpan().ReverseBits();
        Assert.Equal([0x01], data);
    }

    [Fact]
    public void ReverseBits_Bytes_HandlesLargeSpan()
    {
        var data = new byte[64];
        data[0] = 0x80; // 10000000
        data.AsSpan().ReverseBits();
        Assert.Equal(0x01, data[0]); // 00000001
    }

    [Fact]
    public void WriteUInt_BigEndian()
    {
        using var stream = new MemoryStream();
        stream.WriteUInt(0x01020304);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], stream.ToArray());
    }

    [Fact]
    public void WriteInt_BigEndian()
    {
        using var stream = new MemoryStream();
        stream.WriteInt(0x01020304);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], stream.ToArray());
    }

    [Fact]
    public void SetUInt_BigEndian()
    {
        Span<byte> span = stackalloc byte[8];
        span.SetUInt(0x01020304, 2);
        Assert.Equal(0x01, span[2]);
        Assert.Equal(0x02, span[3]);
        Assert.Equal(0x03, span[4]);
        Assert.Equal(0x04, span[5]);
    }
}
