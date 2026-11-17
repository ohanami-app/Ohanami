using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// あいまい一致の走査器(編集距離 1 の部分一致。ADR 0032): 置き換え・抜け・余分・濁点の違い、対象にする語の条件(日本語だけ・4 文字以上)、
/// 区切りの規則(連続は 1 つ・端は落とす・境界は境界とだけ一致・日本語の間の空白は無視)、一致の範囲、総当たりの答え(オラクル)との一致、並列と直列の同一。
/// </summary>
public sealed class FuzzyMatcherTests
{
    private static string Norm(string s) => NormalizedText.Create(s).Text;

    /// <summary>語 <paramref name="query"/> が文書 <paramref name="doc"/> にあいまい一致(編集距離 1 以内)するか。対象にできない語は例外にする(テストの書き間違いを見逃さない)。</summary>
    private static bool Fuzzy(string query, string doc) =>
        new FuzzyMatcher.Scanner(FuzzyMatcher.TryPlan(query) ?? throw new InvalidOperationException($"対象にできない語: {query}")).Matches(Norm(doc));

    // ---------------------------------------------------------------- 型: 置き換え・抜け・余分

    [Fact]
    public void A_substitution_a_missing_character_and_an_extra_character_each_match_at_distance_one()
    {
        Assert.True(Fuzzy("とろんぷ", "これはとろんぶです"));         // 半濁点が濁点に(置き換え 1 つ)
        Assert.True(Fuzzy("てすちんぐ", "ですちんぐ"));               // 濁点が余分に付いた(置き換え 1 つの例)
        Assert.True(Fuzzy("ノルテリア", "ノルリアの花"));             // 1 文字の抜け(文書に無い)
        Assert.True(Fuzzy("とろんぷ", "とろんんぷ"));                 // 1 文字の余分(文書に多い)
        Assert.True(Fuzzy("とろんぷ", "これはとろんぷです"));         // 距離 0(正確な一致)も含む(ふつうは正確な一致が 0 件の語にだけ使う)
    }

    [Fact]
    public void Two_edits_do_not_match_and_edits_at_the_ends_of_the_word_count_the_same()
    {
        Assert.False(Fuzzy("とろんぷ", "とごんぶ"));                   // 置き換え 2 つ
        Assert.True(Fuzzy("とろんぷ", "とんぷ"));                      // 「ろ」の抜け 1 つ
        Assert.False(Fuzzy("とろんぷ", "とぷ"));                       // 2 文字の抜け
        Assert.True(Fuzzy("ぷろぐらむ", "ろぐらむ"));                   // 語の先頭の 1 文字の抜け
        Assert.True(Fuzzy("ぷろぐらむ", "ぷろぐら"));                   // 語の末尾の 1 文字の抜け
        Assert.True(Fuzzy("ぷろぐらむ", "ほろぐらむ"));                 // 先頭の置き換え
        Assert.True(Fuzzy("ぷろぐらむ", "ぷろぐらん"));                 // 末尾の置き換え
        Assert.True(Fuzzy("ぷろぐらむ", "ぷぐらむ"));                   // 1 文字の抜け(中ほど)
        Assert.False(Fuzzy("ぷろぐらむ", "ぷらむ"));                    // 2 文字が抜けている
    }

    [Fact]
    public void Nothing_matches_a_document_without_japanese_text_or_an_empty_one()
    {
        Assert.False(Fuzzy("とろんぷ", ""));
        Assert.False(Fuzzy("とろんぷ", "tromp 12345"));
        Assert.False(Fuzzy("とろんぷ", "、。！"));
    }

    // ---------------------------------------------------------------- 対象にする語

    [Fact]
    public void Only_japanese_words_of_four_or_more_characters_are_planned()
    {
        Assert.Null(FuzzyMatcher.TryPlan("なかま"));              // 3 文字: 残りが 2 文字で、当たる画像が多すぎる
        Assert.NotNull(FuzzyMatcher.TryPlan("なかまた"));          // 4 文字
        Assert.Null(FuzzyMatcher.TryPlan("tromp"));               // 英数字の語
        Assert.Null(FuzzyMatcher.TryPlan("ガチャ10連"));           // 日本語に英数字が混ざる語も対象にしない(日本語だけ)
        Assert.Null(FuzzyMatcher.TryPlan("あ、い、う"));           // 境界は文字数に数えない(3 文字)
        Assert.NotNull(FuzzyMatcher.TryPlan("あい、うえ"));        // 4 文字 + 境界 1 つ
        Assert.Null(FuzzyMatcher.TryPlan("。。。"));               // 文字が無い
        Assert.NotNull(FuzzyMatcher.TryPlan("ﾄﾛﾝﾌﾟ"));            // 半角カナは NFKC で全角になってから数える
    }

