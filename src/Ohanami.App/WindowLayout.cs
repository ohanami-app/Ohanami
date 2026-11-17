namespace Ohanami.App;

/// <summary>
/// メイン画面の大きさの決まり(DIP。論理単位 = DPI 96 のピクセル)。<see cref="MainWindow"/>・<see cref="ResultsPanel"/>・<see cref="DetailView"/> が使い、
/// 試験(MinWindowSizeTests・IndexSectionHeightTests)が、最小の窓で何が見えるかを確かめる。
/// </summary>
internal static class WindowLayout
{
    /// <summary>
    /// 窓の最小の高さ(外側。ADR 0039)。左パネルの「インデックス」欄が固定の高さ(IndexRowsView)になり、最小の窓でフォルダ一覧が 2 行も見えなくなったため、
    /// 長いパスが 2 行に折り返す行(約 56px)が 2 行以上見える高さにした(IndexSectionHeightTests が、最小の窓で一覧の見える高さを固定する)。
    /// </summary>
    public const double MinWindowHeight = 560;

    /// <summary>
    /// 窓の最小の幅(外側。画像そのものが出ないのは致命的なので、画像が最低限見える幅にしてある。ADR 0039)。
    /// 最小の幅で詳細画面を開いても、画像に使える幅が一覧のサムネイルの幅(<see cref="ThumbnailWidth"/>)以上になる最小の幅
    /// (= <see cref="ChromeWidth"/> + 左の列 <see cref="LeftColumnWidth"/> + 右パネルの余白 <see cref="RightPanelMarginWidth"/> + 詳細の右の欄 <see cref="DetailSideWidth"/> + 詳細の内側の余白 <see cref="DetailInnerMarginWidth"/> + 画像の余白 <see cref="DetailImagePadding"/> + サムネイルの幅)。
    /// MinWindowSizeTests が、最小の窓で、この条件を固定する。
    /// </summary>
    public const double MinWindowWidth = 1042;

    /// <summary>窓の枠の幅(左右の合計。実機の測定: 8 + 8)。外側の幅 − これ = 中(クライアント)の幅。</summary>
    public const double ChromeWidth = 16;

    /// <summary>窓の枠の高さ(上 31 + 下 8。実機の測定)。</summary>
    public const double ChromeHeight = 39;

    /// <summary>左の列(左パネル)の幅。</summary>
    public const double LeftColumnWidth = 300;

    /// <summary>右パネルの左右の余白(左 6 + 右 12)。</summary>
    public const double RightPanelMarginWidth = 18;

    /// <summary>一覧のサムネイル 1 枚の幅。詳細画面の画像は、最小の幅でも、これ以上の幅で見える(画像の見え方が一覧より小さくならない、を「最低限」と決めた)。</summary>
    public const double ThumbnailWidth = 256;

    /// <summary>
    /// 詳細画面の右の欄(操作と内容)の幅(固定)は 424(詳細画面の内側の余白 <see cref="DetailInnerMargin"/> の分の 16px を、右の欄から出して、最小の幅 1042 を保つ。
    /// 右の欄は折り返す文字が中心で、幅 424 でも欄は切れない。決めた最小の幅と「画像は一覧のサムネイル以上」の約束を、動かさないため)。
    /// </summary>
    public const double DetailSideWidth = 424;

    /// <summary>詳細画面の内側の余白(上・左・右・下。1 辺)。戻る・前・次のボタンが面の端に接して見えたので足した。</summary>
    public const double DetailInnerMargin = 8;

    /// <summary>詳細画面の内側の余白の、左右の合計。</summary>
    public const double DetailInnerMarginWidth = 2 * DetailInnerMargin;

    /// <summary>詳細画面の画像の欄の、画像のまわりの余白(左右の合計 6 × 2)。</summary>
    public const double DetailImagePadding = 12;
}
