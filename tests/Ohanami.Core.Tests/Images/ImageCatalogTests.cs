using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Tags;
using Ohanami.Core.Tests.Captions;
using Ohanami.Core.Tests.Data;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests.Images;

/// <summary>
/// 画像 1 枚の詳細と失敗の一覧(ADR 0006): 段階ごとの状態(未作成・済・失敗・要再作成)、パス(見つからない印つき)、現行の caption、手動タグ、
/// 画像が無ければ null、失敗の一覧の並び・上限・件数・パス。実物の SQLite を使う。
/// </summary>
public sealed class ImageCatalogTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private readonly ImageCatalog _catalog;
    private readonly ManualTags _tags;
    private static CancellationToken Ct => CaptionFixture.Ct;
    private static string H(int i) => CaptionFixture.Hash(i);

    public ImageCatalogTests()
    {
        _catalog = new ImageCatalog(_fx.Db, new Dictionary<Stage, string>
        {
            [Stage.Embed] = "embed-current",
            [Stage.Ocr] = _fx.ModelA,
            [Stage.Caption] = _fx.ModelA,
        });
        _tags = new ManualTags(_fx.Db, _fx.Index);
    }

    public void Dispose() => _fx.Dispose();

    /// <summary>models に埋め込みモデルの行を 2 つ(現行 "embed-current" と古い "embed-old")作る(stage_status の外部キー用)。</summary>
    private Task EnsureEmbedModelsAsync() => _fx.Db.WriteAsync((c, tx) =>
        TestDb.Exec(c, tx, "INSERT OR IGNORE INTO models(id, kind, name, spec_json, created_at) VALUES ('embed-current', 'image-embedding', 'n', '{}', 't'), ('embed-old', 'image-embedding', 'n', '{}', 't')"), Ct);

    private Task Stage_(int image, string stage, string status, string model, string? error = null, int attempts = 1, string at = "2026-11-06T00:00:00.000Z") =>
        _fx.Db.WriteAsync((c, tx) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ($h, $s, $st, $m, 1, NULL, $e, $a, $u)";
            cmd.Parameters.AddWithValue("$h", H(image));
            cmd.Parameters.AddWithValue("$s", stage);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", attempts);
            cmd.Parameters.AddWithValue("$u", at);
            cmd.ExecuteNonQuery();
        }, Ct);

    private async Task<long> SetUpAsync(int images)
    {
        await _fx.AddImagesAsync(images);
        await EnsureEmbedModelsAsync();
        return await AddFolderAsync(_fx.Db);
    }

    private Task Path_(long folder, int image, string path, string? missingSince = null, long mtimeTicks = 638000000000000000, long size = 123) =>
        _fx.Db.WriteAsync((c, tx) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ($p, $h, $f, $m, $s, $x)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$h", H(image));
            cmd.Parameters.AddWithValue("$f", folder);
            cmd.Parameters.AddWithValue("$m", mtimeTicks);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$x", (object?)missingSince ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }, Ct);

    private async Task<ImageDetail> Detail(int image)
    {
        ImageDetail? d = await _catalog.GetDetailAsync(H(image), Ct);
        Assert.NotNull(d);
        return d;
    }

    // ---------------------------------------------------------------- 引数・画像が無い

    [Fact]
    public async Task An_unknown_image_gives_null_and_a_malformed_hash_is_an_argument_error()
    {
        await _fx.AddImagesAsync(1);
        Assert.Null(await _catalog.GetDetailAsync(H(99), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.GetDetailAsync("abc", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.GetDetailAsync(H(1).ToUpperInvariant().Replace('0', 'A'), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.GetDetailAsync(new string('g', 64), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _catalog.GetDetailAsync(null!, Ct));
    }

    [Fact]
    public void The_catalog_needs_a_current_model_id_for_every_stage()
    {
        Assert.Throws<ArgumentException>(() => new ImageCatalog(_fx.Db, new Dictionary<Stage, string> { [Stage.Embed] = "a", [Stage.Ocr] = "b" }));
    }

    // ---------------------------------------------------------------- 画像・パス

    [Fact]
    public async Task The_detail_has_the_image_facts_and_paths_present_first_then_missing_each_in_path_order()
    {
        long folder = await SetUpAsync(2);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"UPDATE images SET size = 4567, format = 'jpeg', width = 1920, height = 1080 WHERE hash = '{H(1)}'"), Ct);
        await Path_(folder, 1, @"C:\shots\b.png", mtimeTicks: 638000000000000000, size: 4567);
        await Path_(folder, 1, @"C:\shots\A.png", mtimeTicks: 638100000000000000, size: 4567);
        await Path_(folder, 1, @"C:\shots\0-gone.png", missingSince: "2026-11-05T01:00:00.000Z", size: 4567);
        await Path_(folder, 1, @"C:\shots\z-gone.png", missingSince: "2026-11-05T02:00:00.000Z", size: 4567);

        ImageDetail d = await Detail(1);

        Assert.Equal(H(1), d.Hash);
        Assert.Equal((4567L, "jpeg", 1920, 1080), (d.Size, d.Format, d.Width, d.Height));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), d.FirstSeenAt);
        Assert.Equal([@"C:\shots\A.png", @"C:\shots\b.png", @"C:\shots\0-gone.png", @"C:\shots\z-gone.png"], d.Paths.Select(p => p.Path).ToArray());   // 現存(大文字・小文字を区別しない順)→ 見つからない
        Assert.Equal([false, false, true, true], d.Paths.Select(p => p.Missing).ToArray());
        Assert.Equal([null, null, new DateTimeOffset(2026, 11, 5, 1, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero)], d.Paths.Select(p => p.MissingSince).ToArray());
        Assert.Equal(new DateTimeOffset(638100000000000000, TimeSpan.Zero), d.Paths[0].ModifiedAt);       // 走査したときの更新日時(UTC)
        Assert.Equal(new DateTimeOffset(638000000000000000, TimeSpan.Zero), d.Paths[1].ModifiedAt);
        Assert.All(d.Paths, p => Assert.Equal(4567, p.Size));
    }

    [Fact]
    public async Task An_image_before_embedding_has_no_dimensions_and_an_image_without_paths_has_an_empty_path_list()
    {
        await SetUpAsync(1);
        ImageDetail d = await Detail(1);
        Assert.Null(d.Width);
        Assert.Null(d.Height);
        Assert.Empty(d.Paths);
        Assert.Null(d.Caption);
        Assert.Empty(d.ManualTags);
    }

    // ---------------------------------------------------------------- 段階ごとの状態

    [Fact]
    public async Task The_detail_always_has_the_three_stages_in_order_and_a_new_image_is_pending_in_all_of_them()
    {
        await SetUpAsync(1);
        ImageDetail d = await Detail(1);
        Assert.Equal([Stage.Embed, Stage.Ocr, Stage.Caption], d.Stages.Select(s => s.Stage).ToArray());
        Assert.All(d.Stages, s =>
        {
            Assert.Equal(StageState.Pending, s.State);
            Assert.Null(s.ModelId);
            Assert.Null(s.Error);
            Assert.Equal(0, s.Attempts);
            Assert.Null(s.UpdatedAt);
        });
    }

    [Fact]
    public async Task Done_with_the_current_model_is_done_and_done_with_another_model_is_outdated()
    {
        await SetUpAsync(2);
        await Stage_(1, "embed", "done", "embed-current", attempts: 1, at: "2026-11-06T01:02:03.000Z");
        await Stage_(2, "embed", "done", "embed-old", attempts: 2);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", []);          // ocr と caption が現行のモデルで done
        await _fx.SaveAsync(2, _fx.ModelB, "o", "d", []);          // 別の VLM(現行は A)で done

        ImageDetail one = await Detail(1);
        Assert.Equal([StageState.Done, StageState.Done, StageState.Done], one.Stages.Select(s => s.State).ToArray());
        Assert.Equal("embed-current", one.Stages[0].ModelId);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 1, 2, 3, TimeSpan.Zero), one.Stages[0].UpdatedAt);

        ImageDetail two = await Detail(2);
        Assert.Equal([StageState.Outdated, StageState.Outdated, StageState.Outdated], two.Stages.Select(s => s.State).ToArray());
        Assert.Equal(["embed-old", _fx.ModelB, _fx.ModelB], two.Stages.Select(s => s.ModelId).ToArray());
        Assert.Equal(2, two.Stages[0].Attempts);
    }

    [Fact]
    public async Task A_failed_stage_shows_its_error_and_attempts_whatever_its_model_and_a_failed_vlm_run_fails_both_stages()
    {
        await SetUpAsync(2);
        await Stage_(1, "embed", "failed", "embed-old", error: "デコードできない画像", attempts: 3);
        await _fx.Store.SaveFailureAsync(1, H(2), _fx.ModelB, "finish_reason=length", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(0), Ct);

        ImageDetail one = await Detail(1);
        Assert.Equal(StageState.Failed, one.Stages[0].State);
        Assert.Equal("デコードできない画像", one.Stages[0].Error);
        Assert.Equal(3, one.Stages[0].Attempts);
        Assert.Equal("embed-old", one.Stages[0].ModelId);                       // 失敗は、モデルが現行と違っても「失敗」(StageSummary.Failed と同じ定義)

        ImageDetail two = await Detail(2);
        Assert.Equal(StageState.Pending, two.Stages[0].State);
        Assert.Equal([StageState.Failed, StageState.Failed], two.Stages.Skip(1).Select(s => s.State).ToArray());
        Assert.All(two.Stages.Skip(1), s => Assert.Equal("finish_reason=length", s.Error));
        Assert.Null(two.Caption);                                                // 失敗では caption の行を作らない
    }

    [Fact]
    public async Task The_states_agree_with_the_summary_definitions_for_every_combination()
    {
        // StageSummary(EmbedRunner.Summarize)と同じ定義: 行なし = Pending、done かつ現行 = Done、done かつ別 = Outdated、failed = Failed
        await SetUpAsync(4);
        await Stage_(1, "embed", "done", "embed-current");
        await Stage_(2, "embed", "done", "embed-old");
        await Stage_(3, "embed", "failed", "embed-current", error: "x");
        var expected = new[] { StageState.Done, StageState.Outdated, StageState.Failed, StageState.Pending };
        for (int i = 1; i <= 4; i++) Assert.Equal(expected[i - 1], (await Detail(i)).Stages[0].State);
    }

    // ---------------------------------------------------------------- caption・手動タグ

    [Fact]
    public async Task The_current_caption_is_the_latest_one_whatever_the_model_and_carries_its_tags_and_time()
    {
        await SetUpAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "古い文字", "古い説明", ["古い"], minutes: 1);
        await _fx.SaveAsync(1, _fx.ModelB, "新しい文字", "新しい説明", ["銀髪", "ロングヘア"], minutes: 5);

        CaptionDetail c = Assert.IsType<CaptionDetail>((await Detail(1)).Caption);

        Assert.Equal(_fx.ModelB, c.ModelId);
        Assert.Equal(("新しい文字", "新しい説明"), (c.Ocr, c.Description));
        Assert.Equal(["銀髪", "ロングヘア"], c.Tags);                              // VLM のタグは並びのまま
        Assert.Equal(CaptionFixture.T(5).ToUniversalTime(), c.CreatedAt);
    }

    [Fact]
    public async Task Manual_tags_are_included_by_name_and_do_not_leak_into_the_caption()
    {
        await SetUpAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", ["vlm"]);
        await _tags.AddAsync(H(1), "猫", Ct);
        await _tags.AddAsync(H(1), "Apple", Ct);
        await _tags.AddAsync(H(2), "caption なし", Ct);

        ImageDetail one = await Detail(1);
        Assert.Equal(["Apple", "猫"], one.ManualTags);
        Assert.Equal(["vlm"], one.Caption!.Tags);

        ImageDetail two = await Detail(2);                                       // caption が無い画像(手動タグだけ)も詳細が出る
        Assert.Null(two.Caption);
        Assert.Equal(["caption なし"], two.ManualTags);
    }

    // ---------------------------------------------------------------- 同じ時点で読む

    [Fact]
    public async Task The_detail_is_read_inside_one_snapshot()
    {
        await SetUpAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", []);
        await _tags.AddAsync(H(1), "猫", Ct);

        // ReadSnapshot の中で、1 つ目の読み取りのあとに書き込みが入っても、2 つ目の読み取りは同じ時点を見る
        (long captions, long tagsAfterWrite) = _fx.Db.ReadSnapshot(conn =>
        {
            long a = TestDb.Long(conn, "SELECT count(*) FROM captions");
            _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM captions"), Ct).GetAwaiter().GetResult();
            _tags.RemoveAsync(H(1), "猫", Ct).GetAwaiter().GetResult();
            return (a, TestDb.Long(conn, "SELECT count(*) FROM image_tags") * 10 + TestDb.Long(conn, "SELECT count(*) FROM captions"));
        });

        Assert.Equal(1, captions);
        Assert.Equal(11, tagsAfterWrite);                                         // image_tags 1 行、captions 1 行(読み始めた時点のまま)
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM captions"));              // 終わったあとは新しい状態
    }

    // ---------------------------------------------------------------- 失敗の一覧

    [Fact]
    public async Task The_failure_list_is_newest_first_with_a_limit_and_the_total_before_the_limit()
    {
        long folder = await SetUpAsync(5);
        for (int i = 1; i <= 5; i++) await Path_(folder, i, $@"C:\shots\{i}.png");
        await Stage_(1, "embed", "failed", "embed-current", error: "e1", attempts: 1, at: "2026-11-06T00:00:01.000Z");
        await Stage_(2, "embed", "failed", "embed-current", error: "e2", attempts: 2, at: "2026-11-06T00:00:05.000Z");
        await Stage_(3, "embed", "failed", "embed-old", error: "e3", attempts: 1, at: "2026-11-06T00:00:03.000Z");
        await Stage_(4, "embed", "done", "embed-current");                          // 成功は入らない
        await Stage_(5, "embed", "failed", "embed-current", error: "e5", attempts: 4, at: "2026-11-06T00:00:05.000Z");   // 2 と同じ時刻 → hash 順

        FailureList all = await _catalog.ListFailuresAsync(Stage.Embed, 10, Ct);
        Assert.Equal(Stage.Embed, all.Stage);
        Assert.Equal(4, all.Total);
        Assert.Equal([H(2), H(5), H(3), H(1)], all.Items.Select(i => i.Hash).ToArray());
        FailureItem first = all.Items[0];
        Assert.Equal((@"C:\shots\2.png", "e2", 2, "embed-current"), (first.Path, first.Error, first.Attempts, first.ModelId));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 5, TimeSpan.Zero), first.UpdatedAt);

        FailureList limited = await _catalog.ListFailuresAsync(Stage.Embed, 2, Ct);
        Assert.Equal([H(2), H(5)], limited.Items.Select(i => i.Hash).ToArray());
        Assert.Equal(4, limited.Total);                                              // 上限で切る前の数
    }

    [Fact]
    public async Task The_failure_path_is_the_first_present_path_and_null_when_every_path_is_missing_and_the_image_still_counts()
    {
        long folder = await SetUpAsync(3);
        await Path_(folder, 1, @"C:\shots\zz.png");
        await Path_(folder, 1, @"C:\shots\B.png");
        await Path_(folder, 1, @"C:\shots\0.png", missingSince: "2026-11-05T01:00:00.000Z");     // 見つからない印のパスは選ばない
        await Path_(folder, 2, @"C:\shots\gone.png", missingSince: "2026-11-05T01:00:00.000Z");
        // 画像 3 はパスの行が無い
        foreach (int i in new[] { 1, 2, 3 }) await Stage_(i, "embed", "failed", "embed-current", error: "x", at: $"2026-11-06T00:00:0{i}.000Z");

        FailureList list = await _catalog.ListFailuresAsync(Stage.Embed, 10, Ct);

        Assert.Equal(3, list.Total);                                                  // StageSummary.Failed と同じ定義(現存するパスの有無は問わない)
        Assert.Equal([null, null, @"C:\shots\B.png"], list.Items.Select(i => i.Path).ToArray());   // 新しい順: 3(パス無し)、2(印のみ)、1
    }

    [Fact]
    public async Task Failures_are_listed_per_stage_and_the_two_vlm_stages_fail_together()
    {
        await SetUpAsync(2);
        await Stage_(1, "embed", "failed", "embed-current", error: "embed-error");
        await _fx.Store.SaveFailureAsync(1, H(2), _fx.ModelA, "vlm-error", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(0), Ct);

        FailureList embed = await _catalog.ListFailuresAsync(Stage.Embed, 10, Ct);
        FailureList ocr = await _catalog.ListFailuresAsync(Stage.Ocr, 10, Ct);
        FailureList caption = await _catalog.ListFailuresAsync(Stage.Caption, 10, Ct);

        Assert.Equal([H(1)], embed.Items.Select(i => i.Hash).ToArray());
        Assert.Equal([H(2)], ocr.Items.Select(i => i.Hash).ToArray());
        Assert.Equal([H(2)], caption.Items.Select(i => i.Hash).ToArray());
        Assert.Equal("vlm-error", ocr.Items[0].Error);
    }

    [Fact]
    public async Task With_no_failures_the_list_is_empty_and_the_limit_must_be_positive()
    {
        await SetUpAsync(1);
        FailureList list = await _catalog.ListFailuresAsync(Stage.Embed, ct: Ct);
        Assert.Empty(list.Items);
        Assert.Equal(0, list.Total);
        Assert.Equal(200, ImageCatalog.DefaultFailureLimit);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _catalog.ListFailuresAsync(Stage.Embed, 0, Ct));
    }

    [Fact]
    public async Task A_cancelled_token_cancels_the_read_before_it_starts()
    {
        await SetUpAsync(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _catalog.GetDetailAsync(H(1), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _catalog.ListFailuresAsync(Stage.Embed, 5, cts.Token));
    }
}
