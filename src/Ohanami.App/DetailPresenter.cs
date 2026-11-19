using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>テキストの 1 区間。<paramref name="Highlight"/> は、検索に一致した範囲(強調して出す)。</summary>
internal sealed record TextSegment(string Text, bool Highlight);

/// <summary>詳細画面の、テキストの欄 1 つ。<paramref name="Segments"/> をつなぐと元の文字列になる。<paramref name="IsEmpty"/> は文字列が空(「(なし)」と出す)。</summary>
internal sealed record DetailField(string Label, IReadOnlyList<TextSegment> Segments, bool HasMatch, bool IsEmpty);

/// <summary>手動タグ 1 つ(詳細画面の「×」つきの札)。<paramref name="Segments"/> をつなぐと名前。一致した範囲があれば強調の区間になる(検索のときの欄と、いまのタグの並びが同じときだけ)。</summary>
internal sealed record TagChip(string Name, IReadOnlyList<TextSegment> Segments, bool HasMatch);

/// <summary>
/// 詳細画面の手動タグの欄。<paramref name="Editable"/> は、外す・足す操作ができること(画像の詳細を読めたときだけ。読み込み中・記録が無い・読めなかったときは、
/// 検索の結果にあるタグを見せるだけ)。
/// </summary>
internal sealed record ManualTagsModel(IReadOnlyList<TagChip> Chips, bool Editable);

/// <summary>パス 1 つの表示。<paramref name="Note"/> は、更新日時・サイズ・見つからない印など(あれば)。</summary>
internal sealed record DetailPathLine(string Path, string? Note, bool Missing);

/// <summary>詳細の読み込みの状態(画面の表示を決める)。</summary>
internal enum DetailLoadKind
{
    /// <summary>まだ読んでいる。検索の結果の情報(順位・一致の欄・パス)だけ出す。</summary>
    Loading,

    /// <summary>読めた(<see cref="DetailLoad.Detail"/> がある)。</summary>
    Loaded,

    /// <summary>画像の記録が無い(<c>GetImageDetailAsync</c> が null。検索のあとで記録が消された)。落とさず「記録が見つからない」を出す。</summary>
    RecordMissing,

    /// <summary>読み込みに失敗した(<see cref="DetailLoad.Error"/> に理由)。</summary>
    Failed,
}

internal sealed record DetailLoad(DetailLoadKind Kind, ImageDetail? Detail = null, string? Error = null)
{
    public static DetailLoad Loading { get; } = new(DetailLoadKind.Loading);

    public static DetailLoad RecordMissing { get; } = new(DetailLoadKind.RecordMissing);

    public static DetailLoad Loaded(ImageDetail detail) => new(DetailLoadKind.Loaded, detail);

    public static DetailLoad Failed(string error) => new(DetailLoadKind.Failed, null, error);
}

/// <summary>詳細画面の右側に出す内容(文字列だけ。WPF の型を含まないので、要素を作らずにテストできる)。</summary>
internal sealed record DetailModel(
    string Title,
    IReadOnlyList<string> RankLines,
    IReadOnlyList<DetailField> Fields,
    string? FieldsNote,
    ManualTagsModel ManualTags,
    IReadOnlyList<string> StageLines,
    IReadOnlyList<string> InfoLines,
    IReadOnlyList<DetailPathLine> PathLines,
    string? RecordNotice)
{
    /// <summary>出た理由(言葉で。<see cref="ReasonText"/>)。検索の結果でない詳細(新しい順の一覧・処理状態の一覧から開いた)は空。</summary>
    public IReadOnlyList<string> ReasonLines { get; init; } = [];

    /// <summary><see cref="RankLines"/> と同じ長さの、各行のツールチップ(無ければ null)。特徴ベクトル検索の行に、類似度の数字を入れる(本文には出さない)。</summary>
    public IReadOnlyList<string?> RankTips { get; init; } = [];

    /// <summary>
    /// 「文字と説明」の区画の見出し。検索の結果から開いたときだけ「(一致した箇所は強調)」が付く。検索の結果でない詳細(新しい順の一覧・処理状態の一覧から開いた)は、
    /// 一致の強調が無いので付けない。
    /// </summary>
    public string FieldsHeader { get; init; } = DetailPresenter.FieldsHeaderWithHighlight;
}

