using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ZXing;
using ZXing.PngWriter;

using PngWriter = ZXing.PngWriter.PngWriter;

if (args.Length > 0 && args[0] == "sizes")
{
    // compare encoded size of each compression for every case
    Console.WriteLine($"| Case | ZLib (bytes) | FixedHuffman (bytes) | Difference |");
    Console.WriteLine($"|---|---:|---:|---:|");
    foreach (var c in CompressionBenchmarks.AllCases)
    {
        var zlib = CompressionBenchmarks.Generate(c, PngCompression.ZLib).Length;
        var fixedHuffman = CompressionBenchmarks.Generate(c, PngCompression.FixedHuffman).Length;
        Console.WriteLine($"| {c} | {zlib:n0} | {fixedHuffman:n0} | {(fixedHuffman - zlib) * 100.0 / zlib:+0.0;-0.0}% |");
    }
    return;
}

if (args.Length > 1 && args[0] == "dump")
{
    // write each case's PNG for external analysis
    Directory.CreateDirectory(args[1]);
    foreach (var c in CompressionBenchmarks.AllCases)
        foreach (var compression in new[] { PngCompression.ZLib, PngCompression.FixedHuffman })
            File.WriteAllBytes(Path.Combine(args[1], $"{c.Name} {compression}.png"), CompressionBenchmarks.Generate(c, compression));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(CompressionBenchmarks).Assembly).Run(args);

public record BarcodeCase(string Name, BarcodeFormat Format, string Content, int Width, int Height, bool PureBarcode = true)
{
    public override string ToString() => Name;
}

[MemoryDiagnoser]
public class CompressionBenchmarks
{
    public static readonly BarcodeCase[] AllCases =
    [
        new("QR short", BarcodeFormat.QR_CODE, "1", 0, 0),
        new("QR example.com", BarcodeFormat.QR_CODE, "https://example.com", 0, 0),
        new("QR example.com 300px", BarcodeFormat.QR_CODE, "https://example.com", 300, 300),
        new("QR 500 chars 1000px", BarcodeFormat.QR_CODE, new string('x', 500), 1000, 1000),
        new("QR 2000 chars", BarcodeFormat.QR_CODE, string.Concat(Enumerable.Range(0, 400).Select(i => (i * 7919 % 100000).ToString("00000"))), 0, 0),
        new("DataMatrix", BarcodeFormat.DATA_MATRIX, "Hello World", 0, 0),
        new("Aztec 200px", BarcodeFormat.AZTEC, "Hello World", 200, 200),
        new("PDF417", BarcodeFormat.PDF_417, "Hello World", 0, 0),
        new("Code128", BarcodeFormat.CODE_128, "Hello World", 0, 0),
        new("Code128 with text", BarcodeFormat.CODE_128, "Hello World", 300, 100, false),
        new("EAN13 with text", BarcodeFormat.EAN_13, "5901234123457", 0, 0, false),
    ];

    public static IEnumerable<BarcodeCase> Cases => AllCases;

    [ParamsSource(nameof(Cases))]
    public BarcodeCase Case { get; set; } = AllCases[0];

    [Params(false, true)]
    public bool FixedHuffman { get; set; }

    private PngWriter _writer = null!;

    [GlobalSetup]
    public void Setup() => _writer = CreateWriter(Case, FixedHuffman ? PngCompression.FixedHuffman : PngCompression.ZLib);

    [Benchmark]
    public Stream Write() => _writer.Write(Case.Content);

    internal static byte[] Generate(BarcodeCase c, PngCompression compression)
    {
        using var stream = (MemoryStream)CreateWriter(c, compression).Write(c.Content);
        return stream.ToArray();
    }

    private static PngWriter CreateWriter(BarcodeCase c, PngCompression compression)
    {
        var writer = new PngWriter { Format = c.Format };
        writer.Options.Width = c.Width;
        writer.Options.Height = c.Height;
        writer.Options.PureBarcode = c.PureBarcode;
        ((PngRenderer)writer.Renderer).Compression = compression;
        return writer;
    }
}
