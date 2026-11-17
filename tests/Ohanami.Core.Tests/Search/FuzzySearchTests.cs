using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Captions;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>
/// 全文検索の、OCR の誤読への fallback(ADR 0032): 正確な一致が索引に 1 件も無い語(日本語だけ・4 文字以上)だけを、編集距離 1 のあいまい一致で引く。
/// 実物の SQLite・FTS5 で、語ごとの fallback・並べ方(絞り込みだけ)・一致の範囲と理由の材料(TextDetail.FuzzyTerms)・区切りをまたがないこと・対象外の語を確かめる。
/// </summary>
public sealed class FuzzySearchTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly CaptionFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task SetUpImagesAsync(int count)
    {
        await _fx.AddImagesAsync(count);
        long folder = await AddFolderAsync(_fx.Db);
        for (int i = 1; i <= count; i++) await AddImageAsync(_fx.Db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
    }

    private Task Save(int image, string ocr, string description = "", string[]? tags = null, int minutes = 1) =>
        _fx.SaveAsync(image, _fx.ModelA, ocr, description, tags ?? [], minutes);

    private SearchService FullTextOnly() => new(_fx.Db, [new FullTextPath(_fx.Db, _fx.Index)], new VectorTable("none", Dim));

    private async Task<SearchResponse> Search(string query)
    {
        await _fx.Index.EnsureAsync(Ct);
        return await FullTextOnly().SearchAsync(new SearchQuery(query), Ct);
    }

    private static string[] Hashes(SearchResponse r) => r.Results.Select(x => x.Hash).ToArray();

    private static TextDetail Detail(SearchResult r) => Assert.IsType<TextDetail>(Assert.Single(r.Hits).Detail);

    private static IReadOnlyList<TextSpan> Spans(TextDetail d, TextField field) => d.Matches.Single(m => m.Field == field).Spans;

    // ---------------------------------------------------------------- 具体例と、働く条件

    [Fact]
    public async Task The_example_of_the_user_finds_the_misread_dialogue_through_the_separator_and_says_it_was_fuzzy()
    {
        await SetUpImagesAsync(3);
        await Save(1, "これは、とろんぶ、ねむ！という声");       // OCR は半濁点を濁点に読み違えた
        await Save(2, "関係のない文書");
        await Save(3, "とろんぷだけの文書");                     // 「とろんぷ、ねむ」は含まない(「とろんぷ」だけ)

        SearchResponse r = await Search("とろんぷ、ねむ！");

        Assert.Equal([Hash(1)], Hashes(r));
        TextDetail d = Detail(r.Results[0]);
        Assert.Equal(["とろんぷ、ねむ！"], d.FuzzyTerms);          // あいまい一致で当たった語(画面の「読み違いかもしれない」の材料)
        TextSpan span = Assert.Single(Spans(d, TextField.Ocr));
        Assert.Equal("とろんぶ、ねむ", d.Matches.Single(m => m.Field == TextField.Ocr).Text.Substring(span.Start, span.Length));   // 一致の範囲は元の位置(「！」は含まない)
        Assert.Empty(Spans(d, TextField.Description));
    }

    [Fact]
    public async Task A_term_with_an_exact_match_anywhere_is_never_fuzzy_so_misreads_are_not_added_to_it()
    {
        await SetUpImagesAsync(2);
        await Save(1, "とろんぶの話");                            // 誤読
        await Save(2, "とろんぷの話");                            // 正確な一致
        SearchResponse r = await Search("とろんぷ");

        Assert.Equal([Hash(2)], Hashes(r));                        // 誤読の画像は出ない(語ごとの fallback。正確な一致がある語には何も起きない)
        Assert.Empty(Detail(r.Results[0]).FuzzyTerms);
    }

    [Fact]
    public async Task The_switch_is_decided_by_the_whole_index_so_a_later_exact_image_removes_the_fuzzy_hit()
    {
        await SetUpImagesAsync(2);
        await Save(1, "とろんぶの話");
        Assert.Equal([Hash(1)], Hashes(await Search("とろんぷ")));  // 今日は、あいまい一致で見つかる

        await Save(2, "とろんぷの話");                              // 正確に含む画像が入る
        Assert.Equal([Hash(2)], Hashes(await Search("とろんぷ")));  // 翌日からは出ない(設計 4 節の既知の性質)
    }

    // ---------------------------------------------------------------- 対象外

    [Fact]
    public async Task Words_of_three_characters_and_words_with_alphanumerics_are_never_fuzzy()
    {
        await SetUpImagesAsync(2);
        await Save(1, "なかもと stanp の話");
        await Save(2, "無関係");

        Assert.Empty((await Search("なかま")).Results);             // 3 文字
        Assert.Empty((await Search("stamp")).Results);              // 英数字の語
        Assert.Empty((await Search("ガチャ10連")).Results);          // 日本語に英数字が混ざる語
    }

    [Fact]
    public async Task A_separator_in_the_document_is_not_crossed_by_a_fuzzy_match()
    {
        await SetUpImagesAsync(2);
        await Save(1, "銀髪、ロング");                              // 区切りをまたぐ(「銀髪ロング」に余分 1 つで当たってはいけない)
        await Save(2, "銀髪のロング");                               // 「銀髪ロング」に余分 1 つで当たる(区切りではなく文字)
        SearchResponse r = await Search("銀髪ロング");

        Assert.Equal([Hash(2)], Hashes(r));
    }

    [Fact]
    public async Task A_fuzzy_term_filters_a_phrase_result_without_changing_its_bm25_order()
    {
        await SetUpImagesAsync(4);
        await Save(1, "洞窟の森。とろんぶ");
        await Save(2, "洞窟 洞窟 洞窟 の中で森。とろんぶ");           // 「洞窟」が多い → BM25 で先
        await Save(3, "洞窟だけ。とろんぷす");                         // 最初は「とろんぷ」を正確に含む(下で、誤読の形に直す)
        await Save(4, "洞窟のみ。何もない");

        // クエリ「洞窟 とろんぷ」の「とろんぷ」は文書 3 に正確に含まれる → あいまい一致にならない(文書 1・2 の誤読は出ない)
        Assert.Equal([Hash(3)], Hashes(await Search("洞窟 とろんぷ")));

        // 文書 3 を、正確に含まない形に直す(誤読「とろんぶ」)。「とろんぷ」は正確な一致が 0 件になり、あいまい一致で 1・2・3 が残る。順位は「洞窟」の BM25
        await Save(3, "洞窟だけ。とろんぶす", minutes: 2);
        SearchResponse r = await Search("洞窟 とろんぷ");
        Assert.Equivalent(new[] { Hash(1), Hash(2), Hash(3) }, Hashes(r));
        SearchResponse plain = await Search("洞窟");
        Assert.Equal(Hashes(plain).Where(h => h != Hash(4)).ToArray(), Hashes(r));   // 順位は「洞窟」だけの BM25 と同じ並び(あいまい一致の語は順位に効かない)
        Assert.All(r.Results, x => Assert.Equal(["とろんぷ"], Detail(x).FuzzyTerms));
    }

    [Fact]
    public async Task Only_fuzzy_terms_tie_in_rank_and_a_single_character_term_ranks_by_occurrences()
    {
        await SetUpImagesAsync(4);
        await Save(1, "ぷろぐらむ、ぷろぐらむ");
        await Save(2, "ほろぐらむ");                                  // 先頭と末尾の 2 か所が違う(「ぷろぐらん」とは距離 2)
        await Save(3, "ぷろぐらむの森森森");                          // 「森」が 3 つ
        await Save(4, "ぷろぐらむの森");                              // 「森」が 1 つ

        // 正確な一致がある語(1・3・4)は、あいまい一致にならない
        Assert.Equivalent(new[] { Hash(1), Hash(3), Hash(4) }, Hashes(await Search("ぷろぐらむ")));

        // 「ぷろぐらん」はどの文書にも正確には無い → あいまい一致(1・3・4 が「ぷろぐらむ」と 1 文字違い。2 は 2 文字違い)
        SearchResponse r = await Search("ぷろぐらん");
        Assert.Equal(3, r.Results.Count);
        Assert.DoesNotContain(Hash(2), Hashes(r));
        Assert.All(r.Results, x => Assert.Equal(2.0, x.Rank));                                    // 順位に効く語が無いので全部同点(3 件の平均順位 = 2)
        Assert.Equal(Hashes(r).OrderBy(h => h, StringComparer.Ordinal).ToArray(), Hashes(r));     // 並びは hash の順

        // 1 文字の語があれば、その出現回数の合計で並ぶ(あいまい一致の語は絞り込みだけ)
        SearchResponse withSingle = await Search("ぷろぐらん 森");
        Assert.Equal([Hash(3), Hash(4)], Hashes(withSingle));          // 森 3 回 > 森 1 回
        Assert.Equal([1.0, 2.0], withSingle.Results.Select(x => x.Rank).ToArray());
    }

    // ---------------------------------------------------------------- 一致の範囲・欄

    [Fact]
    public async Task The_fuzzy_range_is_found_in_each_field_and_the_detail_says_which_words_were_fuzzy()
    {
        await SetUpImagesAsync(1);
        await Save(1, "ここにとろんぶ", "説明にもとろんぶがある", ["とろんぶ"]);
        SearchResponse r = await Search("とろんぷ");

        TextDetail d = Detail(Assert.Single(r.Results));
        Assert.Equal(["とろんぷ"], d.FuzzyTerms);
        TextSpan ocr = Assert.Single(Spans(d, TextField.Ocr));
        Assert.Equal((3, 4), (ocr.Start, ocr.Length));
        Assert.Single(Spans(d, TextField.Description));
        Assert.Single(Spans(d, TextField.Tags));
    }

    // ---------------------------------------------------------------- 計画

    [Fact]
    public void A_fuzzy_term_is_a_filter_only_in_the_plan()
    {
        QueryPlan fuzzy = QueryPlanner.Plan("とろんぷ").ToFuzzy(FuzzyMatcher.TryPlan("とろんぷ")!);
        var onlyFuzzy = new SearchPlan([fuzzy]);
        Assert.Null(onlyFuzzy.FtsMatch);                                // MATCH には入らない
        Assert.True(onlyFuzzy.RankedByOccurrences);                      // 順位に効く語が無い
        Assert.Single(onlyFuzzy.Fuzzies);

        var withPhrase = new SearchPlan([QueryPlanner.Plan("洞窟"), fuzzy]);
        Assert.Equal(QueryPlanner.Plan("洞窟").FtsMatch, withPhrase.FtsMatch);   // フレーズの語だけ(置き換えた語は入らない)
        Assert.False(withPhrase.RankedByOccurrences);                    // フレーズがあれば BM25

        Assert.True(new SearchPlan([QueryPlanner.Plan("森"), fuzzy]).RankedByOccurrences);
        Assert.Throws<InvalidOperationException>(() => fuzzy.ToFuzzy(FuzzyMatcher.TryPlan("とろんぷ")!));   // 置き換えは Phrase の語だけ
        // 基準の実装(DB に依存しない Matches)も同じ意味
        Assert.True(fuzzy.Matches(NormalizedText.Create("とろんぶ")));
        Assert.False(fuzzy.Matches(NormalizedText.Create("すだんぶ")));
    }

    [Fact]
    public void Words_the_planner_wrote_keep_their_source_for_the_reason_display()
    {
        Assert.Equal("とろんぷ、ねむ！", QueryPlanner.Plan("とろんぷ、ねむ！").Word);
    }
}
