using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.Core.Tests.Scanning;

/// <summary>フォルダの登録・解除・有効/無効。</summary>
public sealed class FolderCatalogTests : IDisposable
{
    private readonly ScanFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sub(string name)
    {
        string path = _fx.PathOf(name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Add_registers_the_full_path_without_trailing_separator()
    {
        string sub = Sub("shots");
        FolderInfo f = await _fx.Folders.AddAsync(sub + Path.DirectorySeparatorChar, Ct);

        Assert.Equal(sub, f.Path);
        Assert.True(f.Enabled);
        Assert.Equal(0, f.ImageCount);
        Assert.Null(f.LastScanAt);
        Assert.Null(f.LastError);
        Assert.Equal([f], _fx.Folders.List());
    }

    [Fact]
    public async Task Add_resolves_forward_slashes_and_dot_segments()
    {
        string sub = Sub("shots");
        FolderInfo f = await _fx.Folders.AddAsync(Path.Combine(_fx.Root, "other", "..", "shots").Replace('\\', '/') + "/", Ct);
        Assert.Equal(sub, f.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_path_is_rejected(string path)
    {
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(path, Ct));
    }

    [Fact]
    public async Task Path_with_invalid_characters_is_rejected()
    {
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync("C:\\bad|name?", Ct));
    }

    [Fact]
    public async Task Missing_directory_and_file_paths_are_rejected()
    {
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(_fx.PathOf("no-such"), Ct));
        string file = _fx.Write("a.png", ScanFixture.FakePng("a"));
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(file, Ct)); // ファイルはフォルダではない
        Assert.Empty(_fx.Folders.List());
    }

    [Fact]
    public async Task Already_registered_folder_is_rejected_ignoring_case_and_trailing_separator()
    {
        string sub = Sub("Shots");
        await _fx.Folders.AddAsync(sub, Ct);
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(sub, Ct));
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(UpperAsciiOnly(sub) + "\\", Ct));
        Assert.Single(_fx.Folders.List());
    }

    /// <summary>
    /// ASCII の英字だけを大文字にする。パスの比較(PathKey)は、SQLite の COLLATE NOCASE と同じく ASCII の A〜Z だけで大文字小文字をそろえるので、
    /// ToUpperInvariant だと、一時フォルダのパスに ASCII 以外の文字(ユーザー名の ë など)があるとき、別のパスになってしまう。
    /// </summary>
    private static string UpperAsciiOnly(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
                span[i] = src[i] is >= 'a' and <= 'z' ? (char)(src[i] - 32) : src[i];
        });

    [Fact]
    public async Task Nested_folders_are_rejected_in_both_directions()
    {
        string parent = Sub("lib");
        string child = Sub("lib/inner");
        string sibling = Sub("lib2"); // 名前が前方一致するだけの別のフォルダは入れ子ではない

        await _fx.Folders.AddAsync(parent, Ct);
        var e1 = await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(child, Ct));
        Assert.Contains("入れ子", e1.Message);
        await _fx.Folders.AddAsync(sibling, Ct);

        // 逆向き: 子を先に登録済みなら、親は拒否
        string root2 = Sub("p2");
        string child2 = Sub("p2/c2");
        await _fx.Folders.AddAsync(child2, Ct);
        var e2 = await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.AddAsync(root2, Ct));
        Assert.Contains("入れ子", e2.Message);
        Assert.Equal(3, _fx.Folders.List().Count);
    }

    [Fact]
    public async Task Concurrent_registration_of_the_same_folder_succeeds_exactly_once()
    {
        string sub = Sub("race");
        Task<FolderInfo>[] tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => _fx.Folders.AddAsync(sub, Ct), Ct)).ToArray();
        int ok = 0, rejected = 0;
        foreach (Task<FolderInfo> t in tasks)
        {
            try { await t; ok++; }
            catch (FolderRegistrationException) { rejected++; }
        }
        Assert.Equal(1, ok);
        Assert.Equal(7, rejected);
        Assert.Single(_fx.Folders.List());
    }

    [Fact]
    public void Drive_root_keeps_its_separator_and_other_paths_lose_it()
    {
        Assert.Equal(@"C:\", FolderCatalog.NormalizeRegistrationPath(@"C:\"));
        Assert.Equal(@"C:\", FolderCatalog.NormalizeRegistrationPath("C:/"));
        Assert.Equal(@"C:\Shots", FolderCatalog.NormalizeRegistrationPath(@"C:\Shots\"));
        Assert.Equal(@"C:\Shots", FolderCatalog.NormalizeRegistrationPath(@"  C:\Shots\\  "));
        Assert.Equal(@"C:\Shots\Sub", FolderCatalog.NormalizeRegistrationPath(@"C:\Shots\x\..\Sub"));
    }

    [Fact]
    public async Task Enable_and_disable_are_persisted_and_unknown_ids_throw()
    {
        FolderInfo f = await _fx.Folders.AddAsync(Sub("e"), Ct);
        await _fx.Folders.SetEnabledAsync(f.Id, false, Ct);
        Assert.False(_fx.Folders.List().Single().Enabled);
        await _fx.Folders.SetEnabledAsync(f.Id, true, Ct);
        Assert.True(_fx.Folders.List().Single().Enabled);

        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.SetEnabledAsync(f.Id + 1, false, Ct));
        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.Folders.RemoveAsync(f.Id + 1, Ct));
    }

    [Fact]
    public async Task List_counts_only_paths_that_are_not_missing_and_reports_scan_time_and_ids_in_order()
    {
        string a = Sub("a");
        string b = Sub("b");
        File.WriteAllBytes(Path.Combine(a, "1.png"), ScanFixture.FakePng("1"));
        File.WriteAllBytes(Path.Combine(a, "2.png"), ScanFixture.FakePng("2"));
        File.WriteAllBytes(Path.Combine(b, "3.png"), ScanFixture.FakePng("3"));
        FolderInfo fa = await _fx.Folders.AddAsync(a, Ct);
        FolderInfo fb = await _fx.Folders.AddAsync(b, Ct);
        await _fx.ScanAsync();

        File.Delete(Path.Combine(a, "2.png"));
        await _fx.ScanAsync();

        var list = _fx.Folders.List();
        Assert.Equal([fa.Id, fb.Id], list.Select(f => f.Id));
        Assert.Equal(1, list[0].ImageCount); // missing の 2.png は数えない
        Assert.Equal(1, list[1].ImageCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), list[0].LastScanAt);
    }

    [Fact]
    public async Task Folder_can_be_registered_again_after_removal()
    {
        string sub = Sub("again");
        FolderInfo f = await _fx.Folders.AddAsync(sub, Ct);
        await _fx.Folders.RemoveAsync(f.Id, Ct);
        Assert.Empty(_fx.Folders.List());
        FolderInfo again = await _fx.Folders.AddAsync(sub, Ct);
        Assert.Equal(sub, again.Path);
        Assert.Equal([again], _fx.Folders.List()); // id は再利用されうる(AUTOINCREMENT ではない)ので、比べない
    }
}
