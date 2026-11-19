using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Captions;
using Ohanami.Core.Tests.Data;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>
/// 全文検索の経路(ADR 0029・0033): 合成の captions(実物の SQLite・FTS5)での順位(BM25、1 文字、出現回数、AND)、見た目の経路との融合、
/// 母集団の和、NotReady / Failed / Skipped と Message、TextDetail の位置、SearchService の継ぎ目(PrepareDetails)。
/// </summary>
public sealed class FullTextPathTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly CaptionFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>画像 1〜n を DB に入れ(見える画像: パスあり)、埋め込みモデルを登録する。</summary>
    private async Task<string> SetUpImagesAsync(int count)
    {
        await _fx.AddImagesAsync(count);
        long folder = await AddFolderAsync(_fx.Db);
        for (int i = 1; i <= count; i++) await AddImageAsync(_fx.Db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
        return await RegisterModelAsync(_fx.Db);
    }

    private Task Save(int image, string ocr, string description = "", string[]? tags = null, int minutes = 1) =>
        _fx.SaveAsync(image, _fx.ModelA, ocr, description, tags ?? [], minutes);

    private SearchService FullTextOnly() => new(_fx.Db, [new FullTextPath(_fx.Db, _fx.Index)], new VectorTable("none", Dim));

    private static string[] Hashes(SearchResponse r) => r.Results.Select(x => x.Hash).ToArray();

    // ---------------------------------------------------------------- 順位(BM25)

    [Fact]
    public async Task Phrase_hits_are_ranked_by_bm25_so_short_and_frequent_documents_come_first()
    {
        await SetUpImagesAsync(4);
        await Save(1, "ガチャを引く。ガチャを引く。ガチャを引く");
        await Save(2, "ガチャを引く");
        await Save(3, "ガチャの結果。たくさんの長い文章がここに入っていて、関係のない言葉がずっと続く");
        await Save(4, "関係のない文書");
        await _fx.Index.EnsureAsync(Ct);

        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery("ガチャ"), Ct);

        Assert.Equal(3, r.Results.Count);                               // 疎な経路: ヒットした画像だけ(画像 4 は出ない)
        Assert.Equal(Hash(3), r.Results[^1].Hash);                      // 長くて 1 回しか出ない文書が最後
        Assert.Equal([1.0, 2.0, 3.0], r.Results.Select(x => x.Rank).ToArray());
        PathHit hit = Assert.Single(r.Results[0].Hits);
        Assert.Equal("fulltext", hit.PathId);
        Assert.Null(hit.ModelId);
        Assert.True(hit.Score > 0);                                     // スコアは -rank(BM25 の rank は負の数。大きいほど良い)
        Assert.True(r.Results[0].Hits[0].Score >= r.Results[1].Hits[0].Score && r.Results[1].Hits[0].Score >= r.Results[2].Hits[0].Score);
        PathOutcome outcome = Assert.Single(r.Paths);
        Assert.Equal(new PathOutcome("fulltext", null, 3, PathState.Ok, null), outcome);
    }

    [Fact]
    public async Task Terms_separated_by_spaces_are_anded()
    {
        await SetUpImagesAsync(4);
        await Save(1, "銀髪の少女と猫がいる");
        await Save(2, "銀髪の少女");
        await Save(3, "猫だけがいる");
        await Save(4, "金髪の少女と犬");
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = FullTextOnly();

        Assert.Equal([Hash(1)], Hashes(await service.SearchAsync(new SearchQuery("銀髪 猫"), Ct)));
        Assert.Equivalent(new[] { Hash(1), Hash(2) }, Hashes(await service.SearchAsync(new SearchQuery("銀髪 少女"), Ct)));
        Assert.Empty((await service.SearchAsync(new SearchQuery("銀髪 犬"), Ct)).Results);
        Assert.Equivalent(new[] { Hash(1), Hash(2) }, Hashes(await service.SearchAsync(new SearchQuery("銀髪　少女"), Ct)));   // 全角空白
    }

    [Fact]
    public async Task A_one_character_term_only_filters_when_a_phrase_is_present()
    {
        await SetUpImagesAsync(3);
        await Save(1, "森の中の洞窟");
        await Save(2, "洞窟の中");
        await Save(3, "森と洞窟と森");
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = FullTextOnly();

        Assert.Equivalent(new[] { Hash(1), Hash(3) }, Hashes(await service.SearchAsync(new SearchQuery("洞窟 森"), Ct)));   // 「森」は絞り込み。順位は BM25
        Assert.Equivalent(new[] { Hash(1), Hash(2) }, Hashes(await service.SearchAsync(new SearchQuery("洞窟 中"), Ct)));   // 3 には「中」が無い
    }

    // ---------------------------------------------------------------- 順位(1 文字 = 出現回数)

    [Fact]
    public async Task Only_one_character_terms_are_ranked_by_occurrence_count_and_ties_get_the_mean_rank()
    {
        await SetUpImagesAsync(5);
        await Save(1, "森");
        await Save(2, "森と森と森");
        await Save(3, "森と森");
        await Save(4, "森森");
        await Save(5, "花");
        await _fx.Index.EnsureAsync(Ct);

        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery("森"), Ct);

        Assert.Equal(Hash(2), r.Results[0].Hash);                                    // 出現回数 3
        Assert.Equal([1.0, 2.5, 2.5, 4.0], r.Results.Select(x => x.Rank).ToArray()); // 回数 2 の 2 枚は同点 = 2〜3 位の平均 2.5
        Assert.Equal(Hash(1), r.Results[3].Hash);                                    // 回数 1
        Assert.Equal([3.0, 2.0, 2.0, 1.0], r.Results.Select(x => Assert.Single(x.Hits).Score).ToArray());   // スコア = 出現回数(多いほど良い)
        Assert.DoesNotContain(r.Results, x => x.Hash == Hash(5));
    }

    [Fact]
    public async Task The_occurrence_score_equals_the_plans_count_for_several_one_character_terms()
    {
        await SetUpImagesAsync(3);
        await Save(1, "森と森と森", "と");
        await Save(2, "森と森");
        await Save(3, "ＨＰ hp");
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = FullTextOnly();

        foreach (string query in new[] { "森 と", "森", "と", "h p", "p" })
        {
            SearchPlan plan = QueryPlanner.PlanSearch(query);
            SearchResponse r = await service.SearchAsync(new SearchQuery(query), Ct);
            foreach (SearchResult x in r.Results)
            {
                int i = Array.IndexOf(Enumerable.Range(1, 3).Select(Hash).ToArray(), x.Hash) + 1;
                string[][] docs = [["森と森と森", "と", ""], ["森と森", "", ""], ["ＨＰ hp", "", ""]];
                double expected = plan.CountOccurrences(NormalizedText.Create(FtsDocumentBuilder.Join(docs[i - 1][0], docs[i - 1][1], [])));
                Assert.Equal(expected, Assert.Single(x.Hits).Score);
            }
        }
    }

    // ---------------------------------------------------------------- 融合(見た目 + 全文)と母集団の和

    private async Task<(SearchService Service, VectorTable Table)> FusedServiceAsync(string embeddingModel, params (int Image, double Angle)[] vectors)
    {
        foreach ((int image, double angle) in vectors) await AddVectorAsync(_fx.Db, embeddingModel, Hash(image), Unit(angle));
        var table = new VectorTable(embeddingModel, Dim);
        await table.LoadAsync(_fx.Db, Ct);
        var visual = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null);
        return (new SearchService(_fx.Db, [visual, new FullTextPath(_fx.Db, _fx.Index)], table), table);
    }

    [Fact]
    public async Task The_visual_and_full_text_paths_are_fused_and_every_hit_lists_both_paths_in_the_order_of_the_path_array()
    {
        string model = await SetUpImagesAsync(4);
        // 全文で当たるのは 3 と 4(4 のほうが BM25 で上)。見た目は角度が小さい順(1 が 1 位、4 が 4 位)
        await Save(3, "猫と犬と鳥と魚と馬と牛と羊と鹿と猿");
        await Save(4, "猫猫猫猫");
        await _fx.Index.EnsureAsync(Ct);
        (SearchService service, _) = await FusedServiceAsync(model, (1, 10), (2, 20), (3, 30), (4, 40));

        SearchResponse r = await service.SearchAsync(new SearchQuery("猫"), Ct);

        // 融合 = 1/(60 + 見た目の順位) + 1/(60 + 全文の順位)(全文に出ない画像は見た目だけ)。4: 1/64 + 1/61、3: 1/63 + 1/62、1: 1/61、2: 1/62
        Assert.Equal([Hash(4), Hash(3), Hash(1), Hash(2)], Hashes(r));
        Assert.Equal(1.0 / 64 + 1.0 / 61, r.Results[0].FusedScore, 12);
        Assert.Equal([PathState.Ok, PathState.Ok], r.Paths.Select(p => p.State).ToArray());
        Assert.Equal(["visual", "fulltext"], r.Paths.Select(p => p.PathId).ToArray());
        Assert.Equal(4, r.Paths[0].Population);
        Assert.Equal(2, r.Paths[1].Population);
        foreach (SearchResult x in r.Results)
        {
            double fused = x.Hits.Sum(h => 1.0 / (60 + h.Rank));
            Assert.Equal(fused, x.FusedScore, 12);
            Assert.Equal(x.Hits.Select(h => h.PathId).OrderBy(id => id == "visual" ? 0 : 1).ToArray(), x.Hits.Select(h => h.PathId).ToArray()); // 経路の配列の順
        }
        Assert.Equal(["visual", "fulltext"], r.Results[0].Hits.Select(h => h.PathId).ToArray());
        Assert.Equal(4.0, r.Results[0].Hits[0].Rank);              // 4 の見た目の順位は 4 位
        Assert.Equal(1.0, r.Results[0].Hits[1].Rank);              // 全文では 1 位
        Assert.Single(r.Results[2].Hits);                          // 全文に出ない 1 は見た目だけ
        Assert.IsType<VisualDetail>(r.Results[0].Hits[0].Detail);
        Assert.IsType<TextDetail>(r.Results[0].Hits[1].Detail);
        output.WriteLine(string.Join(" > ", r.Results.Select(x => x.Hash[^1] + ":" + x.FusedScore.ToString("F5"))));
    }

    [Fact]
    public async Task An_image_without_an_embedding_still_appears_when_the_full_text_path_hits_it()
    {
        string model = await SetUpImagesAsync(3);
        await Save(3, "埋め込みがまだ無い画像のセリフ");           // 画像 3 にはベクトルが無い
        await _fx.Index.EnsureAsync(Ct);
        (SearchService service, _) = await FusedServiceAsync(model, (1, 10), (2, 20));

        SearchResponse r = await service.SearchAsync(new SearchQuery("セリフ"), Ct);

        // 母集団は「ベクトルの表 ∪ 全文のヒット」: 全文だけで当たる画像 3 が結果に出る。見た目の 1 位(1/61)と全文の 1 位(1/61)は同点なので、どちらも 1.5 位
        SearchResult onlyText = r.Results.Single(x => x.Hash == Hash(3));
        Assert.Equal("fulltext", Assert.Single(onlyText.Hits).PathId);
        Assert.Equal(1.5, onlyText.Rank);
        Assert.Equal(1.5, r.Results.Single(x => x.Hash == Hash(1)).Rank);
        Assert.Equal(3, r.Results.Count);                          // 見た目の 2 枚も(全文のヒットが無くても)出る
        Assert.Equal(2, r.IndexedImages);                          // 「埋め込み済み」の数は、ベクトルのある画像だけ
        Assert.Equal(3, r.TotalImages);
    }

    [Fact]
    public async Task A_full_text_hit_on_an_image_that_is_not_visible_is_left_out()
    {
        await SetUpImagesAsync(2);
        await Save(1, "見えるセリフ");
        await Save(2, "消えたセリフ");
        await _fx.Index.EnsureAsync(Ct);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"UPDATE image_paths SET missing_since = '2026-11-06T00:00:00.000Z' WHERE hash = '{Hash(2)}'"), Ct);

        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery("セリフ"), Ct);

        Assert.Equal([Hash(1)], Hashes(r));                        // missing_since のパスしか無い画像は、順位を付ける前に除かれる
        Assert.Equal(1.0, r.Results[0].Rank);
        Assert.Equal(1, r.Paths[0].Population);
    }

    // ---------------------------------------------------------------- 状態

    [Fact]
    public async Task While_the_index_is_not_checked_yet_the_path_is_not_ready_and_the_visual_path_still_answers()
    {
        string model = await SetUpImagesAsync(2);
        await Save(1, "セリフ");                                    // (Save が索引を確認して Ready にするので、確認前の状態は別の FtsIndex で作る)
        FtsIndex unchecked1 = new(_fx.Db, _fx.Log);
        Assert.Equal(FtsIndexState.NotChecked, unchecked1.State);
        await AddVectorAsync(_fx.Db, model, Hash(1), Unit(10));
        await AddVectorAsync(_fx.Db, model, Hash(2), Unit(20));
        var table = new VectorTable(model, Dim);
        await table.LoadAsync(_fx.Db, Ct);
        var service = new SearchService(_fx.Db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null), new FullTextPath(_fx.Db, unchecked1)], table);

        SearchResponse r = await service.SearchAsync(new SearchQuery("セリフ"), Ct);

        PathOutcome ft = r.Paths.Single(p => p.PathId == "fulltext");
        Assert.Equal(PathState.NotReady, ft.State);
        Assert.Contains("確認", ft.Message, StringComparison.Ordinal);
        Assert.Equal(0, ft.Population);
        Assert.Equal(PathState.Ok, r.Paths.Single(p => p.PathId == "visual").State);
        Assert.Equal(2, r.Results.Count);                          // 見た目の結果は返る
        Assert.All(r.Results, x => Assert.Single(x.Hits));
    }

    [Fact]
    public async Task While_the_index_is_being_rebuilt_the_path_is_not_ready_and_afterwards_it_answers()
    {
        await SetUpImagesAsync(1);
        await Save(1, "セリフ");
        await _fx.TamperSpecAsync();                                 // 規約が違う → 作り直しが要る
        var triple = new FtsIndex(_fx.Db, _fx.Log);
        var service = new SearchService(_fx.Db, [new FullTextPath(_fx.Db, triple)], new VectorTable("none", Dim));

        using var holding = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Task blocker = Task.Run(() => _fx.Db.WriteAsync((c, tx) => { holding.Set(); release.Wait(TimeSpan.FromSeconds(30), Ct); }, Ct), Ct);
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10), Ct));
        Task ensure = triple.EnsureAsync(Ct);
        var sw = Stopwatch.StartNew();
        while (triple.State != FtsIndexState.Rebuilding && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5, Ct);
        Assert.Equal(FtsIndexState.Rebuilding, triple.State);

        SearchResponse during = await service.SearchAsync(new SearchQuery("セリフ"), Ct);
        PathOutcome o = Assert.Single(during.Paths);
        Assert.Equal(PathState.NotReady, o.State);
        Assert.Contains("作り直し", o.Message, StringComparison.Ordinal);
        Assert.Empty(during.Results);

        release.Set();
        await blocker;
        await ensure;
        SearchResponse after = await service.SearchAsync(new SearchQuery("セリフ"), Ct);
        Assert.Equal(PathState.Ok, Assert.Single(after.Paths).State);
        Assert.Equal([Hash(1)], Hashes(after));
    }

    [Fact]
    public async Task A_failed_index_makes_the_path_fail_with_the_reason_and_the_visual_path_still_answers()
    {
        string model = await SetUpImagesAsync(1);
        await Save(1, "セリフ");
        await _fx.TamperSpecAsync();                                 // 規約が違う → 作り直しが要る
        var broken = new FtsIndex(_fx.Db, _fx.Log)
        {
            BuildDocument = (_, _, _, _) => throw new System.IO.IOException("作り直しに失敗(テスト)"),
        };
        await Assert.ThrowsAsync<System.IO.IOException>(() => broken.EnsureAsync(Ct));
        await AddVectorAsync(_fx.Db, model, Hash(1), Unit(10));
        var table = new VectorTable(model, Dim);
        await table.LoadAsync(_fx.Db, Ct);
        var service = new SearchService(_fx.Db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null), new FullTextPath(_fx.Db, broken)], table);

        SearchResponse r = await service.SearchAsync(new SearchQuery("セリフ"), Ct);

        PathOutcome ft = r.Paths.Single(p => p.PathId == "fulltext");
        Assert.Equal(PathState.Failed, ft.State);
        Assert.Contains("作り直しに失敗(テスト)", ft.Message, StringComparison.Ordinal);
        Assert.Equal(PathState.Ok, r.Paths.Single(p => p.PathId == "visual").State);
        Assert.Single(r.Results);
    }

    // ---------------------------------------------------------------- 検索できない語(Skipped と Message)

    [Fact]
    public async Task Terms_that_cannot_be_searched_are_dropped_and_named_in_the_message_while_the_rest_is_searched()
    {
        await SetUpImagesAsync(2);
        await Save(1, "猫がいる");
        await Save(2, "犬がいる");
        await _fx.Index.EnsureAsync(Ct);

        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery("猫 。 ！？"), Ct);

        PathOutcome o = Assert.Single(r.Paths);
        Assert.Equal(PathState.Ok, o.State);                                    // 語が除かれても、状態は Ok
        Assert.Equal("検索できない語を除いて検索した: 「。」、「！？」", o.Message);
        Assert.Equal([Hash(1)], Hashes(r));
    }

    [Fact]
    public async Task When_no_term_can_be_searched_the_path_is_skipped_with_the_words_and_the_visual_path_still_answers()
    {
        string model = await SetUpImagesAsync(2);
        await Save(1, "猫");
        await _fx.Index.EnsureAsync(Ct);
        (SearchService service, _) = await FusedServiceAsync(model, (1, 10), (2, 20));

        SearchResponse r = await service.SearchAsync(new SearchQuery("。 ！"), Ct);

        PathOutcome ft = r.Paths.Single(p => p.PathId == "fulltext");
        Assert.Equal(PathState.Skipped, ft.State);
        Assert.Contains("「。」", ft.Message, StringComparison.Ordinal);
        Assert.Contains("「！」", ft.Message, StringComparison.Ordinal);
        Assert.Equal(0, ft.Population);
        Assert.Equal(PathState.Ok, r.Paths.Single(p => p.PathId == "visual").State);
        Assert.Equal(2, r.Results.Count);
    }

    [Fact]
    public async Task A_query_with_a_control_character_fails_the_full_text_path_only()
    {
        string model = await SetUpImagesAsync(1);
        await Save(1, "猫");
        await _fx.Index.EnsureAsync(Ct);
        (SearchService service, _) = await FusedServiceAsync(model, (1, 10));

        SearchResponse r = await service.SearchAsync(new SearchQuery("a\u0001b"), Ct);

        PathOutcome ft = r.Paths.Single(p => p.PathId == "fulltext");
        Assert.Equal(PathState.Failed, ft.State);                              // 入力の誤りは、握りつぶさず経路の失敗にする
        Assert.Contains("制御文字", ft.Message, StringComparison.Ordinal);
        Assert.Equal(PathState.Ok, r.Paths.Single(p => p.PathId == "visual").State);
    }

    // ---------------------------------------------------------------- TextDetail

    private async Task<TextDetail> DetailOfFirst(string query, SearchService? service = null)
    {
        SearchResponse r = await (service ?? FullTextOnly()).SearchAsync(new SearchQuery(query), Ct);
        PathHit hit = Assert.Single(r.Results[0].Hits);
        return Assert.IsType<TextDetail>(hit.Detail);
    }

    [Fact]
    public async Task Text_detail_has_the_three_fields_in_order_with_the_match_positions_on_the_original_text()
    {
        await SetUpImagesAsync(1);
        await Save(1, "ガチャ10連を引く", "ＨＰ回復の画面", ["銀髪", "ロングヘア"]);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("ガチャ hp 銀髪");

        Assert.Equal([TextField.Ocr, TextField.Description, TextField.Tags, TextField.ManualTags], d.Matches.Select(m => m.Field).ToArray());
        Assert.Equal("ガチャ10連を引く", d.Matches[0].Text);
        Assert.Equal([new TextSpan(0, 3)], d.Matches[0].Spans);              // ガチャ
        Assert.Equal("ＨＰ回復の画面", d.Matches[1].Text);                    // 全角のまま(元のテキスト)
        Assert.Equal([new TextSpan(0, 2)], d.Matches[1].Spans);              // ｈｐ ← 「hp」。全角の 2 文字
        Assert.Equal("銀髪、ロングヘア", d.Matches[2].Text);                  // tags は「、」でつないだ文字列
        Assert.Equal([new TextSpan(0, 2)], d.Matches[2].Spans);              // 銀髪
    }

    [Fact]
    public async Task Text_detail_spans_are_correct_with_half_width_kana_newlines_and_a_one_character_term()
    {
        await SetUpImagesAsync(1);
        await Save(1, "ｶﾞﾁｬの結果\nこの帽子に\nもポップコーン", "森の中", ["森"]);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("ガチャ 帽子にも 森");

        Assert.Equal([new TextSpan(0, 4), new TextSpan(10, 5)], d.Matches[0].Spans);   // ｶﾞﾁｬ(4 文字)と、改行をまたぐ「帽子に\nも」(5 文字)
        Assert.Equal("帽子に\nも", d.Matches[0].Text.Substring(10, 5));
        Assert.Equal([new TextSpan(0, 1)], d.Matches[1].Spans);                       // 1 文字の語「森」
        Assert.Equal([new TextSpan(0, 1)], d.Matches[2].Spans);
    }

    [Fact]
    public async Task Text_detail_positions_stay_correct_for_text_with_control_characters_and_the_text_is_the_sanitized_one()
    {
        await SetUpImagesAsync(1);
        await Save(1, "あ\u0001ガチャ\u0000でした", "説明\u0007です", ["タグ\u001F銀髪"]);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOf("ガチャ 説明 銀髪");

        Assert.Equal("あ\uFFFDガチャ\uFFFDでした", d.Matches[0].Text);              // 置換済み(長さは同じ)
        Assert.Equal([new TextSpan(2, 3)], d.Matches[0].Spans);                   // 「ガチャ」の位置が、元の(置換前の)テキスト上の位置と同じ
        Assert.Equal("あ\u0001ガチャ\u0000でした".IndexOf("ガチャ", StringComparison.Ordinal), d.Matches[0].Spans[0].Start);
        Assert.Equal("説明\uFFFDです", d.Matches[1].Text);
        Assert.Equal([new TextSpan(0, 2)], d.Matches[1].Spans);
        Assert.Equal("タグ\uFFFD銀髪", d.Matches[2].Text);
        Assert.Equal([new TextSpan(3, 2)], d.Matches[2].Spans);

        async Task<TextDetail> DetailOf(string q) => await DetailOfFirst(q);
    }

    [Fact]
    public async Task Text_detail_uses_the_newest_caption_which_is_the_one_the_index_was_made_from()
    {
        await SetUpImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "モデルAの古い文字", "", [], 1);
        await _fx.SaveAsync(1, _fx.ModelB, "モデルBの新しい文字", "", [], 2);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("新しい");

        Assert.Equal("モデルBの新しい文字", d.Matches[0].Text);
        Assert.Equal([new TextSpan(5, 3)], d.Matches[0].Spans);
    }

    [Fact]
    public async Task Text_detail_for_a_term_that_matched_only_in_another_field_has_no_spans_in_the_others()
    {
        await SetUpImagesAsync(1);
        await Save(1, "セリフ", "説明文", ["タグ"]);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("説明文");

        Assert.Empty(d.Matches[0].Spans);
        Assert.Equal([new TextSpan(0, 3)], d.Matches[1].Spans);
        Assert.Empty(d.Matches[2].Spans);
        Assert.Equal("セリフ", d.Matches[0].Text);                                // 一致が無い欄も、文字列は返す
    }

    // ---------------------------------------------------------------- SearchService の継ぎ目(PrepareDetails)

    [Fact]
    public async Task Prepare_details_is_called_once_per_path_after_the_top_is_decided_and_before_any_detail_with_only_the_top_images()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            long folder = await AddFolderAsync(db);
            for (int i = 1; i <= 5; i++) await AddImageAsync(db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
            string[] hashes = Enumerable.Range(1, 5).Select(Hash).ToArray();
            double[] scores = [5, 4, 3, 2, 1];                                   // 先頭の 2 つが上位
            var events = new List<string>();
            var prepared = new List<int[]>();
            var path = new FakePath("probe", null, () => PathSearchResult.Ok(
                hashes, scores,
                detail: i => { events.Add("detail:" + i); return new VisualDetail(scores[i], scores[i]); },
                prepare: indices => { events.Add("prepare"); prepared.Add(indices.ToArray()); }));
            var service = new SearchService(db, [path], new VectorTable("none", Dim));

            SearchResponse r = await service.SearchAsync(new SearchQuery("q", Limit: 2), Ct);

            Assert.Equal(2, r.Results.Count);
            Assert.Equal(["prepare", "detail:0", "detail:1"], events);            // 1 回だけ、DetailOf の前
            Assert.Equal([0, 1], Assert.Single(prepared));                        // 結果に載る画像の、その経路での位置だけ
        }
    }

    [Fact]
    public async Task Prepare_details_gets_each_paths_own_positions_when_the_paths_have_different_populations()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            long folder = await AddFolderAsync(db);
            for (int i = 1; i <= 4; i++) await AddImageAsync(db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
            var preparedA = new List<int[]>();
            var preparedB = new List<int[]>();
            // 経路 A: 画像 1〜4。経路 B: 画像 4 と 2 だけ(位置は 0 と 1)
            var a = new FakePath("a", null, () => PathSearchResult.Ok([Hash(1), Hash(2), Hash(3), Hash(4)], [4, 3, 2, 1], i => new VisualDetail(i, i), prepare: x => preparedA.Add(x.ToArray())));
            var b = new FakePath("b", null, () => PathSearchResult.Ok([Hash(4), Hash(2)], [9, 8], i => new VisualDetail(i, i), prepare: x => preparedB.Add(x.ToArray())));
            var service = new SearchService(db, [a, b], new VectorTable("none", Dim));

            SearchResponse r = await service.SearchAsync(new SearchQuery("q", Limit: 10), Ct);

            Assert.Equal(4, r.Results.Count);
            Assert.Equivalent(new[] { 0, 1, 2, 3 }, Assert.Single(preparedA));
            Assert.Equivalent(new[] { 0, 1 }, Assert.Single(preparedB));
        }
    }

    [Fact]
    public async Task Detail_of_before_prepare_details_is_a_loud_error()
    {
        await SetUpImagesAsync(1);
        await Save(1, "セリフ");
        await _fx.Index.EnsureAsync(Ct);
        PathSearchResult result = await new FullTextPath(_fx.Db, _fx.Index).SearchAsync("セリフ", Ct);
        Assert.Equal(PathState.Ok, result.State);
        Assert.Throws<InvalidOperationException>(() => result.DetailOf(0));
        result.PrepareDetails([0]);
        Assert.IsType<TextDetail>(result.DetailOf(0));
    }

    // 「索引に行がある画像の caption が無い」は、例外にしない。caption が無くても手動タグで索引に入る画像があり、
    // 手動タグは検索のあとで外されうる(正当な競合)ので、caption も手動タグも無い場合は空の詳細を返す。そのテストは Tags/ManualTagSearchTests。

    // ---------------------------------------------------------------- 量(300 件分の詳細の時間)

    [Fact]
    public async Task Three_hundred_results_with_details_are_built_from_one_batched_read_in_reasonable_time()
    {
        const int total = 400;
        await _fx.AddImagesAsync(total);
        long folder = await AddFolderAsync(_fx.Db);
        await _fx.Db.WriteAsync((c, tx) =>
        {
            for (int i = 1; i <= total; i++)
                TestDb.Exec(c, tx, $"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\shots\\{i}.png', '{Hash(i)}', {folder}, 0, 100)");
        }, Ct);
        for (int i = 1; i <= total; i++)
            await Save(i, "共通のセリフ " + new string('あ', i % 7 + 1) + "とても長い説明の代わりの文章がここに入る", "ガチャの結果が表示された画面の説明文です", ["銀髪", "ロングヘア", "地図画面"], minutes: i % 50);
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = FullTextOnly();

        var sw = Stopwatch.StartNew();
        SearchResponse r = await service.SearchAsync(new SearchQuery("セリフ ガチャ", Limit: 300), Ct);
        sw.Stop();
        output.WriteLine($"結果 {r.Results.Count} 件(詳細つき)、検索全体 {sw.ElapsedMilliseconds}ms(Debug ビルド。合成データ)");

        Assert.Equal(300, r.Results.Count);
        Assert.All(r.Results, x =>
        {
            TextDetail d = Assert.IsType<TextDetail>(Assert.Single(x.Hits).Detail);
            Assert.Equal(4, d.Matches.Count);
            Assert.NotEmpty(d.Matches[0].Spans);
            Assert.NotEmpty(d.Matches[1].Spans);
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"300 件の詳細つきの検索が遅い: {sw.ElapsedMilliseconds}ms");
    }

    // ---------------------------------------------------------------- 寛容版の計画

    [Fact]
    public void The_lenient_plan_drops_terms_without_searchable_characters_and_keeps_the_order_of_the_rest()
    {
        LenientSearchPlan p = QueryPlanner.PlanSearchLenient("猫 。 犬 ！？ 鳥");
        Assert.Equal(["。", "！？"], p.Dropped);
        Assert.Equal(["猫", "犬", "鳥"], p.Plan!.Terms.Select(t => t.SingleChar).ToArray());
    }

    [Fact]
    public void The_lenient_plan_has_no_plan_when_every_term_is_dropped_and_still_rejects_control_characters_and_blank_queries()
    {
        LenientSearchPlan all = QueryPlanner.PlanSearchLenient("。 、 ！");
        Assert.Null(all.Plan);
        Assert.Equal(["。", "、", "！"], all.Dropped);
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearchLenient(""));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearchLenient("   "));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearchLenient("猫 a\u0001b"));      // 制御文字は語を除くのでなく、クエリ全体の誤り
        Assert.Throws<ArgumentNullException>(() => QueryPlanner.PlanSearchLenient(null!));
        LenientSearchPlan none = QueryPlanner.PlanSearchLenient("猫 犬");
        Assert.Empty(none.Dropped);
        Assert.Equal(QueryPlanner.PlanSearch("猫 犬").FtsMatch, none.Plan!.FtsMatch);                // 除く語が無ければ PlanSearch と同じ計画
    }
}
