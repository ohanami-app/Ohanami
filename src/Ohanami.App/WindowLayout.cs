namespace Ohanami.App;

/// <summary>
/// メイン画面の大きさの決まり(DIP。論理単位 = DPI 96 のピクセル)。<see cref="MainWindow"/>・<see cref="MainLayout"/>・<see cref="ResultsPanel"/>・<see cref="DetailView"/> が使い、
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
    /// 窓の最小の幅(外側)。この幅は、詳細画面の画像からは決めない(詳細画面は窓の全面に重なるので、最小の幅でも画像は十分に広い。ADR 0050)。
    /// 幅を変えない理由は、状態の行(最小の幅 1042 で、起動に失敗した文の「すべきこと」が収まると測ってある。ADR 0047・NativeFailureVisibilityTests)と、
    /// 一覧(1042 で 2 列になる。MinWindowSizeTests)。詳細画面の画像に使える幅が、最小の幅でも一覧のサムネイルの幅(<see cref="ThumbnailWidth"/>)以上であることは、MinWindowSizeTests が固定する。
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
    /// 詳細画面の右の欄(操作と内容)の幅(固定)。右の欄は折り返す文字が中心で、幅 424 でも欄は切れない。
    /// 画像の欄の幅は、詳細画面の幅(窓の中の幅)から、この右の欄と、内側の余白(<see cref="DetailInnerMarginWidth"/>)を引いた残り。
    /// </summary>
    public const double DetailSideWidth = 424;

    /// <summary>詳細画面の内側の余白(上・左・右・下。1 辺)。戻る・前・次のボタンが面の端に接して見えたので足した。</summary>
    public const double DetailInnerMargin = 8;

    /// <summary>詳細画面の内側の余白の、左右の合計。</summary>
    public const double DetailInnerMarginWidth = 2 * DetailInnerMargin;

    /// <summary>詳細画面の画像の欄の、画像のまわりの余白(左右の合計 6 × 2)。</summary>
    public const double DetailImagePadding = 12;
}