    [Fact]
    public void The_planned_word_drops_boundaries_at_its_ends_and_collapses_runs_of_separators()
    {
        int B = FuzzyMatcher.Boundary;
        int[] expected = [.. "とろんぷ".EnumerateRunes().Select(r => r.Value), B, .. "ねむ".EnumerateRunes().Select(r => r.Value)];
        Assert.Equal(expected, FuzzyMatcher.TryPlan("とろんぷ、ねむ")!.Items);
        Assert.Equal(expected, FuzzyMatcher.TryPlan("とろんぷ、ねむ！")!.Items);          // 端の境界は落とす(正確な一致と同じ)
        Assert.Equal(expected, FuzzyMatcher.TryPlan("「とろんぷ、、。 ねむ」")!.Items);   // 連続する区切りは 1 つの境界
        Assert.Equal("とろんぷ、ねむ！", FuzzyMatcher.TryPlan("とろんぷ、ねむ！")!.Source);   // 理由の表示のために、書かれた語を持つ
    }

    // ---------------------------------------------------------------- 区切りの規則

    [Fact]
    public void A_separator_in_the_document_is_not_matched_by_an_extra_missing_or_replaced_one()
    {
        Assert.False(Fuzzy("銀髪ロング", "銀髪、ロング"));             // 余分な境界(区切りをまたぐ)。素の文字列なら余分 1 つで当たってしまう例
        Assert.False(Fuzzy("銀髪、ロング", "銀髪ロング"));             // 境界の抜け
        Assert.False(Fuzzy("とろんぷ、ねむ", "とろんぷのねむ"));       // 境界を文字で置き換えた
        Assert.True(Fuzzy("銀髪、ロング", "銀髪。ロング"));            // 境界どうしは、区切りの種類が違っても一致する
        Assert.True(Fuzzy("銀髪、ロング", "銀髪 / ロング"));           // 連続する区切り(空白と記号)は 1 つの境界
        Assert.True(Fuzzy("とろんぷ、、ねむ", "とろんぶ。 。ねむ"));   // クエリ側の連続も 1 つの境界
    }

    [Fact]
    public void The_example_of_the_user_matches_with_the_separator_inside_the_word()
    {
        Assert.True(Fuzzy("とろんぷ、ねむ！", "とろんぶ、ねむ！"));    // 文書「とろんぶ、ねむ！」にクエリ「とろんぷ、ねむ！」(設計の具体例)
        Assert.True(Fuzzy("とろんぷ、ねむ！", "こんにちは。とろんぶ、ねむ！またね"));
        Assert.False(Fuzzy("とろんぷ、ねむ！", "とろんぶ、ねも！"));    // 距離 2
    }

    [Fact]
    public void A_boundary_never_takes_part_in_an_edit_and_edits_next_to_a_boundary_still_work()
    {
        Assert.False(Fuzzy("とろ、んぷ", "とろ"));                      // 境界と 2 文字の抜けは要る(境界は落とせない)
        Assert.False(Fuzzy("とろんぷ、ねむ", "とろんぷ"));              // 境界の抜けと 2 文字の抜け
        Assert.True(Fuzzy("とろんぷ、ねむ", "とろんぷ、む"));          // 境界の隣の文字の抜けは許す
        Assert.True(Fuzzy("とろんぷ、ねむ", "とろんぷ、あねむ"));      // 境界の隣の文字の余分も許す
        Assert.True(Fuzzy("とろんぷ、ねむ", "とろんぷ、ほむ"));        // 境界の隣の文字の置き換えも許す
    }

