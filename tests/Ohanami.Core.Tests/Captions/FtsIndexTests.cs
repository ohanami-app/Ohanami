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
/// FtsIndex(ADR 0030): 規約の照合と作り直し(1 つの書き込みトランザクション、その間の状態)、増分と作り直しが同じ中身になること、
/// 「、」「|」の区切りで bigram がまたがないこと(実物の FTS5)、search_text の LIKE、doc-select=latest-caption-v1。
/// </summary>
public sealed class FtsIndexTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private static CancellationToken Ct => CaptionFixture.Ct;
    private static string H(int i) => CaptionFixture.Hash(i);

    public void Dispose() => _fx.Dispose();

    private FtsIndex NewIndex() => new(_fx.Db, _fx.Log);

    // ---------------------------------------------------------------- 規約文字列

    [Fact]
    public void The_spec_string_lists_every_rule_that_decides_the_index_contents()
    {
        string expected =
            "fts-spec/1\n" +
            "tokenize=unicode61 remove_diacritics 1\n" +
            "layout=single\n" +
            "normalize=nfkc-lower-kana1\n" +
            "tokenizer=bigram-pad-v1\n" +
            "field-separator=|\n" +
            "tag-separator=、\n" +
            "doc-select=latest-caption+manual-tags-v1\n" +
            "sanitize=sanitize-v1\n";
        Assert.Equal(expected, FtsIndexSpec.Current.CanonicalString());
        Assert.DoesNotContain('\r', expected);
    }

    [Fact]
    public void The_table_definition_is_one_column_with_the_tokenizer_spec()
    {
        Assert.Equal("CREATE VIRTUAL TABLE fts_docs USING fts5(tokens, tokenize = 'unicode61 remove_diacritics 1')", FtsIndexSpec.Current.CreateTableSql());
        Assert.Equal("INSERT INTO fts_docs(rowid, tokens) VALUES ($id, $tokens)", FtsIndexSpec.Current.InsertSql());
        Assert.Equal("single", FtsIndexSpec.Layout);          // 1 列に決めた(3 列と比べて、結果は同じだった。ADR 0030)
    }

    // ---------------------------------------------------------------- 作る・作り直す

    [Fact]
    public async Task On_a_database_without_the_index_it_creates_fts_docs_records_the_spec_and_becomes_ready()
    {
        FtsIndex index = NewIndex();
        Assert.Equal(FtsIndexState.NotChecked, index.State);
        await index.EnsureAsync(Ct);

        Assert.Equal(FtsIndexState.Ready, index.State);
        Assert.Null(index.FailureMessage);
        Assert.Equal(1, index.RebuildCount);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM sqlite_master WHERE name = 'fts_docs'"));
        Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
        Assert.Equal("tokens", _fx.Text("SELECT group_concat(name) FROM pragma_table_info('fts_docs')"));
    }

    [Fact]
    public async Task When_the_stored_spec_matches_nothing_is_rebuilt()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ", "d", []);
        Assert.Equal(1, _fx.Index.RebuildCount);

        FtsIndex again = NewIndex();             // 同じ規約の別のインスタンス(アプリを再起動した場面)
        await again.EnsureAsync(Ct);

        Assert.Equal(FtsIndexState.Ready, again.State);
        Assert.Equal(0, again.RebuildCount);     // 規約が同じなら作り直さない
        Assert.Equal([H(1)], _fx.Hits("ガチャ"));
    }

    [Fact]
    public async Task A_changed_spec_rebuilds_the_index_from_captions_and_search_results_stay_the_same()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "説明", ["銀髪"], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "別のセリフ", "ガチャの結果", [], minutes: 2);
        await _fx.SaveAsync(3, _fx.ModelB, "", "", ["猫"], minutes: 3);
        List<string> before = _fx.Hits("ガチャ");
        var dumpBefore = _fx.IndexDump();

        await _fx.TamperSpecAsync();                       // 古い版のアプリが作った索引が DB にある場面(記録された規約が現行と違う)
        FtsIndex reopened = NewIndex();
        await reopened.EnsureAsync(Ct);

        Assert.Equal(1, reopened.RebuildCount);
        Assert.Equal((3, 0), reopened.LastRebuild);
        Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
        _fx.UseIndex(reopened);
        Assert.Equal(before, _fx.Hits("ガチャ"));
        Assert.Equal([H(1)], _fx.Hits("銀髪"));
        Assert.Equal([H(3)], _fx.Hits("猫"));
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal(dumpBefore, _fx.IndexDump());          // 作り直した索引は、増分で作った索引と同じ中身
    }

    [Fact]
    public async Task Any_one_element_of_the_spec_being_different_is_enough_to_rebuild()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", []);
        foreach (string tampered in new[]
        {
            FtsIndexSpec.Current.CanonicalString().Replace("tokenizer=bigram-pad-v1", "tokenizer=bigram-pad-v0", StringComparison.Ordinal),
            FtsIndexSpec.Current.CanonicalString().Replace("sanitize=sanitize-v1", "sanitize=sanitize-v0", StringComparison.Ordinal),
            FtsIndexSpec.Current.CanonicalString().Replace("doc-select=latest-caption+manual-tags-v1", "doc-select=latest-caption-v1", StringComparison.Ordinal),
            FtsIndexSpec.Current.CanonicalString().Replace("tag-separator=、", "tag-separator=,", StringComparison.Ordinal),
            FtsIndexSpec.Current.CanonicalString().Replace("normalize=nfkc-lower-kana1", "normalize=nfc", StringComparison.Ordinal),
            FtsIndexSpec.Current.CanonicalString().Replace("tokenize=unicode61 remove_diacritics 1", "tokenize=unicode61 remove_diacritics 0", StringComparison.Ordinal),
            "",
            FtsIndexSpec.Current.CanonicalString() + "extra=1\n",
        })
        {
            await _fx.Db.WriteAsync((c, tx) =>
            {
                using SqliteCommand cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE index_meta SET value = $v WHERE key = 'fts_spec'";
                cmd.Parameters.AddWithValue("$v", tampered);
                cmd.ExecuteNonQuery();
            }, Ct);
            FtsIndex fresh = NewIndex();
            await fresh.EnsureAsync(Ct);
            Assert.Equal(1, fresh.RebuildCount);                  // 記録された規約が現行と 1 文字でも違えば作り直す
            Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
        }
    }

    [Fact]
    public async Task An_index_made_with_the_previous_normalization_is_rebuilt_on_open_and_then_finds_the_other_kana_script()
    {
        // 正規化の版が nfkc-lower だったときに作った索引(ひらがなとカタカナを区別していた)は、nfkc-lower-kana1 の版で開くときに作り直される
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "ホルテバルドの風景", "d", [], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "ほるてばるどの絵", "d", [], minutes: 2);
        await _fx.SaveAsync(3, _fx.ModelA, "カ", "d", [], minutes: 3);
        string previous = FtsIndexSpec.Current.CanonicalString().Replace("normalize=nfkc-lower-kana1", "normalize=nfkc-lower", StringComparison.Ordinal);
        Assert.NotEqual(previous, FtsIndexSpec.Current.CanonicalString());
        await _fx.Db.WriteAsync((c, tx) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE index_meta SET value = $v WHERE key = 'fts_spec'";
            cmd.Parameters.AddWithValue("$v", previous);
            cmd.ExecuteNonQuery();
        }, Ct);

        FtsIndex reopened = NewIndex();
        await reopened.EnsureAsync(Ct);

        Assert.Equal(1, reopened.RebuildCount);
        Assert.Equal((3, 0), reopened.LastRebuild);
        Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
        _fx.UseIndex(reopened);
        Assert.Equal([H(1), H(2)], _fx.Hits("ほるてばるど"));   // ひらがなで打って、カタカナの文書にも当たる
        Assert.Equal([H(1), H(2)], _fx.Hits("ホルテバルド"));    // カタカナで打って、ひらがなの文書にも当たる
        Assert.Equal([H(3)], _fx.Hits("か"));                  // 1 文字(search_text の LIKE)も、書き方を問わない
        Assert.Equal([H(3)], _fx.Hits("カ"));
        Assert.Empty(_fx.Hits("ほるてばると"));                 // 濁点の有無は別
        Assert.Equal("ほるてばるどの絵|d|", _fx.Text($"SELECT norm_text FROM search_text WHERE hash = '{H(2)}'"));
        Assert.Equal("ほるてばるどの風景|d|", _fx.Text($"SELECT norm_text FROM search_text WHERE hash = '{H(1)}'"));
    }

    [Fact]
    public async Task A_document_written_in_katakana_is_found_with_hiragana_and_the_other_way_round_without_a_rebuild()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "ホルテバルド", "d", ["ヴァイオリン"], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "ほるてばるど", "d", ["ゔぁいおりん"], minutes: 2);
        Assert.Equal([H(1), H(2)], _fx.Hits("ほるてばるど"));
        Assert.Equal([H(1), H(2)], _fx.Hits("ホルテバルド"));
        Assert.Equal([H(1), H(2)], _fx.Hits("ﾎﾙﾃﾊﾞﾙﾄﾞ"));
        Assert.Equal([H(1), H(2)], _fx.Hits("ヴァイオリン"));
        Assert.Equal([H(1), H(2)], _fx.Hits("ゔぁいおりん"));
        Assert.Empty(_fx.Hits("ほるてはるど"));
    }

    [Fact]
    public async Task A_missing_fts_docs_table_is_rebuilt_even_when_the_recorded_spec_looks_right()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ", "d", []);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DROP TABLE fts_docs"), Ct);

        FtsIndex fresh = NewIndex();
        await fresh.EnsureAsync(Ct);

        Assert.Equal(1, fresh.RebuildCount);
        Assert.Equal([H(1)], _fx.Hits("ガチャ"));
    }

    [Fact]
    public async Task A_rebuild_makes_the_same_index_as_the_incremental_updates_for_every_image()
    {
        await _fx.AddImagesAsync(6);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連を引く", "ＨＰが回復した", ["銀髪", "ロングヘア"], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "はい。いいえ", "ｶﾞﾁｬの結果", ["猫"], minutes: 2);
        await _fx.SaveAsync(3, _fx.ModelA, "あ\u0001い\uD800う", "制御文字", [], minutes: 3);
        await _fx.SaveAsync(4, _fx.ModelA, "", "", [], minutes: 4);
        await _fx.SaveAsync(5, _fx.ModelA, "古いモデル", "d", [], minutes: 5);
        await _fx.SaveAsync(5, _fx.ModelB, "新しいモデル", "d", ["t"], minutes: 6);
        await _fx.SaveAsync(1, _fx.ModelB, "モデルBのガチャ", "d", [], minutes: 7);          // 画像 1 は B のほうが新しい
        await _fx.SaveAsync(1, _fx.ModelA, "モデルAをもう一度", "d", [], minutes: 8);        // さらに A を作り直した(A が最新に戻る)
        // 画像 6 は caption なし
        var incremental = _fx.IndexDump();
        Assert.Equal(5, incremental.Count);

        FtsIndex rebuilt = NewIndex();
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM index_meta"), Ct);   // 規約の記録を消して、作り直しを起こす
        await rebuilt.EnsureAsync(Ct);

        Assert.Equal(1, rebuilt.RebuildCount);
        Assert.Equal((5, 0), rebuilt.LastRebuild);
        _fx.UseIndex(rebuilt);
        Assert.Equal(incremental, _fx.IndexDump());                    // 増分で作った索引と、作り直した索引が同じ中身(ハッシュごとの文書・モデル・トークン)
        Assert.Equal(_fx.ModelB, incremental[H(5)].Model);
        Assert.Equal(_fx.ModelA, incremental[H(1)].Model);
    }

    // ---------------------------------------------------------------- 作り直しの間の状態

    [Fact]
    public async Task While_the_rebuild_waits_for_the_writer_the_state_is_rebuilding_and_becomes_ready_afterwards()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ", "d", []);
        await _fx.TamperSpecAsync();
        FtsIndex reopened = NewIndex();

        using var holding = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // 書き込みの口をふさいで、作り直しが始まったまま待つ場面を作る
        // (WriteAsync は、ロックを取れたら work を呼び出したスレッドで同期に実行する。別のスレッドで始めないと、このテスト自身が止まる)
        Task blocker = Task.Run(() => _fx.Db.WriteAsync((c, tx) =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(30), Ct);
        }, Ct), Ct);
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10), Ct));

        Task ensure = reopened.EnsureAsync(Ct);
        Assert.True(await WaitUntil(() => reopened.State == FtsIndexState.Rebuilding));   // 規約の照合で「違う」と分かった時点で Rebuilding
        Assert.False(ensure.IsCompleted);
        Assert.Equal(0, reopened.RebuildCount);

        // 作り直し中も、読みは妨げられない(古い索引がそのまま引ける)
        Assert.Equal([H(1)], _fx.Hits("ガチャ"));

        release.Set();
        await blocker;
        await ensure;
        Assert.Equal(FtsIndexState.Ready, reopened.State);
        Assert.Equal(1, reopened.RebuildCount);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(5, Ct);
        }
        return condition();
    }

    [Fact]
    public async Task Concurrent_ensure_calls_share_one_rebuild()
    {
        await _fx.AddImagesAsync(2);
        FtsIndex index = NewIndex();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => index.EnsureAsync(Ct)));
        Assert.Equal(1, index.RebuildCount);
        Assert.Equal(FtsIndexState.Ready, index.State);
        await index.EnsureAsync(Ct);                          // 済んでからの呼び出しも何もしない
        Assert.Equal(1, index.RebuildCount);
    }

    [Fact]
    public async Task A_cancelled_wait_stops_waiting_but_the_rebuild_finishes()
    {
        FtsIndex index = NewIndex();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.EnsureAsync(cts.Token));
        await index.EnsureAsync(Ct);                          // 始まった作り直しは終わる(待ち直せる)
        Assert.Equal(FtsIndexState.Ready, index.State);
        Assert.Equal(1, index.RebuildCount);
    }

    // ---------------------------------------------------------------- 作り直しの失敗

    [Fact]
    public async Task A_failed_rebuild_rolls_back_keeps_the_old_index_and_spec_and_can_be_retried()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ", "d", [], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "猫", "d", [], minutes: 2);
        await _fx.TamperSpecAsync("fts-spec/0\n");         // 規約が違う → 作り直しが要る
        string oldSpec = _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'");

        FtsIndex reopened = NewIndex();
        reopened.BuildDocument = (_, _, _, _) => throw new IOException("作り直しの途中で失敗(テスト)");
        await Assert.ThrowsAsync<IOException>(() => reopened.EnsureAsync(Ct));

        Assert.Equal(FtsIndexState.Failed, reopened.State);
        Assert.Contains("途中で失敗", reopened.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Equal(oldSpec, _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));     // 規約の記録はそのまま(巻き戻った)
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM search_text"));                              // 古い索引はそのまま
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Equal([H(1)], _fx.Hits("ガチャ"));

        // 直したあとにもう一度呼べば、やり直す
        reopened.BuildDocument = FtsDocumentBuilder.Build;
        await reopened.EnsureAsync(Ct);
        Assert.Equal(FtsIndexState.Ready, reopened.State);
        Assert.Null(reopened.FailureMessage);
        Assert.Equal(FtsIndexSpec.Current.CanonicalString(), _fx.Text("SELECT value FROM index_meta WHERE key = 'fts_spec'"));
    }

    [Fact]
    public async Task A_rebuild_skips_an_image_whose_document_cannot_be_built_and_logs_a_warning()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "いち", "d", []);
        await _fx.SaveAsync(2, _fx.ModelA, "BAD", "d", []);
        await _fx.SaveAsync(3, _fx.ModelA, "さん", "d", []);

        FtsIndex fresh = NewIndex();
        fresh.BuildDocument = (o, d, t, m) => o == "BAD" ? throw new InvalidOperationException("作れない(テスト)") : FtsDocumentBuilder.Build(o, d, t, m);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM index_meta"), Ct);
        await fresh.EnsureAsync(Ct);

        Assert.Equal(FtsIndexState.Ready, fresh.State);
        Assert.Equal((2, 1), fresh.LastRebuild);
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM captions"));                 // captions は残る
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains(H(2), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebuild_with_a_corrupt_tags_json_is_a_loud_failure_not_a_silent_skip()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", ["a"]);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "UPDATE captions SET tags_json = '{\"not\":\"array\"}'"), Ct);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM index_meta"), Ct);
        FtsIndex fresh = NewIndex();
        await Assert.ThrowsAsync<InvalidDataException>(() => fresh.EnsureAsync(Ct));
        Assert.Equal(FtsIndexState.Failed, fresh.State);
    }

    // ---------------------------------------------------------------- 区切り(実物の FTS5)

    [Fact]
    public async Task Tags_joined_with_the_ideographic_comma_do_not_make_bigrams_across_tags_in_the_real_fts5()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "", "", ["銀髪", "ロングヘア"]);                 // タグが 2 つ
        await _fx.SaveAsync(2, _fx.ModelA, "", "", ["銀髪ロングヘア"]);                      // 1 つのタグ
        await _fx.SaveAsync(3, _fx.ModelA, "銀髪", "ロングヘア", []);                        // 欄が違う

        Assert.Equal([H(2)], _fx.Hits("銀髪ロング"));                                       // タグの境目をまたぐ一致は出ない
        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("銀髪 ロング"));                          // 空白で AND にすれば、どれでも当たる
        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("銀髪 ロングヘア"));
        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("ロングヘア"));
        // 「、」を打てば、区切りをはさんで隣り合うフレーズ。索引の側も区切りはトークンを作らないので、欄・タグの違いは問わず、2 つの run が続く文書(1 と 3)に当たる。1 つのタグ(2)には当たらない
        Assert.Equal([H(1), H(3)], _fx.Hits("銀髪、ロングヘア"));
    }

    [Fact]
    public async Task Fields_joined_with_the_bar_do_not_make_bigrams_across_ocr_description_and_tags()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "あか", "いろ", []);                              // ocr の末尾と説明文の先頭
        await _fx.SaveAsync(2, _fx.ModelA, "あかいろ", "", []);                              // 1 つの欄
        await _fx.SaveAsync(3, _fx.ModelA, "", "あか", ["いろ"]);                            // 説明文の末尾と tags の先頭

        Assert.Equal([H(2)], _fx.Hits("かい"));                                             // 欄の境目の「かい」は出ない
        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("あか"));
        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("いろ"));
        Assert.Equal([H(2)], _fx.Hits("あかいろ"));
    }

    [Fact]
    public async Task Newline_or_space_between_japanese_text_inside_one_field_is_removed_so_a_wrapped_line_still_matches()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "この帽子に\nもポップコーン効果", "d", []);
        Assert.Equal([H(1)], _fx.Hits("帽子にも"));
        Assert.Equal([H(1)], _fx.Hits("帽子に もポップ")); // 空白は AND の区切りなので、2 語「帽子に」「もポップ」がどちらも当たる
    }

    [Fact]
    public async Task The_real_fts5_agrees_with_the_reference_matcher_for_documents_saved_through_the_store()
    {
        string[][] docs =
        [
            ["ガチャ10連を引く", "ＨＰが回復した", "銀髪"],
            ["はい。いいえ", "ｶﾞﾁｬ", "猫、犬"],
            ["", "森の中の洞窟", "ロングヘア"],
            ["あ", "い", "う"],
        ];
        await _fx.AddImagesAsync(docs.Length);
        for (int i = 0; i < docs.Length; i++)
            await _fx.SaveAsync(i + 1, _fx.ModelA, docs[i][0], docs[i][1], docs[i][2].Split('、'));
        string[] queries = ["ガチャ", "ガチャ10連", "hp", "回復した", "はい", "はいい", "いいえ", "洞窟 森", "猫", "犬", "ロング", "あ", "う", "銀髪 ガチャ", "猫、犬", "犬猫"];
        foreach (string q in queries)
        {
            SearchPlan plan = QueryPlanner.PlanSearch(q);
            List<string> expected = Enumerable.Range(0, docs.Length)
                .Where(i => plan.Matches(NormalizedText.Create(FtsDocumentBuilder.Join(docs[i][0], docs[i][1], docs[i][2].Split('、')))))
                .Select(i => H(i + 1)).ToList();
            Assert.True(expected.SequenceEqual(_fx.Hits(q)), $"クエリ「{q}」: 基準 {string.Join(",", expected.Select(h => h[^2..]))} / FTS {string.Join(",", _fx.Hits(q).Select(h => h[^2..]))}");
        }
    }

    // ---------------------------------------------------------------- search_text の LIKE

    [Fact]
    public async Task One_character_queries_use_like_on_the_normalized_text()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "ＨＰ回復", "d", []);          // 全角 → 半角・小文字に正規化される
        await _fx.SaveAsync(2, _fx.ModelA, "MP", "d", []);
        await _fx.SaveAsync(3, _fx.ModelA, "ｶﾞﾁｬ", "森", []);

        Assert.Equal([H(1), H(2)], _fx.Hits("p"));                          // 英数字の 1 文字は部分一致
        Assert.Equal([H(1)], _fx.Hits("h"));
        Assert.Equal([H(3)], _fx.Hits("ガ"));                               // 半角カナも NFKC 後の 1 文字
        Assert.Equal([H(3)], _fx.Hits("森"));
        Assert.Equal([H(1)], _fx.Hits("回 p"));                             // 1 文字どうしの AND
        Assert.Empty(_fx.Hits("犬"));
        // LIKE の特殊文字は、パターンの作り方(SearchPlan.LikePattern)でエスケープされる
        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM search_text WHERE norm_text LIKE '{SearchPlan.LikePattern("%").Replace("'", "''")}' ESCAPE '\\'"));
    }

    // ---------------------------------------------------------------- doc-select = latest-caption-v1

    [Fact]
    public async Task The_document_comes_from_the_newest_caption_of_the_image_whatever_the_model()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "モデルAの文字", "d", [], minutes: 10);
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));
        Assert.Equal([H(1)], _fx.Hits("モデルAの文字"));

        await _fx.SaveAsync(1, _fx.ModelB, "モデルBの文字", "d", [], minutes: 20);       // 新しいモデルの結果
        Assert.Equal(_fx.ModelB, _fx.Text("SELECT model_id FROM search_text"));
        Assert.Empty(_fx.Hits("モデルAの文字"));
        Assert.Equal([H(1)], _fx.Hits("モデルBの文字"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM captions"));                    // 古い結果は captions に残る(索引だけが新しいほう)
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs"));

        await _fx.SaveAsync(1, _fx.ModelA, "モデルAの文字(再処理)", "d", [], minutes: 30);   // A を作り直したので、A が最新に戻る
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));
        Assert.Equal([H(1)], _fx.Hits("再処理"));
        Assert.Empty(_fx.Hits("モデルBの文字"));
    }

    [Fact]
    public async Task A_caption_with_an_older_created_at_than_the_existing_newest_does_not_replace_the_index_row()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "新しいほう", "d", [], minutes: 30);
        await _fx.SaveAsync(1, _fx.ModelB, "古い時刻で書いた", "d", [], minutes: 10);     // 時計のずれなどで、created_at が既存より古い
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));          // 索引は最新の caption(created_at が大きいほう)のまま
        Assert.Equal([H(1)], _fx.Hits("新しいほう"));
        Assert.Empty(_fx.Hits("古い時刻"));
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM captions"));
        // 作り直しでも同じ選び方
        var incremental = _fx.IndexDump();
        FtsIndex fresh = NewIndex();
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM index_meta"), Ct);
        await fresh.EnsureAsync(Ct);
        _fx.UseIndex(fresh);
        Assert.Equal(incremental, _fx.IndexDump());
    }

    [Fact]
    public async Task With_the_same_created_at_the_caption_written_later_wins_in_both_the_incremental_update_and_a_rebuild()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "先に書いた", "d", [], minutes: 5);
        await _fx.SaveAsync(1, _fx.ModelB, "あとに書いた", "d", [], minutes: 5);          // 同じ created_at → rowid が大きいほう
        Assert.Equal(_fx.ModelB, _fx.Text("SELECT model_id FROM search_text"));
        var incremental = _fx.IndexDump();
        FtsIndex fresh = NewIndex();
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM index_meta"), Ct);
        await fresh.EnsureAsync(Ct);
        _fx.UseIndex(fresh);
        Assert.Equal(incremental, _fx.IndexDump());
    }

    [Fact]
    public async Task Images_without_any_caption_have_no_index_row_and_an_image_with_only_a_failure_has_none_either()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "o", "d", []);
        await _fx.Store.SaveFailureAsync(1, H(2), _fx.ModelA, "length", "{\"attempts\":[{\"attempt\":1}]}", CaptionFixture.T(1), Ct);
        Assert.Equal(H(1), _fx.Text("SELECT hash FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));
    }
}
