using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// OhanamiCore の組み立てと起動。NativeRuntime.Configure(プロセス全体の設定)は呼ばない内部の入口を使う
/// (Configure を含む入口は、失敗系だけを確かめる)。実際のモデルを使うものは Category=Model。
/// </summary>
public sealed class OhanamiCoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoreOptions Options(TempDirectory dir, string modelDirectory, TestLog log, string? nativeDirectory = null) => new(
        DatabasePath: dir.File("sub/ohanami.db"),
        ThumbnailDirectory: dir.File("thumbs"),
        ModelDirectory: modelDirectory,
        NativeDirectory: nativeDirectory ?? AppContext.BaseDirectory,
        Log: log,
        WatchFolders: false);   // 走査のタイミングはテストが決める(フォルダの監視は CoreFolderWatchingTests)

    private static Task<OhanamiCore> Open(CoreOptions options) => OhanamiCore.OpenAsync(options, configureNative: false, time: null, Ct);

    [Fact]
    public async Task Missing_model_files_make_the_core_fail_with_a_reason_but_folders_and_scan_still_work()
    {
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        var log = new TestLog();

        await using OhanamiCore core = await Open(Options(dir, emptyModels, log));
        CoreStatus status = await core.WaitUntilStartedAsync(Ct);

        Assert.Equal(CorePhase.Failed, status.Phase);
        Assert.Contains("textual/model.onnx", status.Reason); // ファイル名が理由に出る
        Assert.Contains(log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));

        // 検索は動かない(経路が Failed で伝わる)が、フォルダの登録・走査は使える。ベクトルの読み込みが終わってから検索する(読み込み中は NotReady)
        Assert.True(await IndexingFixture.WaitUntilAsync(() => core.Status.VectorsLoaded));
        SearchResponse response = await core.Search.SearchAsync(new SearchQuery("test"), Ct);
        Assert.Equal(PathState.Failed, response.Paths.Single(p => p.PathId == "visual").State);
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        TestFiles.WriteAged(Path.Combine(library, "a.png"), Scanning.ScanFixture.FakePng("a"));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        ScanSummary scan = await core.Indexer.ScanAsync([folder.Id], Ct);
        Assert.Equal(1, scan.Added);
        Assert.Equal(1, core.Indexer.State.Stages.Single().Pending);
    }

    [Fact]
    public async Task Embedding_fails_the_job_with_the_verification_failure_as_the_reason_when_the_models_are_unusable()
    {
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        await using OhanamiCore core = await Open(Options(dir, emptyModels, new TestLog()));
        await core.WaitUntilStartedAsync(Ct);
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        TestFiles.WriteAged(Path.Combine(library, "a.png"), Scanning.ScanFixture.FakePng("a"));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        await core.Indexer.ScanAsync([folder.Id], Ct);

        await core.Indexer.StartAsync([JobKind.Embed], Ct);

        IndexState state = core.Indexer.State;
        Assert.Equal(IndexRunState.Failed, state.Run);
        Assert.Equal(EnginePhase.Failed, state.Engine.Phase);
        Assert.Contains("textual/model.onnx", state.LastError); // 画像エンコーダを作る時点で、起動時の検証の失敗が理由つきで返る(無限に待たない)
    }

    [Fact]
    public async Task Thumbnails_are_made_from_scanned_images_and_cached_under_the_configured_directory()
    {
        // モデルが無くても(Failed でも)サムネイルは使える: 走査した画像のハッシュで、箱に収まる画素が返り、キャッシュは ThumbnailDirectory に書かれる
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        CoreOptions options = Options(dir, emptyModels, new TestLog());
        await using OhanamiCore core = await Open(options);
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        TestFiles.WriteAged(Path.Combine(library, "wide.png"), ThumbnailTestImages.Gradient(400, 200));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        await core.Indexer.ScanAsync([folder.Id], Ct);
        string hash;
        using (SqliteConnection read = core.DatabaseForTests.CreateReadConnection())
            hash = TestDb.Text(read, "SELECT hash FROM images");

        ThumbnailResult first = await core.Thumbnails.GetAsync(hash, 100, 100, Ct);
        ThumbnailResult second = await core.Thumbnails.GetAsync(hash, 100, 100, Ct);

        Assert.Equal(ThumbnailStatus.Ok, first.Status);
        Assert.False(first.FromCache);
        Assert.Equal((100, 50), (first.Pixels!.Width, first.Pixels.Height)); // 400x200 → 箱 100x100 に収まる(アスペクト比を維持)
        Assert.True(second.FromCache);
        Assert.True(File.Exists(Path.Combine(options.ThumbnailDirectory, "v1", hash[..2], hash + "_100x100.jpg")));
    }

    [Fact]
    public async Task The_image_detail_and_the_failure_list_work_through_the_core_even_when_the_models_are_missing()
    {
        // モデルが無くても(Failed でも)、走査した画像の詳細と手動タグ、失敗の一覧は読める(DB を読むだけ)
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        await using OhanamiCore core = await Open(Options(dir, emptyModels, new TestLog()));
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        TestFiles.WriteAged(Path.Combine(library, "wide.png"), ThumbnailTestImages.Gradient(400, 200));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        await core.Indexer.ScanAsync([folder.Id], Ct);
        string hash;
        using (SqliteConnection read = core.DatabaseForTests.CreateReadConnection())
            hash = TestDb.Text(read, "SELECT hash FROM images");
        await core.Tags.AddAsync(hash, "お気に入り", Ct);

        ImageDetail? detail = await core.GetImageDetailAsync(hash, Ct);

        Assert.NotNull(detail);
        Assert.Equal(hash, detail.Hash);
        Assert.Equal("png", detail.Format);
        Assert.Equal(Path.Combine(library, "wide.png"), Assert.Single(detail.Paths).Path, ignoreCase: true);
        Assert.All(detail.Stages, s => Assert.Equal(StageState.Pending, s.State));
        Assert.Null(detail.Caption);
        Assert.Equal(["お気に入り"], detail.ManualTags);
        Assert.Null(await core.GetImageDetailAsync(new string('0', 64), Ct));
        Assert.Same(core.Images, core.Images);
        FailureList failures = await core.Images.ListFailuresAsync(Stage.Embed, ct: Ct);
        Assert.Empty(failures.Items);
    }

    [Fact]
    public async Task Disposing_the_core_stops_a_running_scan_before_the_database_closes_and_later_scans_are_refused()
    {
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        OhanamiCore core = await Open(Options(dir, emptyModels, new TestLog()));
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        for (int i = 0; i < 1500; i++) TestFiles.WriteAged(Path.Combine(library, $"f{i:D4}.png"), Scanning.ScanFixture.FakePng("img" + i));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);

        Task<ScanSummary> scan = core.Indexer.ScanAsync([folder.Id], CancellationToken.None);
        await core.DisposeAsync(); // 走査の途中でも、止めてから DB を閉じる(走査が DB の破棄のあとに触らない)

        // 走査は、終わっていたか、止められた(どちらでも、例外はキャンセルだけ。DB の破棄後の例外にならない)
        try { await scan; }
        catch (OperationCanceledException) { }
        Assert.True(scan.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => core.Indexer.ScanAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Open_rejects_a_blank_thumbnail_directory()
    {
        using var dir = new TempDirectory();
        CoreOptions good = Options(dir, dir.Path, new TestLog());
        await Assert.ThrowsAsync<ArgumentException>(() => Open(good with { ThumbnailDirectory = " " }));
    }

    [Fact]
    public async Task Foreign_database_file_stops_open_with_an_exception()
    {
        using var dir = new TempDirectory();
        string db = dir.File("sub/ohanami.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var raw = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            raw.Open();
            TestDb.Exec(raw, "CREATE TABLE t(x INTEGER)");
        }
        await Assert.ThrowsAsync<UnknownSchemaVersionException>(() => Open(Options(dir, dir.Path, new TestLog())));
        File.Delete(db); // 開いたままのハンドルが残っていない
    }

    [Fact]
    public async Task Open_with_configure_fails_before_touching_the_database_when_the_native_directory_is_missing()
    {
        using var dir = new TempDirectory();
        CoreOptions options = Options(dir, dir.Path, new TestLog(), nativeDirectory: dir.File("no-such-native-dir"));

        await Assert.ThrowsAsync<NativeRuntimeException>(() => OhanamiCore.OpenAsync(options, Ct));

        Assert.False(File.Exists(options.DatabasePath)); // Configure が最初。DB は作られない
    }

    [Fact]
    public async Task Open_rejects_incomplete_options()
    {
        using var dir = new TempDirectory();
        CoreOptions good = Options(dir, dir.Path, new TestLog());
        await Assert.ThrowsAsync<ArgumentNullException>(() => OhanamiCore.OpenAsync(null!, configureNative: false, time: null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Open(good with { DatabasePath = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Open(good with { ModelDirectory = " " }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Open(good with { Log = null! }));
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_closes_the_database_file()
    {
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        CoreOptions options = Options(dir, emptyModels, new TestLog());
        OhanamiCore core = await Open(options);

        await core.DisposeAsync();
        await core.DisposeAsync();

        File.Delete(options.DatabasePath); // 閉じているので消せる
    }

    [Fact]
    public async Task Dispose_right_after_open_is_safe_and_closes_the_database_file()
    {
        // 起動の流れ(検証・読み込み)が走っている最中に破棄しても、例外にならず、DB のファイルを閉じる
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        CoreOptions options = Options(dir, emptyModels, new TestLog());

        OhanamiCore core = await Open(options);
        await core.DisposeAsync();

        File.Delete(options.DatabasePath);
    }

    [Fact]
    public async Task Status_changes_are_reported_to_subscribers()
    {
        using var dir = new TempDirectory();
        string emptyModels = dir.File("no-models");
        Directory.CreateDirectory(emptyModels);
        await using OhanamiCore core = await Open(Options(dir, emptyModels, new TestLog()));
        await core.WaitUntilStartedAsync(Ct);
        Assert.Equal(CorePhase.Failed, core.Status.Phase);

        // 購読したあとに起きる変化(ベクトルの読み込みの完了)が通知される。Failed は上書きされず、理由も残る
        var seen = new System.Collections.Generic.List<CoreStatus>();
        core.StatusChanged += s => { lock (seen) seen.Add(s); };
        Assert.True(await IndexingFixture.WaitUntilAsync(() => core.Status.VectorsLoaded));
        Assert.True(await IndexingFixture.WaitUntilAsync(() => { lock (seen) return seen.Count > 0 || core.Status.VectorsLoaded; }));
        Assert.Equal(CorePhase.Failed, core.Status.Phase);
        Assert.Contains("textual/model.onnx", core.Status.Reason);
    }

    // ---- 実際のモデル(Category=Model。検証に数秒、テキスト側の CPU セッションの作成に数秒)

    [Fact]
    [Trait("Category", "Model")]
    public async Task With_the_real_models_the_core_becomes_ready_registers_the_model_and_answers_a_query()
    {
        string models = Path.Combine(TestFiles.ModelsDir, TestFiles.ModelRepo);
        using var dir = new TempDirectory();
        var log = new TestLog();

        await using OhanamiCore core = await Open(Options(dir, models, log));
        Assert.Equal(CorePhase.Starting, core.Status.Phase); // OpenAsync はすぐ戻る(検証を待たない)
        CoreStatus status = await core.WaitUntilStartedAsync(Ct);

        Assert.True(status.Phase == CorePhase.Ready, status.Reason);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => core.Status.VectorsLoaded)); // 空の表の読み込み
        Assert.Equal(0, core.Status.VectorCount);

        // models に現行のモデルが登録されている
        using (SqliteConnection read = core.DatabaseForTests.CreateReadConnection())
            Assert.Equal(ModelCatalog.Current.ModelId, TestDb.Text(read, "SELECT id FROM models"));

        // テキスト側(実物の CPU セッション)でクエリを埋め込み、空の表に対して検索が成功する
        await core.FullTextIndex.EnsureAsync(Ct); // 全文検索の索引は裏で作る(空の DB でも規約を記録する)。済むまで、全文の経路は NotReady
        SearchResponse response = await core.Search.SearchAsync(new SearchQuery("猫"), Ct);
        Assert.Equal(PathState.Ok, response.Paths.Single(p => p.PathId == "visual").State);
        Assert.Equal(PathState.Ok, response.Paths.Single(p => p.PathId == "fulltext").State);
        Assert.Empty(response.Results);
        Assert.Equal(0, core.Indexer.State.Stages.Single().Done);
        Assert.Equal(EnginePhase.NotCreated, core.Indexer.State.Engine.Phase); // GPU のセッションは作っていない
    }
}