/// <summary>
/// 詳細画面の内容を作る(純粋な関数)。
/// <list type="bullet">
/// <item><b>一致の欄は、検索の <see cref="TextDetail"/> の Text と Spans をそのまま出す</b>。<see cref="ImageDetail"/> の caption と突き合わせない
///   (VLM が裏で動くと、検索してから詳細を開くまでに caption が変わりうる。範囲は検索のときの文字列の上でしか正しくない)。
///   全文検索に当たっていない画像(見た目だけで出た画像)は、TextDetail が無いので、<see cref="ImageDetail.Caption"/> と手動タグを、強調なしで出す。</item>
/// <item>状態・パス・寸法は <see cref="ImageDetail"/> の値。画像の記録が無い(null)ときは、「記録が見つからない」を出す(落ちない)。読み込み中は、検索の結果の情報だけ。</item>
/// <item>内部の名前(visual・fulltext・cosine)は出さず、経路の日本語の名前(<see cref="ResultsPanel.PathLabel"/>)を使う。文言は ADR 0038 に沿う。</item>
/// </list>
/// </summary>
internal static class DetailPresenter
{
    public const string NoValue = "(なし)";

    /// <summary>検索の結果ではない詳細(処理状態の一覧から開いた)の、順位の欄の文。順位・類似度・一致の強調は、検索のときの情報なので出さない。</summary>
    public const string StandaloneRankLine = "検索の結果ではありません (インデックスの状態の窓の一覧から開きました)";

    /// <summary>検索していないときの新しい順の一覧から開いた詳細の、順位の欄の文。順位・類似度は検索のときの情報なので出さない(一覧の中の位置は、詳細画面の「n / m」)。</summary>
    public const string RecentRankLine = "検索の結果ではありません (新しい順の一覧です。ファイルの更新日時の順)";

    /// <summary>「文字と説明」の区画の見出し(検索の結果から開いたとき。一致した範囲を強調する)。</summary>
    public const string FieldsHeaderWithHighlight = "文字と説明 (一致した箇所は強調)";

    /// <summary>「文字と説明」の区画の見出し(検索の結果でない詳細。強調は無い)。</summary>
    public const string FieldsHeaderPlain = "文字と説明";

    /// <summary>検索の結果に入っているのに、全文検索には当たっていない画像(見た目だけで出た)の、欄の先頭の注記。検索の結果でない詳細には出さない。</summary>
    public static string NotMatchedByTextNote => $"{ProcessingWords.StageName(JobKind.Vlm)}で一致していません (文字の一致の強調はありません)";

