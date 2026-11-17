using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class FileLocatorTests
{
    private static IReadOnlyList<string> Item(string hash, params string[] paths) => paths;

    [Fact]
    public void First_existing_path_is_used_and_missing_ones_are_skipped()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "c:\\b.png", "c:\\c.png" };

        Assert.Equal("c:\\b.png", FileLocator.FirstExisting(["c:\\a.png", "c:\\b.png", "c:\\c.png"], existing.Contains));
        Assert.Null(FileLocator.FirstExisting(["c:\\a.png"], existing.Contains));
        Assert.Null(FileLocator.FirstExisting([], existing.Contains));
    }

    [Fact]
    public void Resolve_gives_one_path_per_image_counts_missing_images_and_does_not_hand_over_the_same_file_twice()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c:\\x\\a.png", "c:\\x\\b.png" };
        IReadOnlyList<string>[] items =
        [
            Item("h1", "c:\\x\\gone.png", "c:\\x\\a.png"),
            Item("h2", "c:\\x\\gone2.png"),                  // 見つからない
            Item("h3", "C:\\X\\A.PNG"),                      // 別の画像だが同じファイル(大文字小文字違い): 2 度渡さない
            Item("h4", "c:\\x\\b.png"),
        ];

        (string[] paths, int missing) = FileLocator.Resolve(items, existing.Contains);

        Assert.Equal(["c:\\x\\a.png", "c:\\x\\b.png"], paths);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void Resolve_with_nothing_found_returns_an_empty_list()
    {
        (string[] paths, int missing) = FileLocator.Resolve([Item("h", "c:\\nope.png")], _ => false);

        Assert.Empty(paths);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void The_default_check_uses_the_real_file_system()
    {
        using var temp = new TempDirectory();
        string real = temp.File("real.png");
        File.WriteAllBytes(real, [1]);

        Assert.Equal(real, FileLocator.FirstExisting([temp.File("no.png"), real]));
        Assert.Null(FileLocator.FirstExisting([temp.File("no.png")]));
        Assert.Equal([real], FileLocator.Resolve([Item("h", real)]).Paths);
    }
}
