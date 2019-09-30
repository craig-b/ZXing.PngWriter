# ZXing.PngWriter
An extremely fast barcode generator binding for ZXing.Net

Built for .net core 3.0 this library takes advantage of ```Span<T>```'s and ```ArrayPool<T>```'s to produce png ```Streams``` with minimal overhead.

4 to 30 times faster than other ZXing.Net bindings with the resulting file being up to 100x smaller!

[![NuGet](https://img.shields.io/nuget/v/ZXing.PngWriter "Download from NuGet")](https://www.nuget.org/packages/ZXing.PngWriter)
