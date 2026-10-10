using System.Buffers.Binary;
using System.IO.Compression;
using ZXing;
using ZXing.Common;

using PngWriter = ZXing.PngWriter.PngWriter;

namespace ZXing.PngWriter.Tests;

public class PngDeflaterTests
{
    private static readonly bool[] DynamicHuffman = [false, true];

    public static TheoryData<bool, BarcodeFormat, string, int, int, bool> Barcodes
    {
        get
        {
            var data = new TheoryData<bool, BarcodeFormat, string, int, int, bool>();
            foreach (var dynamicHuffman in DynamicHuffman)
                foreach (var (format, content, width, height, pureBarcode) in BarcodeCases)
                    data.Add(dynamicHuffman, format, content, width, height, pureBarcode);
            return data;
        }
    }

    private static readonly (BarcodeFormat, string, int, int, bool)[] BarcodeCases =
    [
        (BarcodeFormat.QR_CODE, "1", 0, 0, true),
        (BarcodeFormat.QR_CODE, "https://example.com", 0, 0, true),
        (BarcodeFormat.QR_CODE, "https://example.com", 300, 300, true),
        (BarcodeFormat.QR_CODE, "https://example.com", 301, 301, false),
        (BarcodeFormat.QR_CODE, new string('x', 1500), 2500, 2500, true),
        (BarcodeFormat.DATA_MATRIX, "Hello World", 0, 0, true),
        (BarcodeFormat.AZTEC, "Hello World", 200, 200, true),
        (BarcodeFormat.PDF_417, "Hello World", 0, 0, true),
        (BarcodeFormat.CODE_128, "Hello World", 0, 0, true),
        (BarcodeFormat.CODE_128, "Hello World", 333, 120, false),
        (BarcodeFormat.EAN_13, "5901234123457", 0, 0, false),
        (BarcodeFormat.ITF, "1234567890", 3000, 50, true),
    ];

    [Theory]
    [MemberData(nameof(Barcodes))]
    public void Deflater_DecodesToSamePixelsAsZLib(bool dynamicHuffman, BarcodeFormat format, string content, int width, int height, bool pureBarcode)
    {
        var compression = dynamicHuffman ? PngCompression.DynamicHuffman : PngCompression.FixedHuffman;
        var deflated = Decode(Generate(format, content, width, height, pureBarcode, compression));
        var zlib = Decode(Generate(format, content, width, height, pureBarcode, PngCompression.ZLib));

        Assert.Equal(zlib.Width, deflated.Width);
        Assert.Equal(zlib.Height, deflated.Height);
        Assert.Equal(zlib.Pixels, deflated.Pixels);
    }

    [Theory]
    [MemberData(nameof(Barcodes))]
    public void Deflater_MatchesBitMatrix(bool dynamicHuffman, BarcodeFormat format, string content, int width, int height, bool pureBarcode)
    {
        var compression = dynamicHuffman ? PngCompression.DynamicHuffman : PngCompression.FixedHuffman;
        if (!pureBarcode) return; // text is drawn below the barcode

        var writer = CreateWriter(format, width, height, pureBarcode, compression);
        var matrix = writer.Encode(content);
        var image = Decode(ToBytes(writer.Write(content)));

        Assert.Equal(matrix.Width, image.Width);
        Assert.Equal(matrix.Height, image.Height);
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                Assert.True(matrix[x, y] == !image.Pixels[(y * image.Width) + x], $"Pixel ({x}, {y}) differs");
    }

    [Theory]
    [MemberData(nameof(Barcodes))]
    public void Deflater_AllocatesOutputAtFinalSize(bool dynamicHuffman, BarcodeFormat format, string content, int width, int height, bool pureBarcode)
    {
        var compression = dynamicHuffman ? PngCompression.DynamicHuffman : PngCompression.FixedHuffman;
        var stream = (MemoryStream)CreateWriter(format, width, height, pureBarcode, compression).Write(content);

        // the header chunks' first write grows the stream to MemoryStream's minimum of 256 bytes;
        // after that the image data grows it once to exactly its final length
        Assert.Equal(Math.Max(stream.Length, 256), stream.Capacity);
    }

