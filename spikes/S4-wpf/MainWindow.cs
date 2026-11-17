using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace S4Wpf;

/// <summary>
/// 画面はすべてコードで組む(XAML・Binding なし)。毎フレームの処理は OnRendering の1か所に集め、呼ぶ順序を明示する:
/// シナリオ(オフセットを動かす)→ グリッドの更新(結果の取り込み・先読み・再描画要求)→ フレーム時間の記録。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly Options _o;
    private readonly List<ThumbItem> _items;
    private readonly ThumbnailGridControl _grid;
    private readonly ScrollBar _bar;
    private readonly TextBlock _status;
    private readonly FrameMeter _meter = new();
    private readonly BenchThresholds _thresholds = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ThumbnailLoader? _loader;       // 表示用(または prime 用)
    private ScenarioRunner? _scenario;
    private double _lastMs;
    private double _lastStatusMs;
    private double _prevHandlerMs;
    private bool _syncingBar;
    private string _lastAction = "";
    private bool _finished;
    private string _outDir = "";
    private bool _renderingHooked;

    public MainWindow(Options o, List<ThumbItem> items)
    {
        _o = o;
        _items = items;
        Title = "S4 サムネイル一覧(スパイク)";
        Width = o.WindowWidth;
        Height = o.WindowHeight;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;   // プライマリのモニタの中央に出す(ベンチはプライマリで測る)

        _grid = new ThumbnailGridControl(items, o.CellWidthDip, o.CellHeightDip, o.GapDip, o.PrefetchScreens);
        _bar = new ScrollBar { Orientation = Orientation.Vertical, Width = 18 };
        _status = new TextBlock { Foreground = Brushes.Gainsboro, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)), Padding = new Thickness(8, 4, 8, 4), FontFamily = new FontFamily("Consolas, Yu Gothic UI"), TextWrapping = TextWrapping.Wrap };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_grid, 0);
        Grid.SetColumn(_bar, 1);
        body.Children.Add(_grid);
        body.Children.Add(_bar);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 0);
        Grid.SetRow(_status, 1);
        root.Children.Add(body);
        root.Children.Add(_status);
        Content = root;

        _bar.Scroll += (_, e) =>
        {
            if (_syncingBar) return;
            _grid.Offset = e.NewValue;
        };
        _grid.ViewChanged = SyncScrollBar;
        _grid.DragFinished = OnDragFinished;
        _grid.TtfpRecorded = ms => _meter.AddTtfp(ms);
        BuildContextMenu();

        Loaded += OnWindowLoaded;
        Closed += (_, _) => Cleanup();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ------------------------------------------------------------------ 起動

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _grid.SetScale(dpi.DpiScaleX);

        bool decode = _o.Bench is null or "pass" or "warm" or "jump";
        bool prime = _o.Bench == "prime";
        if (decode || prime) _loader = CreateLoader();
        _grid.Loader = prime ? null : _loader;

        if (_o.Bench != null)
        {
            _outDir = Path.Combine(_o.OutRoot, _o.Label ?? (_o.Bench + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
            if (Directory.Exists(_outDir) && Directory.EnumerateFileSystemEntries(_outDir).Any())
                throw new InvalidOperationException($"出力ディレクトリが空でない: {_outDir}(別の --label を使う)");
            Directory.CreateDirectory(_outDir);
            _grid.AlwaysRedraw = true;
            _scenario = new ScenarioRunner(_o, _grid, _meter, prime ? _loader : null);
            _meter.StartLagProbe(Dispatcher);
            Console.WriteLine(_scenario.Description);
        }

        _lastMs = _meter.NowMs;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
        SyncScrollBar();
    }

    private ThumbnailLoader CreateLoader()
    {
        int bw = (int)Math.Round(_grid.CellWidthPx);
        int bh = (int)Math.Round(_grid.CellHeightPx);
        return new ThumbnailLoader(_items, bw, bh, _o.DiskCacheDir, _o.LruCapacity, _o.Workers);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_o.Bench != null)
        {
            Console.Error.WriteLine($"警告: ベンチ中に DPI が変わった({oldDpi.DpiScaleX} → {newDpi.DpiScaleX})。この結果は信用しないこと");
        }
        // セルのデバイスピクセルが変わるので、サムネイルを作り直す(キャッシュのキーにもピクセル数が入る)
        _grid.SetScale(newDpi.DpiScaleX);
        if (_loader != null && _o.Bench != "prime")
        {
            _grid.Loader = null;
            _loader.Dispose();
            _loader = CreateLoader();
            _grid.Loader = _loader;
        }
        ReportAction($"DPI が {oldDpi.DpiScaleX} → {newDpi.DpiScaleX} に変わった。サムネイルを作り直す");
    }

    private void SyncScrollBar()
    {
        _syncingBar = true;
        try
        {
            _bar.Maximum = _grid.MaxOffset;
            _bar.ViewportSize = _grid.ViewportHeight;
            _bar.LargeChange = Math.Max(1, _grid.ViewportHeight - _grid.RowHeight);
            _bar.SmallChange = _grid.RowHeight;
            _bar.Value = _grid.Offset;
        }
        finally { _syncingBar = false; }
    }

    // ------------------------------------------------------------------ 毎フレーム

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_finished) return;
        var args = (RenderingEventArgs)e;
        double nowMs = _meter.NowMs;
        double dt = (nowMs - _lastMs) / 1000.0;
        _lastMs = nowMs;

        long h0 = Stopwatch.GetTimestamp();
        // 先に前のフレームぶんの記録(直前のハンドラ所要・その間の OnRender・取り込み件数)を渡す
        _meter.OnRendering(args.RenderingTime, _grid.Offset, _loader?.InFlightOrQueued ?? 0, _loader?.Count ?? 0, _loader?.CompletedTotal ?? 0,
            _prevHandlerMs, _grid.TakeRenderMs(), _grid.TakeDrained());
        _scenario?.Tick(dt, nowMs);
        _grid.Update(nowMs);
        _prevHandlerMs = (Stopwatch.GetTimestamp() - h0) * 1000.0 / Stopwatch.Frequency;

        if (_scenario is { Finished: true })
        {
            FinishBench();
            return;
        }
        if (nowMs - _lastStatusMs > 250)
        {
            _lastStatusMs = nowMs;
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"{_grid.ItemCount} 件 / 表示 {_grid.FirstVisible}〜{_grid.LastVisible} / 列 {_grid.Columns} / 選択 {_grid.SelectedCount} / メモリ {_loader?.Count ?? 0} 件 / 待ち {_loader?.InFlightOrQueued ?? 0}"));
        if (_scenario != null) sb.Append($" / ベンチ {_o.Bench}: {_meter.CurrentPhase}");
        if (_lastAction.Length > 0) sb.Append("\n").Append(_lastAction);
        sb.Append("\n操作: クリック=選択 / Ctrl+クリック=追加 / ドラッグで外のアプリへ(Copy のみ) / 右クリックでメニュー / Ctrl+C=画像コピー / Ctrl+Shift+C=ファイルコピー / E=エクスプローラーで表示 / Ctrl+A=全選択");
        _status.Text = sb.ToString();
    }

    // ------------------------------------------------------------------ ベンチの終了

    private void FinishBench()
    {
        _finished = true;
        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
        _meter.EndPhase();
        _meter.StopLagProbe();
        string kind = _scenario!.Kind;
        string summary = _meter.BuildSummary(BuildHeader(), _loader?.Snapshot(), _thresholds, kind, Native.PrimaryRefreshHz());
        _meter.WriteFiles(_outDir, summary);
        Console.WriteLine(summary);
        Console.WriteLine($"結果を書き出した: {Path.GetFullPath(_outDir)}");
        Program.ExitCode = 0;
        Close();
    }

    private string BuildHeader()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var sb = new StringBuilder();
        sb.AppendLine($"# S4 ベンチ: {_o.Bench}");
        sb.AppendLine(_scenario?.Description ?? "");
        sb.AppendLine($"日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"件数: {_items.Count}(画像ディレクトリ {Path.GetFullPath(_o.ImagesDir)} のファイルを論理的に水増し。キャッシュのキーは項目番号つき)");
        sb.AppendLine($"表示スケール(DPI): {dpi.DpiScaleX * 100:0}%({dpi.PixelsPerInchX:0} dpi)");
        sb.AppendLine($"セル: {_o.CellWidthDip}×{_o.CellHeightDip} DIP → デバイスピクセル {_grid.CellWidthPx:0}×{_grid.CellHeightPx:0}(デコードの箱もこの大きさ)、すき間 {_o.GapDip} DIP");
        sb.AppendLine($"ウィンドウ: {ActualWidth:0}×{ActualHeight:0} DIP / グリッド {_grid.ActualWidth:0}×{_grid.ActualHeight:0} DIP、列 {_grid.Columns}、行 {_grid.RowCount}、画面あたり約 {(int)Math.Ceiling(_grid.ActualHeight / _grid.RowHeight) * _grid.Columns} 件");
        sb.AppendLine($"モニタのリフレッシュレート(プライマリ): {Native.PrimaryRefreshRate()} Hz / WPF のレンダリング層: {RenderCapability.Tier >> 16}");
        sb.AppendLine($"ローダー: ワーカー {_o.Workers} / メモリ LRU {_o.LruCapacity} 件 / ディスクキャッシュ {(_o.DiskCacheDir ?? "なし")} / 先読み {_o.PrefetchScreens} 画面");
        sb.AppendLine($"自動スクロール速度: {_o.Speed:0} DIP/秒");
        sb.AppendLine($"環境: {Environment.OSVersion} / .NET {Environment.Version} / 論理 CPU {Environment.ProcessorCount}");
        sb.AppendLine($"合格線(T=各フェーズの p50): ウォーム p95≤{_thresholds.WarmP95T}T・2T超≤{_thresholds.WarmOver2TPercent}%・6T超=0 / コールド・高速・jump 2T超≤{_thresholds.ColdOver2TPercent}%・6T超≤{_thresholds.ColdOver6TCount}個・コールドの画像表示 p95≤{_thresholds.ColdTtfpP95Ms}ms / ワーキングセット増分≤{_thresholds.MaxWorkingSetGrowthMiB}MiB");
        return sb.ToString();
    }

    private void Cleanup()
    {
        if (_renderingHooked) System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        _meter.StopLagProbe();
        _loader?.Dispose();
    }

    // ------------------------------------------------------------------ 操作(要件8・9)

    private void ReportAction(string text)
    {
        _lastAction = $"[{DateTime.Now:HH:mm:ss}] {text}";
        Console.WriteLine(_lastAction);
        UpdateStatus();
    }

    private string? FirstSelectedPath() => _grid.SelectedItems().FirstOrDefault()?.Path;

    private void OnDragFinished(DragDropEffects effect, int count, string firstPath)
    {
        string line = $"ドラッグ結果: {effect}({count} ファイル、先頭 {Path.GetFileName(firstPath)})。None なら、ドロップ先が受けなかったか、キャンセルされた";
        ReportAction(line);
        try
        {
            string dir = Path.Combine(_o.OutRoot, "manual");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "drag-log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{effect}\t{count}\t{firstPath}\n", new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            ReportAction("drag-log.txt に書けなかった: " + ex.Message);
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "エクスプローラーで表示(explorer.exe /select)", () => RunAction(() => Task.FromResult(Actions.RevealWithProcessStart(RequirePath()))));
        AddMenu(menu, "エクスプローラーで表示(シェル API)", () => RunAction(() => Task.FromResult(Actions.RevealWithShellApi(RequirePath()))));
        menu.Items.Add(new Separator());
        AddMenu(menu, "画像をコピー(Clipboard.SetImage)", () => RunAction(() => Actions.CopyImageWithSetImageAsync(RequirePath())));
        AddMenu(menu, "画像をコピー(DIB + PNG 形式)", () => RunAction(() => Actions.CopyImageWithDibAndPngAsync(RequirePath())));
        AddMenu(menu, "ファイルとしてコピー(SetFileDropList)", () => RunAction(() => Task.FromResult(Actions.CopyFile(SelectedPaths()))));
        _grid.ContextMenu = menu;
        _grid.ContextMenuOpening += (_, e) =>
        {
            if (_grid.SelectedCount == 0) e.Handled = true;   // 何も選んでいないときはメニューを出さない
        };
    }

    private static void AddMenu(ContextMenu menu, string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private string RequirePath() => FirstSelectedPath() ?? throw new InvalidOperationException("選択がない");
    private string[] SelectedPaths() => _grid.SelectedItems().Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private async void RunAction(Func<Task<string>> action)
    {
        try
        {
            ReportAction(await action());
        }
        catch (Exception ex)
        {
            ReportAction("失敗: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_grid.SelectedCount == 0) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && !shift && e.Key == Key.C)
        {
            RunAction(() => Actions.CopyImageWithSetImageAsync(RequirePath()));
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.C)
        {
            RunAction(() => Task.FromResult(Actions.CopyFile(SelectedPaths())));
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.E)
        {
            RunAction(() => Task.FromResult(Actions.RevealWithProcessStart(RequirePath())));
            e.Handled = true;
        }
    }
}

/// <summary>画面のリフレッシュレート(プライマリ モニタ)。ベンチの記録用。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    public static double PrimaryRefreshHz()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettingsW(null, -1, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    public static string PrimaryRefreshRate()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettingsW(null, -1, ref dm) ? dm.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture) : "取得失敗";
    }
}