    /// <summary>
    /// 詳細の内容を作る。<paramref name="paths"/> は最後の検索の経路ごとの状態、<paramref name="zone"/> は日時を出すタイムゾーン(画面は <see cref="TimeZoneInfo.Local"/>)。
    /// <paramref name="fromSearch"/> が false なら処理状態の一覧から開いた詳細、<paramref name="fromRecentList"/> が true なら新しい順の一覧から開いた詳細(どちらも順位の欄は注記だけ)。
    /// </summary>
    public static DetailModel Build(SearchResult result, IReadOnlyList<PathOutcome> paths, DetailLoad load, TimeZoneInfo zone, bool fromSearch = true, bool fromRecentList = false, bool noTextIndexHint = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(zone);
        ImageDetail? detail = load.Detail;

        string title = Title(result, detail);
        bool isSearchResult = fromSearch && !fromRecentList;
        IReadOnlyList<string> ranks = fromRecentList ? [RecentRankLine] : fromSearch ? RankLines(result, paths) : [StandaloneRankLine];
        IReadOnlyList<string?> rankTips = isSearchResult ? RankEntries(result, paths).Select(e => e.Tip).ToArray() : [null];
        IReadOnlyList<string> reasons = isSearchResult ? ReasonText.Lines(result, NoTextIndex(paths, load, noTextIndexHint)) : [];
        (IReadOnlyList<DetailField> fields, string? note) = FieldsOf(result, load, isSearchResult);
        ManualTagsModel manualTags = ManualTagsOf(result, load);

        IReadOnlyList<string> stages = detail is null ? [] : detail.Stages.Select(s => StageLine(s, zone)).ToArray();
        IReadOnlyList<string> info = detail is null ? [] : InfoLines(detail, zone);
        IReadOnlyList<DetailPathLine> pathLines = detail is null
            ? result.Paths.Select(p => new DetailPathLine(p, null, false)).ToArray()
            : detail.Paths.Select(p => PathLine(p, zone)).ToArray();

        string? notice = load.Kind switch
        {
            DetailLoadKind.Loading => "読み込み中…",
            DetailLoadKind.RecordMissing => "この画像の記録がありません (検索のあとで、元のファイルが無い画像の記録が消された可能性があります)。検索の結果の情報だけを出しています",
            DetailLoadKind.Failed => "詳細を読み込めませんでした: " + UiText.Plain(load.Error),
            _ => null,
        };
        string fieldsHeader = isSearchResult ? FieldsHeaderWithHighlight : FieldsHeaderPlain;
        return new DetailModel(title, ranks, fields, note, manualTags, stages, info, pathLines, notice) { ReasonLines = reasons, RankTips = rankTips, FieldsHeader = fieldsHeader };
    }

    /// <summary>
    /// 「テキスト検索のインデックスがまだありません」を理由に出すか。全文の経路が使えている(Ok。索引の作り直しの最中は、文書が一時的に無いので出さない)ときだけ。
    /// 詳細を読めていれば、その画像に caption も手動タグも無いこと(= 索引に文書が無い)で決め、まだ読めていなければ、一覧が引いておいた値(<paramref name="hint"/>)を使う。
    /// </summary>
    internal static bool NoTextIndex(IReadOnlyList<PathOutcome> paths, DetailLoad load, bool hint)
    {
        if (!paths.Any(p => p.PathId == ResultsPanel.FullTextPathId && p.State == PathState.Ok)) return false;
        return load.Detail is { } d ? d.Caption is null && d.ManualTags.Count == 0 : hint;
    }

    // ---------------------------------------------------------------- 見出し・順位

    private static string Title(SearchResult result, ImageDetail? detail)
    {
        string? path = detail?.Paths.FirstOrDefault(p => !p.Missing)?.Path ?? result.Paths.FirstOrDefault() ?? detail?.Paths.FirstOrDefault()?.Path;
        return path is null ? "(パスなし)" : Path.GetFileName(path);
    }

    /// <summary>
    /// 統合順位と、経路ごとの順位。経路が使えなかった・順位が無いときは、その旨(全文検索は「一致なし」、特徴ベクトル検索は「この画像のインデックスがまだ無い」。経路ごとに言い分ける)。
    /// **類似度は本文に出す**(数字を奥に置かない)。その見方(割合ではない・同じ検索の中だけ)は、行のツールチップ(<see cref="SimilarityTip"/>)に置く。
    /// </summary>
    internal static IReadOnlyList<string> RankLines(SearchResult result, IReadOnlyList<PathOutcome> paths) =>
        RankEntries(result, paths).Select(e => e.Line).ToArray();

