using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Captions;

/// <summary>
/// CaptionStore(ADR 0028): 1 枚の更新 = captions・stage_status(ocr と caption)・search_text・fts_docs を 1 トランザクション。
/// 失敗の記録、読み出し、引数の検査。実物の SQLite(FTS5)。
/// </summary>
public sealed class CaptionStoreTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private static CancellationToken Ct => CaptionFixture.Ct;
    private static string H(int i) => CaptionFixture.Hash(i);

    public void Dispose() => _fx.Dispose();

    // ---------------------------------------------------------------- 1 枚の更新

    [Fact]
    public async Task A_success_writes_captions_both_stages_the_search_text_and_the_fts_row_together()
    {
        await _fx.AddImagesAsync(1);
        CaptionSaveOutcome outcome = await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "ＨＰ回復の画面", ["銀髪", "ロングヘア"], minutes: 5, attempt: 2);
        Assert.Equal(CaptionSaveOutcome.Saved, outcome);

        // captions: 元の文字列のまま(正規化は索引の側)
        using (SqliteConnection read = _fx.Db.CreateReadConnection())
        {
            CaptionRow row = Assert.IsType<CaptionRow>(CaptionStore.Find(read, H(1), _fx.ModelA));
            Assert.Equal("ガチャ10連", row.Ocr);
            Assert.Equal("ＨＰ回復の画面", row.Description);
            Assert.Equal(["銀髪", "ロングヘア"], row.Tags);
            Assert.Equal("stop", row.FinishReason);
            Assert.Equal(2, row.Attempt);
            Assert.Equal("{\"max_tokens\":2048}", row.SettingsJson);
            Assert.Equal(1234, row.PromptTokens);
            Assert.Equal(1, row.RunId);
            Assert.Equal("2026-11-06T00:05:00.000Z", row.CreatedAt);
        }

        // stage_status: ocr と caption が同じ内容(done、同じ model_id・run_id、処理した回数 1)。settings_json は NULL(設定は captions が持つ)
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done'"));
        Assert.Equal(2, _fx.Count($"SELECT count(*) FROM stage_status WHERE hash = '{H(1)}' AND model_id = '{_fx.ModelA}' AND run_id = 1 AND attempts = 1 AND settings_json IS NULL AND error IS NULL"));
        Assert.Equal("caption,ocr", _fx.Text("SELECT group_concat(stage, ',') FROM (SELECT stage FROM stage_status ORDER BY stage)"));

        // 索引: search_text は正規化後の全文書、fts_docs は同じ rowid
        Assert.Equal("がちゃ10連|hp回復の画面|銀髪、ろんぐへあ", _fx.Text("SELECT norm_text FROM search_text"));
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs f JOIN search_text s ON s.id = f.rowid"));
        Assert.Equal([H(1)], _fx.Hits("ガチャ10連"));
        Assert.Equal([H(1)], _fx.Hits("hp"));
    }

    [Fact]
    public async Task Saving_the_same_image_and_model_again_replaces_the_caption_and_the_index_row_without_duplicates()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ふるい", "説明", ["猫"], minutes: 1);
        await _fx.SaveAsync(1, _fx.ModelA, "あたらしい", "説明", ["犬"], minutes: 2);

        Assert.Equal(1, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE attempts = 2"));      // 処理した回数
        Assert.Empty(_fx.Hits("ふるい"));
        Assert.Equal([H(1)], _fx.Hits("あたらしい"));
        Assert.Equal([H(1)], _fx.Hits("犬"));
        Assert.Empty(_fx.Hits("猫"));
    }

    [Fact]
    public async Task The_search_text_id_stays_the_same_when_an_image_is_updated()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "いち", "d", []);
        await _fx.SaveAsync(2, _fx.ModelA, "に", "d", []);
        long id1 = _fx.Count($"SELECT id FROM search_text WHERE hash = '{H(1)}'");
        await _fx.SaveAsync(1, _fx.ModelA, "いちの二", "d", [], minutes: 1);
        Assert.Equal(id1, _fx.Count($"SELECT id FROM search_text WHERE hash = '{H(1)}'"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM fts_docs"));
    }

    // ---------------------------------------------------------------- 原子性

    [Fact]
    public async Task An_exception_in_the_middle_of_the_transaction_leaves_nothing_behind()
    {
        await _fx.AddImagesAsync(1);
        int calls = 0;
        _fx.Index.BuildDocument = (o, d, t, m) =>
        {
            // トランザクションの中(captions と stage_status を書いたあと)で、想定外の例外(InvalidOperationException / InvalidDataException 以外=索引の文書の問題ではない)
            calls++;
            throw new IOException("途中で失敗(テスト)");
        };

        await Assert.ThrowsAsync<IOException>(() => _fx.SaveAsync(1, _fx.ModelA, "ocr", "desc", ["tag"]));

        Assert.Equal(1, calls);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM fts_docs"));

        // 巻き戻ったあとも、そのまま使える(同じ画像をもう一度書ける)
        _fx.Index.BuildDocument = FtsDocumentBuilder.Build;
        Assert.Equal(CaptionSaveOutcome.Saved, await _fx.SaveAsync(1, _fx.ModelA, "ocr", "desc", ["tag"]));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM captions"));
    }

    [Fact]
    public async Task An_unknown_model_or_image_fails_the_whole_update_and_writes_nothing()
    {
        await _fx.AddImagesAsync(1);
        await Assert.ThrowsAsync<SqliteException>(() => _fx.SaveAsync(1, "no-such-model", "o", "d", []));
        await Assert.ThrowsAsync<SqliteException>(() => _fx.SaveAsync(99, _fx.ModelA, "o", "d", []));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM fts_docs"));
    }

    // ---------------------------------------------------------------- 索引の行を作れないとき(captions は捨てない)

    [Fact]
    public async Task When_the_index_document_cannot_be_built_captions_and_done_are_still_written_but_no_index_row()
    {
        await _fx.AddImagesAsync(1);
        _fx.Index.BuildDocument = (_, _, _, _) => throw new InvalidOperationException("クラスタごとの NFKC が合わない(テスト)");

        CaptionSaveOutcome outcome = await _fx.SaveAsync(1, _fx.ModelA, "o", "d", ["t"], attempt: 2);

        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, outcome);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done' AND error IS NULL"));   // VLM の結果は捨てない(再試行しても直らない失敗)
        Assert.Equal(1, _fx.Store.SavedWithoutIndexCount);
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN ", StringComparison.Ordinal) && m.Contains(H(1), StringComparison.Ordinal) && m.Contains("クラスタ", StringComparison.Ordinal));
        Assert.Equal(CaptionFixture.Hash(1), _fx.Text("SELECT hash FROM captions"));
    }

    [Fact]
    public async Task When_the_index_document_cannot_be_built_the_older_index_row_of_that_image_is_removed()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "古い文字", "d", [], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "別の画像", "d", [], minutes: 1);
        _fx.Index.BuildDocument = (o, d, t, m) => o == "BAD" ? throw new InvalidOperationException("作れない(テスト)") : FtsDocumentBuilder.Build(o, d, t, m);

        CaptionSaveOutcome outcome = await _fx.SaveAsync(1, _fx.ModelB, "BAD", "d", [], minutes: 2);   // 最新の caption は作れない文書

        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, outcome);
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM captions WHERE hash = '" + H(1) + "'"));
        Assert.Empty(_fx.Hits("古い文字"));                    // 古い caption から作った行を残すと、最新の caption と食い違う
        Assert.Equal([H(2)], _fx.Hits("別の画像"));            // ほかの画像の索引はそのまま
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs"));
    }

    [Fact]
    public async Task A_broken_tags_json_in_the_latest_caption_is_saved_without_index_and_does_not_lose_the_new_caption()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "最初の文字", "d", [], minutes: 1);
        // 別の経路で入った壊れた行(モデル B、新しい時刻)。最新の caption はこれになる
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx,
            $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at) VALUES ('{H(1)}', '{_fx.ModelB}', 'o', 'd', '{{\"not\":\"array\"}}', 'stop', 1, '{{}}', NULL, 1, '2026-11-06T00:10:00.000Z')"), Ct);

        CaptionSaveOutcome outcome = await _fx.SaveAsync(1, _fx.ModelA, "二回目の文字", "d", [], minutes: 2);

        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, outcome);
        using (SqliteConnection read = _fx.Db.CreateReadConnection())
            Assert.Equal("二回目の文字", Assert.IsType<CaptionRow>(CaptionStore.Find(read, H(1), _fx.ModelA)).Ocr);   // 新しい caption は保存された
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));                                             // 索引の行は消えた(セーブポイントで巻き戻して、行を消す)
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done'"));
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN ", StringComparison.Ordinal) && m.Contains("tags_json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_index_itself_is_failed_captions_are_saved_without_index_and_the_rebuild_is_not_retried_for_every_image()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "一枚目の文字", "d", [], minutes: 1);
        await _fx.TamperSpecAsync();                           // 規約が違う → 次に開くと作り直し
        var broken = new FtsIndex(_fx.Db, _fx.Log) { BuildDocument = (_, _, _, _) => throw new IOException("作り直しの失敗(テスト)") };
        _fx.UseIndex(broken);

        CaptionSaveOutcome first = await _fx.SaveAsync(2, _fx.ModelA, "二枚目の文字", "d", [], minutes: 2);
        CaptionSaveOutcome second = await _fx.SaveAsync(3, _fx.ModelA, "三枚目の文字", "d", [], minutes: 3);

        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, first);
        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, second);
        Assert.Equal(FtsIndexState.Failed, broken.State);
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM captions"));                                                  // VLM のジョブは止まらず、captions は全部入る
        Assert.Equal(6, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done'"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));                                               // 古い索引はそのまま(触らない)
        Assert.Equal(1, _fx.Log.Messages.Count(m => m.StartsWith("ERROR ", StringComparison.Ordinal) && m.Contains("作り直せなかった", StringComparison.Ordinal)));   // 作り直しは 1 回だけ試した
        Assert.Equal(2, _fx.Store.SavedWithoutIndexCount);

        // 直れば、作り直しで captions から入る
        broken.BuildDocument = FtsDocumentBuilder.Build;
        await broken.EnsureAsync(Ct);
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal([H(3)], _fx.Hits("三枚目の文字"));
    }

    [Fact]
    public async Task A_rebuild_and_an_incremental_save_treat_an_unbuildable_document_the_same_way()
    {
        await _fx.AddImagesAsync(2);
        Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>, FtsDocument> bad = (o, d, t, m) => o == "BAD" ? throw new InvalidOperationException("作れない(テスト)") : FtsDocumentBuilder.Build(o, d, t, m);
        _fx.Index.BuildDocument = bad;
        await _fx.SaveAsync(1, _fx.ModelA, "ふつう", "d", [], minutes: 1);
        Assert.Equal(CaptionSaveOutcome.SavedWithoutIndex, await _fx.SaveAsync(2, _fx.ModelA, "BAD", "d", [], minutes: 1));
        var incremental = _fx.IndexDump();

        await _fx.TamperSpecAsync();
        var rebuilt = new FtsIndex(_fx.Db, _fx.Log) { BuildDocument = bad };
        await rebuilt.EnsureAsync(Ct);
        _fx.UseIndex(rebuilt);

        Assert.Equal((1, 1), rebuilt.LastRebuild);                                // 作り直しも、captions は残して索引に入れず、件数を出す
        Assert.Equal(incremental, _fx.IndexDump());
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(4, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done'"));
    }

    // ---------------------------------------------------------------- 失敗の記録

    [Fact]
    public async Task A_failure_marks_ocr_and_caption_failed_with_the_error_and_settings_and_writes_no_caption()
    {
        await _fx.AddImagesAsync(1);
        const string settings = "{\"attempts\":[{\"attempt\":1,\"finish_reason\":\"length\"},{\"attempt\":2,\"dry_multiplier\":0.8,\"finish_reason\":\"length\"},{\"attempt\":3,\"repeat_penalty\":1.1,\"finish_reason\":\"length\"}]}";
        await _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelA, "finish_reason=length", settings, CaptionFixture.T(1), Ct);

        Assert.Equal(0, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'failed' AND error = 'finish_reason=length' AND attempts = 1"));
        Assert.Equal(settings, _fx.Text("SELECT settings_json FROM stage_status WHERE stage = 'caption'"));
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM stage_status WHERE stage = 'ocr'"));
    }

    [Fact]
    public async Task A_failure_leaves_the_caption_and_the_index_row_made_by_an_earlier_model_untouched()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "モデルAの文字", "説明", [], minutes: 1);
        var before = _fx.IndexDump();

        await _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelB, "HTTP 400", "{\"attempts\":[{\"attempt\":1,\"error\":\"HTTP 400\"}]}", CaptionFixture.T(2), Ct);

        Assert.Equal(before, _fx.IndexDump());                             // 索引はそのまま(古いモデルの結果で引ける)
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(2, _fx.Count($"SELECT count(*) FROM stage_status WHERE status = 'failed' AND model_id = '{_fx.ModelB}'"));  // 段階の行は新しいモデルで failed
        Assert.Equal([H(1)], _fx.Hits("モデルAの文字"));
    }

    [Fact]
    public async Task A_later_success_replaces_a_failure_and_clears_the_error_and_the_failure_settings()
    {
        await _fx.AddImagesAsync(1);
        await _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelA, "boom", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(1), Ct);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", [], minutes: 2);

        Assert.Equal(2, _fx.Count("SELECT count(*) FROM stage_status WHERE status = 'done' AND error IS NULL AND settings_json IS NULL AND attempts = 2"));
    }

    [Theory]
    [InlineData("{\"attempts\":[{\"attempt\":1}]}", true)]
    [InlineData("{\"source\":\"s1-import\",\"attempts\":[{\"attempt\":1,\"finish_reason\":\"length\"},{\"attempt\":2,\"error\":\"timeout\"}]}", true)]
    [InlineData("{}", false)]                                                // attempts が無い
    [InlineData("{\"attempts\":[]}", false)]                               // 空
    [InlineData("{\"attempts\":{}}", false)]                               // 配列でない
    [InlineData("{\"attempts\":[1]}", false)]                              // 要素がオブジェクトでない
    [InlineData("{\"attempts\":[{}]}", false)]                             // attempt が無い
    [InlineData("{\"attempts\":[{\"attempt\":0}]}", false)]              // 1 未満
    [InlineData("{\"attempts\":[{\"attempt\":\"1\"}]}", false)]        // 整数でない
    [InlineData("{\"attempts\":[{\"attempt\":1.5}]}", false)]
    [InlineData("[{\"attempt\":1}]", false)]                               // オブジェクトでない
    [InlineData("oops", false)]
    public void The_failure_settings_must_have_the_agreed_shape(string json, bool ok)
    {
        if (ok) CaptionStore.CheckFailureSettings(json, "settingsJson");
        else Assert.Throws<ArgumentException>(() => CaptionStore.CheckFailureSettings(json, "settingsJson"));
    }

    [Fact]
    public async Task A_failure_is_confirmed_once_started_so_a_cancelled_token_only_matters_before_it_starts()
    {
        await _fx.AddImagesAsync(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelA, "e", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(0), cts.Token));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));         // 始める前に取り消されたら、何も書かない
    }

    // ---------------------------------------------------------------- 引数の検査

    [Fact]
    public async Task Only_stop_is_saved_as_a_success_and_bad_arguments_are_rejected_before_anything_is_written()
    {
        await _fx.AddImagesAsync(1);
        CaptionResult good = _fx.Result(1, _fx.ModelA, "o", "d");
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { FinishReason = "length" }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { FinishReason = "" }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _fx.Store.SaveAsync(1, good with { Attempt = 0 }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _fx.Store.SaveAsync(1, good with { PromptTokens = -1 }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { SettingsJson = "not json" }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { SettingsJson = "[1]" }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { Tags = ["a", null!] }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _fx.Store.SaveAsync(1, good with { Ocr = null! }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveAsync(1, good with { Hash = "" }, CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelA, "", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.SaveFailureAsync(1, H(1), _fx.ModelA, "e", "oops", CaptionFixture.T(0), Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
    }

    [Fact]
    public async Task An_empty_ocr_and_empty_tags_are_saved_as_they_are()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "", "説明だけ", []);
        using SqliteConnection read = _fx.Db.CreateReadConnection();
        CaptionRow row = Assert.IsType<CaptionRow>(CaptionStore.Find(read, H(1), _fx.ModelA));
        Assert.Equal("", row.Ocr);
        Assert.Empty(row.Tags);
        Assert.Equal("[]", _fx.Text("SELECT tags_json FROM captions"));
        Assert.Equal("|説明だけ|", _fx.Text("SELECT norm_text FROM search_text"));
    }

    [Fact]
    public async Task Caption_text_with_control_characters_is_kept_as_is_and_only_the_index_is_sanitized()
    {
        await _fx.AddImagesAsync(1);
        string ocr = "あ\u0001い\uD800う";
        string desc = "説明\u0000です";
        Assert.Equal(CaptionSaveOutcome.Saved, await _fx.SaveAsync(1, _fx.ModelA, ocr, desc, ["タグ\u0007"]));

        using SqliteConnection read = _fx.Db.CreateReadConnection();
        CaptionRow row = Assert.IsType<CaptionRow>(CaptionStore.Find(read, H(1), _fx.ModelA));
        // captions には元の文字列。ただし孤立したサロゲートは、SQLite の TEXT(UTF-8)に入る時点で U+FFFD になる(DB の境界で起きること。製品のコードの置換ではない)。
        // 制御文字(U+0001、U+0000、U+0007)は元のまま残る
        Assert.Equal("あ\u0001い�う", row.Ocr);
        Assert.Equal(desc, row.Description);
        Assert.Equal(["タグ\u0007"], row.Tags);
        string norm = _fx.Text("SELECT norm_text FROM search_text");
        Assert.DoesNotContain('\u0001', norm);              // 索引文書は置換済み(U+FFFD は記号=区切り)
        Assert.DoesNotContain('\u0000', norm);
        Assert.DoesNotContain('\u0007', norm);
        Assert.Contains('�', norm);
        Assert.Equal([H(1)], _fx.Hits("説明"));
        Assert.Empty(_fx.Hits("あう"));                      // 置換した文字を挟んだ「あ」と「う」は、つながらない
    }

    // ---------------------------------------------------------------- 読み出し

    [Fact]
    public async Task Find_returns_null_for_a_missing_caption_and_FindLatest_picks_the_newest_across_models()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "A", "d", [], minutes: 1);
        await _fx.SaveAsync(1, _fx.ModelB, "B", "d", [], minutes: 2);
        using SqliteConnection read = _fx.Db.CreateReadConnection();
        Assert.Null(CaptionStore.Find(read, H(2), _fx.ModelA));
        Assert.Null(CaptionStore.Find(read, H(1), "no-such-model"));
        Assert.Null(CaptionStore.FindLatest(read, H(2)));
        Assert.Equal(_fx.ModelB, CaptionStore.FindLatest(read, H(1))!.ModelId);
        Assert.Equal("A", CaptionStore.Find(read, H(1), _fx.ModelA)!.Ocr);
    }

    [Fact]
    public async Task FindLatestMany_reads_in_chunks_dedupes_and_skips_images_without_captions()
    {
        const int total = CaptionStore.ReadChunk * 2 + 37;   // チャンクをまたぐ
        await _fx.AddImagesAsync(total);
        await _fx.Db.WriteAsync((c, tx) =>
        {
            for (int i = 1; i <= total; i += 2)               // 奇数の画像だけ caption を持つ(索引は作らない。読み出しだけの確認)
                TestDb.Exec(c, tx, $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, run_id, created_at) " +
                                   $"VALUES ('{H(i)}', '{_fx.ModelA}', 'o{i}', 'd', '[\"t\"]', 'stop', 1, '{{}}', 1, '2026-11-06T00:00:00.000Z')");
            // 画像 1 だけ、もう 1 つ新しい caption(別のモデル)
            TestDb.Exec(c, tx, $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, run_id, created_at) " +
                               $"VALUES ('{H(1)}', '{_fx.ModelB}', 'newer', 'd', '[]', 'stop', 1, '{{}}', 1, '2026-11-06T01:00:00.000Z')");
        }, Ct);

        using SqliteConnection read = _fx.Db.CreateReadConnection();
        IEnumerable<string> ask = Enumerable.Range(1, total).Select(H).Concat([H(1), H(3)]);   // 重複つき
        Dictionary<string, CaptionRow> got = CaptionStore.FindLatestMany(read, ask);
        Assert.Equal((total + 1) / 2, got.Count);
        Assert.False(got.ContainsKey(H(2)));
        Assert.Equal("newer", got[H(1)].Ocr);
        Assert.Equal("o3", got[H(3)].Ocr);
        int lastOdd = total % 2 == 1 ? total : total - 1;
        Assert.Equal("o" + lastOdd, got[H(lastOdd)].Ocr);
        Assert.Empty(CaptionStore.FindLatestMany(read, []));
    }

    [Fact]
    public async Task A_caption_row_whose_tags_json_is_not_an_array_of_strings_is_an_error_when_read()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", ["a"]);
        foreach (string bad in new[] { "{\"a\":1}", "[1]", "[\"a\",null]", "not json", "\"x\"" })
        {
            await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"UPDATE captions SET tags_json = '{bad}'"), Ct);
            using SqliteConnection read = _fx.Db.CreateReadConnection();
            Assert.Throws<InvalidDataException>(() => CaptionStore.Find(read, H(1), _fx.ModelA));
            Assert.Throws<InvalidDataException>(() => CaptionStore.FindLatest(read, H(1)));
            Assert.Throws<InvalidDataException>(() => CaptionStore.FindLatestMany(read, [H(1)]));
        }
    }

    // ---------------------------------------------------------------- 規約の待ち

    [Fact]
    public async Task Saving_first_waits_for_the_index_to_be_ensured_so_the_tables_exist()
    {
        await _fx.AddImagesAsync(1);
        Assert.Equal(FtsIndexState.NotChecked, _fx.Index.State);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", []);          // EnsureAsync を呼ばずに書いても、中で待つ
        Assert.Equal(FtsIndexState.Ready, _fx.Index.State);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
    }

    [Fact]
    public async Task Writing_with_an_index_whose_spec_differs_from_the_database_is_refused_inside_the_transaction()
    {
        await _fx.AddImagesAsync(1);
        await _fx.Index.EnsureAsync(Ct);
        // 別の規約で記録されてしまった(別のコードが先に作り直した、など)DB に、この FtsIndex が EnsureAsync を経ずに書こうとする
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "UPDATE index_meta SET value = 'fts-spec/9' WHERE key = 'fts_spec'"), Ct);
        await _fx.Db.WriteAsync((c, tx) =>
        {
            Assert.Throws<InvalidOperationException>(() => _fx.Index.RequireCurrent(c, tx));
        }, Ct);
    }
}
