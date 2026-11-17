using System;
using System.Collections.Generic;

namespace Ohanami.Core;

/// <summary>
/// 段階 1 つの、その画像での状態。<c>StageSummary</c> の件数と同じ定義:
/// Pending = stage_status に行が無い(未処理)、Done = 現行の model_id で完了、Failed = 失敗の記録がある(model_id は問わない)、
/// Outdated = 完了しているが現行の model_id と違う(要再作成。ADR 0006)。
/// </summary>
public enum StageState { Pending, Done, Failed, Outdated }

/// <summary>
/// 段階 1 つの状態。<paramref name="ModelId"/>・<paramref name="Error"/>・<paramref name="Attempts"/>・<paramref name="UpdatedAt"/> は、stage_status の行の値
/// (<see cref="StageState.Pending"/> では行が無いので null / 0 / null)。<paramref name="Error"/> は Failed で入る(記録が無ければ null)。
/// </summary>
public sealed record StageDetail(Stage Stage, StageState State, string? ModelId, string? Error, int Attempts, DateTimeOffset? UpdatedAt);

/// <summary>
/// 画像のパス 1 つ。<paramref name="Missing"/> は <c>missing_since</c> の印があること(<paramref name="MissingSince"/> はその時刻)。
/// <paramref name="ModifiedAt"/> と <paramref name="Size"/> は、最後に走査したときのファイルの更新日時(UTC)とサイズ(DB の値。いまのファイルを見ていない)。
/// </summary>
public sealed record ImagePathDetail(string Path, bool Missing, DateTimeOffset? MissingSince, DateTimeOffset ModifiedAt, long Size);

/// <summary>現行の caption(VLM の結果。索引文書の元と同じ選び方=作成日時が最新のもの)。<paramref name="Tags"/> は VLM のタグ(順序のまま)。</summary>
public sealed record CaptionDetail(string ModelId, string Ocr, string Description, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt);

/// <summary>
/// 詳細画面の元になる、画像 1 枚の情報。1 つの読み取りのトランザクションの中で(同じ時点で)読んだもの。
/// <list type="bullet">
/// <item><paramref name="Size"/>・<paramref name="Format"/> は画像の内容(images の行)。<paramref name="Width"/>・<paramref name="Height"/> は、埋め込みが済むまで null。</item>
/// <item><paramref name="Paths"/> は、現存するパス(パス順)のあとに、見つからない印のあるパス(パス順)。</item>
/// <item><paramref name="Stages"/> は埋め込み・OCR・説明文の 3 段階を、この順で常に持つ。</item>
/// <item><paramref name="Caption"/> は、caption が無ければ null(VLM の前、または失敗)。</item>
/// <item><paramref name="ManualTags"/> は手動タグの名前(名前順。無ければ空)。</item>
/// </list>
/// **なぜ一致したか(経路ごとの順位・類似度・一致箇所)は入れない**: 検索の結果(<c>SearchResult.Hits</c> と <c>TextDetail</c>)にあり、二重に持たない。
/// </summary>
public sealed record ImageDetail(
    string Hash,
    long Size,
    string Format,
    int? Width,
    int? Height,
    DateTimeOffset FirstSeenAt,
    IReadOnlyList<ImagePathDetail> Paths,
    IReadOnlyList<StageDetail> Stages,
    CaptionDetail? Caption,
    IReadOnlyList<string> ManualTags);

/// <summary>失敗の一覧の 1 件(段階 1 つで失敗した画像 1 枚)。<paramref name="Path"/> は現存する最初のパス(パス順。無ければ null=見つからない画像)。</summary>
public sealed record FailureItem(string Hash, string? Path, string? Error, int Attempts, DateTimeOffset UpdatedAt, string ModelId);

/// <summary>
/// フォルダに無い画像(見つからない画像)の一覧の 1 件(<see cref="MissingImageList"/>)。現存するパスが 1 つも無い画像。
/// <paramref name="LastPath"/> は、最後に分かっているパス(見つからない印の付いたパスのうち、印の日時が新しいもの。パスの行が 0 行の画像は null)、
/// <paramref name="MissingSince"/> は、その印の日時(パスの行が 0 行なら null)、<paramref name="ManualTagCount"/> は手動タグの数、
/// <paramref name="HasCaption"/> は VLM の結果(説明文など)があること。
/// </summary>
public sealed record MissingImageItem(string Hash, string? LastPath, DateTimeOffset? MissingSince, int ManualTagCount, bool HasCaption);

/// <summary>
/// フォルダに無い画像の一覧(消す対象のうち、上限までの件数。無くなった日時の新しい順。日時の無いもの=パスの行が 0 行のものは最後)。
/// <paramref name="Total"/> は上限で切る前の数、<paramref name="WithManualTags"/> は、そのうち手動タグのある画像の数(上限で切る前。消して戻らないのは、手動タグだけのため)。
/// 対象は、消す操作(<see cref="Indexing.Indexer.PurgeMissingAsync"/>)と同じ条件(見せたものと消えるものを食い違わせない)。
/// </summary>
public sealed record MissingImageList(IReadOnlyList<MissingImageItem> Items, int Total, int WithManualTags);

/// <summary>失敗の一覧。<paramref name="Items"/> は新しい順(更新日時が新しいものから)で、上限までの件数。<paramref name="Total"/> は上限で切る前の失敗の数。</summary>
public sealed record FailureList(Stage Stage, IReadOnlyList<FailureItem> Items, int Total);