    /// <summary>順位の行と、そのツールチップ(特徴ベクトル検索の行だけ、類似度の見方)。</summary>
    internal static IReadOnlyList<(string Line, string? Tip)> RankEntries(SearchResult result, IReadOnlyList<PathOutcome> paths)
    {
        var entries = new List<(string, string?)> { ("統合: " + Rank(result.Rank), null) };
        var shown = new HashSet<string>(StringComparer.Ordinal);
        (string, string?) OfHit(PathHit hit) => (ResultsPanel.PathLabel(hit.PathId) + ": " + HitText(hit), SimilarityTip(hit, ScoreCorrected(paths, hit.PathId)));
        foreach (PathOutcome outcome in paths)
        {
            shown.Add(outcome.PathId);
            PathHit? hit = result.Hits.FirstOrDefault(h => h.PathId == outcome.PathId);
            if (hit is not null) { entries.Add(OfHit(hit)); continue; }
            entries.Add((ResultsPanel.PathLabel(outcome.PathId) + ": " + outcome.State switch
            {
                PathState.NotReady => "準備中",
                PathState.Failed => "失敗",
                PathState.Skipped => "この検索では使いませんでした",
                _ => outcome.PathId == ResultsPanel.VisualPathId ? NoVisualRank : NoTextRank,
            }, null));
        }
        foreach (PathHit hit in result.Hits.Where(h => !shown.Contains(h.PathId))) entries.Add(OfHit(hit));
        return entries;
    }

    /// <summary>全文検索で順位が無い(この検索の語に一致しなかった)ときの、順位の行の言い方(「対象になっていない」とは言わない: 母集団は一致した画像だけ。ADR 0038 の一般則)。</summary>
    internal const string NoTextRank = "一致なし";

    /// <summary>特徴ベクトル検索で順位が無い(この画像の特徴ベクトルがまだ無い)ときの、順位の行の言い方。</summary>
    internal const string NoVisualRank = "この画像のインデックスがまだありません";

    /// <summary>その経路の点に、この検索語で補正をかけたか(<see cref="PathOutcome.ScoreCorrected"/>)。経路の報告が無ければ false。</summary>
    internal static bool ScoreCorrected(IReadOnlyList<PathOutcome> paths, string pathId) =>
        paths.FirstOrDefault(p => p.PathId == pathId)?.ScoreCorrected ?? false;

    /// <summary>
    /// 特徴ベクトル検索の行のツールチップ(類似度の見方)。類似度の値そのものは行の本文(<see cref="HitText"/>)。特徴ベクトル検索以外は null。
    /// 「1 に近いほど近い」とは書かない(測った分布では、当たりと外れが重なる。ADR 0034・0051)。割合ではないことと、使えるのは同じ検索の中の並び順だけであることを言う。
    /// 値の水準は、補正をかけた検索とかけない検索で違う(ADR 0051)ので、水準の目安(「0.1 前後」など)は書かない。
    /// <paramref name="corrected"/> のとき(類似度 = 順位を決めた点が、コサインから、どの言葉にも近い画像の分を引いた値のとき)は、そう言う。
    /// </summary>
    internal static string? SimilarityTip(PathHit hit, bool corrected) =>
        hit.Detail is VisualDetail
            ? (corrected
                ? "コサイン類似度から、どの言葉にも近い画像の分を引いた値です (検索の言葉と、画像の見た目の近さ)。"
                : "コサイン類似度 (検索の言葉と、画像の見た目の近さ) です。")
                + "大きいほど近いことを表しますが、割合ではありません。同じ検索の中の並び順にだけ使えます。数字だけでは、合っているかどうかは決まりません"
            : null;

    /// <summary>順位の行の本文。特徴ベクトル検索は、順位を決めた点(<see cref="VisualDetail.Score"/>。補正をかけた検索では補正後)を類似度として出す。</summary>
    internal static string HitText(PathHit hit) =>
        Rank(hit.Rank) + (hit.Detail is VisualDetail v ? string.Create(CultureInfo.InvariantCulture, $" (類似度 {v.Score:0.0000})") : "");

    internal static string Rank(double rank) => string.Create(CultureInfo.InvariantCulture, $"{rank:0.#} 位");

    // ---------------------------------------------------------------- テキストの欄

