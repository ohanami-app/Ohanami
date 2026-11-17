using System;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Folders;

/// <summary>FolderCatalog の「操作のあと」の呼び出し(フォルダの監視の Refresh をつなぐ。ADR 0009): 成功した登録・解除・有効/無効のあとだけ、1 回ずつ。</summary>
public sealed class FolderCatalogHookTests : IDisposable
{
    private readonly ScanFixture _fx = new();
    private int _calls;

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FolderCatalog NewCatalog() => new(_fx.Db, _fx.Time, afterChange: () => Interlocked.Increment(ref _calls));

    [Fact]
    public async Task The_hook_runs_once_after_each_successful_register_toggle_and_remove()
    {
        FolderCatalog catalog = NewCatalog();

        FolderInfo f = await catalog.AddAsync(_fx.Root, Ct);
        Assert.Equal(1, _calls);
        await catalog.SetEnabledAsync(f.Id, false, Ct);
        Assert.Equal(2, _calls);
        await catalog.RemoveAsync(f.Id, Ct);
        Assert.Equal(3, _calls);
    }

    [Fact]
    public async Task The_hook_does_not_run_after_a_rejected_operation()
    {
        FolderCatalog catalog = NewCatalog();
        FolderInfo f = await catalog.AddAsync(_fx.Root, Ct);
        _calls = 0;

        await Assert.ThrowsAsync<FolderRegistrationException>(() => catalog.AddAsync(_fx.Root, Ct));           // 登録済み
        await Assert.ThrowsAsync<FolderRegistrationException>(() => catalog.AddAsync(_fx.Root + "-nothing", Ct)); // 存在しない
        await Assert.ThrowsAsync<FolderRegistrationException>(() => catalog.RemoveAsync(f.Id + 100, Ct));
        await Assert.ThrowsAsync<FolderRegistrationException>(() => catalog.SetEnabledAsync(f.Id + 100, true, Ct));

        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task A_catalog_without_a_hook_works_as_before()
    {
        var catalog = new FolderCatalog(_fx.Db, _fx.Time);

        FolderInfo f = await catalog.AddAsync(_fx.Root, Ct);
        await catalog.RemoveAsync(f.Id, Ct);

        Assert.Empty(catalog.List());
    }
}