    [Theory]
    [InlineData(false, BarcodeFormat.QR_CODE, "https://example.com")]
    [InlineData(false, BarcodeFormat.CODE_128, "Hello World")]
    [InlineData(false, BarcodeFormat.DATA_MATRIX, "Hello World")]
    [InlineData(true, BarcodeFormat.QR_CODE, "https://example.com")]
    [InlineData(true, BarcodeFormat.CODE_128, "Hello World")]
    [InlineData(true, BarcodeFormat.DATA_MATRIX, "Hello World")]
    public void Deflater_RoundTripsThroughZXingReader(bool dynamicHuffman, BarcodeFormat format, string content)
    {
        var compression = dynamicHuffman ? PngCompression.DynamicHuffman : PngCompression.FixedHuffman;
        var image = Decode(Generate(format, content, 0, 0, true, compression));
        var gray = image.Pixels.Select(white => white ? (byte)255 : (byte)0).ToArray();
        var reader = new BarcodeReaderGeneric { Options = { PossibleFormats = [format], PureBarcode = true } };

        var result = reader.Decode(new RGBLuminanceSource(gray, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8));

        Assert.NotNull(result);
        Assert.Equal(content, result.Text);
    }

    public static TheoryData<bool, int, int, int> RandomImages
    {
        get
        {
            var data = new TheoryData<bool, int, int, int>();
            foreach (var dynamicHuffman in DynamicHuffman)
                foreach (var (width, height, seed) in RandomImageCases)
                    data.Add(dynamicHuffman, width, height, seed);
            return data;
        }
    }

    private static readonly (int, int, int)[] RandomImageCases =
    [
        (1, 1, 1), (1, 50, 1), (9, 7, 2), (16, 200, 3), (64, 500, 4), (2063, 40, 5), (2064, 40, 6), (2072, 40, 7), (4000, 300, 8), (100, 5000, 9),
        // all random rows, so many distinct literal and distance symbols
        (800, 400, -1), (3, 3000, -2),
    ];

    [Theory]
    [MemberData(nameof(RandomImages))]
    public void Deflater_RandomImages_DecodeToWrittenRows(bool dynamicHuffman, int width, int height, int seed)
    {
        var compression = dynamicHuffman ? PngCompression.DynamicHuffman : PngCompression.FixedHuffman;
        var random = new Random(seed);
        var widthInBytes = (width + 7) >> 3;
        // a small pool of rows, so identical rows recur both adjacently and further back; negative seeds use fully random rows
        var pool = seed < 0
            ? Enumerable.Range(0, height).Select(_ => CreateRow(random, widthInBytes, 2)).ToArray()
            : Enumerable.Range(0, 6).Select(i => CreateRow(random, widthInBytes, i)).ToArray();

        var rows = new List<byte[]>();
        while (rows.Count < height)
        {
            var row = pool[random.Next(pool.Length)];
            var repeat = Math.Min(random.Next(1, 20), height - rows.Count);
            for (int i = 0; i < repeat; i++) rows.Add(row);
        }

        using var imageWriter = new PngImageWriter(width, height, null, compression);
        foreach (var row in rows) imageWriter.WriteLine(row.AsSpan());
        imageWriter.Finish();

        var image = Decode(ToBytes(imageWriter.Stream));
        Assert.Equal(height, image.Height);
        for (int y = 0; y < height; y++)
            Assert.True(rows[y].AsSpan().SequenceEqual(image.Rows[y]), $"Row {y} differs");
    }

    private static byte[] CreateRow(Random random, int widthInBytes, int kind)
    {
        var row = new byte[widthInBytes];
        switch (kind)
        {
            case 0: Array.Fill(row, (byte)0xFF); break;
            case 1: Array.Fill(row, (byte)0x00); break;
            case 2: random.NextBytes(row); break;
            default:
                // runs of random lengths, like barcode modules
                for (int i = 0; i < widthInBytes;)
                {
                    var value = (byte)random.Next(256);
                    var length = random.Next(1, 40);
                    for (int j = 0; j < length && i < widthInBytes; j++) row[i++] = value;
                }
                break;
        }
        return row;
    }

