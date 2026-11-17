using Ohanami.Core.Data;
using Ohanami.Core.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Scanning;

/// <summary>PathKey と ImageFormatDetector(純粋な関数)。</summary>
public sealed class ScanningUnitTests
{
    [Theory]
    [InlineData(@"C:\Shots\A.PNG", @"c:\shots\a.png")]
    [InlineData(@"c:\shots\a.png", @"c:\shots\a.png")]
    [InlineData(@"C:\画像\ABC.png", @"c:\画像\abc.png")]
    public void PathKey_lowercases_only_ascii_letters(string path, string expected) =>
        Assert.Equal(expected, PathKey.Normalize(path));

    [Fact]
    public void PathKey_does_not_fold_non_ascii_case_like_sqlite_nocase()
    {
        // SQLite の NOCASE は ASCII だけ。É と é は別のパスとして扱う(ADR 0007)
        Assert.NotEqual(PathKey.Normalize(@"C:\É.png"), PathKey.Normalize(@"C:\é.png"));
    }

    [Theory]
    [InlineData(@"C:\Shots\a.png", @"C:\Shots", true)]
    [InlineData(@"C:\SHOTS\sub\a.png", @"c:\shots\", true)]
    [InlineData(@"C:\Shots2\a.png", @"C:\Shots", false)]   // 前方一致の取り違えをしない
    [InlineData(@"C:\Shot\a.png", @"C:\Shots", false)]
    [InlineData(@"C:\Shots", @"C:\Shots", true)]          // ディレクトリ自身
    [InlineData(@"C:\a.png", @"C:\", true)]
    public void IsUnder(string path, string directory, bool expected) =>
        Assert.Equal(expected, PathKey.IsUnder(PathKey.Normalize(path), PathKey.DirectoryKey(directory)));

    [Fact]
    public void Png_jpeg_and_webp_are_detected_by_their_magic_numbers()
    {
        Assert.Equal(ImageFormat.Png, ImageFormatDetector.Detect(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }));
        Assert.Equal(ImageFormat.Jpeg, ImageFormatDetector.Detect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(ImageFormat.Webp, ImageFormatDetector.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]                  // PNG の署名が途中まで
    [InlineData(new byte[] { 0xFF, 0xD8 })]                              // JPEG が 2 バイト
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]      // GIF
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // BMP
    public void Other_or_truncated_content_is_not_detected(byte[] header) =>
        Assert.Null(ImageFormatDetector.Detect(header));

    [Fact]
    public void Riff_container_that_is_not_webp_is_not_detected()
    {
        Assert.Null(ImageFormatDetector.Detect("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Null(ImageFormatDetector.Detect("RIFF\0\0\0\0WEB"u8)); // 12 バイトに足りない
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.PNG", true)]
    [InlineData("a.Jpg", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.WEBP", true)]
    [InlineData("a.gif", false)]
    [InlineData("a.png.txt", false)]
    [InlineData("png", false)]
    [InlineData("a.jfif", false)]
    public void Only_four_extensions_are_scanned(string name, bool expected) =>
        Assert.Equal(expected, ImageFormatDetector.HasSupportedExtension(name));
}
