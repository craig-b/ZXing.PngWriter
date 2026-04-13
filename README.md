# ZXing.PngWriter
An extremely fast barcode generator binding for ZXing.Net

Built for .NET 8+ this library takes advantage of `Span<T>`, `Vector<T>` SIMD, and `ArrayPool<T>` to produce png `Stream`s with minimal overhead.

4 to 30 times faster than other ZXing.Net bindings with the resulting file being up to 100x smaller!

[![NuGet](https://img.shields.io/nuget/v/ZXing.PngWriter "Download from NuGet")](https://www.nuget.org/packages/ZXing.PngWriter)

## Usage

```csharp
var writer = new PngWriter
{
    Format = BarcodeFormat.QR_CODE
};

using var stream = writer.Write("https://example.com");
```

With PNG metadata:

```csharp
var writer = new PngWriter
{
    Format = BarcodeFormat.QR_CODE
};

var textInfo = new TextualInformation
{
    Software = "My App",
    Comment = "Generated QR code"
};

using var stream = writer.Write("https://example.com", textInfo);
```

## Performance

Generates **up to ~8,000 QR codes per second** on a single thread (120–580 μs depending on content size).

Recent optimizations (cross-platform SIMD, removal of reflection, built-in compression and hashing) improved throughput by **10–66%** and reduced allocations by **6–57%** compared to v0.3.
