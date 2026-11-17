namespace Ohanami.App;

/// <summary>
/// 一覧の 1 枚。運ぶのは内容ハッシュだけ(サムネイルの鍵)。パス・順位・スコアなどは、最後の検索結果を持つ側(<see cref="ResultsPanel"/>)がハッシュで引く
/// (グリッドは <c>pathsOf</c> / <see cref="ThumbnailGridControl.TooltipOf"/> で問い合わせる。詳細画面も同じ引き方ができる)。
/// </summary>
internal sealed record GridItem(string Hash);