    /// <param name="fromSearch">検索の結果から開いたか。そうでない(新しい順の一覧・処理状態の一覧から開いた)ときは、「全文検索には当たっていない」の注記を出さない
    /// (検索していないのに、検索に当たらなかったように読めるため)。説明文がまだ無い、という注記は、検索に関係なく出す。</param>
    private static (IReadOnlyList<DetailField> Fields, string? Note) FieldsOf(SearchResult result, DetailLoad load, bool fromSearch)
    {
        TextDetail? text = result.Hits.Select(h => h.Detail).OfType<TextDetail>().FirstOrDefault();
        if (text is not null)
        {
            // 検索のときの文字列と範囲をそのまま出す(ImageDetail の caption とは突き合わせない)
            // (手動タグの欄は、編集できる札として別に出す。<see cref="ManualTagsOf"/>)
            return (text.Matches.Where(m => m.Field != TextField.ManualTags).Select(m => Field(DetailFieldLabel(m.Field), m.Text, m.Spans)).ToArray(), null);
        }

        ImageDetail? detail = load.Detail;
        if (detail is null) return ([], null);
        // 全文検索に当たっていない(見た目だけで出た)画像: 現行の caption と手動タグを、強調なしで出す
        CaptionDetail? caption = detail.Caption;
        DetailField[] fields =
        [
            Field(DetailFieldLabel(TextField.Ocr), caption?.Ocr ?? "", []),
            Field(DetailFieldLabel(TextField.Description), caption?.Description ?? "", []),
            Field(DetailFieldLabel(TextField.Tags), caption is null ? "" : string.Join("、", caption.Tags), []),
        ];
        return (fields, caption is null ? "まだ説明文 (VLM の結果) がありません" : fromSearch ? NotMatchedByTextNote : null);
    }

    /// <summary>
    /// 手動タグの欄。**名前は、画像の詳細を読めていればその値(編集のたびに読み直した最新)、読めていなければ検索の結果の欄から**(読むだけ)。
    /// 強調は、検索のときの手動タグの欄(<see cref="TextDetail"/>)の文字列が、いまのタグ(名前を「、」でつないだもの)と同じときだけ付ける
    /// (編集したあとは、検索のときの範囲がいまのタグと合わないので、強調しない。範囲は検索のときの文字列の上でしか正しくない)。
    /// </summary>
    internal static ManualTagsModel ManualTagsOf(SearchResult result, DetailLoad load)
    {
        FieldMatch? searched = result.Hits.Select(h => h.Detail).OfType<TextDetail>().FirstOrDefault()?.Matches.FirstOrDefault(m => m.Field == TextField.ManualTags);
        bool editable = load.Kind == DetailLoadKind.Loaded;
        string[] names = load.Detail is { } detail
            ? [.. detail.ManualTags]
            : searched is { Text.Length: > 0 } ? searched.Text.Split(FtsTagSeparator) : [];
        bool sameAsSearched = searched is not null && string.Join(FtsTagSeparator, names) == searched.Text;

        var chips = new List<TagChip>(names.Length);
        int pos = 0;
        foreach (string name in names)
        {
            IReadOnlyList<TextSegment> segments = [new TextSegment(name, false)];
            bool hasMatch = false;
            if (sameAsSearched && searched is not null)
            {
                TextSpan[] inside = [.. searched.Spans.Where(sp => sp.Start >= pos && sp.Start + sp.Length <= pos + name.Length).Select(sp => new TextSpan(sp.Start - pos, sp.Length))];
                if (inside.Length > 0) { segments = Segments(name, inside); hasMatch = true; }
            }
            chips.Add(new TagChip(name, segments, hasMatch));
            pos += name.Length + FtsTagSeparator.Length;
        }
        return new ManualTagsModel(chips, editable);
    }

    /// <summary>索引文書・検索の結果の欄で、タグをつなぐ区切り(Core の <c>FtsSpec.Tag</c> と同じ。App は Core の内部の型を参照しないので、ここに書く)。</summary>
    private const string FtsTagSeparator = "、";

    /// <summary>詳細画面の欄の名前(一覧のツールチップの「タグ」と違い、VLM のタグと手動タグを区別する)。</summary>
    internal static string DetailFieldLabel(TextField field) => field switch
    {
        TextField.Ocr => "文字 (OCR)",
        TextField.Description => "説明文",
        TextField.Tags => "VLM のタグ",
        TextField.ManualTags => "手動タグ",
        _ => field.ToString(),
    };