    [Fact]
    public void Whitespace_between_japanese_characters_is_ignored_and_a_word_of_the_document_acts_as_a_boundary()
    {
        Assert.True(Fuzzy("とろんぷ", "とろ んぶ"));                    // 折り返しの空白・改行は無かったことにする(索引と同じ)
        Assert.True(Fuzzy("とろんぷ", "とろ\nんぶ"));
        Assert.True(Fuzzy("とろんぷ", "とろ　　んぶ"));                  // 全角空白
        Assert.True(Fuzzy("銀髪、ロング", "銀髪abロング"));             // 英数字の単語は、日本語の連続を切る(正確な一致でも同じ)
        Assert.False(Fuzzy("銀髪ロング", "銀髪abロング"));
        Assert.False(Fuzzy("とろんぷ", "とろ、んぶ"));                  // 区切りは空白と違い、日本語の連続を切る
    }

    // ---------------------------------------------------------------- 一致の範囲(元の位置)

    private static SearchPlan PlanOf(string word) =>
        new([QueryPlanner.Plan(word).ToFuzzy(FuzzyMatcher.TryPlan(word)!)]);

    [Fact]
    public void The_matched_range_is_given_in_the_original_text()
    {
        // 「これはとろんぶ、ねむ！です」: と(3)〜む(9)。「！」は範囲に入らない
        IReadOnlyList<TextRange> ranges = Highlighter.Find("これはとろんぶ、ねむ！です", PlanOf("とろんぷ、ねむ"));
        Assert.Equal([new TextRange(3, 7)], ranges);
    }

    [Fact]
    public void The_matched_range_goes_back_through_normalization_and_ignored_whitespace()
    {
        // 半角カナ(濁点つきは 2 コードユニット)。NFKC で全角になった位置を、元の位置に戻す
        Assert.Equal([new TextRange(0, 8)], Highlighter.Find("ﾄﾛﾝﾌﾞ、ﾈﾑ！", PlanOf("トロンプ、ネム")));
        // 日本語の間の空白は、範囲の中に入る(元の文字列の上の範囲なので)
        Assert.Equal([new TextRange(1, 5)], Highlighter.Find("「とろ んぶ」", PlanOf("とろんぷ")));
    }

    [Fact]
    public void A_document_without_a_match_has_no_range_and_several_matches_are_all_found()
    {
        Assert.Empty(Highlighter.Find("関係のない文書", PlanOf("とろんぷ")));
        IReadOnlyList<TextRange> ranges = Highlighter.Find("とろんぶ。ほげ。とろんぷす", PlanOf("とろんぷ"));
        Assert.Equal(2, ranges.Count);                                   // 1 つ目は置き換え、2 つ目は(正確な一致を含む)別の場所
        Assert.Equal(0, ranges[0].Start);
    }

    // ---------------------------------------------------------------- 総当たりの答え(オラクル)との一致

    /// <summary>
    /// 編集距離 1 以内の部分一致の定義どおりの答え: 語 q の「そのまま」「1 文字を落とす」「1 文字を別の文字にする」「1 文字を足す」のどれかが、文書の項目の連続として現れるか
    /// (境界は変えない・足さない・落とさない)。走査器の表の更新とは別の書き方で、同じ規則を書いたもの。
    /// </summary>
    private static bool Oracle(int[] q, List<int> doc, int[] alphabet)
    {
        var variants = new List<int[]> { q };
        for (int i = 0; i < q.Length; i++)
        {
            if (q[i] == FuzzyMatcher.Boundary) continue;
            variants.Add([.. q.Take(i), .. q.Skip(i + 1)]);                                         // 抜け
            foreach (int c in alphabet.Where(c => c != q[i])) variants.Add([.. q.Take(i), c, .. q.Skip(i + 1)]);   // 置き換え
        }
        for (int pos = 0; pos <= q.Length; pos++)
            foreach (int c in alphabet) variants.Add([.. q.Take(pos), c, .. q.Skip(pos)]);          // 余分
        return variants.Any(v => Contains(doc, v));
    }

