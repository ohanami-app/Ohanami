using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 格子状のサムネイル一覧(ADR 0041)。1 つの要素が OnRender で「見えているセルだけ」を描く(子要素・Binding・テンプレートなし)。
/// 位置はセルサイズ固定の算術だけで決まる。当たり判定・選択・ドラッグ開始もこのクラスがコードで持つ。
/// 毎フレーム、ウィンドウの Rendering ハンドラから <see cref="Update"/> が呼ばれる(呼ぶ順序は MainWindow が決める)。
/// 項目は <see cref="GridItem"/>(内容ハッシュ + パス)で、<see cref="SetItems"/> で丸ごと入れ替える(検索のたび)。サムネイルはハッシュで引く。
/// ベンチ用の仕掛け(フレーム時間の記録、ttfp、自動スクロール)は持たない。
/// </summary>
internal sealed class ThumbnailGridControl : FrameworkElement
{
    private IReadOnlyList<GridItem> _items = [];
    private int _itemsVersion;
    private ThumbnailLoader? _loader;
    private readonly double _cellWDip, _cellHDip, _gapDip, _prefetchScreens;
    private readonly Func<GridItem, IReadOnlyList<string>> _pathsOf;

    // デバイスピクセルに揃えたレイアウト(DPI が分かってから決まる)
    private double _scale = 1.0;
    private double _cellW, _cellH, _gap;
    private int _cols = 1;
    private double _offset;           // DIP。デバイスピクセルに丸めた値を使う
    private double _lastOffsetForDir;
    private int _dir = 1;

    private readonly HashSet<int> _selected = [];
    private int _hover = -1;

    // ホバーのツールチップ: WPF の ToolTipService に頼らず、グリッドが自分で管理する(<see cref="HoverTooltip"/>)
    private HoverTooltip _hoverTip = null!;
    private Window? _hookedWindow;
    private int _firstVisible, _lastVisible;
    private bool _dirty = true;

    // 先読みの欲しいリスト・保護する(見えている)ハッシュは、画面の範囲か項目が変わったときだけ作り直す
    private (int First, int Last, int Dir, int Cols, int Version, int Height) _wantedKey = (-1, -1, 0, 0, -1, -1);
    private (int First, int Last, int Version) _protectedKey = (-1, -1, -1);

    // ドラッグ開始の判定
    private Point _downPoint;
    private int _downIndex = -1;
    /// <summary>押したままの動きで、ドラッグを始めてよいか。ダブルクリックの 2 回目の押下では false(<see cref="MouseShortcuts.ArmsDrag"/>)。</summary>
    private bool _dragArmed;
    private bool _pendingSingleSelect;

    /// <summary>Shift+クリックの範囲選択の起点(最後に、普通のクリックか Ctrl+クリックで押した項目の添字。無ければ -1)。規則は <see cref="GridSelectionRules"/>。</summary>
    private int _anchor = -1;

    // 文字(空のとき・サムネイルが作れなかったセル)
    private FormattedText? _emptyText, _unavailableText, _undecodableText;
    private string _emptyMessage = "";

    /// <summary>
    /// 再描画・更新が要るようになった(オフセット・選択・ホバー・項目・大きさの変化)。MainWindow は、これが来たときだけ毎フレームの購読をつなぎ、
    /// <see cref="NeedsFrame"/> が false になったら外す(放置のあいだ UI スレッドを回さない)。UI スレッドで呼ばれる。
    /// </summary>
    public Action? FrameRequested;

    /// <summary>項目を開く合図(ダブルクリック、Enter)。詳細画面の入口。</summary>
    public Action<GridItem>? ItemActivated;

    /// <summary>ホバーで出す説明(統合順位など)を、項目から作る。無ければツールチップなし。</summary>
    public Func<GridItem, string?>? TooltipOf;

    /// <summary>ビュー(オフセット・列数・大きさ)が変わった。スクロールバーの同期用。</summary>
    public Action? ViewChanged;

    /// <summary>選択が変わった(利用者の操作で)。</summary>
    public Action? SelectionChanged;

    /// <summary>画面に出して知らせたいこと(ドラッグできなかった理由など)。</summary>
    public Action<string>? Notice;