    private static DetailField Field(string label, string text, IReadOnlyList<TextSpan> spans) =>
        new(label, Segments(text, spans), spans.Count > 0, text.Length == 0);

    /// <summary>
    /// 文字列を、一致の範囲で区切った区間にする(つなぐと元の文字列)。範囲は UTF-16 の開始位置と長さ、昇順で重ならず、文字列の中に収まること
    /// (違えば <see cref="ArgumentException"/>。検索の結果の文字列と範囲は、作った側が合わせて返す)。
    /// </summary>
    public static IReadOnlyList<TextSegment> Segments(string text, IReadOnlyList<TextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        var list = new List<TextSegment>();
        int pos = 0;
        foreach (TextSpan span in spans)
        {
            if (span.Length <= 0 || span.Start < pos || span.Start + span.Length > text.Length)
                throw new ArgumentException($"一致の範囲 {span.Start}+{span.Length} が不正(文字列の長さ {text.Length}、直前の終わり {pos})", nameof(spans));
            if (span.Start > pos) list.Add(new TextSegment(text[pos..span.Start], false));
            list.Add(new TextSegment(text.Substring(span.Start, span.Length), true));
            pos = span.Start + span.Length;
        }
        if (pos < text.Length) list.Add(new TextSegment(text[pos..], false));
        return list;
    }

    // ---------------------------------------------------------------- 状態・情報・パス

    /// <summary>段階の表示名(埋め込み・文字の書き起こし・説明文)。</summary>
    internal static string StageLabel(Stage stage) => stage switch
    {
        Stage.Embed => ProcessingWords.StageName(JobKind.Embed),
        Stage.Ocr => "文字 (OCR)",
        Stage.Caption => "説明文",
        _ => stage.ToString(),
    };

    internal static string StageLine(StageDetail s, TimeZoneInfo zone)
    {
        string when = s.UpdatedAt is { } t ? " (" + Time(t, zone) + ")" : "";
        string state = s.State switch
        {
            StageState.Pending => ProcessingWords.WordPending,
            StageState.Done => "済" + when,
            StageState.Outdated => ProcessingWords.WordOutdated + " (モデルが替わったため、作り直しが必要です)" + when,
            StageState.Failed => "失敗" + when + (s.Error is { Length: > 0 } e ? ": " + UiText.Plain(e) : "") + string.Create(CultureInfo.InvariantCulture, $" (試行 {s.Attempts} 回)"),
            _ => s.State.ToString(),
        };
        return StageLabel(s.Stage) + ": " + state;
    }

    internal static IReadOnlyList<string> InfoLines(ImageDetail d, TimeZoneInfo zone) =>
    [
        d.Width is { } w && d.Height is { } h ? string.Create(CultureInfo.InvariantCulture, $"寸法: {w} × {h}") : $"寸法: 未計測 ({ProcessingWords.StageName(JobKind.Embed)}のインデックスを作るときに分かります)",
        "ファイルのサイズ: " + Size(d.Size),
        "形式: " + d.Format.ToUpperInvariant(),
        "最初に見つけた日時: " + Time(d.FirstSeenAt, zone),
    ];

    internal static DetailPathLine PathLine(ImagePathDetail p, TimeZoneInfo zone)
    {
        string note = "更新 " + Time(p.ModifiedAt, zone) + "、" + Size(p.Size);
        if (p.Missing) note = "元のファイルがありません" + (p.MissingSince is { } since ? " (" + Time(since, zone) + " から)" : "") + ": " + note;
        return new DetailPathLine(p.Path, note, p.Missing);
    }

    internal static string Size(long bytes)
    {
        string exact = string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} バイト");
        if (bytes >= 1024L * 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.##} MB({exact})");
        if (bytes >= 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB({exact})");
        return exact;
    }

    internal static string Time(DateTimeOffset t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
