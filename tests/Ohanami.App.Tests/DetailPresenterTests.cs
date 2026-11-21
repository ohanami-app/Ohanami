using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 詳細画面の内容: 一致の欄は検索の TextDetail の文字列と範囲をそのまま出す、強調の区間、状態・パス・寸法は ImageDetail の値、
/// 画像の記録が無いとき、経路は日本語の名前(内部の名前を出さない)、詳細画面のキーの判定。WPF の要素を作らない(純粋な関数)。
/// </summary>
public sealed class DetailPresenterTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("tokyo-test", TimeSpan.FromHours(9), "tokyo", "tokyo");
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 3, 4, 5, TimeSpan.Zero);

    private static ImageDetail Detail(
        CaptionDetail? caption = null, IReadOnlyList<string>? manual = null, IReadOnlyList<ImagePathDetail>? paths = null,
        IReadOnlyList<StageDetail>? stages = null, int? width = 1920, int? height = 1080, long size = 1234567) =>
        new(Hash, size, "png", width, height, T0, paths ?? [new ImagePathDetail(@"C:\shots\a.png", false, null, T0, size)],
            stages ?? [new StageDetail(Stage.Embed, StageState.Done, "m", null, 1, T0), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            caption, manual ?? []);

    private static TextDetail Text(string ocr, TextSpan[] ocrSpans, string description = "", TextSpan[]? descSpans = null, string tags = "", TextSpan[]? tagSpans = null, string manual = "", TextSpan[]? manualSpans = null) =>
        new([
            new FieldMatch(TextField.Ocr, ocr, ocrSpans),
            new FieldMatch(TextField.Description, description, descSpans ?? []),
            new FieldMatch(TextField.Tags, tags, tagSpans ?? []),
            new FieldMatch(TextField.ManualTags, manual, manualSpans ?? []),
        ]);

    private static SearchResult Result(PathDetail? fulltext = null, double rank = 2, params string[] paths)
    {
        var hits = new List<PathHit> { new("visual", "m", 3, 0.2, new VisualDetail(0.21, 0.21)) };
        if (fulltext is not null) hits.Add(new PathHit("fulltext", null, 1, 5.5, fulltext));
        return new SearchResult(Hash, paths.Length == 0 ? [@"C:\shots\a.png"] : paths, rank, 0.03, hits);
    }

    private static readonly PathOutcome[] BothOk = [new("visual", "m", 100, PathState.Ok, null), new("fulltext", null, 30, PathState.Ok, null)];

    private static DetailModel Build(SearchResult r, DetailLoad? load = null, PathOutcome[]? paths = null, TimeZoneInfo? zone = null) =>
        DetailPresenter.Build(r, paths ?? BothOk, load ?? DetailLoad.Loaded(Detail()), zone ?? Utc);

    // ---------------------------------------------------------------- 強調の区間

    [Fact]
    public void Segments_split_the_text_at_the_spans_and_concatenate_back_to_the_original()
    {
        IReadOnlyList<TextSegment> s = DetailPresenter.Segments("ガチャ10連を引く", [new TextSpan(0, 3), new TextSpan(6, 1)]);

        Assert.Equal([("ガチャ", true), ("10連", false), ("を", true), ("引く", false)], s.Select(x => (x.Text, x.Highlight)).ToArray());
        Assert.Equal("ガチャ10連を引く", string.Concat(s.Select(x => x.Text)));
    }

    [Fact]
    public void Segments_handle_no_spans_adjacent_spans_the_whole_text_and_an_empty_text()
    {
        Assert.Equal([("abc", false)], DetailPresenter.Segments("abc", []).Select(x => (x.Text, x.Highlight)).ToArray());
        Assert.Equal([("ab", true), ("c", true)], DetailPresenter.Segments("abc", [new TextSpan(0, 2), new TextSpan(2, 1)]).Select(x => (x.Text, x.Highlight)).ToArray());   // 隣り合う範囲(別の語)は別の区間のまま
        Assert.Equal([("abc", true)], DetailPresenter.Segments("abc", [new TextSpan(0, 3)]).Select(x => (x.Text, x.Highlight)).ToArray());
        Assert.Empty(DetailPresenter.Segments("", []));
    }

    [Theory]
    [InlineData(0, 4)]      // 文字列の外へはみ出す
    [InlineData(-1, 1)]
    [InlineData(1, 0)]      // 長さ 0
    public void Spans_that_do_not_fit_the_text_are_refused_loudly(int start, int length)
    {
        Assert.Throws<ArgumentException>(() => DetailPresenter.Segments("abc", [new TextSpan(start, length)]));
    }

    [Fact]
    public void Overlapping_or_unordered_spans_are_refused_loudly()
    {
        Assert.Throws<ArgumentException>(() => DetailPresenter.Segments("abcdef", [new TextSpan(0, 3), new TextSpan(2, 2)]));
        Assert.Throws<ArgumentException>(() => DetailPresenter.Segments("abcdef", [new TextSpan(3, 1), new TextSpan(0, 1)]));
    }

    // ---------------------------------------------------------------- 一致の欄(TextDetail をそのまま)

    [Fact]
    public void The_text_fields_come_from_the_search_text_detail_exactly_and_never_from_the_image_caption()
    {
        // 検索のあとに VLM が caption を書き換えた場面: ImageDetail の caption は別の文字列。範囲は検索のときの文字列の上でしか正しくない
        var caption = new CaptionDetail("m", "書き換わったあとの文字", "別の説明", ["別タグ"], T0);
        SearchResult r = Result(Text("ガチャ10連を引く", [new TextSpan(0, 3)], "ＨＰ回復", [new TextSpan(0, 2)], "銀髪、ロングヘア", [new TextSpan(0, 2)], "お気に入り", [new TextSpan(0, 5)]));

        DetailModel m = Build(r, DetailLoad.Loaded(Detail(caption, ["お気に入り"])));

        Assert.Equal(["文字 (OCR)", "説明文", "VLM のタグ"], m.Fields.Select(f => f.Label).ToArray());          // 手動タグは、編集できる札として別に出す
        Assert.Equal("ガチャ10連を引く", string.Concat(m.Fields[0].Segments.Select(s => s.Text)));
        Assert.Equal(["ガチャ"], m.Fields[0].Segments.Where(s => s.Highlight).Select(s => s.Text).ToArray());
        Assert.Equal("ＨＰ回復", string.Concat(m.Fields[1].Segments.Select(s => s.Text)));           // 全角のまま(元のテキスト)
        Assert.Equal(["ＨＰ"], m.Fields[1].Segments.Where(s => s.Highlight).Select(s => s.Text).ToArray());
        Assert.Equal(["銀髪"], m.Fields[2].Segments.Where(s => s.Highlight).Select(s => s.Text).ToArray());
        Assert.Equal(["お気に入り"], m.ManualTags.Chips.Single().Segments.Where(s => s.Highlight).Select(s => s.Text).ToArray());     // 検索のときと同じタグなので、強調が付く
        Assert.All(m.Fields, f => Assert.DoesNotContain("書き換わった", string.Concat(f.Segments.Select(s => s.Text)), StringComparison.Ordinal));
        Assert.Null(m.FieldsNote);
    }

    [Fact]
    public void An_empty_field_is_marked_empty_and_a_field_without_a_match_has_no_highlight()
    {
        DetailModel m = Build(Result(Text("セリフ", [new TextSpan(0, 3)])));

        Assert.False(m.Fields[0].IsEmpty);
        Assert.True(m.Fields[0].HasMatch);
        Assert.True(m.Fields[1].IsEmpty);                       // 説明文が空
        Assert.False(m.Fields[1].HasMatch);
        Assert.Empty(m.Fields[1].Segments);
        Assert.Empty(m.ManualTags.Chips);                       // 手動タグが無い
    }

    // ---------------------------------------------------------------- 検索の結果でない詳細: 「全文検索で一致していない」と「(一致した箇所は強調)」を出さない

    [Fact]
    public void A_detail_opened_from_the_recent_list_or_the_failure_list_does_not_say_it_missed_the_text_search_nor_mention_highlighting()
    {
        var caption = new CaptionDetail("m", "文字", "説明", ["銀髪"], T0);
        SearchResult noHits = new(Hash, [@"C:\shots\a.png"], 1, 0, []);             // 検索の結果ではない一覧の 1 枚(経路ごとの情報は無い)
        DetailLoad loaded = DetailLoad.Loaded(Detail(caption));

        DetailModel recent = DetailPresenter.Build(noHits, [], loaded, Utc, fromSearch: false, fromRecentList: true);
        DetailModel standalone = DetailPresenter.Build(noHits, [], loaded, Utc, fromSearch: false);

        foreach (DetailModel m in new[] { recent, standalone })
        {
            Assert.Null(m.FieldsNote);                                                       // 「全文検索で一致していない」は出さない(検索していない)
            Assert.Equal(DetailPresenter.FieldsHeaderPlain, m.FieldsHeader);                 // 「(一致した箇所は強調)」も付けない(強調が無い)
            Assert.Equal("文字と説明", m.FieldsHeader);
            Assert.Equal(["文字", "説明", "銀髪"], m.Fields.Select(f => string.Concat(f.Segments.Select(s => s.Text))).ToArray());   // 欄そのものは出る
            Assert.All(m.Fields, f => Assert.False(f.HasMatch));
        }
    }

    [Fact]
    public void A_detail_from_the_recent_list_still_says_when_the_image_has_no_description_yet_because_that_is_not_about_search()
    {
        SearchResult noHits = new(Hash, [@"C:\shots\a.png"], 1, 0, []);

        DetailModel m = DetailPresenter.Build(noHits, [], DetailLoad.Loaded(Detail(caption: null)), Utc, fromSearch: false, fromRecentList: true);

        Assert.Equal("まだ説明文 (VLM の結果) がありません", m.FieldsNote);
        Assert.Equal(DetailPresenter.FieldsHeaderPlain, m.FieldsHeader);
    }

    [Fact]
    public void A_detail_opened_from_a_search_result_keeps_the_note_and_the_highlight_header_as_before()
    {
        var caption = new CaptionDetail("m", "文字", "説明", ["銀髪"], T0);

        DetailModel missed = Build(Result(fulltext: null), DetailLoad.Loaded(Detail(caption)));                         // 見た目だけで出た画像
        DetailModel matched = Build(Result(Text("セリフ", [new TextSpan(0, 3)])), DetailLoad.Loaded(Detail(caption)));   // 文字で当たった画像

        Assert.Equal(DetailPresenter.NotMatchedByTextNote, missed.FieldsNote);
        Assert.Equal(DetailPresenter.FieldsHeaderWithHighlight, missed.FieldsHeader);
        Assert.Null(matched.FieldsNote);
        Assert.Equal("文字と説明 (一致した箇所は強調)", matched.FieldsHeader);
    }

    [Fact]
    public void An_image_that_only_matched_visually_shows_the_current_caption_and_manual_tags_without_highlight()
    {
        var caption = new CaptionDetail("m", "文字", "説明", ["銀髪", "ロングヘア"], T0);

        DetailModel m = Build(Result(fulltext: null), DetailLoad.Loaded(Detail(caption, ["猫", "犬"])));

        Assert.Equal(["文字 (OCR)", "説明文", "VLM のタグ"], m.Fields.Select(f => f.Label).ToArray());
        Assert.Equal(["文字", "説明", "銀髪、ロングヘア"], m.Fields.Select(f => string.Concat(f.Segments.Select(s => s.Text))).ToArray());
        Assert.All(m.Fields, f => Assert.False(f.HasMatch));
        Assert.Equal(["猫", "犬"], m.ManualTags.Chips.Select(c => c.Name).ToArray());                // 手動タグは画像の詳細の値(強調なし)
        Assert.All(m.ManualTags.Chips, c => Assert.False(c.HasMatch));
        Assert.True(m.ManualTags.Editable);
        Assert.Contains($"{TestWords.TextSearch}で一致していません", m.FieldsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_without_a_caption_says_so_and_still_shows_its_manual_tags()
    {
        DetailModel m = Build(Result(fulltext: null), DetailLoad.Loaded(Detail(caption: null, manual: ["猫"])));

        Assert.True(m.Fields[0].IsEmpty);
        Assert.Equal(["猫"], m.ManualTags.Chips.Select(c => c.Name).ToArray());
        Assert.Contains("まだ説明文 (VLM の結果) がありません", m.FieldsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void While_loading_a_visual_only_result_has_no_text_fields_yet()
    {
        DetailModel m = Build(Result(fulltext: null), DetailLoad.Loading);
        Assert.Empty(m.Fields);
        Assert.Equal("読み込み中…", m.RecordNotice);
    }

    [Fact]
    public void While_loading_a_text_result_already_shows_the_matched_fields_from_the_search()
    {
        DetailModel m = Build(Result(Text("セリフ", [new TextSpan(0, 3)])), DetailLoad.Loading);
        Assert.Equal(3, m.Fields.Count);
        Assert.Equal("セリフ", string.Concat(m.Fields[0].Segments.Select(s => s.Text)));
    }

    // ---------------------------------------------------------------- 手動タグの札(ADR 0036)

    [Fact]
    public void Manual_tag_chips_come_from_the_loaded_detail_and_are_editable_only_then()
    {
        DetailModel loaded = Build(Result(fulltext: null), DetailLoad.Loaded(Detail(manual: ["猫", "犬"])));
        Assert.Equal(["猫", "犬"], loaded.ManualTags.Chips.Select(c => c.Name).ToArray());
        Assert.True(loaded.ManualTags.Editable);

        Assert.False(Build(Result(fulltext: null), DetailLoad.Loading).ManualTags.Editable);
        Assert.False(Build(Result(fulltext: null), DetailLoad.RecordMissing).ManualTags.Editable);          // 画像が無いので、編集できない
        Assert.False(Build(Result(fulltext: null), DetailLoad.Failed("x")).ManualTags.Editable);
        Assert.Empty(Build(Result(fulltext: null), DetailLoad.Loading).ManualTags.Chips);                  // 検索の結果に手動タグの欄が無ければ、読み込み中は空
    }

    [Fact]
    public void Before_the_detail_is_read_the_chips_come_from_the_search_field_read_only_and_with_the_highlight()
    {
        SearchResult r = Result(Text("x", [], manual: "あ、お気に入り", manualSpans: [new TextSpan(2, 5)]));

        ManualTagsModel loading = Build(r, DetailLoad.Loading).ManualTags;
        ManualTagsModel missing = Build(r, DetailLoad.RecordMissing).ManualTags;

        Assert.Equal(["あ", "お気に入り"], loading.Chips.Select(c => c.Name).ToArray());
        Assert.False(loading.Editable);
        Assert.Equal(["お気に入り"], loading.Chips[1].Segments.Where(s => s.Highlight).Select(s => s.Text).ToArray());
        Assert.False(loading.Chips[0].HasMatch);
        Assert.Equal(["あ", "お気に入り"], missing.Chips.Select(c => c.Name).ToArray());
        Assert.False(missing.Editable);
    }

    [Fact]
    public void The_highlight_of_a_chip_is_a_part_of_its_name_and_only_while_the_tags_are_the_same_as_when_searched()
    {
        // 検索のとき: 「お気に入り」の中の「気に」に一致した
        SearchResult r = Result(Text("x", [], manual: "あ、お気に入り", manualSpans: [new TextSpan(3, 2)]));

        ManualTagsModel same = Build(r, DetailLoad.Loaded(Detail(manual: ["あ", "お気に入り"]))).ManualTags;
        Assert.Equal([("お", false), ("気に", true), ("入り", false)], same.Chips[1].Segments.Select(s => (s.Text, s.Highlight)).ToArray());

        // タグを足した(検索のときの範囲は、いまのタグの並びと合わない)→ 強調しない(間違った場所を光らせない)
        ManualTagsModel edited = Build(r, DetailLoad.Loaded(Detail(manual: ["あ", "お気に入り", "新しい"]))).ManualTags;
        Assert.All(edited.Chips, c => { Assert.False(c.HasMatch); Assert.Equal(c.Name, string.Concat(c.Segments.Select(s => s.Text))); });
        ManualTagsModel removed = Build(r, DetailLoad.Loaded(Detail(manual: ["お気に入り"]))).ManualTags;
        Assert.All(removed.Chips, c => Assert.False(c.HasMatch));
    }

    // ---------------------------------------------------------------- 順位(日本語の経路名)

    [Fact]
    public void Rank_lines_use_japanese_path_names_show_the_similarity_in_the_text_and_never_internal_names()
    {
        DetailModel m = Build(Result(Text("x", []), rank: 23.5));

        Assert.Equal(["統合: 23.5 位", $"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)", $"{TestWords.TextSearch}: 1 位"], m.RankLines.ToArray());     // 類似度は本文に出す(ADR 0038)
        Assert.Null(m.RankTips[0]);
        Assert.Contains("割合ではありません", m.RankTips[1], StringComparison.Ordinal);                         // 見方は特徴ベクトル検索の行のツールチップ(割合ではない・同じ検索の中だけ・「1 に近いほど近い」とは書かない)
        Assert.Contains("同じ検索の中", m.RankTips[1], StringComparison.Ordinal);
        Assert.DoesNotContain("1 に近い", m.RankTips[1], StringComparison.Ordinal);
        Assert.Null(m.RankTips[2]);
        Assert.Equal(m.RankLines.Count, m.RankTips.Count);
        string all = string.Join("\n", m.RankLines);
        Assert.DoesNotContain("visual", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fulltext", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cosine", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_similarity_shown_is_the_score_that_decided_the_rank_and_the_tooltip_says_so_only_when_the_query_was_corrected()
    {
        // 補正をかけた検索: 順位を決めた点(補正後 0.05)を出し、元のコサイン(0.21)は出さない
        SearchResult corrected = new(Hash, [@"C:.png"], 2, 0.1, [new PathHit("visual", "m", 3, 0.05, new VisualDetail(0.05, 0.21))]);
        PathOutcome[] correctedPaths = [new("visual", "m", 100, PathState.Ok, null, ScoreCorrected: true)];
        (string Line, string? Tip) c = DetailPresenter.RankEntries(corrected, correctedPaths)[1];
        Assert.Equal($"{TestWords.VisualWithModel}: 3 位 (類似度 0.0500)", c.Line);
        Assert.DoesNotContain("0.2100", c.Line, StringComparison.Ordinal);
        Assert.StartsWith("コサイン類似度 (検索の言葉と、画像の見た目の近さ) から、どの言葉にも近い画像の分を引いた値です。", c.Tip, StringComparison.Ordinal);
        Assert.Contains("割合ではありません", c.Tip, StringComparison.Ordinal);
        Assert.Contains("同じ検索の中", c.Tip, StringComparison.Ordinal);

        // 補正をかけない検索: 引いたとは言わない
        SearchResult plain = new(Hash, [@"C:.png"], 2, 0.1, [new PathHit("visual", "m", 3, 0.21, new VisualDetail(0.21, 0.21))]);
        (string Line, string? Tip) p = DetailPresenter.RankEntries(plain, [new("visual", "m", 100, PathState.Ok, null)])[1];
        Assert.Equal($"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)", p.Line);
        Assert.DoesNotContain("どの言葉にも近い", p.Tip, StringComparison.Ordinal);
        Assert.Contains("割合ではありません", p.Tip, StringComparison.Ordinal);

        // どちらのツールチップにも、値の水準の目安は書かない(補正をかけた検索とかけない検索で、水準が違う)
        foreach (string? tip in new[] { c.Tip, p.Tip })
        {
            Assert.DoesNotContain("0.1 前後", tip, StringComparison.Ordinal);
            Assert.DoesNotContain("前後", tip, StringComparison.Ordinal);
            Assert.DoesNotContain("1 に近い", tip, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_path_without_a_hit_says_why_by_the_state_of_the_path()
    {
        PathOutcome[] paths =
        [
            new("visual", "m", 100, PathState.NotReady, "ベクトルを読み込み中"),
            new("fulltext", null, 0, PathState.Skipped, "検索できる語が無い"),
        ];
        SearchResult noHits = new(Hash, [@"C:\a.png"], 1, 0.1, []);

        string[] lines = DetailPresenter.RankLines(noHits, paths).ToArray();

        Assert.Equal(["統合: 1 位", $"{TestWords.VisualWithModel}: 準備中", $"{TestWords.TextSearch}: この検索では使いませんでした"], lines);
        Assert.Equal($"{TestWords.TextSearch}: 一致なし", DetailPresenter.RankLines(noHits, [new("fulltext", null, 3, PathState.Ok, null)])[1]);
        Assert.Equal($"{TestWords.VisualWithModel}: 失敗", DetailPresenter.RankLines(noHits, [new("visual", "m", 0, PathState.Failed, "x")])[1]);
        Assert.Equal($"{TestWords.VisualWithModel}: この画像のインデックスがまだありません", DetailPresenter.RankLines(noHits, [new("visual", "m", 100, PathState.Ok, null)])[1]);   // 経路ごとに言い分ける(全文検索は「一致なし」)
    }

    [Fact]
    public void A_hit_from_a_path_missing_in_the_outcome_list_is_still_shown()
    {
        string[] lines = DetailPresenter.RankLines(Result(Text("x", [])), []).ToArray();
        Assert.Equal(["統合: 2 位", $"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)", $"{TestWords.TextSearch}: 1 位"], lines);
    }

    [Fact]
    public void Integer_ranks_have_no_decimal_point()
    {
        Assert.Equal("統合: 3 位", DetailPresenter.RankLines(new SearchResult(Hash, [], 3, 0, []), [])[0]);
    }

    // ---------------------------------------------------------------- 状態・情報・パス

    [Fact]
    public void Stage_lines_show_each_state_with_the_time_in_the_given_zone_and_the_failure_reason()
    {
        StageDetail[] stages =
        [
            new(Stage.Embed, StageState.Done, "m", null, 1, T0),
            new(Stage.Ocr, StageState.Failed, "v", "finish_reason=length", 3, T0),
            new(Stage.Caption, StageState.Outdated, "old", null, 1, T0),
        ];

        string[] lines = Build(Result(), DetailLoad.Loaded(Detail(stages: stages)), zone: Tokyo).StageLines.ToArray();

        Assert.Equal("特徴ベクトル検索: 済 (2026-11-06 12:04)", lines[0]);                               // UTC 03:04 → +9 時間
        Assert.Equal("文字 (OCR): 失敗 (2026-11-06 12:04): finish_reason=length (試行 3 回)", lines[1]);
        Assert.Equal("説明文: 要再作成 (モデルが替わったため、作り直しが必要です) (2026-11-06 12:04)", lines[2]);
        Assert.Equal("特徴ベクトル検索: 未作成", DetailPresenter.StageLine(new StageDetail(Stage.Embed, StageState.Pending, null, null, 0, null), Utc));
    }

    [Fact]
    public void Info_lines_show_dimensions_size_format_and_first_seen_and_say_when_dimensions_are_not_known_yet()
    {
        string[] lines = DetailPresenter.InfoLines(Detail(width: 1920, height: 1080, size: 2_500_000), Utc).ToArray();
        Assert.Equal("寸法: 1920 × 1080", lines[0]);
        Assert.Equal("ファイルのサイズ: 2.38 MB (2,500,000 バイト)", lines[1]);
        Assert.Equal("形式: PNG", lines[2]);
        Assert.Equal("最初に見つけた日時: 2026-11-06 03:04", lines[3]);

        Assert.Equal("寸法: 未計測 (特徴ベクトル検索のインデックスを作るときに分かります)", DetailPresenter.InfoLines(Detail(width: null, height: null), Utc)[0]);
        Assert.Equal("512 バイト", DetailPresenter.Size(512));
        Assert.Equal("1.5 KB (1,536 バイト)", DetailPresenter.Size(1536));
    }

    [Fact]
    public void Path_lines_mark_missing_paths_with_the_time_and_keep_the_order_given()
    {
        ImagePathDetail[] paths =
        [
            new(@"C:\shots\a.png", false, null, T0, 1000),
            new(@"C:\old\a.png", true, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), T0, 1000),
        ];

        IReadOnlyList<DetailPathLine> lines = Build(Result(), DetailLoad.Loaded(Detail(paths: paths))).PathLines;

        Assert.Equal([@"C:\shots\a.png", @"C:\old\a.png"], lines.Select(l => l.Path).ToArray());
        Assert.Equal([false, true], lines.Select(l => l.Missing).ToArray());
        Assert.StartsWith("更新 2026-11-06 03:04", lines[0].Note, StringComparison.Ordinal);
        Assert.StartsWith("元のファイルがありません (2026-11-05 00:00 から)", lines[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_is_the_file_name_of_the_first_present_path()
    {
        ImagePathDetail[] paths = [new(@"C:\gone\z.png", true, T0, T0, 1), new(@"C:\shots\b.png", false, null, T0, 1)];
        Assert.Equal("b.png", Build(Result(), DetailLoad.Loaded(Detail(paths: paths))).Title);
        Assert.Equal("a.png", Build(Result(paths: [@"D:\x\a.png"]), DetailLoad.Loading).Title);                           // 読み込み中は検索の結果のパス
        Assert.Equal("(パスなし)", Build(new SearchResult(Hash, [], 1, 0, []), DetailLoad.Loading).Title);
    }

    // ---------------------------------------------------------------- 画像の記録が無いとき・読めなかったとき

    [Fact]
    public void When_the_image_record_is_missing_the_model_says_so_and_falls_back_to_the_search_result_without_failing()
    {
        SearchResult r = Result(Text("セリフ", [new TextSpan(0, 3)]), paths: [@"C:\shots\a.png", @"D:\copy\a.png"]);

        DetailModel m = Build(r, DetailLoad.RecordMissing);

        Assert.Contains("この画像の記録がありません", m.RecordNotice, StringComparison.Ordinal);
        Assert.Empty(m.StageLines);                                      // 状態・寸法は ImageDetail が無いので出さない
        Assert.Empty(m.InfoLines);
        Assert.Equal([@"C:\shots\a.png", @"D:\copy\a.png"], m.PathLines.Select(p => p.Path).ToArray());   // パスは検索の結果から
        Assert.Equal("セリフ", string.Concat(m.Fields[0].Segments.Select(s => s.Text)));                  // 一致の欄は検索のとき文字列のまま
        Assert.Equal(["統合: 2 位", $"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)", $"{TestWords.TextSearch}: 1 位"], m.RankLines.ToArray());
    }

    [Fact]
    public void A_failed_load_shows_the_reason_and_still_the_search_result_parts()
    {
        DetailModel m = Build(Result(), DetailLoad.Failed("database is locked"));
        Assert.Equal("詳細を読み込めませんでした: database is locked", m.RecordNotice);
        Assert.NotEmpty(m.RankLines);
        Assert.Single(m.PathLines);
    }

    [Fact]
    public void A_loaded_detail_has_no_notice()
    {
        Assert.Null(Build(Result()).RecordNotice);
    }

    // ---------------------------------------------------------------- キー(詳細画面が開いているとき)

    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None, false, nameof(DetailKeyAction.Close))]
    [InlineData(Key.Escape, ModifierKeys.None, true, nameof(DetailKeyAction.Close))]         // 検索ボックスにフォーカスがあっても Esc は閉じる
    [InlineData(Key.Left, ModifierKeys.None, false, nameof(DetailKeyAction.Previous))]
    [InlineData(Key.Right, ModifierKeys.None, false, nameof(DetailKeyAction.Next))]
    [InlineData(Key.Left, ModifierKeys.None, true, nameof(DetailKeyAction.None))]            // 検索ボックスでは、キャレットの移動
    [InlineData(Key.Right, ModifierKeys.None, true, nameof(DetailKeyAction.None))]
    [InlineData(Key.Left, ModifierKeys.Shift, false, nameof(DetailKeyAction.None))]
    [InlineData(Key.Right, ModifierKeys.Control, false, nameof(DetailKeyAction.None))]
    [InlineData(Key.Escape, ModifierKeys.Shift, false, nameof(DetailKeyAction.None))]
    [InlineData(Key.C, ModifierKeys.Control, false, nameof(DetailKeyAction.CopyImage))]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, false, nameof(DetailKeyAction.CopyFiles))]
    [InlineData(Key.E, ModifierKeys.None, false, nameof(DetailKeyAction.RevealInExplorer))]
    [InlineData(Key.E, ModifierKeys.None, true, nameof(DetailKeyAction.None))]               // 検索ボックスでは E は文字
    [InlineData(Key.C, ModifierKeys.Control, true, nameof(DetailKeyAction.None))]            // 検索ボックスの Ctrl+C は文字列のコピー
    [InlineData(Key.E, ModifierKeys.Control, false, nameof(DetailKeyAction.None))]
    [InlineData(Key.Up, ModifierKeys.None, false, nameof(DetailKeyAction.None))]
    public void Detail_keys_are_resolved_strictly(Key key, ModifierKeys modifiers, bool searchBoxFocused, string expected)
    {
        Assert.Equal(Enum.Parse<DetailKeyAction>(expected), KeyShortcuts.ResolveDetail(key, Key.None, modifiers, searchBoxFocused));
    }

    [Fact]
    public void An_ime_processed_key_never_closes_or_navigates_but_a_letter_shortcut_uses_the_real_key()
    {
        Assert.Equal(DetailKeyAction.None, KeyShortcuts.ResolveDetail(Key.ImeProcessed, Key.Escape, ModifierKeys.None, false));   // 変換の取り消しは IME のもの
        Assert.Equal(DetailKeyAction.None, KeyShortcuts.ResolveDetail(Key.ImeProcessed, Key.Left, ModifierKeys.None, false));
        Assert.Equal(DetailKeyAction.None, KeyShortcuts.ResolveDetail(Key.ImeProcessed, Key.Right, ModifierKeys.None, false));
        Assert.Equal(DetailKeyAction.RevealInExplorer, KeyShortcuts.ResolveDetail(Key.ImeProcessed, Key.E, ModifierKeys.None, false));
    }
}
