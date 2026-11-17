using System;
using System.Collections.Generic;

namespace Ohanami.Core;

/// <summary>検索のリクエスト。<paramref name="Limit"/> は融合の<b>後</b>に適用する上位の件数。</summary>
public sealed record SearchQuery(string Text, int Limit = 300);

/// <summary>検索の結果。</summary>
public sealed record SearchResponse(
    string Query,
    IReadOnlyList<SearchResult> Results,       // 融合の後の上位 Limit 件
    IReadOnlyList<PathOutcome> Paths,          // 経路ごとの結果(母集団の大きさ、状態、準備中・失敗の理由)
    int IndexedImages, int TotalImages,         // まだ全件が埋め込み済みでないことを UI が伝えるため
    TimeSpan Elapsed);

/// <summary>
/// 検索していないときの一覧(<see cref="SearchService.ListRecentAsync"/>)の 1 枚。<paramref name="Paths"/> は missing_since の無いパスで、先頭が並べた日時のもとになったパス
/// (残りはパスの昇順)。<paramref name="ModifiedAt"/> は、そのパスの、最後に走査したときのファイルの更新日時(UTC。DB の値。いまのファイルを見ていない)。
/// </summary>
public sealed record RecentImage(string Hash, IReadOnlyList<string> Paths, DateTimeOffset ModifiedAt);

/// <summary>
/// 検索していないときの一覧。<paramref name="Images"/> は新しい順の上位 limit 件。<paramref name="TotalImages"/> は、今あるパスのある画像の全数
/// (<see cref="SearchResponse.TotalImages"/> の「見える画像」と同じ数え方。limit で切られた分を含む)。
/// </summary>
public sealed record RecentResponse(IReadOnlyList<RecentImage> Images, int TotalImages, TimeSpan Elapsed);

/// <summary>結果 1 件(画像 1 枚)。</summary>
public sealed record SearchResult(
    string Hash,
    IReadOnlyList<string> Paths,                // missing_since の無いパス(DB の印だけで決める。実在の確認はしない)。ドラッグ・コピーはこれを使う
    double Rank,                                // 統合後の順位(1 始まり。同点は平均順位なので 23.5 のような小数になりうる。表示で丸めるのは App)
    double FusedScore,
    IReadOnlyList<PathHit> Hits);               // 経路ごとの順位・スコア・説明。その経路の母集団に入っていない経路は含めない

/// <summary>
/// 1 つの経路での、その画像の順位・スコア・説明。
/// <paramref name="PathId"/> は経路の種類で、安定した文字列("visual"・"fulltext")。モデルを替えても変わらない。
/// <paramref name="ModelId"/> は、その経路が使ったモデル(visual なら現行の model_id。fulltext は null)。
/// <paramref name="Rank"/> も同点は平均順位。
/// </summary>
public sealed record PathHit(string PathId, string? ModelId, double Rank, double Score, PathDetail Detail);

/// <summary>経路ごとの説明。App は型で分岐する。</summary>
public abstract record PathDetail;

/// <summary>SigLIP2 の経路(特徴ベクトル検索)。検索語と画像のコサイン類似度。</summary>
public sealed record VisualDetail(double Cosine) : PathDetail;

/// <summary>元のテキスト上の範囲(UTF-16 の開始位置と長さ)。</summary>
public sealed record TextSpan(int Start, int Length);

/// <summary>全文検索の対象の欄。<c>Tags</c> は VLM が付けたタグ、<c>ManualTags</c> は利用者が付けた手動タグ(ADR 0036)。</summary>
public enum TextField { Ocr, Description, Tags, ManualTags }

/// <summary>
/// 1 つの欄の一致箇所。<paramref name="Text"/> は表示に使う文字列で、DB の caption の文字列に、制御文字・孤立したサロゲートの U+FFFD への置換(長さは変わらない)をかけたもの。
/// <paramref name="Tags"/>・<paramref name="ManualTags"/> の <paramref name="Text"/> は、タグを「、」でつないだ文字列(手動タグは名前順)。
/// <paramref name="Spans"/> は <paramref name="Text"/> 上の範囲(昇順、重ならない。一致が無ければ空)。
/// </summary>
public sealed record FieldMatch(TextField Field, string Text, IReadOnlyList<TextSpan> Spans);

/// <summary>
/// 全文検索の経路(テキスト検索)。欄ごとの、元のテキスト上の一致箇所(ocr、説明文、tags、手動タグの 4 欄を常にこの順で持つ)。
/// caption が無い画像(手動タグだけで索引に入った画像)は、最初の 3 欄の文字列が空(一致なし)。手動タグが無い画像は、手動タグの欄の文字列が空。
/// </summary>
public sealed record TextDetail(IReadOnlyList<FieldMatch> Matches) : PathDetail
{
    /// <summary>
    /// あいまい一致(OCR の誤読への fallback。ADR 0032)で当たった語(クエリに書かれた語)。空なら、すべての語が正確に一致した。
    /// 語は、日本語だけで 4 文字以上、かつ正確な一致が索引に 1 件も無かったときだけ、編集距離 1 以内の部分一致で引く。その語で当たった画像には、この一覧にその語が入る
    /// (一致の範囲は <see cref="FieldMatch.Spans"/> に、正確な一致と同じに入る)。「文字が似ている(読み違いかもしれない)」の表示の材料。
    /// </summary>
    public IReadOnlyList<string> FuzzyTerms { get; init; } = [];
}

/// <summary>経路の状態の報告。<paramref name="Population"/> は、その経路が順位を付けた画像の数(missing_since のパスしか無い画像を除く)。</summary>
public sealed record PathOutcome(string PathId, string? ModelId, int Population, PathState State, string? Message);

/// <summary>
/// Ok = 結果を返した(<see cref="PathOutcome.Message"/> に、一部の語を除いたなどの注意が入ることがある)、NotReady = 準備中(ベクトルを読み込み中、全文検索の索引を作り直し中など。結果には入らない)、
/// Failed = この検索では失敗した、Skipped = この経路では引けないクエリだった(全文検索で、検索できる語が 1 つも無い。Message に理由。失敗ではない)。
/// </summary>
public enum PathState { Ok, NotReady, Failed, Skipped }

/// <summary>経路の一覧に出す情報(PathId と表示名)。</summary>
public sealed record SearchPathInfo(string PathId, string DisplayName);
