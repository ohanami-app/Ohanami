using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Embedding;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>Indexer + EmbedRunner(偽のエンコーダ・偽の前処理。GPU もモデルも要らない)。ADR 0028。</summary>
public sealed class IndexerTests : IDisposable
{
    private static readonly JobKind[] Embed = [JobKind.Embed];

    private readonly IndexingFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ModelId => _fx.Factory.Model.ModelId;

    // ---- 正常系

    [Fact]
    public async Task Start_embeds_every_pending_image_and_records_vector_status_dimensions_and_run()
    {
        List<string> paths = await _fx.AddImagesAsync(5);
        Assert.Equal(5, _fx.Summary().Pending);

        await _fx.Indexer.StartAsync(Embed, Ct);

        IndexState state = _fx.Indexer.State;
        Assert.Equal(IndexRunState.Waiting, state.Run);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 5, 0, 0), state.Stages.Single());
        Assert.Equal(5, _fx.Count("SELECT count(*) FROM image_vectors"));
        Assert.Equal(5, _fx.Count($"SELECT count(*) FROM stage_status WHERE stage = 'embed' AND status = 'done' AND model_id = '{ModelId}'"));
        Assert.Equal(5, _fx.Count("SELECT count(*) FROM images WHERE width IS NOT NULL AND height IS NOT NULL"));
        Assert.Equal(5, _fx.Embedded.Count);

        // ベクトルは 4608 バイト(1152 × float32)で、偽のエンコーダが返した one-hot と一致する
        byte[] file0 = File.ReadAllBytes(paths[0]);
        string hash0 = ScanFixture.Sha256Hex(file0);
        using SqliteConnection read = _fx.Scan.Db.CreateReadConnection();
        using SqliteCommand cmd = read.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM image_vectors WHERE hash = $h AND model_id = $m";
        cmd.Parameters.AddWithValue("$h", hash0);
        cmd.Parameters.AddWithValue("$m", ModelId);
        byte[] blob = (byte[])cmd.ExecuteScalar()!;
        Assert.Equal(4608, blob.Length);
        var floats = new float[1152];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        Assert.Equal(1f, floats[FakePreprocessor.Checksum(file0)]);
        Assert.Equal(1f, floats.Sum());

        // 実行の記録: 1 回の実行、終了時刻あり、自己検査の結果が runtime_json に入る
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs"));
        Assert.Equal("embed", _fx.Scan.Text("SELECT job_kind FROM runs"));
        Assert.Equal("test-1.0", _fx.Scan.Text("SELECT app_version FROM runs"));
        Assert.NotNull(_fx.Scan.Text("SELECT ended_at FROM runs"));
        Assert.Contains("1.31.1-fake", _fx.Scan.Text("SELECT runtime_json FROM runs")!);
        Assert.Contains("cudnn64_9.dll", _fx.Scan.Text("SELECT runtime_json FROM runs")!);
        Assert.Equal(1, _fx.Count("SELECT count(DISTINCT run_id) FROM stage_status"));
        // 資源は空いた
        Assert.Equal(1, _fx.Factory.CreateCount);
        Assert.Equal(1, _fx.Factory.DisposeCount);
        // models の行が登録されている(FK のため)
        Assert.Equal(ModelId, _fx.Scan.Text("SELECT id FROM models"));
    }

    [Fact]
    public async Task The_settings_the_encoder_reports_are_saved_in_the_run_row()
    {
        _fx.Factory.Settings = new Dictionary<string, string> { ["cuda.use_tf32"] = "0", ["session.intra_op_threads"] = "2" };
        await _fx.AddImagesAsync(2);

        await _fx.Indexer.StartAsync(Embed, Ct);

        string json = _fx.Scan.Text("SELECT runtime_json FROM runs")!;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("0", doc.RootElement.GetProperty("settings").GetProperty("cuda.use_tf32").GetString());
        Assert.Equal("2", doc.RootElement.GetProperty("settings").GetProperty("session.intra_op_threads").GetString());
    }

    [Fact]
    public async Task Queue_is_rebuilt_from_the_database_on_every_start()
    {
        await _fx.AddImagesAsync(3, "first");
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(3, _fx.Factory.EmbedCount);

        // 何も無ければ、エンコーダを作らずに待機(Waiting)に入る(GPU を使わない)
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(1, _fx.Factory.CreateCount);
        Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);

        // 新しい画像だけが処理される
        await _fx.AddImagesAsync(2, "second");
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(5, _fx.Factory.EmbedCount);
        Assert.Equal(2, _fx.Factory.CreateCount);
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM runs"));
        Assert.Equal(5, _fx.Summary().Done);
    }

    [Fact]
    public async Task Images_whose_only_paths_are_missing_are_not_embedded()
    {
        List<string> paths = await _fx.AddImagesAsync(3);
        File.Delete(paths[1]);
        await _fx.Scan.ScanAsync(); // missing_since が付く

        Assert.Equal(2, _fx.Summary().Pending);
        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(2, _fx.Factory.EmbedCount);
        string missingHash = ScanFixture.Sha256Hex(ScanFixture.FakePng("img-1"));
        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM stage_status WHERE hash = '{missingHash}'"));
        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM image_vectors WHERE hash = '{missingHash}'"));
    }

    [Fact]
    public async Task Same_content_at_two_paths_is_embedded_once()
    {
        _fx.Scan.Write("a.png", ScanFixture.FakePng("dup"));
        _fx.Scan.Write("copy/b.png", ScanFixture.FakePng("dup"));
        await _fx.Scan.RegisterRootAsync();
        await _fx.Scan.ScanAsync();
        Assert.Equal(1, _fx.Summary().Pending);

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(1, _fx.Factory.EmbedCount);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM image_vectors"));
    }

    // ---- 失敗の扱い

    [Fact]
    public async Task Per_image_encoder_failure_is_recorded_as_failed_and_the_job_continues()
    {
        await _fx.AddImagesAsync(4);
        _fx.Factory.EmbedBehavior = (index, tensor) =>
            index == 2 ? throw new ImageRunException("run が失敗(テスト)", new InvalidOperationException()) : DefaultVector(tensor);

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 3, 1, 0), _fx.Summary());
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM image_vectors")); // 失敗した画像のベクトルは作らない
        Assert.Contains("埋め込みに失敗した", _fx.Scan.Text("SELECT error FROM stage_status WHERE status = 'failed'")!);
        Assert.Equal(1, _fx.Count("SELECT attempts FROM stage_status WHERE status = 'failed'"));
        Assert.Equal(4, _fx.Count("SELECT count(*) FROM images WHERE width IS NOT NULL")); // 幅・高さは失敗した画像にも入る
    }

    [Fact]
    public async Task Invalid_encoder_output_and_non_finite_values_are_recorded_as_failed()
    {
        await _fx.AddImagesAsync(3);
        _fx.Factory.EmbedBehavior = (index, tensor) => index switch
        {
            1 => throw new InvalidDataException("ノルムが不正(テスト)"),
            2 => NonFinite(),
            _ => DefaultVector(tensor),
        };

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(new StageSummary(Stage.Embed, 0, 1, 2, 0), _fx.Summary());
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM image_vectors"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM stage_status WHERE error LIKE '%NaN%'"));

        static float[] NonFinite() { var v = new float[1152]; v[0] = float.NaN; return v; }
    }

    [Fact]
    public async Task Undecodable_image_is_recorded_as_failed_without_calling_the_encoder()
    {
        _fx.Scan.Write("good.png", ScanFixture.FakePng("good"));
        _fx.Scan.Write("bad.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)'B', (byte)'A', (byte)'D']); // 前処理が「デコードできない」とする
        await _fx.Scan.RegisterRootAsync();
        await _fx.Scan.ScanAsync();
        Assert.Equal(2, _fx.Summary().Pending);

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(1, _fx.Factory.EmbedCount);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 1, 1, 0), _fx.Summary());
        Assert.Contains("デコード", _fx.Scan.Text("SELECT error FROM stage_status WHERE status = 'failed'")!);
    }

    [Fact]
    public async Task Unreadable_file_is_skipped_without_a_record_and_retried_on_the_next_start()
    {
        List<string> paths = await _fx.AddImagesAsync(3);
        byte[] victim = File.ReadAllBytes(paths[1]);
        File.Delete(paths[1]); // 走査のあと・埋め込みの前に消えた(取り外したドライブなどの代わり。まだ missing_since は付いていない)

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(2, _fx.Factory.EmbedCount);
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status")); // 読めなかった画像は failed にして固定しない
        Assert.Equal(1, _fx.Summary().Pending);
        Assert.Equal(1, _fx.Indexer.State.SkippedImages); // 読めずに飛ばした枚数が State に出る(LastError とは別)
        Assert.Contains(_fx.Log.Messages, m => m == "INFO ジョブ: 埋め込みが終わった(処理 2 枚、失敗 0、飛ばした(画像) 1 枚、所要 00:00:00)");
        SkippedItem skipped = Assert.Single(await _fx.Indexer.ListSkippedAsync(Ct));   // 飛ばした画像の一覧(理由つき)
        Assert.Equal(JobKind.Embed, skipped.Job);
        Assert.Equal(paths[1], skipped.Path, ignoreCase: true);                         // 現存するパス(まだ missing の印は付いていない)
        Assert.Contains(paths[1], skipped.Reason, StringComparison.OrdinalIgnoreCase);  // 理由に、開けなかったパスと OS のメッセージが入る
        Assert.Null(_fx.Indexer.State.LastError);
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("飛ばす", StringComparison.Ordinal));

        File.WriteAllBytes(paths[1], victim); // 戻ってきた
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(3, _fx.Summary().Done);
        Assert.Equal(0, _fx.Indexer.State.SkippedImages); // 次の Start で 0 に戻る
        Assert.Empty(await _fx.Indexer.ListSkippedAsync(Ct));
    }

    [Fact]
    public async Task A_waiting_embed_job_is_not_woken_by_a_scan_because_of_a_skipped_image_only_but_is_woken_by_a_real_new_image()
    {
        // 本物の SQL(待ちの数え方・飛ばした集合を引く IN)で: 読めない画像(共有違反。走査は mtime とサイズが同じなので読み直さず、待ちに残る)があっても、
        // 走査のたびに待機から動き出さない。新しい画像が入ったときだけ、同じ指定で動き出す
        List<string> paths = await _fx.AddImagesAsync(3);
        await using (var locked = new FileStream(paths[1], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await _fx.Indexer.StartAsync(Embed, Ct);
            Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);
            Assert.Equal(2, _fx.Factory.EmbedCount);
            Assert.Equal(1, _fx.Indexer.State.SkippedImages);
            Assert.Equal(1, _fx.Summary().Pending);
            Assert.Equal(1, _fx.Factory.CreateCount);

            await _fx.Indexer.ScanAsync(null, Ct);
            await _fx.Indexer.ScanAsync(null, Ct);
            Assert.Equal(1, _fx.Factory.CreateCount);                       // 往復しない(エンコーダを作り直さない)
            Assert.DoesNotContain(_fx.Log.Messages, m => m.Contains("待機から続ける", StringComparison.Ordinal));

            _fx.Scan.Write("newcomer.png", ScanFixture.FakePng("newcomer"));
            await _fx.Indexer.ScanAsync(null, Ct);                          // 新着 → 同じ指定(埋め込み)で自動で動き出す
            Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Factory.CreateCount == 2 && _fx.Indexer.State.Run == IndexRunState.Waiting));
            Assert.Equal(3, _fx.Summary().Done);
            Assert.Equal(1, _fx.Summary().Pending);                         // 読めない 1 枚は待ちのまま
            Assert.Equal(1, _fx.Indexer.State.SkippedImages);
            Assert.Equal(1, _fx.Log.Messages.Count(m => m.Contains("待機から続ける", StringComparison.Ordinal)));
        }
    }

    // ---- 読んだ内容がDBのハッシュと同じか(走査のあとで書き換えられたファイルを、古いハッシュの下に入れない)

    [Fact]
    public async Task A_file_rewritten_after_the_scan_is_not_embedded_under_the_old_hash_and_the_next_scan_picks_it_up()
    {
        List<string> paths = await _fx.AddImagesAsync(3);
        string oldHash = _fx.Scan.Text("SELECT hash FROM image_paths WHERE path LIKE '%img1.png'")!;
        byte[] rewritten = ScanFixture.FakePng("img-1-rewritten");
        _fx.Scan.Write("img1.png", rewritten, DateTime.UtcNow.AddMinutes(5)); // 走査のあと・埋め込みの前に、別の内容に書き換わった

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(2, _fx.Factory.EmbedCount);                       // 書き換わった 1 枚は Embed していない
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status")); // 記録もしない(failed に固定しない)
        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM image_vectors WHERE hash = '{oldHash}'"));
        Assert.Equal(1, _fx.Indexer.State.SkippedImages);
        Assert.Null(_fx.Indexer.State.LastError);
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("内容が走査のときと違う", StringComparison.Ordinal));

        // 次の走査が、書き換わった内容を新しいハッシュとして拾い、その内容の結果が新しいハッシュの下に入る
        await _fx.Scan.ScanAsync();
        string newHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rewritten));
        Assert.Equal(newHash, _fx.Scan.Text("SELECT hash FROM image_paths WHERE path LIKE '%img1.png'"));
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(1, _fx.Count($"SELECT count(*) FROM image_vectors WHERE hash = '{newHash}'"));
        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM image_vectors WHERE hash = '{oldHash}'"));
        Assert.Equal(0, _fx.Indexer.State.SkippedImages);
    }

    [Fact]
    public async Task When_one_of_several_paths_was_rewritten_the_next_intact_path_is_used()
    {
        byte[] content = ScanFixture.FakePng("shared-content");
        _fx.Scan.Write("a/dup.png", content);   // パスの昇順で先に試される
        _fx.Scan.Write("b/dup.png", content);
        await _fx.Scan.RegisterRootAsync();
        await _fx.Scan.ScanAsync();
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM images"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM image_paths"));
        _fx.Scan.Write("a/dup.png", ScanFixture.FakePng("changed-later"), DateTime.UtcNow.AddMinutes(5));

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(1, _fx.Factory.EmbedCount);                // b/dup.png の中身(ハッシュと一致)で埋め込んだ
        (string embeddedHash, float[] vector) = Assert.Single(_fx.Embedded);
        Assert.Equal(_fx.Scan.Text("SELECT hash FROM images"), embeddedHash);
        Assert.Equal(FakePreprocessor.Checksum(content), Array.IndexOf(vector, 1f)); // 偽のエンコーダは中身から決まる位置が 1 の one-hot を返す=書き換え後の内容ではない
        Assert.Equal(1, _fx.Summary().Done);
        Assert.Equal(0, _fx.Indexer.State.SkippedImages);
    }

    [Fact]
    public async Task When_every_path_was_rewritten_the_image_is_skipped_without_a_record()
    {
        byte[] content = ScanFixture.FakePng("shared-content");
        _fx.Scan.Write("a/dup.png", content);
        _fx.Scan.Write("b/dup.png", content);
        await _fx.Scan.RegisterRootAsync();
        await _fx.Scan.ScanAsync();
        _fx.Scan.Write("a/dup.png", ScanFixture.FakePng("changed-a"), DateTime.UtcNow.AddMinutes(5));
        _fx.Scan.Write("b/dup.png", ScanFixture.FakePng("changed-b"), DateTime.UtcNow.AddMinutes(5));

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(0, _fx.Factory.EmbedCount);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(1, _fx.Indexer.State.SkippedImages);
        Assert.Equal(1, _fx.Summary().Pending);
    }

    [Fact]
    public async Task Fatal_encoder_error_fails_the_job_but_keeps_what_was_written_and_releases_resources()
    {
        await _fx.AddImagesAsync(5);
        _fx.Factory.OnEmbed = index => { if (index == 3) throw new InvalidOperationException("致命的(テスト)"); };

        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        await start; // Task は失敗させず、状態で伝える

        IndexState state = _fx.Indexer.State;
        Assert.Equal(IndexRunState.Failed, state.Run);
        Assert.Contains("致命的", state.LastError);
        Assert.Equal(2, _fx.Summary().Done); // 3 枚目は 1 トランザクションの前に落ちたので、行が残らない
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM image_vectors"));
        Assert.Equal(1, _fx.Factory.DisposeCount);
        Assert.NotNull(_fx.Scan.Text("SELECT ended_at FROM runs"));
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));

        // 直したら、残りだけが処理される
        _fx.Factory.OnEmbed = null;
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);
        Assert.Equal(5, _fx.Summary().Done);
    }

    [Fact]
    public async Task Wrong_output_dimension_is_fatal()
    {
        await _fx.AddImagesAsync(2);
        _fx.Factory.EmbedBehavior = (_, _) => new float[768];

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(IndexRunState.Failed, _fx.Indexer.State.Run);
        Assert.Contains("次元", _fx.Indexer.State.LastError);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM image_vectors"));
    }

    [Fact]
    public async Task Encoder_creation_failure_is_reported_in_engine_status_and_nothing_is_written()
    {
        await _fx.AddImagesAsync(3);
        _fx.Factory.CreateFailure = new NativeRuntimeException("GPU の自己検査に失敗した: cudnn64_9.dll がアプリのディレクトリの外から読まれている(テスト)");

        await _fx.Indexer.StartAsync(Embed, Ct);

        IndexState state = _fx.Indexer.State;
        Assert.Equal(IndexRunState.Failed, state.Run);
        Assert.Equal(EnginePhase.Failed, state.Engine.Phase);
        Assert.Contains("cudnn64_9.dll", state.Engine.Message);
        Assert.Contains("cudnn64_9.dll", state.LastError);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM runs")); // runs の行は自己検査のあと。失敗した実行は行を残さない
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM models"));
        Assert.Equal(3, _fx.Summary().Pending);
    }

    // ---- 自己検査の結果と実行の記録

    [Fact]
    public async Task Engine_status_goes_from_not_created_through_checking_to_ready_with_the_self_check_and_back()
    {
        await _fx.AddImagesAsync(3);
        var states = new List<IndexState>();
        _fx.Indexer.StateChanged += s => { lock (states) states.Add(s); };
        var gate = new ManualResetEventSlim();
        _fx.Factory.OnEmbed = index => { if (index == 2) gate.Wait(TimeSpan.FromSeconds(10)); };

        Assert.Equal(EnginePhase.NotCreated, _fx.Indexer.State.Engine.Phase);
        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Factory.EmbedCount >= 2));

        EngineStatus engine = _fx.Indexer.State.Engine;
        Assert.Equal(EnginePhase.Ready, engine.Phase);
        Assert.NotNull(engine.SelfCheck);
        Assert.True(engine.SelfCheck!.Passed);
        Assert.Equal("1.31.1-fake", engine.SelfCheck.OrtVersion);
        Assert.Equal(IndexRunState.Running, _fx.Indexer.State.Run);
        Assert.Equal(JobKind.Embed, _fx.Indexer.State.CurrentJob);

        // 間引きの末尾の通知(≤100ms 後)で、Ready の状態が通知される
        Assert.True(await IndexingFixture.WaitUntilAsync(() => { lock (states) return states.Any(s => s.Engine.Phase == EnginePhase.Ready); }));
        gate.Set();
        await start;
        Assert.Equal(EnginePhase.NotCreated, _fx.Indexer.State.Engine.Phase); // 空けたので
        lock (states)
        {
            Assert.Contains(states, s => s.Engine.Phase == EnginePhase.Ready);
            Assert.Equal(IndexRunState.Waiting, states[^1].Run); // 最後の状態が間引きで失われない
        }
    }

    [Fact]
    public async Task Run_row_is_created_after_the_encoder_and_before_the_first_stage_status_write()
    {
        await _fx.AddImagesAsync(2);
        long runsAtCreate = -1;
        _fx.Factory.BeforeCreate = _ =>
        {
            runsAtCreate = _fx.Count("SELECT count(*) FROM runs"); // エンコーダを作る時点では、まだ行が無い
            return Task.CompletedTask;
        };
        long runsAtFirstEmbed = -1, statusAtFirstEmbed = -1;
        _fx.Factory.OnEmbed = index =>
        {
            if (index != 1) return;
            runsAtFirstEmbed = _fx.Count("SELECT count(*) FROM runs"); // 最初の Embed の時点では、行がある(書き込みの前)
            statusAtFirstEmbed = _fx.Count("SELECT count(*) FROM stage_status");
        };

        await _fx.Indexer.StartAsync(Embed, Ct);

        Assert.Equal(0, runsAtCreate);
        Assert.Equal(1, runsAtFirstEmbed);
        Assert.Equal(0, statusAtFirstEmbed);
    }

    // ---- 一時停止・再開・キャンセル・切り替え

    [Fact]
    public async Task Pause_waits_for_the_current_image_releases_resources_and_resume_continues_with_a_new_run()
    {
        await _fx.AddImagesAsync(12);
        var reached = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        _fx.Factory.OnEmbed = index =>
        {
            if (index != 3) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // 3 枚目の処理の途中で止めておく
        };

        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(10), Ct));

        Task pause = _fx.Indexer.PauseAsync();
        Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Indexer.State.Run == IndexRunState.Pausing));
        Assert.False(pause.IsCompleted); // 処理中の 1 枚が終わるまで待つ
        release.Set();
        await pause;
        await start; // Pause で Start の Task も終わる

        IndexState paused = _fx.Indexer.State;
        Assert.Equal(IndexRunState.Paused, paused.Run);
        Assert.Equal(1, _fx.Factory.DisposeCount); // 資源は空いた(GPU のセッションを Dispose)
        Assert.Equal(EnginePhase.NotCreated, paused.Engine.Phase);
        int doneAtPause = _fx.Summary().Done;
        Assert.InRange(doneAtPause, 3, 3 + 1); // 3 枚目は確定してから止まる(先読みした分は捨てる)。3 枚目を捨てていない
        Assert.Equal(12 - doneAtPause, _fx.Summary().Pending);
        Assert.NotNull(_fx.Scan.Text("SELECT ended_at FROM runs"));

        _fx.Factory.OnEmbed = null;
        await _fx.Indexer.ResumeAsync(Ct);

        Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);
        Assert.Equal(12, _fx.Summary().Done);
        Assert.Equal(2, _fx.Factory.CreateCount); // 再開でエンコーダを作り直す
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM runs")); // 新しい実行の行
        Assert.Equal(2, _fx.Count("SELECT count(DISTINCT run_id) FROM stage_status"));
        Assert.Equal(12, _fx.Factory.EmbedCount); // 同じ画像を 2 回 Embed していない
    }

    [Fact]
    public async Task Pause_and_resume_are_no_ops_when_there_is_nothing_to_pause_or_resume()
    {
        await _fx.Indexer.PauseAsync();
        await _fx.Indexer.ResumeAsync(Ct);
        Assert.Equal(IndexRunState.Idle, _fx.Indexer.State.Run);
        Assert.Equal(0, _fx.Factory.CreateCount);
    }

    [Fact]
    public async Task Start_while_running_is_rejected()
    {
        await _fx.AddImagesAsync(4);
        var gate = new ManualResetEventSlim();
        _fx.Factory.OnEmbed = index => { if (index == 1) gate.Wait(TimeSpan.FromSeconds(10)); };

        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Factory.EmbedCount >= 1));
        Assert.Throws<InvalidOperationException>(() => { _ = _fx.Indexer.StartAsync(Embed, Ct); });

        gate.Set();
        await start;
    }

    [Fact]
    public async Task Start_rejects_empty_job_lists_and_job_kinds_without_a_runner()
    {
        Assert.Throws<ArgumentException>(() => { _ = _fx.Indexer.StartAsync([], Ct); });
        Assert.Throws<ArgumentException>(() => { _ = _fx.Indexer.StartAsync([(JobKind)99], Ct); });
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Indexer.RetryFailedAsync((JobKind)99, Ct));
    }

    [Fact]
    public async Task Caller_cancellation_stops_at_an_image_boundary_ends_idle_and_cancels_the_task()
    {
        await _fx.AddImagesAsync(10);
        using var cts = new CancellationTokenSource();
        _fx.Factory.OnEmbed = index => { if (index == 3) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Indexer.StartAsync(Embed, cts.Token));

        IndexState state = _fx.Indexer.State;
        Assert.Equal(IndexRunState.Idle, state.Run); // Paused ではない
        Assert.Equal(1, _fx.Factory.DisposeCount);
        int done = _fx.Summary().Done;
        Assert.InRange(done, 3, 4); // 3 枚目は確定してから止まる
        Assert.Equal(done, _fx.Count("SELECT count(*) FROM image_vectors"));
        Assert.NotNull(_fx.Scan.Text("SELECT ended_at FROM runs"));
    }

    [Fact]
    public async Task Yield_request_returns_to_the_indexer_after_the_current_image_and_keeps_the_encoder()
    {
        await _fx.AddImagesAsync(6);
        _fx.Factory.OnEmbed = index => { if (index == 2) _fx.Indexer.RequestYield(); };

        await _fx.Indexer.StartAsync(Embed, Ct);

        // 合図で戻ったあと、Indexer はランナーを選び直して続きを処理した。資源は握ったまま=エンコーダは 1 回しか作らない
        Assert.Equal(IndexRunState.Waiting, _fx.Indexer.State.Run);
        Assert.Equal(6, _fx.Summary().Done);
        Assert.Equal(1, _fx.Factory.CreateCount);
        Assert.Equal(1, _fx.Factory.DisposeCount);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs"));
        Assert.Equal(6, _fx.Factory.EmbedCount); // 合図で戻った 2 枚目を捨てていない(重複して Embed していない)
    }

    // ---- 件数・再処理

    [Fact]
    public async Task Outdated_results_are_counted_not_processed_and_can_be_requeued_while_old_vectors_stay()
    {
        await _fx.AddImagesAsync(3);
        await _fx.Indexer.StartAsync(Embed, Ct);
        string old = "siglip2-so400m-p16-384:000000000000";
        await _fx.Scan.Db.WriteAsync((c, tx) =>
        {
            // 別のモデルで作った結果が残っている状況: モデルを替えて、stage_status を古い model_id に向ける
            TestDb.Exec(c, tx, $"INSERT INTO models(id, kind, name, spec_json, created_at) SELECT '{old}', kind, name, spec_json, created_at FROM models");
            TestDb.Exec(c, tx, $"UPDATE stage_status SET model_id = '{old}'");
            TestDb.Exec(c, tx, $"INSERT INTO image_vectors(hash, model_id, embedding) SELECT hash, '{old}', embedding FROM image_vectors");
        }, Ct);

        Assert.Equal(new StageSummary(Stage.Embed, 0, 0, 0, 3), _fx.Summary()); // 3 件が要再作成

        await _fx.Indexer.StartAsync(Embed, Ct); // 自動では処理しない
        Assert.Equal(3, _fx.Factory.EmbedCount);

        await _fx.Indexer.RequeueOutdatedAsync(JobKind.Embed, Ct);
        Assert.Equal(new StageSummary(Stage.Embed, 3, 0, 0, 0), _fx.Summary());
        Assert.Equal(6, _fx.Count("SELECT count(*) FROM image_vectors")); // 古いモデルのベクトルは残る

        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 3, 0, 0), _fx.Summary());
        Assert.Equal(6, _fx.Count("SELECT count(*) FROM image_vectors")); // 現行のモデルの分は上書きされるだけ(増えない)
        Assert.Equal(3, _fx.Count($"SELECT count(*) FROM image_vectors WHERE model_id = '{ModelId}'"));
    }

    [Fact]
    public async Task RetryFailed_puts_failed_images_back_in_the_queue()
    {
        await _fx.AddImagesAsync(3);
        _fx.Factory.EmbedBehavior = (index, tensor) =>
            index == 1 ? throw new ImageRunException("失敗(テスト)", new InvalidOperationException()) : DefaultVector(tensor);
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 2, 1, 0), _fx.Summary());

        await _fx.Indexer.StartAsync(Embed, Ct); // 失敗は自動では再試行しない
        Assert.Equal(3, _fx.Factory.EmbedCount);

        await _fx.Indexer.RetryFailedAsync(JobKind.Embed, Ct);
        Assert.Equal(new StageSummary(Stage.Embed, 1, 2, 0, 0), _fx.Summary());

        _fx.Factory.EmbedBehavior = null;
        await _fx.Indexer.StartAsync(Embed, Ct);
        Assert.Equal(new StageSummary(Stage.Embed, 0, 3, 0, 0), _fx.Summary());
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'failed'"));
    }

    // ---- 状態の通知

    [Fact]
    public async Task State_events_report_running_then_idle_and_progress_numbers()
    {
        await _fx.AddImagesAsync(8);
        var states = new List<IndexState>();
        using var pathSeen = new ManualResetEventSlim();
        _fx.Indexer.StateChanged += s =>
        {
            lock (states) states.Add(s);
            if (s.CurrentPath is not null) pathSeen.Set();
        };
        _fx.Factory.OnEmbed = index =>
        {
            // 1 枚目は、「処理中の画像のパス」が入った進捗の通知が届くまで止めておく(上限 10 秒)。
            // 進捗の通知は 100ms の間引きのタイマーで届くので、8 枚 × 15ms ≈ 0.12 秒で終わるのを前提にすると、CPU に負荷がかかってタイマーが遅れたとき、
            // タイマーが鳴る前にジョブが終わって(終了の通知がタイマーを取り消す)、途中の通知が 1 件も無いまま終わる(繰り返し実行すると落ちることがあった)。待つ条件を時間でなく通知にする
            if (index == 1) pathSeen.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(15); // 速度の計算に使える間隔を作る
        };

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await _fx.Indexer.StartAsync(Embed, Ct);
        elapsed.Stop();

        IndexState[] seen;
        lock (states) seen = states.ToArray();
        Assert.Contains(seen, s => s.Run == IndexRunState.Running);
        Assert.Contains(seen, s => s.CurrentPath is not null);
        Assert.Equal(IndexRunState.Waiting, seen[^1].Run);
        Assert.Equal(8, seen[^1].Stages.Single().Done);
        // 進捗の通知は 100ms に 1 回までに間引かれる。上限は、実際にかかった時間から出す(CPU に負荷がかかると、8 枚 × 15ms が 2 秒以上になることがあり、固定の件数では落ちた)。
        // 即時の通知(実行の切り替えなど)の分を 8 件見込む。0.2 秒で終わるふつうの速さのときは、前の固定の上限(10 件)と同じになる
        double limit = 8 + elapsed.Elapsed.TotalMilliseconds / 100;
        Assert.True(seen.Length <= limit, $"通知が多すぎる: {seen.Length}(上限 {limit:F1}、かかった時間 {elapsed.ElapsedMilliseconds}ms)");
    }

    [Fact]
    public async Task Rate_and_remaining_time_are_reported_while_running()
    {
        await _fx.AddImagesAsync(20);
        var gate = new ManualResetEventSlim();
        _fx.Factory.OnEmbed = index =>
        {
            Thread.Sleep(10);
            if (index == 8) gate.Wait(TimeSpan.FromSeconds(10));
        };

        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Factory.EmbedCount >= 8));

        IndexState state = _fx.Indexer.State;
        Assert.NotNull(state.ImagesPerSecond);
        Assert.True(state.ImagesPerSecond > 0);
        Assert.NotNull(state.Remaining);
        Assert.True(state.Remaining > TimeSpan.Zero);

        gate.Set();
        await start;
        Assert.Null(_fx.Indexer.State.Remaining); // Running でなくなれば残り時間は出さない
    }

    [Fact]
    public async Task Scan_progress_is_reported_in_the_state_and_cleared_afterwards_while_run_stays_idle()
    {
        for (int i = 0; i < 6; i++) _fx.Scan.Write($"s{i}.png", ScanFixture.FakePng("scan-" + i));
        await _fx.Scan.RegisterRootAsync();
        var states = new List<IndexState>();
        _fx.Indexer.StateChanged += s => { lock (states) states.Add(s); };

        ScanSummary summary = await _fx.Indexer.ScanAsync(ct: Ct);

        Assert.Equal(6, summary.Added);
        Assert.Null(_fx.Indexer.State.Scan);
        IndexState[] seen;
        lock (states) seen = states.ToArray();
        Assert.Contains(seen, s => s.Scan is not null);
        Assert.All(seen.Where(s => s.Scan is not null), s => Assert.Equal(IndexRunState.Idle, s.Run)); // 走査中でも Run は Idle(別の軸)
        Assert.Equal(6, _fx.Summary().Pending);
    }

    [Fact]
    public async Task Scan_and_embedding_can_run_at_the_same_time()
    {
        await _fx.AddImagesAsync(4, "early");
        var gate = new ManualResetEventSlim();
        _fx.Factory.OnEmbed = index => { if (index == 2) gate.Wait(TimeSpan.FromSeconds(10)); };
        Task start = _fx.Indexer.StartAsync(Embed, Ct);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => _fx.Factory.EmbedCount >= 2));

        for (int i = 0; i < 3; i++) _fx.Scan.Write($"late{i}.png", ScanFixture.FakePng("late-" + i));
        ScanSummary scan = await _fx.Indexer.ScanAsync(ct: Ct); // 埋め込みの最中に走査できる

        Assert.Equal(3, scan.Added);
        Assert.Equal(IndexRunState.Running, _fx.Indexer.State.Run);
        gate.Set();
        await start;
        Assert.Equal(7, _fx.Summary().Done + _fx.Summary().Pending); // 走査で足された分は、このあとの Start で処理される
    }

    // ---- 補助

    private static float[] DefaultVector(float[] tensor)
    {
        var v = new float[1152];
        v[(int)tensor[0]] = 1f;
        return v;
    }
}