    /// <summary>ドラッグしようとして、元のファイルが 1 つも無かった(実在を確かめて分かった)項目。呼び出し側が、そのフォルダを走査し直す。</summary>
    public Action<IReadOnlyList<GridItem>>? MissingFilesDetected;

    /// <summary>ドラッグが終わった。(結果、渡したファイル数、見つからなかった画像の数、先頭のパス)</summary>
    public Action<DragDropEffects, int, int, string?>? DragFinished;

    private static readonly Typeface TextTypeface = new("Yu Gothic UI");

    public ThumbnailGridControl(double cellWidthDip, double cellHeightDip, double gapDip, double prefetchScreens, Func<GridItem, IReadOnlyList<string>> pathsOf)
    {
        ArgumentNullException.ThrowIfNull(pathsOf);
        _pathsOf = pathsOf;
        _cellWDip = cellWidthDip;
        _cellHDip = cellHeightDip;
        _gapDip = gapDip;
        _prefetchScreens = prefetchScreens;
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        // ツールチップは、グリッドの ToolTip プロパティに代入しない(WPF の持ち主の探索は「マウスの下の要素が変わったとき」にしか走らず、1 つの要素のグリッドでは、ほぼ出ない)。HoverTooltip が管理する
        _hoverTip = new HoverTooltip(TooltipOfIndex, new WpfToolTipSurface(this), new DispatcherTooltipTimer());
        Loaded += (_, _) =>
        {
            _hookedWindow = Window.GetWindow(this);
            if (_hookedWindow is not null) _hookedWindow.Deactivated += OnWindowDeactivated;   // 別のアプリに移ったら、ツールチップを残さない
        };
        Unloaded += (_, _) =>
        {
            if (_hookedWindow is not null) _hookedWindow.Deactivated -= OnWindowDeactivated;
            _hookedWindow = null;
            _hoverTip.Dismiss();
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) _hoverTip.Dismiss(); };
        // 一覧は文字を受けない。日本語 IME がオンのままだと、E のような単体キーが IME に食われて(Key.ImeProcessed)ショートカットが動かないので、IME を無効にする
        InputMethod.SetIsInputMethodEnabled(this, false);
        RecomputeLayout();
    }

    private void MarkDirty()
    {
        _dirty = true;
        FrameRequested?.Invoke();
    }

    /// <summary>次のフレームで <see cref="Update"/> と再描画が要るか。</summary>
    public bool NeedsFrame => _dirty;


    // ------------------------------------------------------------ 公開

    public ThumbnailLoader? Loader
    {
        get => _loader;
        set
        {
            _loader = value;
            _wantedKey = (-1, -1, 0, 0, -1, -1);
            _protectedKey = (-1, -1, -1);
            MarkDirty();
        }
    }

    /// <summary>項目が 0 件のとき、中央に出す文字。</summary>
    public string EmptyMessage
    {
        get => _emptyMessage;
        set
        {
            if (value == _emptyMessage) return;
            _emptyMessage = value;
            _emptyText = null;
            MarkDirty();
        }
    }

    public double Scale => _scale;
    public int Columns => _cols;
    public double RowHeight => _cellH + _gap;
    public int RowCount => (_items.Count + _cols - 1) / _cols;
    public double CellWidthPx => _cellW * _scale;
    public double CellHeightPx => _cellH * _scale;
    public double ExtentHeight => _gap + RowCount * (_cellH + _gap);
    public double ViewportHeight => ActualHeight;
    public double MaxOffset => Math.Max(0, ExtentHeight - ActualHeight);
    public int FirstVisible => _firstVisible;
    public int LastVisible => _lastVisible;
    public int ItemCount => _items.Count;
    public IReadOnlyList<GridItem> Items => _items;

    public double Offset
    {
        get => _offset;
        set
        {
            double v = Math.Max(0, Math.Min(value, MaxOffset));
            v = Math.Round(v * _scale) / _scale;     // デバイスピクセルに丸める(にじみ防止)
            if (v == _offset) return;
            _offset = v;
            _hoverTip.Dismiss();   // スクロールした: マウスの下のサムネイルが替わるので、開いているツールチップを閉じる
            MarkDirty();
            ViewChanged?.Invoke();
        }
    }

    /// <summary>
    /// 表示する項目を丸ごと入れ替える(検索のたび)。既定では、選択・ホバー・スクロール位置は捨てて、先頭に戻る。
    /// <paramref name="keepView"/> が真のとき(検索していないときの一覧に、走査で新しい画像が入った場合)は、見ている位置と選択を保つ:
    /// 選択は、新しい項目にも残っているハッシュだけを残す(消えた項目の分は外れる)。スクロールは <see cref="KeptOffset"/> で決める。
    /// </summary>
    public void SetItems(IReadOnlyList<GridItem> items, bool keepView = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        IReadOnlyList<GridItem> old = _items;
        double oldOffset = _offset;
        HashSet<string>? keptSelection = keepView ? _selected.Select(i => old[i].Hash).ToHashSet(StringComparer.Ordinal) : null;
        string? keptAnchor = keepView && _anchor >= 0 && _anchor < old.Count ? old[_anchor].Hash : null;
        _items = items;
        _itemsVersion++;
        _selected.Clear();
        _anchor = -1;   // 既定(検索のたび)は、起点も捨てる。keepView のときだけ、下で、起点の画像が残っていればハッシュで追う
        _hover = -1;
        _downIndex = -1;
        if (keepView)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (keptSelection!.Contains(items[i].Hash)) _selected.Add(i);
                if (keptAnchor is not null && _anchor < 0 && items[i].Hash == keptAnchor) _anchor = i;   // 起点の画像が消えていれば、起点なし(-1 のまま)
            }
            _offset = KeptOffset(old, oldOffset, items);
            _lastOffsetForDir = _offset;   // 向き(先読みの方向)は、今までのまま
        }
        else
        {
            _offset = 0;
            _lastOffsetForDir = 0;
            _dir = 1;
        }
        _hoverTip.Hover(-1);   // 項目が替わった: 開いているツールチップを閉じ、待ちもやめる(マウスが動けば、新しい項目で待ちから始まる)
        _loader?.ForgetUnavailable();   // 前に「原本なし」だったセルは、もう一度試す(その後に原本が戻っているかもしれない)
        MarkDirty();
        ViewChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// 項目を入れ替えたあとのスクロール位置(<see cref="SetItems"/> の keepView)。見ていた画像が、画面の同じ行に残るようにする:
    /// 1 番上を見ていた(0)なら 0 のまま(新しい画像が見える)。そうでなければ、見えていた行の左端から順に、新しい項目にも残っている最初の画像を目印にして、
    /// その画像の行が動いた分(行数 × 行の高さ。行の途中の位置もそのまま)だけ位置をずらす。目印が 1 つも残っていなければ、位置の値をそのまま使う。
    /// どの場合も、新しい項目の範囲に収める。列数は変わらない(SetItems は列数を動かさない)。
    /// </summary>
    private double KeptOffset(IReadOnlyList<GridItem> old, double oldOffset, IReadOnlyList<GridItem> items)
    {
        double rowH = _cellH + _gap;
        double result = oldOffset;
        if (oldOffset > 0 && old.Count > 0 && items.Count > 0)
        {
            var newIndex = new Dictionary<string, int>(items.Count, StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++) newIndex.TryAdd(items[i].Hash, i);
            int firstRow = Math.Max(0, (int)Math.Floor((oldOffset - _gap) / rowH));
            int lastRow = Math.Max(firstRow, (int)Math.Floor((oldOffset + ActualHeight - _gap) / rowH));
            int from = Math.Min(old.Count - 1, firstRow * _cols);
            int to = Math.Min(old.Count - 1, (lastRow + 1) * _cols - 1);
            for (int i = from; i <= to; i++)
            {
                if (!newIndex.TryGetValue(old[i].Hash, out int j)) continue;
                result = oldOffset + (j / _cols - i / _cols) * rowH;
                break;
            }
        }
        double max = Math.Max(0, _gap + ((items.Count + _cols - 1) / _cols) * rowH - ActualHeight);
        double v = Math.Max(0, Math.Min(result, max));
        return Math.Round(v * _scale) / _scale;
    }

    /// <summary>
    /// 選択を、指定したハッシュの項目だけにする(一覧にあるものだけ。ほかは無視)。利用者の選択と同じに、選択の変化を知らせる。
    /// マウスを使わない試験のため(ShellBoundaryGuardTests が、試験でのマウスの合成を禁じている)。
    /// </summary>
    public void SelectByHashes(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var wanted = hashes.ToHashSet(StringComparer.Ordinal);
        _selected.Clear();
        _anchor = -1;   // 起点は「クリックで選んだ項目」なので、この入口(クリックでない選択)では持たない
        for (int i = 0; i < _items.Count; i++)
        {
            if (wanted.Contains(_items[i].Hash)) _selected.Add(i);
        }
        MarkDirty();
        SelectionChanged?.Invoke();
    }

    /// <summary>DPI(表示スケール)が決まった/変わったとき。セルのサイズはデバイスピクセルの整数に揃える。</summary>
    public void SetScale(double scale)
    {
        _scale = scale;
        _unavailableText = null;
        _undecodableText = null;
        _emptyText = null;
        RecomputeLayout();
        MarkDirty();
    }

    private void RecomputeLayout()
    {
        _cellW = Math.Round(_cellWDip * _scale) / _scale;
        _cellH = Math.Round(_cellHDip * _scale) / _scale;
        _gap = Math.Round(_gapDip * _scale) / _scale;
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        double w = ActualWidth;
        int cols = Math.Max(1, (int)Math.Floor((w - _gap) / (_cellW + _gap)));
        if (cols != _cols)
        {
            // 列数が変わると、画面の先頭の項目が動かないようにオフセットを合わせる
            int firstItem = (int)Math.Floor(Math.Max(0, _offset - _gap) / (_cellH + _gap)) * _cols;
            _cols = cols;
            _offset = Math.Round(Math.Min(MaxOffset, (firstItem / _cols) * (_cellH + _gap)) * _scale) / _scale;
            MarkDirty();
            ViewChanged?.Invoke();
        }
    }

    public IEnumerable<GridItem> SelectedItems() => _selected.OrderBy(i => i).Select(i => _items[i]);
    public int SelectedCount => _selected.Count;

    public void SelectAll()
    {
        _selected.Clear();
        for (int i = 0; i < _items.Count; i++) _selected.Add(i);
        MarkDirty();
        SelectionChanged?.Invoke();
    }

    // ------------------------------------------------------------ レイアウトと描画

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    private Size _lastArranged;

    protected override Size ArrangeOverride(Size finalSize)
    {
        // InvalidateVisual は再配置も起こす。大きさが変わっていなければ何もしない(でないと、再描画の要求が再配置を呼び、再配置が再描画を要求して、止まらなくなる)
        if (finalSize == _lastArranged) return finalSize;
        _lastArranged = finalSize;
        UpdateColumns();
        // サイズが変わって最大オフセットが小さくなったときの補正
        if (_offset > MaxOffset) Offset = MaxOffset;
        MarkDirty();
        ViewChanged?.Invoke();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // 実際の処理(ローダーの作り直し)は MainWindow が行う。ここではレイアウトだけ更新。
        SetScale(newDpi.DpiScaleX);
    }

    private Rect CellRect(int index)
    {
        int row = index / _cols;
        int col = index % _cols;
        double margin = LeftMargin();
        double x = margin + col * (_cellW + _gap);
        double y = _gap + row * (_cellH + _gap) - _offset;
        return new Rect(x, y, _cellW, _cellH);
    }

    private double LeftMargin()
    {
        double used = _cols * (_cellW + _gap) - _gap;
        double m = Math.Max(_gap, (ActualWidth - used) / 2);
        return Math.Round(m * _scale) / _scale;
    }

    private void ComputeVisibleRange()
    {
        if (_items.Count == 0) { _firstVisible = 0; _lastVisible = -1; return; }
        double rowH = _cellH + _gap;
        int firstRow = Math.Max(0, (int)Math.Floor((_offset - _gap) / rowH));
        int lastRow = Math.Max(firstRow, (int)Math.Floor((_offset + ActualHeight - _gap) / rowH));
        _firstVisible = Math.Min(_items.Count - 1, firstRow * _cols);
        _lastVisible = Math.Min(_items.Count - 1, (lastRow + 1) * _cols - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(AppColors.GridBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_items.Count == 0)
        {
            DrawCentered(dc, _emptyText ??= MakeText(_emptyMessage, 14), new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }
        ComputeVisibleRange();
        for (int i = _firstVisible; i <= _lastVisible; i++)
        {
            Rect r = CellRect(i);
            LoadedThumb? t = null;
            bool have = _loader != null && _loader.TryGet(_items[i].Hash, out t);
            if (have && t!.Bitmap != null)
            {
                // 箱に収まる大きさで作ってあるので、デバイスピクセルで等倍に置く(中央寄せ)
                double bw = t.Bitmap.PixelWidth / _scale;
                double bh = t.Bitmap.PixelHeight / _scale;
                double bx = r.X + Math.Round((r.Width - bw) / 2 * _scale) / _scale;
                double by = r.Y + Math.Round((r.Height - bh) / 2 * _scale) / _scale;
                dc.DrawRectangle(AppColors.GridPlaceholders[0], null, r);
                dc.DrawImage(t.Bitmap, new Rect(bx, by, bw, bh));
            }
            else if (have)
            {
                dc.DrawRectangle(AppColors.GridFailed, null, r);
                FormattedText text = t!.Status == ThumbnailStatus.SourceUnavailable
                    ? _unavailableText ??= MakeText("元のファイルがありません", 12)
                    : _undecodableText ??= MakeText("読み込めません", 12);
                DrawCentered(dc, text, r);
            }
            else
            {
                dc.DrawRectangle(AppColors.GridPlaceholders[i & 7], null, r);
            }
            if (_selected.Contains(i)) dc.DrawRectangle(null, AppColors.GridSelected, Rect.Inflate(r, -1.5, -1.5));
            else if (i == _hover) dc.DrawRectangle(null, AppColors.GridHover, Rect.Inflate(r, -0.5, -0.5));
        }
    }

    private FormattedText MakeText(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface, size, AppColors.GridText, _scale);

    private static void DrawCentered(DrawingContext dc, FormattedText text, Rect area) =>
        dc.DrawText(text, new Point(area.X + Math.Max(0, (area.Width - text.Width) / 2), area.Y + Math.Max(0, (area.Height - text.Height) / 2)));

    // ------------------------------------------------------------ フレームごとの更新

    /// <summary>
    /// 毎フレーム 1 回(Rendering ハンドラから)。ローダーの結果を取り込み、欲しい項目リストを更新し、必要なら再描画を要求する。
    /// </summary>
    public void Update()
    {
        ComputeVisibleRange();

        if (_offset != _lastOffsetForDir)
        {
            _dir = _offset > _lastOffsetForDir ? 1 : -1;
            _lastOffsetForDir = _offset;
        }

        if (_loader != null)
        {
            UpdateProtected();
            if (_loader.Drain()) MarkDirty();
            UpdateWanted();
        }

        if (_dirty)
        {
            _dirty = false;
            InvalidateVisual();
        }
    }

    private void UpdateProtected()
    {
        var key = (_firstVisible, _lastVisible, _itemsVersion);
        if (key == _protectedKey) return;
        _protectedKey = key;
        var visible = new HashSet<string>(Math.Max(0, _lastVisible - _firstVisible + 1));
        for (int i = _firstVisible; i <= _lastVisible; i++) visible.Add(_items[i].Hash);
        _loader!.SetProtected(visible);
    }

    private void UpdateWanted()
    {
        var key = (_firstVisible, _lastVisible, _dir, _cols, _itemsVersion, (int)ActualHeight);
        if (key == _wantedKey) return;
        _wantedKey = key;
        if (_items.Count == 0) { _loader!.SetWanted([]); return; }

        int rows = (int)Math.Ceiling(ActualHeight / (_cellH + _gap));
        int ahead = (int)Math.Ceiling(rows * _prefetchScreens) * _cols;
        int behind = (int)Math.Ceiling(rows * 0.5) * _cols;
        var list = new List<string>(_lastVisible - _firstVisible + 1 + ahead + behind);
        for (int i = _firstVisible; i <= _lastVisible; i++) list.Add(_items[i].Hash);
        if (_dir >= 0)
        {
            for (int i = _lastVisible + 1; i <= Math.Min(_items.Count - 1, _lastVisible + ahead); i++) list.Add(_items[i].Hash);
            for (int i = _firstVisible - 1; i >= Math.Max(0, _firstVisible - behind); i--) list.Add(_items[i].Hash);
        }
        else
        {
            for (int i = _firstVisible - 1; i >= Math.Max(0, _firstVisible - ahead); i--) list.Add(_items[i].Hash);
            for (int i = _lastVisible + 1; i <= Math.Min(_items.Count - 1, _lastVisible + behind); i++) list.Add(_items[i].Hash);
        }
        _loader!.SetWanted(list.ToArray());
    }

    // ------------------------------------------------------------ 入力

    private int IndexAt(Point p)
    {
        if (_items.Count == 0) return -1;
        double margin = LeftMargin();
        double fx = p.X - margin;
        double fy = p.Y + _offset - _gap;
        if (fx < 0 || fy < 0) return -1;
        double unitW = _cellW + _gap, unitH = _cellH + _gap;
        int col = (int)Math.Floor(fx / unitW);
        int row = (int)Math.Floor(fy / unitH);
        if (col >= _cols) return -1;
        if (fx - col * unitW > _cellW || fy - row * unitH > _cellH) return -1; // すき間
        int idx = row * _cols + col;
        return idx < _items.Count ? idx : -1;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _hoverTip.Dismiss();
        Offset -= e.Delta / 120.0 * (_cellH + _gap) * 0.75;
        SetHover(IndexAt(e.GetPosition(this)));   // マウスは動いていないが、下のサムネイルが替わった: ホバーを取り直す(別のサムネイルなら、待ちから始まる)
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _hoverTip.Dismiss();   // キーの操作では出さない(ツールチップはマウスのホバーだけ)。出ていれば閉じる(Enter で詳細に移るときなど)
        double page = Math.Max(_cellH + _gap, ActualHeight - (_cellH + _gap));
        // IME が処理したキーは、IME が食う前の本当のキーで判定する(一覧は IME を無効にしているので、通常は来ない。念のため)
        switch (KeyShortcuts.EffectiveKey(e.Key, e.ImeProcessedKey))
        {
            case Key.PageDown: Offset += page; e.Handled = true; break;
            case Key.PageUp: Offset -= page; e.Handled = true; break;
            case Key.Down: Offset += _cellH + _gap; e.Handled = true; break;
            case Key.Up: Offset -= _cellH + _gap; e.Handled = true; break;
            case Key.Home: Offset = 0; e.Handled = true; break;
            case Key.End: Offset = MaxOffset; e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control: SelectAll(); e.Handled = true; break;
            case Key.Enter when _selected.Count > 0: ItemActivated?.Invoke(_items[_selected.Min()]); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        SetHover(-1);
        base.OnMouseLeave(e);
    }

    /// <summary>ホバーしているサムネイルの添字を決める(マウスの動き・離れる・ホイールのあと。-1 は、どのサムネイルの上でもない)。変わったときだけ、ツールチップの待ちを始め直す。</summary>
    internal void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        MarkDirty();
        _hoverTip.Hover(index);
    }

    /// <summary>ツールチップの内容(サムネイルの添字から。開く直前に <see cref="HoverTooltip"/> が呼ぶ)。</summary>
    private string? TooltipOfIndex(int index) => index >= 0 && index < _items.Count ? TooltipOf?.Invoke(_items[index]) : null;

    /// <summary>ツールチップを閉じる(ボタンを押した・ウィンドウが非アクティブなど)。同じサムネイルの上では、開き直さない。</summary>
    internal void DismissTooltip() => _hoverTip.Dismiss();

    private void OnWindowDeactivated(object? sender, EventArgs e) => DismissTooltip();

    /// <summary>テスト用: ツールチップのタイマーと表示の面を差し替える(実際のマウス・ウィンドウなしで、状態の流れを確かめる)。</summary>
    internal void UseTooltipParts(ITooltipSurface surface, ITooltipTimer timer) =>
        _hoverTip = new HoverTooltip(TooltipOfIndex, surface, timer);

    /// <summary>テスト用: ツールチップの状態。</summary>
    internal HoverTooltip HoverTipForTests => _hoverTip;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _hoverTip.Dismiss();
        Focus();
        Point p = e.GetPosition(this);
        int idx = IndexAt(p);
        _downPoint = p;
        _downIndex = idx;
        _dragArmed = MouseShortcuts.ArmsDrag(e.ClickCount);   // ダブルクリックの 2 回目は、詳細を開く操作。指がわずかに動いても、ドラッグを始めない
        ApplyClick(idx, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (e.ClickCount == 2 && idx >= 0) ItemActivated?.Invoke(_items[idx]);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// クリックによる選択を適用する(マウスの左ボタンを押したとき。規則は <see cref="GridSelectionRules.Click"/>)。<paramref name="index"/> は押した項目の添字(空きの場所は -1)。
    /// **試験用の入口でもある**(ShellBoundaryGuardTests が、試験でのマウスのイベントの合成を禁じているので、試験はここへ直接当てる)。
    /// </summary>
    internal void ApplyClick(int index, bool ctrl, bool shift)
    {
        if (index >= _items.Count) throw new ArgumentOutOfRangeException(nameof(index), index, "一覧の外の項目");
        ClickOutcome o = GridSelectionRules.Click(_selected, _anchor, index, ctrl, shift);
        _anchor = o.Anchor;
        _pendingSingleSelect = o.PendingSingleSelect;   // 複数選択の 1 つを押した: ドラッグならそのまま群を運び、離しただけなら単独選択にする
        if (!o.Changed) return;
        _selected.Clear();
        _selected.UnionWith(o.Selected);
        MarkDirty();
        SelectionChanged?.Invoke();
    }

    /// <summary>テスト用: 範囲選択の起点にしている項目のハッシュ(無ければ null)。</summary>
    internal string? AnchorHashForTests => _anchor >= 0 && _anchor < _items.Count ? _items[_anchor].Hash : null;

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _hoverTip.Dismiss();
        Focus();
        int idx = IndexAt(e.GetPosition(this));
        if (idx >= 0 && !_selected.Contains(idx)) ApplyClick(idx, ctrl: false, shift: false);   // 選択に入っていない項目の右クリックは、普通のクリックと同じ(その 1 枚だけを選ぶ)
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        SetHover(IndexAt(p));

        if (e.LeftButton == MouseButtonState.Pressed && _dragArmed && _downIndex >= 0 && IsMouseCaptured)
        {
            if (Math.Abs(p.X - _downPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _downPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                int start = _downIndex;
                _downIndex = -1;
                ReleaseMouseCapture();
                StartDrag(start);
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_pendingSingleSelect && _downIndex >= 0)
        {
            _selected.Clear();
            _selected.Add(_downIndex);
            MarkDirty();
            SelectionChanged?.Invoke();
        }
        _pendingSingleSelect = false;
        _downIndex = -1;
        base.OnMouseLeftButtonUp(e);
    }

    /// <summary>
    /// OS 標準のドラッグ&ドロップで、選択中のファイル(フルパス)を外へ渡す。
    /// 許可する操作は Copy のみ(Move を許すと、ドロップ先によって原本が移動・削除されうるため)。
    /// 渡す前に、実在するファイルだけに絞る(検索結果のパスは DB の印だけで決まっていて、ファイルが今も在るかは確かめていない)。1 つも無ければ始めない。
    /// </summary>
    private void StartDrag(int startIndex)
    {
        if (!_selected.Contains(startIndex))
        {
            _selected.Clear();
            _selected.Add(startIndex);
            MarkDirty();
            SelectionChanged?.Invoke();
        }
        (string[] paths, int missing) = FileLocator.Resolve(SelectedItems().Select(_pathsOf));
        if (missing > 0)
        {
            GridItem[] gone = SelectedItems().Where(i => FileLocator.FirstExisting(_pathsOf(i)) is null).ToArray();
            if (gone.Length > 0) MissingFilesDetected?.Invoke(gone);
        }
        if (paths.Length == 0)
        {
            Notice?.Invoke("元のファイルが無いので、ドラッグできません (移動・削除された可能性があります)");
            return;
        }
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        DragDropEffects result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        DragFinished?.Invoke(result, paths.Length, missing, paths[0]);
    }
}
