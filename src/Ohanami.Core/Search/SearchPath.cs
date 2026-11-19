using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ohanami.Core.Search;

/// <summary>
/// 1 つの経路の結果(母集団全体への順位つきのスコア)。<see cref="Hashes"/> と <see cref="Scores"/> は同じ長さで、i 番目の画像のスコアが Scores[i]
/// (大きいほど良い)。順位は付けない(融合の側で付ける)。<see cref="Population"/> は Hashes.Count。
/// State が Ok でなければ、Hashes と Scores は空で、<see cref="Message"/> に理由。Ok のときの Message は、伝えたい注意(あれば)。
/// </summary>
internal sealed class PathSearchResult
{
    public PathState State { get; }
    public string? Message { get; }

    /// <summary>この検索語で、点に補正をかけたか(<see cref="PathOutcome.ScoreCorrected"/> に載る)。</summary>
    public bool ScoreCorrected { get; }
    public IReadOnlyList<string> Hashes { get; }
    public IReadOnlyList<double> Scores { get; }
    private readonly Func<int, PathDetail> _detail;
    private readonly Action<IReadOnlyList<int>>? _prepare;

    private PathSearchResult(PathState state, string? message, IReadOnlyList<string> hashes, IReadOnlyList<double> scores, Func<int, PathDetail> detail, Action<IReadOnlyList<int>>? prepare, bool scoreCorrected = false)
    {
        State = state;
        ScoreCorrected = scoreCorrected;
        Message = message;
        Hashes = hashes;
        Scores = scores;
        _detail = detail;
        _prepare = prepare;
    }

    /// <param name="message">結果は返したが、伝えたいこと(例: 検索できない語を除いて引いた)。<see cref="PathOutcome.Message"/> に載る。</param>
    /// <param name="prepare">詳細の準備(<see cref="PrepareDetails"/>)。経路が詳細をバッチで作りたいときだけ渡す(見た目の経路は何もしない)。</param>
    /// <param name="scoreCorrected">点に補正をかけた(見た目の経路が、関門を通った検索語に)。</param>
    public static PathSearchResult Ok(
        IReadOnlyList<string> hashes, IReadOnlyList<double> scores, Func<int, PathDetail> detail, string? message = null, Action<IReadOnlyList<int>>? prepare = null, bool scoreCorrected = false)
    {
        if (hashes.Count != scores.Count) throw new ArgumentException("ハッシュとスコアの数が違う");
        return new PathSearchResult(PathState.Ok, message, hashes, scores, detail, prepare, scoreCorrected);
    }

    public static PathSearchResult NotReady(string message) => new(PathState.NotReady, message, [], [], static _ => throw new InvalidOperationException(), null);

    /// <summary>この経路では引けないクエリだった(失敗ではない)。結果には入らず、<see cref="PathOutcome"/> に Skipped と理由が出る。</summary>
    public static PathSearchResult Skipped(string message) => new(PathState.Skipped, message, [], [], static _ => throw new InvalidOperationException(), null);

    /// <summary>
    /// <see cref="DetailOf"/> を呼ぶ画像の位置(Hashes の添字)を、呼ぶ前に 1 回だけ知らせる。<see cref="SearchService"/> が、融合で上位 <c>Limit</c> 件を決めたあと・
    /// <see cref="DetailOf"/> の前に、その経路で結果に載る画像の分をまとめて渡す。経路はここで、詳細に要るデータをバッチで読む(DetailOf の中で 1 枚ずつ DB を読まない)。
    /// 何もしない経路もある。
    /// </summary>
    public void PrepareDetails(IReadOnlyList<int> indices) => _prepare?.Invoke(indices);

    /// <summary>i 番目の画像の説明(経路ごとに型が違う)。</summary>
    public PathDetail DetailOf(int i) => _detail(i);
}

/// <summary>
/// 検索の経路。<see cref="SearchService"/> が経路の配列を明示的に持つ(<see cref="VisualPath"/> と全文検索)。
/// 経路の登録はコードに書く(リフレクションで探さない)。
/// </summary>
internal interface ISearchPath
{
    /// <summary>経路の種類を表す安定した文字列("visual")。モデルを替えても変わらない。</summary>
    string PathId { get; }

    /// <summary>表示名。</summary>
    string DisplayName { get; }

    /// <summary>この経路が使うモデルの model_id(見た目の経路は現行の埋め込みの model_id。全文検索は null)。</summary>
    string? ModelId { get; }

    /// <summary>
    /// クエリに対する、母集団全体への順位つきのスコア。準備中なら <see cref="PathSearchResult.NotReady"/>。失敗は例外
    /// (<see cref="SearchService"/> がこの検索での Failed にして、他の経路で続ける)。
    /// </summary>
    Task<PathSearchResult> SearchAsync(string query, CancellationToken ct);
}
