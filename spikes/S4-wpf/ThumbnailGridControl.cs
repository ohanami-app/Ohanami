using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace S4Wpf;

/// <summary>
/// 格子状のサムネイル一覧。1つの要素が OnRender で「見えているセルだけ」を描く(子要素・Binding・テンプレートなし)。
/// 位置はセルサイズ固定の算術だけで決まる。当たり判定・選択・ドラッグ開始もこのクラスがコードで持つ。
/// 毎フレーム、ウィンドウの Rendering ハンドラから <see cref="Update"/> が呼ばれる(呼ぶ順序は MainWindow が決める)。
/// </summary>
public sealed class ThumbnailGridControl : FrameworkElement
{
    private readonly IReadOnlyList<ThumbItem> _items;
    private ThumbnailLoader? _loader;
    private readonly double _cellWDip, _cellHDip, _gapDip, _prefetchScreens;

    // デバイスピクセルに揃えたレイアウト(DPI が分かってから決まる)
    private double _scale = 1.0;
    private double _cellW, _cellH, _gap;
    private int _cols = 1;
    private double _offset;           // DIP。デバイスピクセルに丸めた値を使う
    private double _lastOffsetForDir;
    private int _dir = 1;

    private readonly HashSet<int> _selected = new();
    private int _hover = -1;
    private int _firstVisible, _lastVisible;
    private bool _dirty = true;

    // ドラッグ開始の判定
    private Point _downPoint;
    private int _downIndex = -1;
    private bool _pendingSingleSelect;