    private static bool Contains(List<int> doc, int[] v)
    {
        for (int i = 0; i + v.Length <= doc.Count; i++)
        {
            bool all = true;
            for (int k = 0; k < v.Length; k++) if (doc[i + k] != v[k]) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    [Fact]
    public void The_scanner_agrees_with_a_brute_force_definition_on_many_random_words_and_documents()
    {
        var rng = new Random(20261110);
        const string Letters = "あいうえお";
        int[] alphabet = [.. Letters.EnumerateRunes().Select(r => r.Value)];
        string Doc(int length)
        {
            var chars = new List<char>();
            for (int i = 0; i < length; i++)
            {
                int roll = rng.Next(12);
                chars.Add(roll < 9 ? Letters[rng.Next(Letters.Length)] : roll == 9 ? '、' : roll == 10 ? ' ' : 'a');   // 区切り・空白・英数字(境界)を混ぜる
            }
            return new string(chars.ToArray());
        }

        int agreed = 0, positives = 0;
        var items = new List<int>();
        for (int trial = 0; trial < 3000; trial++)
        {
            string docText = Doc(rng.Next(8, 40));
            FuzzyMatcher.Items(Norm(docText), items, null, null, out _);
            // 語: 日本語 4〜7 文字で、内側に境界を 0〜2 個(両端は文字)
            int chars = rng.Next(4, 8);
            var q = new List<int>();
            for (int i = 0; i < chars; i++)
            {
                if (i > 0 && rng.Next(5) == 0) q.Add(FuzzyMatcher.Boundary);
                q.Add(alphabet[rng.Next(alphabet.Length)]);
            }
            var query = new FuzzyQuery([.. q], "q");
            bool actual = new FuzzyMatcher.Scanner(query).Matches(Norm(docText));
            bool expected = Oracle(query.Items, items, alphabet);
            Assert.True(actual == expected, $"文書「{docText}」、語 {string.Join(",", q)}: 走査器 {actual}、定義どおり {expected}");
            agreed++;
            if (expected) positives++;
        }
        Assert.Equal(3000, agreed);
        Assert.InRange(positives, 100, 2900);   // 当たりも外れも十分に含む(どちらかに偏った標本で一致を確かめたことにしない)
    }

    [Fact]
    public void An_exact_phrase_match_is_always_also_a_fuzzy_match()
    {
        var rng = new Random(7);
        const string Letters = "あいうえおかきくけこ";
        for (int trial = 0; trial < 500; trial++)
        {
            string doc = new(Enumerable.Range(0, rng.Next(10, 40)).Select(_ => rng.Next(8) == 0 ? '、' : Letters[rng.Next(Letters.Length)]).ToArray());
            int start = rng.Next(0, doc.Length - 4);
            string word = doc.Substring(start, rng.Next(4, Math.Min(8, doc.Length - start) + 1));
            if (FuzzyMatcher.TryPlan(word) is not { } fuzzy) continue;   // 文字が 4 つに満たない切り出しは対象外
            QueryPlan exact = QueryPlanner.Plan(word);
            if (exact.Matches(NormalizedText.Create(doc))) Assert.True(new FuzzyMatcher.Scanner(fuzzy).Matches(Norm(doc)), $"文書「{doc}」、語「{word}」");
        }
    }

    // ---------------------------------------------------------------- 並列(直列と同じ結果)

    [Fact]
    public void The_filter_gives_the_same_flags_in_the_same_order_whatever_the_thread_count()
    {
        var rng = new Random(11);
        const string Letters = "あいうえお";
        string[] docs = [.. Enumerable.Range(0, 1500).Select(_ => Norm(new string(Enumerable.Range(0, rng.Next(5, 60)).Select(_ => rng.Next(10) == 0 ? '、' : Letters[rng.Next(Letters.Length)]).ToArray())))];
        FuzzyQuery[] queries = [FuzzyMatcher.TryPlan("あいうえ")!, FuzzyMatcher.TryPlan("おえ、いあ")!];
        bool[] serial = FuzzyScan.Filter(docs, queries, 1, TestContext.Current.CancellationToken);
        Assert.Contains(true, serial);
        Assert.Contains(false, serial);
        foreach (int threads in new[] { 2, 3, 8 })
            Assert.Equal(serial, FuzzyScan.Filter(docs, queries, threads, TestContext.Current.CancellationToken));
        // 複数の語は AND
        bool[] first = FuzzyScan.Filter(docs, [queries[0]], 4, TestContext.Current.CancellationToken);
        bool[] second = FuzzyScan.Filter(docs, [queries[1]], 4, TestContext.Current.CancellationToken);
        Assert.Equal(first.Zip(second, (a, b) => a && b).ToArray(), serial);
    }

    [Fact]
    public void The_default_thread_limit_is_half_of_the_logical_cpus_and_at_least_one()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / 2), FuzzyScan.DefaultMaxThreads);
        Assert.True(FuzzyScan.DefaultMaxThreads >= 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => FuzzyScan.Filter(["あ"], [FuzzyMatcher.TryPlan("あいうえ")!], 0, TestContext.Current.CancellationToken));
    }
}