    private static PngWriter CreateWriter(BarcodeFormat format, int width, int height, bool pureBarcode, PngCompression compression)
    {
        var writer = new PngWriter { Format = format };
        writer.Options.Width = width;
        writer.Options.Height = height;
        writer.Options.PureBarcode = pureBarcode;
        ((PngRenderer)writer.Renderer).Compression = compression;
        return writer;
    }

    private static byte[] Generate(BarcodeFormat format, string content, int width, int height, bool pureBarcode, PngCompression compression) =>
        ToBytes(CreateWriter(format, width, height, pureBarcode, compression).Write(content));

    private static byte[] ToBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private record DecodedImage(int Width, int Height, byte[][] Rows, bool[] Pixels);

    /// <summary>
    /// Minimal decoder for 1-bit grayscale non-interlaced PNGs
    /// </summary>
    private static DecodedImage Decode(byte[] png)
    {
        int width = 0, height = 0;
        var idat = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
                Assert.Equal(1, data[8]);
                Assert.Equal(0, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            offset += 12 + length;
        }

        var zlibData = idat.ToArray();
        var raw = new MemoryStream();
        using (var inflater = new ZLibStream(new MemoryStream(zlibData), CompressionMode.Decompress))
            inflater.CopyTo(raw);
        var bytes = raw.ToArray();

        var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(zlibData.AsSpan(zlibData.Length - 4));
        Assert.Equal(expectedAdler, Adler32(bytes));

        var widthInBytes = (width + 7) >> 3;
        var stride = widthInBytes + 1;
        Assert.Equal(stride * height, bytes.Length);

        var rows = new byte[height][];
        var previous = new byte[widthInBytes];
        for (int y = 0; y < height; y++)
        {
            var filter = bytes[y * stride];
            var row = bytes.AsSpan((y * stride) + 1, widthInBytes).ToArray();
            switch (filter)
            {
                case 0: break;
                case 2:
                    for (int i = 0; i < row.Length; i++) row[i] += previous[i];
                    break;
                default: throw new NotSupportedException($"Filter type {filter} on row {y}");
            }
            rows[y] = row;
            previous = row;
        }

        var pixels = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[(y * width) + x] = (rows[y][x >> 3] & (0x80 >> (x & 7))) != 0;

        return new DecodedImage(width, height, rows, pixels);
    }

    private static uint Adler32(byte[] data)
    {
        uint s1 = 1, s2 = 0;
        foreach (var b in data)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        return (s2 << 16) | s1;
    }
}

public class PngDeflaterCodeLengthTests
{
    [Theory]
    [InlineData(2, 15)]
    [InlineData(30, 15)]
    [InlineData(40, 15)]
    [InlineData(19, 7)]
    [InlineData(286, 15)]
    public void BuildCodeLengths_SkewedFrequencies_AreLimitedAndComplete(int symbols, int maxLength)
    {
        // Fibonacci frequencies give an unlimited Huffman code one level deeper per symbol
        var frequencies = new int[symbols];
        const int cap = int.MaxValue >> 9;
        int a = 1, b = 1;
        for (int i = 0; i < symbols; i++)
        {
            frequencies[i] = a;
            (a, b) = (b, Math.Min(a + b, cap));
        }

        var lengths = new byte[symbols];
        PngDeflater.BuildCodeLengths(frequencies, lengths, maxLength);

        Assert.All(lengths, l => Assert.InRange(l, 1, maxLength));
        // Kraft sum of exactly 1: the code is complete and decodable
        var kraft = lengths.Sum(l => 1L << (maxLength - l));
        Assert.Equal(1L << maxLength, kraft);
        // more frequent symbols never get longer codes
        for (int i = 1; i < symbols; i++)
            if (frequencies[i] > frequencies[i - 1]) Assert.True(lengths[i] <= lengths[i - 1]);
    }

    [Fact]
    public void BuildCodeLengths_UnusedSymbols_HaveLengthZero()
    {
        var frequencies = new int[] { 0, 5, 0, 3, 1, 0 };
        var lengths = new byte[frequencies.Length];
        PngDeflater.BuildCodeLengths(frequencies, lengths, 15);
        Assert.Equal(new byte[] { 0, 1, 0, 2, 2, 0 }, lengths);
    }
}