    // 画面に入ってから画像が出るまでの時間(ttfp)の記録
    private readonly Dictionary<int, double> _enteredAtMs = new();
    private readonly HashSet<int> _visibleSet = new();
    public Action<double>? TtfpRecorded;
    public Action? ViewChanged;
    public Action<DragDropEffects, int, string>? DragFinished;

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22)));
    private static readonly Brush FailedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x70, 0x20, 0x20)));
    private static readonly Pen SelectedPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)), 3));
    private static readonly Pen HoverPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), 1));
    private static readonly Brush[] Placeholders = BuildPlaceholders();

    public ThumbnailGridControl(IReadOnlyList<ThumbItem> items, double cellWidthDip, double cellHeightDip, double gapDip, double prefetchScreens)
    {
        _items = items;
        _cellWDip = cellWidthDip;
        _cellHDip = cellHeightDip;
        _gapDip = gapDip;
        _prefetchScreens = prefetchScreens;
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        RecomputeLayout();
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    private static Brush[] BuildPlaceholders()
    {
        var list = new Brush[8];
        for (int i = 0; i < list.Length; i++)
        {
            byte v = (byte)(0x2C + i * 6);
            var b = new SolidColorBrush(Color.FromRgb(v, v, (byte)(v + 8)));
            b.Freeze();
            list[i] = b;
        }
        return list;
    }

    // ------------------------------------------------------------ 公開

    /// <summary>true なら毎フレーム再描画する(ベンチ中。フレームが途切れて Rendering イベントが止まるのを防ぐ)。</summary>
    public bool AlwaysRedraw;

    public ThumbnailLoader? Loader { get => _loader; set { _loader = value; _dirty = true; } }
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
    public IReadOnlyList<ThumbItem> Items => _items;

    public double Offset
    {
        get => _offset;
        set
        {
            double v = Math.Max(0, Math.Min(value, MaxOffset));
            v = Math.Round(v * _scale) / _scale;     // デバイスピクセルに丸める(にじみ防止)
            if (v == _offset) return;
            _offset = v;
            _dirty = true;
            ViewChanged?.Invoke();
        }
    }

    /// <summary>DPI(表示スケール)が決まった/変わったとき。セルのサイズはデバイスピクセルの整数に揃える。</summary>
    public void SetScale(double scale)
    {
        _scale = scale;
        RecomputeLayout();
        _dirty = true;
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
            _dirty = true;
            ViewChanged?.Invoke();
        }
    }

    public IEnumerable<ThumbItem> SelectedItems() => _selected.OrderBy(i => i).Select(i => _items[i]);
    public int SelectedCount => _selected.Count;

    public void SelectAll()
    {
        _selected.Clear();
        for (int i = 0; i < _items.Count; i++) _selected.Add(i);
        _dirty = true;
    }

    /// <summary>画面に見えているセルの画像が、全部そろっているか(jump の落ち着き時間の判定用)。</summary>
    public bool AllVisibleLoaded()
    {
        if (_loader == null) return true;
        for (int i = _firstVisible; i <= _lastVisible; i++)
        {
            if (!_loader.Contains(i)) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ レイアウトと描画

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateColumns();
        // サイズが変わって最大オフセットが小さくなったときの補正
        if (_offset > MaxOffset) Offset = MaxOffset;
        _dirty = true;
        ViewChanged?.Invoke();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // 実際の処理(ローダーの作り直しなど)は MainWindow が行う。ここではレイアウトだけ更新。
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
        double rowH = _cellH + _gap;
        int firstRow = Math.Max(0, (int)Math.Floor((_offset - _gap) / rowH));
        int lastRow = Math.Max(firstRow, (int)Math.Floor((_offset + ActualHeight - _gap) / rowH));
        _firstVisible = Math.Min(_items.Count - 1, firstRow * _cols);
        _lastVisible = Math.Min(_items.Count - 1, (lastRow + 1) * _cols - 1);
    }

    // 直前の Rendering イベントからの OnRender の所要時間と、取り込んだ結果の数(遅いフレームの原因の分類用)
    private long _renderTicks;
    private int _drainedSince;

    /// <summary>OnRender(描画コマンドの記録)の所要時間の累計(ms)を取り出して 0 に戻す。</summary>
    public double TakeRenderMs()
    {
        double ms = _renderTicks * 1000.0 / Stopwatch.Frequency;
        _renderTicks = 0;
        return ms;
    }

    public int TakeDrained()
    {
        int n = _drainedSince;
        _drainedSince = 0;
        return n;
    }

    protected override void OnRender(DrawingContext dc)
    {
        long t0 = Stopwatch.GetTimestamp();
        try { RenderCells(dc); }
        finally { _renderTicks += Stopwatch.GetTimestamp() - t0; }
    }

    private void RenderCells(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_items.Count == 0) return;
        ComputeVisibleRange();
        for (int i = _firstVisible; i <= _lastVisible; i++)
        {
            Rect r = CellRect(i);
            LoadedThumb? t = null;
            bool have = _loader != null && _loader.TryGet(i, out t);
            if (have && t!.Bitmap != null)
            {
                // デコード時に箱へ収まる大きさにしてあるので、デバイスピクセルで等倍に置く(中央寄せ)
                double bw = t.Bitmap.PixelWidth / _scale;
                double bh = t.Bitmap.PixelHeight / _scale;
                double bx = r.X + Math.Round((r.Width - bw) / 2 * _scale) / _scale;
                double by = r.Y + Math.Round((r.Height - bh) / 2 * _scale) / _scale;
                dc.DrawRectangle(Placeholders[0], null, r);
                dc.DrawImage(t.Bitmap, new Rect(bx, by, bw, bh));
            }
            else if (have)
            {
                dc.DrawRectangle(FailedBrush, null, r);
            }
            else
            {
                dc.DrawRectangle(Placeholders[i & 7], null, r);
            }
            if (_selected.Contains(i)) dc.DrawRectangle(null, SelectedPen, Rect.Inflate(r, -1.5, -1.5));
            else if (i == _hover) dc.DrawRectangle(null, HoverPen, Rect.Inflate(r, -0.5, -0.5));
        }
    }

    // ------------------------------------------------------------ フレームごとの更新

    /// <summary>
    /// 毎フレーム1回(Rendering ハンドラから)。ローダーの結果を取り込み、欲しい項目リストを更新し、必要なら再描画を要求する。
    /// </summary>
    public void Update(double nowMs)
    {
        ComputeVisibleRange();

        if (_offset != _lastOffsetForDir)
        {
            _dir = _offset > _lastOffsetForDir ? 1 : -1;
            _lastOffsetForDir = _offset;
        }

        if (_loader != null)
        {
            _loader.SetProtected(_firstVisible, _lastVisible);
            List<int> added = _loader.Drain();
            _drainedSince += added.Count;
            if (added.Count > 0)
            {
                foreach (int a in added)
                {
                    if (a >= _firstVisible && a <= _lastVisible) { _dirty = true; break; }
                }
            }
            UpdateWanted();
        }

        UpdateTtfp(nowMs);

        if (_dirty || AlwaysRedraw)
        {
            _dirty = false;
            InvalidateVisual();
        }
    }

    private void UpdateWanted()
    {
        if (_loader == null) return;
        // 画面が動いたとき、または結果が取り込まれたあとにも、リストを作り直す(安いので毎フレームでよい)
        int rows = (int)Math.Ceiling(ActualHeight / (_cellH + _gap));
        int ahead = (int)Math.Ceiling(rows * _prefetchScreens) * _cols;
        int behind = (int)Math.Ceiling(rows * 0.5) * _cols;
        var list = new List<int>(_lastVisible - _firstVisible + 1 + ahead + behind);
        for (int i = _firstVisible; i <= _lastVisible; i++) list.Add(i);
        if (_dir >= 0)
        {
            for (int i = _lastVisible + 1; i <= Math.Min(_items.Count - 1, _lastVisible + ahead); i++) list.Add(i);
            for (int i = _firstVisible - 1; i >= Math.Max(0, _firstVisible - behind); i--) list.Add(i);
        }
        else
        {
            for (int i = _firstVisible - 1; i >= Math.Max(0, _firstVisible - ahead); i--) list.Add(i);
            for (int i = _lastVisible + 1; i <= Math.Min(_items.Count - 1, _lastVisible + behind); i++) list.Add(i);
        }
        _loader.SetWanted(list.ToArray());
    }

    private void UpdateTtfp(double nowMs)
    {
        // 見えなくなった項目の記録を捨て、新しく見えた項目(画像がまだない)の時刻を記録する
        var now = new HashSet<int>();
        for (int i = _firstVisible; i <= _lastVisible; i++) now.Add(i);
        foreach (int gone in _visibleSet.Where(v => !now.Contains(v)).ToList())
        {
            _enteredAtMs.Remove(gone);
        }
        foreach (int i in now)
        {
            bool had = _visibleSet.Contains(i);
            bool loaded = _loader == null || _loader.Contains(i);
            if (!had && !loaded) _enteredAtMs[i] = nowMs;
            if (_enteredAtMs.TryGetValue(i, out double t0) && loaded)
            {
                TtfpRecorded?.Invoke(nowMs - t0);
                _enteredAtMs.Remove(i);
            }
        }
        _visibleSet.Clear();
        foreach (int i in now) _visibleSet.Add(i);
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
        Offset -= e.Delta / 120.0 * (_cellH + _gap) * 0.75;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double page = Math.Max(_cellH + _gap, ActualHeight - (_cellH + _gap));
        switch (e.Key)
        {
            case Key.PageDown: Offset += page; e.Handled = true; break;
            case Key.PageUp: Offset -= page; e.Handled = true; break;
            case Key.Down: Offset += _cellH + _gap; e.Handled = true; break;
            case Key.Up: Offset -= _cellH + _gap; e.Handled = true; break;
            case Key.Home: Offset = 0; e.Handled = true; break;
            case Key.End: Offset = MaxOffset; e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control: SelectAll(); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != -1) { _hover = -1; _dirty = true; }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point p = e.GetPosition(this);
        int idx = IndexAt(p);
        _downPoint = p;
        _downIndex = idx;
        _pendingSingleSelect = false;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (idx < 0)
        {
            if (!ctrl && _selected.Count > 0) { _selected.Clear(); _dirty = true; }
        }
        else if (ctrl)
        {
            if (!_selected.Add(idx)) _selected.Remove(idx);
            _dirty = true;
        }
        else if (!_selected.Contains(idx))
        {
            _selected.Clear();
            _selected.Add(idx);
            _dirty = true;
        }
        else if (_selected.Count > 1)
        {
            _pendingSingleSelect = true; // 複数選択の1つを押した: ドラッグならそのまま群を運び、離しただけなら単独選択にする
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int idx = IndexAt(e.GetPosition(this));
        if (idx >= 0 && !_selected.Contains(idx))
        {
            _selected.Clear();
            _selected.Add(idx);
            _dirty = true;
        }
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        int h = IndexAt(p);
        if (h != _hover) { _hover = h; _dirty = true; }

        if (e.LeftButton == MouseButtonState.Pressed && _downIndex >= 0 && IsMouseCaptured)
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
            _dirty = true;
        }
        _pendingSingleSelect = false;
        _downIndex = -1;
        base.OnMouseLeftButtonUp(e);
    }

    /// <summary>
    /// OS 標準のドラッグ&ドロップで、選択中のファイル(フルパス)を外へ渡す。
    /// 許可する操作は Copy のみ(Move を許すと、ドロップ先によって原本が移動・削除されうるため)。
    /// </summary>
    private void StartDrag(int startIndex)
    {
        if (!_selected.Contains(startIndex))
        {
            _selected.Clear();
            _selected.Add(startIndex);
            _dirty = true;
        }
        string[] paths = _selected.OrderBy(i => i).Select(i => _items[i].Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        DragDropEffects result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        DragFinished?.Invoke(result, paths.Length, paths[0]);
    }
}
