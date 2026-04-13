using System.Buffers.Binary;
using System.IO.Hashing;
using ZXing;
using ZXing.PngWriter;

using PngWriter = ZXing.PngWriter.PngWriter;

namespace ZXing.PngWriter.Tests;

public class PngOutputTests
{
    [Fact]
    public void GeneratedPng_HasValidSignature()
    {
        var png = GenerateQrCode("test");
        var expected = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.Equal(expected, png[..8]);
    }

    [Fact]
    public void GeneratedPng_HasValidChunkCrcs()
    {
        var png = GenerateQrCode("test");
        foreach (var chunk in EnumerateChunks(png))
        {
            var expected = Crc32.HashToUInt32(chunk.TypeAndData);
            Assert.Equal(expected, chunk.Crc);
        }
    }

    [Fact]
    public void GeneratedPng_ContainsRequiredChunks()
    {
        var png = GenerateQrCode("test");
        var chunkTypes = EnumerateChunks(png).Select(c => c.Type).ToList();
        Assert.Contains("IHDR", chunkTypes);
        Assert.Contains("IDAT", chunkTypes);
        Assert.Contains("IEND", chunkTypes);
        Assert.Equal("IHDR", chunkTypes.First());
        Assert.Equal("IEND", chunkTypes.Last());
    }

    [Fact]
    public void GeneratedPng_IhdrHasExpectedBitDepthAndColorType()
    {
        var png = GenerateQrCode("test");
        var ihdr = EnumerateChunks(png).First(c => c.Type == "IHDR");
        // 1-bit grayscale
        Assert.Equal(1, ihdr.Data[8]);  // bit depth
        Assert.Equal(0, ihdr.Data[9]);  // color type (grayscale)
    }

    [Fact]
    public void GeneratedPng_WithText_HasValidCrcs()
    {
        var writer = new PngWriter { Format = BarcodeFormat.QR_CODE };
        writer.Options.PureBarcode = false;
        var stream = writer.Write("12345", null);
        var png = ToBytes(stream);

        foreach (var chunk in EnumerateChunks(png))
        {
            var expected = Crc32.HashToUInt32(chunk.TypeAndData);
            Assert.Equal(expected, chunk.Crc);
        }
    }

    [Fact]
    public void GeneratedPng_WithTextualInformation_ContainstEXtChunk()
    {
        var textInfo = new TextualInformation { Software = "ZXing.PngWriter.Tests" };
        var writer = new PngWriter { Format = BarcodeFormat.QR_CODE };
        var stream = writer.Write("test", textInfo);
        var png = ToBytes(stream);

        var chunkTypes = EnumerateChunks(png).Select(c => c.Type).ToList();
        Assert.Contains("tEXt", chunkTypes);
    }

    [Fact]
    public void GeneratedPng_WithCompressedText_ContainszTXtChunk()
    {
        var textInfo = new TextualInformation
        {
            Comment = new TextData("compressed comment") { Compress = true }
        };
        var writer = new PngWriter { Format = BarcodeFormat.QR_CODE };
        var stream = writer.Write("test", textInfo);
        var png = ToBytes(stream);

        var chunkTypes = EnumerateChunks(png).Select(c => c.Type).ToList();
        Assert.Contains("zTXt", chunkTypes);
    }

    [Fact]
    public void GeneratedPng_WithUtf8Text_ContainsiTXtChunk()
    {
        var textInfo = new TextualInformation
        {
            Comment = new TextData("utf8 comment") { UTF8 = true }
        };
        var writer = new PngWriter { Format = BarcodeFormat.QR_CODE };
        var stream = writer.Write("test", textInfo);
        var png = ToBytes(stream);

        var chunkTypes = EnumerateChunks(png).Select(c => c.Type).ToList();
        Assert.Contains("iTXt", chunkTypes);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("Hello World")]
    [InlineData("https://example.com")]
    public void GeneratedPng_VariousContents_AllProduceValidPng(string content)
    {
        var png = GenerateQrCode(content);
        // Valid signature
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        // All CRCs valid
        foreach (var chunk in EnumerateChunks(png))
        {
            Assert.Equal(Crc32.HashToUInt32(chunk.TypeAndData), chunk.Crc);
        }
    }

    private static byte[] GenerateQrCode(string content)
    {
        var writer = new PngWriter { Format = BarcodeFormat.QR_CODE };
        var stream = writer.Write(content);
        return ToBytes(stream);
    }

    private static byte[] ToBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static IEnumerable<PngChunk> EnumerateChunks(byte[] png)
    {
        var offset = 8; // skip signature
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            var typeAndData = png.AsSpan(offset + 4, 4 + length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            yield return new PngChunk(type, data, typeAndData, crc);
            offset += 12 + length;
        }
    }

    private record PngChunk(string Type, byte[] Data, byte[] TypeAndData, uint Crc);
}
