using System.Windows.Media;

namespace Ohanami.App;

/// <summary>
/// このアプリの**自前の色・ブラシを、直書きのまま 1 か所に集めたもの**(ADR 0040 の決まり 2)。画面のコードは、色を直接書かず、ここから取る
/// (<c>Brushes.</c>・<c>Color.FromRgb</c>・<c>new SolidColorBrush</c>・<c>SystemColors.</c> は、このファイル以外に書かない。ThemeGuardTests が見張る)。
/// <para>
/// **Fluent(<see cref="AppTheme"/>)のリソースのキーは、ここでも引かない**。キーは .NET の版で変わりうる(消えると黙って既定値になる)ため。Fluent の部品(Button・TextBox・ListBox など)の
/// 色は Fluent まかせにし、面の色(窓・一覧・詳細)と意味の色(エラーの赤・補助の灰)だけをここで決める。
/// どれも、Fluent(ライト)の上でも、WPF 標準の見た目(Aero2)の上でも読める値にしてある(外すときに、ここは直さなくてよい)。
/// </para>
/// </summary>
internal static class AppColors
{
    // ---- 面(窓・パネル)。Fluent(ライト)の下地(Windows 11 の明るい標準の灰色)と同じ値で、窓・詳細画面・状態のバーをそろえる

    /// <summary>窓の背景の色(<see cref="WindowBackground"/> の元の値。タイトルバーの色にも同じ値を使う: <see cref="AppTheme.StyleWindow"/>)。**static の初期化は書いた順**なので、これを使う <see cref="WindowBackground"/> より前に宣言する(AppColorsTests が見張る)。</summary>
    public static readonly Color WindowBackgroundColor = Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// <summary>窓の背景(メイン窓・状態の窓・手動タグの窓)。Mica は切ってあるので(<see cref="AppTheme"/>)、壁紙に左右されない固定の色。詳細画面の背景も同じ(面の色をそろえる)。</summary>
    public static readonly Brush WindowBackground = Solid(WindowBackgroundColor.R, WindowBackgroundColor.G, WindowBackgroundColor.B);

    /// <summary>文字の一覧(左のフォルダ一覧・状態の窓の一覧)と、左の「インデックス」欄の下地。窓の背景より明るくして、一覧の区切りを出す。</summary>
    public static readonly Brush ListSurface = Solid(0xFF, 0xFF, 0xFF);

    /// <summary>一覧・「インデックス」欄の枠。</summary>
    public static readonly Brush ListBorder = Solid(0xD0, 0xD0, 0xD0);

    /// <summary>状態のバーの背景(窓と同じ。上の枠線だけで区切る)。</summary>
    public static readonly Brush StatusBarBackground = Solid(0xF3, 0xF3, 0xF3);

    /// <summary>状態のバーの上の枠線。</summary>
    public static readonly Brush StatusBarBorder = Solid(0xD0, 0xD0, 0xD0);

    // ---- 意味の色(文字)

    /// <summary>エラー・警告の赤(タグのエラー文・注意文・欠けたパス・手動タグの窓のエラー・通知の帯の背景)。</summary>
    public static readonly Brush Warn = Solid(0xB0, 0x2A, 0x2A);

    /// <summary>成功・できている状態の緑(セットアップの画面の「準備完了」)。白(一覧の面)と #F3F3F3(窓の背景)の上で、文字として読める濃さ(AppColorsTests が、コントラスト比 4.5 以上を見張る)。</summary>
    public static readonly Brush Success = Solid(0x1B, 0x7A, 0x34);

    /// <summary>通知の帯・暗い画像欄の上の白い文字。</summary>
    public static readonly Brush OnDark = Solid(0xFF, 0xFF, 0xFF);

    /// <summary>失敗の赤(起動できなかった状態のバー・取り込みのエラーがあるフォルダの行)。</summary>
    public static readonly Brush Danger = Solid(0xB2, 0x22, 0x22);

    /// <summary>補助の文字(詳細画面の欄名・注記・パスの更新日時など)。</summary>
    public static readonly Brush Muted = Solid(0x70, 0x70, 0x70);

    /// <summary>副の情報の文字(「インデックス」欄の内訳・補足の行、状態の窓の理由の列)。</summary>
    public static readonly Brush Secondary = Solid(0x69, 0x69, 0x69);

    /// <summary>薄い文字(読めない行・候補のラベル)。</summary>
    public static readonly Brush Faint = Solid(0x80, 0x80, 0x80);

    /// <summary>一致した箇所の強調の背景(黄)。</summary>
    public static readonly Brush Highlight = Solid(0xFF, 0xE0, 0x6B);

    public static readonly Brush Transparent = Solid(0x00, 0x00, 0x00, 0x00);

    // ---- 手動タグのチップ

    public static readonly Brush ChipBackground = Solid(0xFF, 0xFF, 0xFF);

    public static readonly Brush ChipBorder = Solid(0xCC, 0xD0, 0xD8);

    // ---- 画像を見せる暗い面

    /// <summary>詳細画面の画像欄の背景。</summary>
    public static readonly Brush ImageHost = Solid(0x1E, 0x1E, 0x1E);

    /// <summary>画像の一覧(自前描画の ThumbnailGridControl)の背景。</summary>
    public static readonly Brush GridBackground = Solid(0x1E, 0x1E, 0x22);

    /// <summary>画像を読めなかったセルの背景。</summary>
    public static readonly Brush GridFailed = Solid(0x4A, 0x24, 0x24);

    /// <summary>一覧のセルの文字(「元のファイルがありません」など)。</summary>
    public static readonly Brush GridText = Solid(0xA8, 0xA8, 0xB0);

    /// <summary>選択したセルの枠(3px)。</summary>
    public static readonly Pen GridSelected = FrozenPen(new Pen(Solid(0x4D, 0xA3, 0xFF), 3));

    /// <summary>マウスを置いたセルの枠(1px)。</summary>
    public static readonly Pen GridHover = FrozenPen(new Pen(Solid(0xAA, 0xAA, 0xAA), 1));

    /// <summary>サムネイルを読み込むまでの、セルの下地(8 色の濃淡)。</summary>
    public static readonly Brush[] GridPlaceholders = BuildGridPlaceholders();

    private static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen p)
    {
        p.Freeze();
        return p;
    }

    private static Brush[] BuildGridPlaceholders()
    {
        var list = new Brush[8];
        for (int i = 0; i < list.Length; i++)
        {
            byte v = (byte)(0x2C + i * 6);
            list[i] = Solid(v, v, (byte)(v + 8));
        }
        return list;
    }
}
