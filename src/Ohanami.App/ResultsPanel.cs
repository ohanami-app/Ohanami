using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ohanami.Core;
using Ohanami.Core.Tags;

namespace Ohanami.App;

/// <summary>
/// 右のパネル: 検索ボックスと結果のサムネイルの格子、結果への操作(ドラッグ・コピー・エクスプローラーで表示)。
/// <list type="bullet">
/// <item>最後の検索結果は、ハッシュで引ける辞書で持つ。グリッドの項目(<see cref="GridItem"/>)はハッシュだけで、パス・ツールチップ・「開く」で出す内容はここから引く
/// (詳細画面も同じ引き方ができる)。</item>
/// <item><b>毎フレームの購読(<c>CompositionTarget.Rendering</c>)は、必要なときだけつなぐ</b>: グリッドが再描画を要求したとき(<see cref="ThumbnailGridControl.FrameRequested"/>)と、
/// ローダーの結果が届いたとき(<see cref="ThumbnailLoader.ResultReady"/>)につなぎ、グリッドが「もう要らない」(<see cref="ThumbnailGridControl.NeedsFrame"/> が false)になったら外す。
/// ウィンドウを開いたまま放置しても、UI スレッドを 165Hz で回さない。</item>
/// </list>
/// </summary>
internal sealed class ResultsPanel : DockPanel
{
    internal const int SearchLimit = 300;
    /// <summary>検索していないときの新しい順の一覧の件数の上限: なし(全部を出す。ADR 0041。検索の結果の <see cref="SearchLimit"/> とは別)。Core の <c>ListRecentAsync</c> に渡す値。</summary>
    internal const int RecentLimit = int.MaxValue;
    private const int ThumbnailCacheCapacity = 1500;
    private const string FileMissingNotice = "元のファイルがありません (移動・削除された可能性があります)";
    private static readonly TimeSpan BannerDuration = TimeSpan.FromSeconds(6);

    private readonly ICoreLog _log;
    private readonly Action<string> _notice;
    private readonly IShellActions _shell;

    private OhanamiCore? _core;
    private bool _detached;   // Core を手放した(閉じる途中)。進行中の検索の結果は捨てる
    private ThumbnailLoader? _loader;
    private readonly ThumbnailGridControl _grid;
    private readonly ScrollBar _bar;
    private bool _syncingBar;
    private bool _renderingHooked;

    private readonly TextBox _searchBox;
    private readonly Button _searchButton;
    private readonly TextBlock _header;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly DispatcherTimer _bannerTimer;

    private Dictionary<string, SearchResult> _results = new(StringComparer.Ordinal);
    private List<string> _order = [];                              // 結果の並び(ハッシュ。詳細画面の前後に使う)
    private Dictionary<string, int> _orderIndex = new(StringComparer.Ordinal);
    private IReadOnlyList<PathOutcome> _lastPaths = [];   // 最後の検索の、経路ごとの状態(ツールチップに、準備中・失敗・注意を出す)
    private int _searchSerial;
    private CancellationTokenSource? _searchCts;
    private string? _lastQuery;   // 最後に出した検索の語(消えた画像を反映するために、同じ語で検索し直す)

    // 検索していないときの新しい順の一覧(ADR 0041)
    private bool _recentActive;                                    // いま出している一覧が、検索の結果ではなく新しい順の一覧か
    private IReadOnlyList<RecentImage> _recentImages = [];         // いま出している新しい順の一覧(走査のあとの作り直しで、同じなら何もしない比較に使う)
    private Dictionary<string, DateTimeOffset> _recentModified = new(StringComparer.Ordinal);   // ハッシュ → 更新日時(ツールチップ)
    private bool _userSearchPending;                               // 検索ボックスからの検索の結果を待っている(走査のあとの一覧の作り直しで、その検索を潰さない)

    // 「なぜ出たのか」: テキスト検索のインデックスにまだ文書が無い画像(VLM が済んでいない、または失敗した画像)。検索の結果に段階の情報が無いので、結果が出たあとに Core へ引く
    private HashSet<string> _noTextIndex = new(StringComparer.Ordinal);
    private int _noTextIndexSerial;

    // 詳細画面: 一覧の上に重ねる全面の表示。開いている画像のハッシュ(開いていなければ null)
    private readonly DetailView _detail = new();
    private string? _detailHash;
    private int _detailSerial;
    private CancellationTokenSource? _detailCts;

    /// <summary>詳細画面の画像の箱(ピクセル)。固定にして、サムネイルのディスクキャッシュを増やしすぎない(箱の大きさがキャッシュの鍵に入っている)。</summary>
    internal const int DetailImageBox = 2048;

    /// <summary>
    /// 検索の呼び出し(<see cref="Attach"/> で Core の検索に結ぶ。テストで差し替える)。
    /// </summary>
    internal Func<SearchQuery, CancellationToken, Task<SearchResponse>>? SearchFunc { get; set; }

    /// <summary>
    /// 検索していないときの新しい順の一覧の読み出し(引数は上限の件数。<see cref="Attach"/> で Core の <c>Search.ListRecentAsync</c> に結ぶ。テストで差し替える)。
    /// null(結ばれていない)なら、検索ボックスを空にしても一覧は空になるだけ。
    /// </summary>
    internal Func<int, CancellationToken, Task<RecentResponse>>? RecentFunc { get; set; }

    /// <summary>画像 1 枚の詳細の読み出し(<see cref="Attach"/> で <c>OhanamiCore.GetImageDetailAsync</c> に結ぶ。テストで差し替える)。</summary>
    internal Func<string, CancellationToken, Task<ImageDetail?>>? DetailFunc { get; set; }

    /// <summary>
    /// 検索の結果のうち、全文検索の索引にまだ文書が無い画像(VLM が済んでいない、または失敗した画像)を引く(<see cref="Attach"/> で Core の <c>Search.FindWithoutTextIndexAsync</c> に結ぶ。テストで差し替える)。
    /// null なら、その理由の行(「テキスト検索のインデックスがまだありません」)は出ない。
    /// </summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? NoTextIndexFunc { get; set; }

    /// <summary>詳細画面の大きな画像の読み出し(ハッシュ → 画素。<see cref="Attach"/> で Core のサムネイルに結ぶ。テストで差し替える)。</summary>
    internal Func<string, CancellationToken, Task<ThumbnailResult>>? ImageFunc { get; set; }

    /// <summary>詳細画面に日時を出すタイムゾーン(画面は地域の時刻。テストで替える)。</summary>
    internal TimeZoneInfo DisplayZone { get; set; } = TimeZoneInfo.Local;

    // 手動タグの編集
    /// <summary>手動タグの窓口(<see cref="Attach"/> で Core の <c>Tags</c> に結ぶ。テストで差し替える)。</summary>
    internal ITagService? TagService { get; set; }

    /// <summary>
    /// 一覧で選んだ画像に付けるタグの名前を聞く(引数は選んだ枚数と、既存のタグの候補。結果は名前、キャンセルは null)。製品は <see cref="TagPromptWindow"/>(モーダル)。テストで差し替える。
    /// </summary>
    internal Func<int, IReadOnlyList<TagCandidate>, Task<string?>> PromptTag { get; set; } = (count, candidates) => Task.FromResult(TagPromptWindow.Ask(count, candidates));

    /// <summary>候補として取る既存のタグの数(使われている数の多い順)。</summary>
    internal const int CandidateLimit = 30;

    private readonly Button _tagButton;
    private readonly DockPanel _actionRow;
    private SearchResult? _detailResult;                       // 開いている詳細の、検索の結果(再描画に使う)
    private SearchResult? _standalone;                         // 検索の結果にない画像の詳細(処理状態の一覧から開いた)。開いている間だけ(順位・前後は無い)
    private DetailLoad _detailLoad = DetailLoad.Loading;       // 開いている詳細の、読み込みの状態(タグを編集したら、手動タグだけ差し替えて再描画する)

    /// <summary>
    /// 元のファイルが見つからない項目があった(ドラッグ・コピー・エクスプローラーで表示で、実在を確かめて分かった)。引数は、その画像の(DB の上で現存する)パス。
    /// MainWindow が、そのフォルダを裏で走査し直し(<c>missing_since</c> を付ける)、終わったら <see cref="RefreshSearch"/> で今の検索をやり直す。
    /// </summary>
    public event Action<IReadOnlyList<string>>? MissingFilesDetected;

    /// <summary>製品の組み立て(<see cref="MainWindow"/>): 本物のシェル・クリップボード・ドラッグ(<see cref="SystemShellActions"/>)を使う。**テストはこれを使わない**(3 つ引数の組み立てに偽物を渡す。ShellBoundaryGuardTests が見張る)。</summary>
    public ResultsPanel(ICoreLog log, Action<string> notice) : this(log, notice, SystemShellActions.Instance)
    {
    }

    /// <param name="shell">クリップボード・エクスプローラー・他のアプリへのドラッグ。製品は <see cref="SystemShellActions"/>、テストは偽物。</param>
    public ResultsPanel(ICoreLog log, Action<string> notice, IShellActions shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _log = log;
        _notice = notice;
        _shell = shell;
        Margin = new Thickness(6, 12, 12, 12);
        LastChildFill = true;

        _grid = new ThumbnailGridControl(WindowLayout.ThumbnailWidth, 144, 8, prefetchScreens: 2, pathsOf: PathsOf)
        {
            EmptyMessage = "フォルダを追加して、検索ボックスに言葉を入れてください",
            TooltipOf = item => !_results.TryGetValue(item.Hash, out SearchResult? r) ? null
                : _recentActive ? BuildRecentTooltip(r, _recentModified.GetValueOrDefault(item.Hash), DisplayZone)
                : BuildTooltip(r, _lastPaths, NoTextIndexNow(item.Hash)),
        };
        _bar = new ScrollBar { Orientation = Orientation.Vertical, Width = 18 };
        _searchBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 4, 6, 4) };
        _searchButton = UiParts.Button("検索", OnSearch);
        _header = new TextBlock { Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        _tagButton = UiParts.Button("手動タグを付ける…", (_, _) => _ = TagSelectedAsync());
        _tagButton.Margin = new Thickness(0, 0, 6, 6);
        _tagButton.IsEnabled = false;
        _tagButton.ToolTip = "一覧で選んだ画像に、手動タグを付けます (Ctrl か Shift で複数を選べます)";
        _actionRow = new DockPanel { LastChildFill = false };
        _actionRow.Children.Add(_tagButton);

        // 操作できなかったときの、目立つ知らせ(一覧の上に重ねる)。下の状態の行の通知だけでは、利用者に見えない
        _bannerText = new TextBlock { Foreground = AppColors.OnDark, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        _banner = new Border
        {
            Background = AppColors.Warn,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(12, 16, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _bannerText,
        };
        _bannerTimer = new DispatcherTimer { Interval = BannerDuration };
        _bannerTimer.Tick += (_, _) => HideBanner();

        var searchRow = new DockPanel { LastChildFill = true };
        SetDock(_searchButton, Dock.Right);
        _searchButton.Margin = new Thickness(8, 0, 0, 0);
        _searchButton.MinWidth = 72;
        searchRow.Children.Add(_searchButton);
        searchRow.Children.Add(_searchBox);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_grid, 0);
        Grid.SetColumn(_bar, 1);
        Grid.SetColumn(_banner, 0);
        Grid.SetColumn(_detail, 0);
        Grid.SetColumnSpan(_detail, 2);   // 一覧とスクロールバーの全面を覆う
        body.Children.Add(_grid);
        body.Children.Add(_bar);
        body.Children.Add(_detail);   // 一覧の上(後に足したものが前面)。上の検索ボックスと見出しは、そのまま見える
        body.Children.Add(_banner);   // 詳細画面よりさらに上(操作できなかった知らせは、詳細画面からの操作でも出す)

        SetDock(searchRow, Dock.Top);
        SetDock(_header, Dock.Top);
        SetDock(_actionRow, Dock.Top);
        Children.Add(searchRow);
        Children.Add(_header);
        Children.Add(_actionRow);
        Children.Add(body);

        _bar.Scroll += (_, e) => { if (!_syncingBar) _grid.Offset = e.NewValue; };
        _grid.ViewChanged = SyncScrollBar;
        _grid.FrameRequested = EnsureRendering;
        _grid.Notice = ShowProblem;
        _grid.MissingFilesDetected = items => ReportMissing(items.Select(PathsOf));
        _grid.DragFinished = OnDragFinished;
        _grid.SelectionChanged = OnSelectionChanged;
        _grid.ItemActivated = OnItemActivated;
        _detail.CloseRequested = CloseDetail;
        _detail.NavigateRequested = delta => NavigateDetail(delta);
        _detail.RevealRequested = RevealSelected;
        _detail.CopyImageRequested = CopySelectedImage;
        _detail.CopyFilesRequested = CopySelectedFiles;
        _detail.DragRequested = StartDetailDrag;
        _detail.TagAddRequested = text => _ = AddDetailTagAsync(text);
        _detail.TagRemoveRequested = name => _ = RemoveDetailTagAsync(name);
        BuildContextMenu();

        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OnSearch(this, new RoutedEventArgs()); e.Handled = true; }
        };
        SetSearchEnabled(false);
    }

    /// <summary>テスト・確認用: グリッド。</summary>
    internal ThumbnailGridControl ResultGrid => _grid;

    internal TextBox SearchBox => _searchBox;

    internal Button SearchButton => _searchButton;

    internal TextBlock HeaderText => _header;

    /// <summary>グリッドにキーボードのフォーカスがあるか(Ctrl+C・E は、検索ボックスの入力を横取りしないよう、これのときだけ効かせる)。</summary>
    public bool GridHasFocus => ReferenceEquals(Keyboard.FocusedElement, _grid);

    // ------------------------------------------------------------------ Core との接続

    /// <summary>画面の表示スケールが分かったとき(ウィンドウを開いたとき)。セルのサイズはデバイスピクセルの整数に揃える。</summary>
    public void SetScale(DpiScale dpi)
    {
        _grid.SetScale(dpi.DpiScaleX);
        SyncScrollBar();
        EnsureRendering();
    }

    public void Attach(OhanamiCore core)
    {
        _core = core;
        _detached = false;
        SearchFunc = (query, ct) => core.Search.SearchAsync(query, ct);
        RecentFunc = (limit, ct) => core.Search.ListRecentAsync(limit, ct);
        NoTextIndexFunc = (hashes, ct) => core.Search.FindWithoutTextIndexAsync(hashes, ct);
        DetailFunc = (hash, ct) => core.GetImageDetailAsync(hash, ct);
        TagService = new CoreTagService(core.Tags);
        ImageFunc = (hash, ct) => core.Thumbnails.GetAsync(hash, DetailImageBox, DetailImageBox, ct);
        _loader = CreateLoader();
        _grid.Loader = _loader;
    }

    /// <summary>Core を手放す(閉じる前): 検索を止め、詳細画面を閉じ、サムネイルのワーカーを止める。</summary>
    public void Detach()
    {
        _searchCts?.Cancel();
        CloseDetail(returnFocus: false);
        DetailFunc = null;
        TagService = null;
        ImageFunc = null;
        _grid.Loader = null;
        _loader?.Dispose();
        _loader = null;
        _core = null;
        _detached = true;
        SearchFunc = null;
        RecentFunc = null;
        NoTextIndexFunc = null;
        HideBanner();
        Unhook();
    }

    public void SetSearchEnabled(bool enabled)
    {
        _searchBox.IsEnabled = enabled;
        _searchButton.IsEnabled = enabled;
    }

    /// <summary>表示スケールが変わった: セルのデバイスピクセルが変わるので、サムネイルを作り直す(箱の大きさがキャッシュの鍵に入っている)。</summary>
    public void HandleDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _grid.SetScale(newDpi.DpiScaleX);
        if (_core is not null && _loader is not null)
        {
            _grid.Loader = null;
            _loader.Dispose();
            _loader = CreateLoader();
            _grid.Loader = _loader;
        }
        _log.Info($"DPI が {oldDpi.DpiScaleX} → {newDpi.DpiScaleX} に変わった。サムネイルを作り直す");
        EnsureRendering();
    }

    private ThumbnailLoader CreateLoader()
    {
        int bw = (int)Math.Round(_grid.CellWidthPx);
        int bh = (int)Math.Round(_grid.CellHeightPx);
        var loader = new ThumbnailLoader(_core!.Thumbnails.GetAsync, _log, bw, bh, ThumbnailCacheCapacity, Math.Max(2, Environment.ProcessorCount / 2));
        // 結果が届いたら(ワーカーのスレッドから)UI スレッドに 1 回だけ渡して、毎フレームの購読をつなぐ
        loader.ResultReady = () => Dispatcher.BeginInvoke(EnsureRendering);
        return loader;
    }

    // ------------------------------------------------------------------ 毎フレームの購読(必要なときだけ)

    private void EnsureRendering()
    {
        if (_renderingHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void Unhook()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    /// <summary>毎フレームの処理はここだけ(グリッドの更新)。もう要らなければ外す(放置のあいだ UI スレッドを回さない)。</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        _grid.Update();
        if (!_grid.NeedsFrame) Unhook();
    }

    /// <summary>テスト・確認用: いま毎フレームの購読がつながっているか。</summary>
    internal bool IsRenderingHooked => _renderingHooked;

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

    // ------------------------------------------------------------------ 検索

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (SearchFunc is null) return;
        string text = _searchBox.Text.Trim();
        _searchCts?.Cancel();
        if (text.Length == 0)
        {
            _lastQuery = null;
            // 検索の欄が空: 新しい順の一覧に戻る。
            // 製品では、SearchFunc と RecentFunc は Attach で一緒に結ばれ、Detach で一緒に外れる(テストで確かめている)ので、ここで RecentFunc が無いことはない。
            // 下の 4 行は、テストが SearchFunc だけを結んだときのためで、製品の画面には出ない(「検索ボックスに言葉を入れる」という専用の空表示は、
            // 製品では到達しないので置かない。空表示は、最初の「フォルダを追加して、検索ボックスに言葉を入れる」のまま)
            if (RecentFunc is not null) { await ShowRecentAsync(); return; }
            _recentActive = false;
            SetResults([]);
            _header.Text = "";
            return;
        }
        await RunSearchAsync(text, keepBanner: false);
    }

    /// <summary>
    /// 今の検索(最後に出した語)をやり直して、結果を差し替える。消えた画像・動かした画像を、走査のあとで検索結果に反映するために使う
    /// (<c>missing_since</c> が付いた画像は、検索の母集団から外れる)。赤い帯の知らせは消さない。検索していなければ何もしない。
    /// </summary>
    public async Task RefreshSearch()
    {
        // 新しい順の一覧を出している間は、一覧を作り直す(撮ったスクリーンショットがすぐ一番上に出る。並びが変わらなければ、何も動かさない)。
        // 検索ボックスからの検索の結果を待っているときは、その検索を潰さない(結果が着けば、新しい一覧で出る)
        if (_recentActive && RecentFunc is not null)
        {
            if (!_userSearchPending) await RunRecentAsync(keepBanner: true, onlyIfChanged: true);
            return;
        }
        if (SearchFunc is null || _lastQuery is not { } text) return;
        _searchCts?.Cancel();
        await RunSearchAsync(text, keepBanner: true);
    }

    /// <summary>
    /// 検索していないときの新しい順の一覧(ファイルの更新日時の新しい順に、全部。上限なし = <see cref="RecentLimit"/>)を出す(起動の直後と、検索の欄を空にしたとき)。
    /// 検索の結果と同じ一覧・同じ詳細画面・同じ操作を使う。Core に結ばれていなければ何もしない。失敗は画面に理由を出して続ける(落ちない)。
    /// </summary>
    public Task ShowRecentAsync() => RunRecentAsync(keepBanner: false, onlyIfChanged: false);

    private async Task RunRecentAsync(bool keepBanner, bool onlyIfChanged)
    {
        Func<int, CancellationToken, Task<RecentResponse>>? recent = RecentFunc;
        if (recent is null) return;
        int serial = ++_searchSerial;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        if (!keepBanner) { _userSearchPending = false; _header.Text = "新しい順の一覧を読み込み中…"; }
        try
        {
            RecentResponse response = await recent(RecentLimit, cts.Token);
            if (serial != _searchSerial || _detached) return;
            _lastQuery = null;
            if (onlyIfChanged && _recentActive && SameRecent(_recentImages, response.Images))
            {
                _header.Text = BuildRecentHeader(response);   // 並びは同じ(自動の走査で何も変わらなかった)。一覧は動かさない
                return;
            }
            // 走査で新しい画像が入った作り直し(onlyIfChanged)で、すでに新しい順の一覧を出していたなら、見ている位置と選択を保つ
            // (新しい画像は一番上に足される)。起動の直後・検索の欄を空にして戻ったときは、先頭から出す
            ShowRecent(response, keepBanner, keepView: onlyIfChanged && _recentActive);
        }
        catch (OperationCanceledException)
        {
            // 検索・別の一覧に置き換えられた、または閉じる途中
        }
        catch (Exception ex)
        {
            if (serial != _searchSerial || _detached) return;
            _log.Error("新しい順の一覧を作れなかった", ex);
            _recentActive = false;
            SetResults([], keepBanner);
            _grid.EmptyMessage = "一覧を作れませんでした";
            _header.Text = "新しい順の一覧を作れませんでした: " + UiText.Plain(ex);
        }
    }

    private void ShowRecent(RecentResponse r, bool keepBanner, bool keepView = false)
    {
        _recentActive = true;   // SetResults の中の詳細画面の合わせ方が、この一覧のものになるよう、先に立てる
        _noTextIndex = new HashSet<string>(StringComparer.Ordinal);   // 新しい順の一覧には、検索の理由は出さない
        _noTextIndexSerial++;
        _lastPaths = [];
        _recentImages = r.Images;
        _recentModified = r.Images.ToDictionary(x => x.Hash, x => x.ModifiedAt, StringComparer.Ordinal);
        _grid.EmptyMessage = "フォルダを追加して、検索ボックスに言葉を入れてください";   // 画像が 1 枚も無いとき
        // 順位は一覧の中の位置(1 始まり)。スコア・経路ごとの情報は無い(検索の結果ではない)
        SetResults(r.Images.Select((x, i) => new SearchResult(x.Hash, x.Paths, i + 1, 0, [])).ToList(), keepBanner, keepView);
        _header.Text = BuildRecentHeader(r);
    }

    /// <summary>新しい順の一覧が同じか(同じ画像が同じ順で、パスの並びも同じ)。同じなら、一覧を作り直さない(選択やスクロールを動かさない)。</summary>
    internal static bool SameRecent(IReadOnlyList<RecentImage> a, IReadOnlyList<RecentImage> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Hash, b[i].Hash, StringComparison.Ordinal) || a[i].ModifiedAt != b[i].ModifiedAt || !a[i].Paths.SequenceEqual(b[i].Paths, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// 新しい順の一覧の見出し。検索の結果ではないと分かる文言にする(画像が 1 枚も無ければ空。一覧の空表示が案内を出す)。
    /// 一覧は全部を出すので(上限なし。<see cref="RecentLimit"/>)、「全 M 枚のうち」は付けない。
    /// </summary>
    internal static string BuildRecentHeader(RecentResponse r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Images.Count == 0) return "";
        return string.Create(CultureInfo.InvariantCulture, $"新しい順 (ファイルの更新日時) に {r.Images.Count} 枚");
    }

    /// <summary>新しい順の一覧の 1 枚のツールチップ: 更新日時(分まで)、元のパス、詳細を開く操作。順位・類似度は検索の情報なので出さない。</summary>
    internal static string BuildRecentTooltip(SearchResult x, DateTimeOffset modified, TimeZoneInfo zone)
    {
        var sb = new StringBuilder();
        sb.Append("更新: ").Append(DetailPresenter.Time(modified, zone));
        if (x.Paths.Count > 0)
        {
            sb.Append('\n').Append(x.Paths[0]);
            if (x.Paths.Count > 1) sb.Append(string.Create(CultureInfo.InvariantCulture, $"\n(ほか {x.Paths.Count - 1} 件のパス)"));
        }
        sb.Append("\nEnter かダブルクリックで詳細を開けます");
        return sb.ToString();
    }

    private async Task RunSearchAsync(string text, bool keepBanner)
    {
        Func<SearchQuery, CancellationToken, Task<SearchResponse>>? search = SearchFunc;
        if (search is null) return;
        int serial = ++_searchSerial;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        if (!keepBanner) { _header.Text = "検索中…"; _userSearchPending = true; }
        try
        {
            SearchResponse response = await search(new SearchQuery(text, SearchLimit), cts.Token);
            if (serial != _searchSerial || _detached) return;
            // 走査のあとの自動のやり直し(keepBanner)で、すでに同じ語の検索の結果を出していたなら、見ている位置と選択を保つ(新しい順の一覧と同じ)。
            // 利用者が検索したとき(keepBanner が偽)・別の語・一覧から検索に移ったときは、先頭から
            bool keepView = keepBanner && !_recentActive && string.Equals(_lastQuery, text, StringComparison.Ordinal);
            _lastQuery = text;
            _userSearchPending = false;
            ShowResults(response, keepBanner, keepView);
        }
        catch (OperationCanceledException)
        {
            // 新しい検索に置き換えられた、または閉じる途中
        }
        catch (Exception ex)
        {
            if (serial != _searchSerial || _detached) return;
            _log.Error("検索に失敗: " + text, ex);
            _userSearchPending = false;
            _recentActive = false;
            SetResults([], keepBanner);
            _grid.EmptyMessage = "検索できませんでした";
            _header.Text = "検索できませんでした: " + UiText.Plain(ex);
        }
    }

    /// <summary>最後の検索結果を、ハッシュで引ける辞書に入れ替え、グリッドの項目(ハッシュだけ)を入れ替える。</summary>
    internal void SetResults(IReadOnlyList<SearchResult> results, bool keepBanner = false, bool keepView = false)
    {
        if (!keepBanner) HideBanner();   // 前の結果への知らせは、結果が替わったら消す(走査のあとの差し替えでは、知らせを残す)
        _results = new Dictionary<string, SearchResult>(results.Count, StringComparer.Ordinal);
        var items = new List<GridItem>(results.Count);
        foreach (SearchResult r in results)
        {
            if (_results.TryAdd(r.Hash, r)) items.Add(new GridItem(r.Hash));
        }
        _order = items.Select(i => i.Hash).ToList();
        _orderIndex = new Dictionary<string, int>(_order.Count, StringComparer.Ordinal);
        for (int i = 0; i < _order.Count; i++) _orderIndex[_order[i]] = i;
        _grid.SetItems(items, keepView);   // keepView: 見ている位置と選択を保つ(走査のあとの自動の作り直しのとき。新しい順の一覧も検索の結果も)。既定は先頭に戻る
        UpdateTagButton();
        SyncDetailWithResults(keepBanner);
    }

    /// <summary>
    /// 結果が差し替わったとき、開いている詳細画面を合わせる。新しい検索なら閉じる(結果の並びが別物になる)。走査のあとの差し替え(<paramref name="refresh"/>)で、
    /// 開いている画像がまだ結果にあれば、そのまま開いておく(位置と前後だけ直す)。結果から外れていれば、閉じて知らせる。
    /// </summary>
    private void SyncDetailWithResults(bool refresh)
    {
        if (_detailHash is not { } open) return;
        if (_standalone is not null)
        {
            // 処理状態の一覧から開いた詳細は、結果の並びに無い。走査のあとの差し替え(refresh)では開いたまま、新しい検索なら閉じる
            if (!refresh) CloseDetail();
            return;
        }
        if (refresh && _orderIndex.TryGetValue(open, out int index))
        {
            _detail.SetPosition(index + 1, _order.Count, index > 0, index < _order.Count - 1);
            return;
        }
        CloseDetail();
        if (refresh) _notice(_recentActive ? "詳細を開いていた画像が一覧から外れました (元のファイルが無くなった可能性があります)。一覧に戻りました" : "詳細を開いていた画像が結果から外れました (元のファイルが無くなった可能性があります)。一覧に戻りました");
    }

    private void ShowResults(SearchResponse r, bool keepBanner = false, bool keepView = false)
    {
        _recentActive = false;   // 検索の結果(SetResults の中の詳細画面の合わせ方が、検索の結果のものになるよう、先に落とす)
        _grid.EmptyMessage = "見つかりませんでした";
        _lastPaths = r.Paths;
        // 画面には内部の語を出さない(BuildHeader)。元の理由は、ここでログに残す
        foreach (PathOutcome p in r.Paths.Where(p => p.State == PathState.Failed))
            _log.Warning($"検索の経路が失敗した: {p.PathId}: {p.Message}");
        _noTextIndex = new HashSet<string>(StringComparer.Ordinal);
        int serial = ++_noTextIndexSerial;
        SetResults(r.Results, keepBanner, keepView);
        _header.Text = BuildHeader(r);
        // 全文の経路が使えているときだけ、文字の索引がまだ無い画像を引く(使えないときに「索引が無い」と言うと、嘘になる)
        if (r.Paths.Any(p => p.PathId == FullTextPathId && p.State == PathState.Ok))
            _ = LoadNoTextIndexAsync(r.Results.Select(x => x.Hash).ToList(), serial);
    }

    /// <summary>この画像は、いま出している検索の結果の中で、文字の索引がまだ無いか(引き終わるまでは false)。</summary>
    private bool NoTextIndexNow(string hash) => _noTextIndex.Contains(hash);

    /// <summary>
    /// 結果の画像のうち、文字の索引がまだ無いものを引いて覚える(一覧のツールチップは、そのとき作るので、次に出すときから反映される)。
    /// 失敗しても、その理由の行が出ないだけ(ログに残す)。引いている間に結果が替わったら捨てる。
    /// </summary>
    private async Task LoadNoTextIndexAsync(IReadOnlyList<string> hashes, int serial)
    {
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? find = NoTextIndexFunc;
        if (find is null || hashes.Count == 0) return;
        try
        {
            IReadOnlyList<string> missing = await find(hashes, CancellationToken.None);
            if (serial != _noTextIndexSerial || _detached) return;
            _noTextIndex = new HashSet<string>(missing, StringComparer.Ordinal);
            if (_detailHash is not null && _standalone is null && !_recentActive) RenderDetail();   // 開いている詳細が、まだ読み込み中なら、理由の行を足す
        }
        catch (Exception ex)
        {
            _log.Warning("文字の索引がまだ無い画像を調べられなかった(理由の行が出ないだけ): " + ex.Message);
        }
    }

    /// <summary>
    /// 検索結果の見出し(件数、特徴ベクトル検索の済み具合、経路ごとの状態: 全文検索で一致した枚数、準備中・失敗・使えなかった理由、検索できない語を除いた知らせ)。
    /// <list type="bullet">
    /// <item>件数の言い方(ADR 0038 の一般則): 特徴ベクトル検索は全部の画像に順位を付けるので、上限(<see cref="SearchLimit"/>)で切れたときは「上位 n 枚」。
    ///   特徴ベクトル検索が使えて切れていないときは「全部の画像を近い順に」(全部が特徴ベクトル化済みのとき。済んでいない画像があるときは「特徴ベクトル検索の対象を近い順に」)。
    ///   特徴ベクトル検索が準備中・失敗のときの件数は、全文検索で一致した画像だけなので、「上位」とは言わない(切れたときを除く)。
    ///   (結果の枚数が上限と同じなら切れたとみなす。ちょうど上限の枚数だったときも「上位」になるが、嘘ではない)</item>
    /// <item>全文検索の行(ADR 0038 の一般則): n は <see cref="PathOutcome.Population"/> = その検索で一致した枚数。一覧に入った一致の枚数が n と違う(上限で落ちた)ときだけ「(一覧にはうち m 枚)」。</item>
    /// </list>
    /// </summary>
    internal static string BuildHeader(SearchResponse r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        bool cut = r.Results.Count >= SearchLimit;
        bool visualOk = r.Paths.Any(p => p.PathId == VisualPathId && p.State == PathState.Ok);
        string countNote = cut || !visualOk ? "" : r.IndexedImages >= r.TotalImages ? " (全部の画像を近い順に)" : $" ({ProcessingWords.StageName(JobKind.Embed)}の対象を近い順に)";
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"「{r.Query}」 {(cut ? "上位 " : "")}{r.Results.Count} 枚{countNote} ({r.Elapsed.TotalMilliseconds:0} ms)。{ProcessingWords.StageName(JobKind.Embed)}の対象 {r.IndexedImages} / {r.TotalImages} 枚"));
        if (r.IndexedImages < r.TotalImages) sb.Append($" (インデックスがまだ無い画像は、{ProcessingWords.StageName(JobKind.Embed)}に出ません)");
        foreach (PathOutcome p in r.Paths)
        {
            switch (p.State)
            {
                case PathState.NotReady:
                    sb.Append('\n').Append(PathLabel(p.PathId)).Append("は準備中です: ").Append(ReasonOf(p)).Append("。しばらくしてから、もう一度検索してください");
                    break;
                case PathState.Failed:
                    sb.Append('\n').Append(PathLabel(p.PathId)).Append("で検索できませんでした: ").Append(ReasonOf(p));
                    break;
                case PathState.Skipped:
                    sb.Append("\n").Append(PathLabel(p.PathId)).Append("は使いませんでした: ").Append(UiText.Plain(p.Message ?? "引けないクエリ"));
                    break;
                default:
                    if (p.PathId == FullTextPathId)
                    {
                        int inList = r.Results.Count(x => x.Hits.Any(h => h.PathId == FullTextPathId));
                        sb.Append(string.Create(CultureInfo.InvariantCulture, $"\n{ProcessingWords.StageName(JobKind.Vlm)}で一致した画像: {p.Population} 枚"));
                        if (inList != p.Population) sb.Append(string.Create(CultureInfo.InvariantCulture, $" (一覧にはうち {inList} 枚)"));
                    }
                    if (p.Message is { Length: > 0 } note) sb.Append("\n").Append(PathLabel(p.PathId)).Append(": ").Append(UiText.Plain(note));
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>全文検索の経路の PathId(Core の <c>FullTextPath.PathId</c> と同じ。App は Core の内部の型を参照しないので、ここに書く)。</summary>
    internal const string FullTextPathId = "fulltext";

    /// <summary>
    /// 経路が準備中・失敗のときの理由の文(画面に出す形)。理由の文が無い(null・空)ときは、経路の ID(fulltext・visual)をそのまま出さず「(理由の記録なし)」。
    /// (Core の準備中の経路は必ず理由の文を付け、失敗は例外の文を付けるので、実際にここへ来るのは Core の外が作った PathOutcome だけ。念のための穴埋め)
    /// </summary>
    private static string ReasonOf(PathOutcome p) => string.IsNullOrEmpty(p.Message) ? ProcessingWords.NoReasonRecorded : UiText.Plain(p.Message);

    /// <summary>特徴ベクトル検索(SigLIP2)の経路の PathId(Core の <c>VisualPath.Id</c> と同じ)。</summary>
    internal const string VisualPathId = "visual";

    /// <summary>
    /// 経路の表示名(知らない経路は PathId のまま)。段階の名前とモデル名は <see cref="ProcessingWords.StageNameWithModel"/>(Core の公開の表示名)から作り、ここに書き写さない。
    /// 全文検索は名前だけ(VLM のモデル名は、ヒントにだけ出す)。
    /// </summary>
    internal static string PathLabel(string pathId) => pathId switch
    {
        VisualPathId => ProcessingWords.StageNameWithModel(JobKind.Embed),
        FullTextPathId => ProcessingWords.StageName(JobKind.Vlm),
        _ => pathId,
    };

    /// <summary>画像のパス(DB の上で現存するもの)。最後の検索結果から、ハッシュで引く。</summary>
    internal IReadOnlyList<string> PathsOf(GridItem item) =>
        _results.TryGetValue(item.Hash, out SearchResult? r) ? r.Paths : [];

    /// <summary>
    /// 画像 1 枚のツールチップ: **出た理由**(言葉。<see cref="ReasonText"/>)、統合順位、経路ごとの順位と類似度、元のパス。<paramref name="paths"/>(最後の検索の経路ごとの状態)があれば、
    /// 準備中・失敗・使えなかった経路と、注意(検索できない語を除いた知らせ)を、パスの前に足す。一致箇所の強調は詳細画面。
    /// <paramref name="noTextIndex"/> は、その画像に全文検索の索引の文書がまだ無いこと(「テキスト検索のインデックスがまだありません」を理由に足す)。
    /// </summary>
    internal static string BuildTooltip(SearchResult x, IReadOnlyList<PathOutcome>? paths = null, bool noTextIndex = false)
    {
        var sb = new StringBuilder();
        foreach (string reason in ReasonText.Lines(x, noTextIndex)) sb.Append(reason).Append('\n');
        // 順位・類似度の書き方は詳細画面と同じ(DetailPresenter。「統合: n 位」「{経路の名前}: n 位」)。数字は理由の下(ツールチップの奥)に残す
        sb.Append("統合: ").Append(DetailPresenter.Rank(x.Rank));
        foreach (PathHit h in x.Hits)
            sb.Append('\n').Append(PathLabel(h.PathId)).Append(": ").Append(DetailPresenter.HitText(h));
        if (paths is not null)
        {
            foreach (PathOutcome p in paths)
            {
                string? state = p.State switch
                {
                    PathState.NotReady => "準備中",
                    PathState.Failed => "失敗",
                    PathState.Skipped => "この検索では使いませんでした",
                    _ => p.Message is { Length: > 0 } ? "注意" : null,
                };
                if (state is null) continue;
                sb.Append('\n').Append(PathLabel(p.PathId)).Append(": ").Append(state);
                if (p.Message is { Length: > 0 } m) sb.Append(" (").Append(UiText.Plain(m)).Append(')');
            }
        }
        if (x.Paths.Count > 0)
        {
            sb.Append('\n').Append(x.Paths[0]);
            if (x.Paths.Count > 1) sb.Append(string.Create(CultureInfo.InvariantCulture, $"\n(ほか {x.Paths.Count - 1} 件のパス)"));
        }
        sb.Append("\nEnter かダブルクリックで詳細を開けます");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 結果への操作(ドラッグ・コピー・エクスプローラーで表示)

    private void OnSelectionChanged()
    {
        int n = _grid.SelectedCount;
        UpdateTagButton();
        if (n > 0) _notice($"{n} 枚を選択中");
    }

    /// <summary>項目を開く合図(ダブルクリック・Enter)。詳細画面を開く。</summary>
    private void OnItemActivated(GridItem item) => _ = OpenDetailAsync(item.Hash);

    // ------------------------------------------------------------------ 詳細画面

    /// <summary>詳細画面が開いているか。</summary>
    internal bool IsDetailOpen => _detailHash is not null;

    /// <summary>開いている画像のハッシュ(開いていなければ null)。</summary>
    internal string? DetailHash => _detailHash;

    /// <summary>テスト・確認用: 詳細画面の要素。</summary>
    internal DetailView DetailViewForTests => _detail;

    /// <summary>
    /// ハッシュの画像の詳細画面を開く(一覧の上に重ねる)。すぐ、検索の結果の情報(順位・一致の欄・パス)を出し、続けて詳細(<see cref="DetailFunc"/>)と大きな画像(<see cref="ImageFunc"/>)を読む。
    /// 戻り値は、その読み込みが終わるまで(テスト用。呼び出し側は待たなくてよい)。結果に無いハッシュは何もしない。読み込みの失敗は、画面に理由を出して続ける(落ちない)。
    /// </summary>
    internal Task OpenDetailAsync(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return _results.ContainsKey(hash) ? ShowDetail(hash) : Task.CompletedTask;
    }

    /// <summary>
    /// ハッシュの画像の詳細画面を、検索の結果の外から開く(処理状態の失敗の一覧の「詳細」)。いまの検索の結果にある画像なら、ふつうに開く(順位・前後つき)。
    /// 結果に無い画像は、画像の詳細(<see cref="DetailFunc"/>)を読んで、順位も一致の強調も無い「検索の結果ではない」詳細として開く(前後は無い。Esc で一覧に戻る)。
    /// 画像の記録が無ければ、開かずに通知する。戻り値は、その読み込みが終わるまで(テスト用)。
    /// </summary>
    internal async Task OpenDetailByHashAsync(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (_results.ContainsKey(hash))
        {
            await OpenDetailAsync(hash);
            return;
        }
        Func<string, CancellationToken, Task<ImageDetail?>>? read = DetailFunc;
        if (read is null) return;
        ImageDetail? detail;
        try
        {
            detail = await read(hash, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("画像の詳細を読めなかった(処理状態の一覧から): " + hash, ex);
            _notice("詳細を読み込めませんでした: " + UiText.Plain(ex));
            return;
        }
        if (_detached) return;
        if (detail is null)
        {
            _notice("この画像の記録がありません (元のファイルが無い画像の記録が消された可能性があります)");
            return;
        }
        string[] present = [.. detail.Paths.Where(p => !p.Missing).Select(p => p.Path)];
        _standalone = new SearchResult(hash, present, 0, 0, []);
        await ShowDetail(hash);
    }

    /// <summary>詳細画面を閉じて、一覧に戻る(フォーカスは一覧へ)。</summary>
    internal void CloseDetail() => CloseDetail(returnFocus: true);

    private void CloseDetail(bool returnFocus)
    {
        if (_detailHash is null) return;
        _detailCts?.Cancel();
        _detailSerial++;
        _detailHash = null;
        _detail.Visibility = Visibility.Collapsed;
        _actionRow.Visibility = Visibility.Visible;
        _detailResult = null;
        _standalone = null;
        _detail.SetImage(null, null);   // 大きな画像を手放す
        if (returnFocus) _grid.Focus();
    }

    /// <summary>前(-1)・次(+1)の結果へ。端では何もしない。</summary>
    internal void NavigateDetail(int delta)
    {
        if (_detailHash is not { } current || !_orderIndex.TryGetValue(current, out int index)) return;
        int target = index + delta;
        if (target < 0 || target >= _order.Count) return;
        _ = ShowDetail(_order[target]);
    }

    private Task ShowDetail(string hash)
    {
        SearchResult result = _standalone is { } alone && alone.Hash == hash ? alone : _results[hash];
        bool inResults = _orderIndex.TryGetValue(hash, out int index);
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        int serial = ++_detailSerial;
        _detailHash = hash;

        if (inResults) _detail.SetPosition(index + 1, _order.Count, index > 0, index < _order.Count - 1);
        else _detail.SetPosition(1, 1, false, false);   // 検索の結果の外の詳細: 前後は無い
        _detailResult = result;
        _detailLoad = DetailLoad.Loading;
        _actionRow.Visibility = Visibility.Collapsed;   // 一覧の「タグを付ける」は、詳細画面の間は隠す(詳細画面の手動タグの欄で編集する)
        _detail.ShowTagError(null);
        _detail.ClearTagInput();
        _detail.SetCandidates([]);
        RenderDetail();
        _detail.ResetScroll();   // 別の画像に移った: 右側は先頭(タイトルと順位)から見せる
        _detail.SetImage(null, "読み込み中…");
        _detail.Visibility = Visibility.Visible;
        _detail.Focus();
        return LoadDetailAsync(hash, result, serial, cts.Token);
    }

    private async Task LoadDetailAsync(string hash, SearchResult result, int serial, CancellationToken ct)
    {
        Task imageTask = LoadDetailImageAsync(hash, serial, ct);
        DetailLoad load;
        try
        {
            Func<string, CancellationToken, Task<ImageDetail?>>? read = DetailFunc;
            if (read is null) throw new InvalidOperationException("Core に結ばれていない");
            ImageDetail? detail = await read(hash, ct);
            load = detail is null ? DetailLoad.RecordMissing : DetailLoad.Loaded(detail);
        }
        catch (OperationCanceledException)
        {
            return;   // 別の画像に移った、または閉じた
        }
        catch (Exception ex)
        {
            if (serial != _detailSerial) return;
            _log.Error("画像の詳細を読めなかった: " + hash, ex);
            load = DetailLoad.Failed(ex.Message);
        }
        if (serial == _detailSerial)
        {
            _detailLoad = load;
            RenderDetail();
            if (load.Kind == DetailLoadKind.RecordMissing) _log.Info("画像の記録が見つからない(詳細画面): " + hash);
            if (load.Kind == DetailLoadKind.Loaded) await RefreshCandidatesAsync(hash, serial, ct);
        }
        await imageTask;
    }

    /// <summary>大きな画像を読む。原本が使えない・デコードできないときは、画像の場所に理由を出す(詳細の他の情報は出る)。</summary>
    private async Task LoadDetailImageAsync(string hash, int serial, CancellationToken ct)
    {
        try
        {
            Func<string, CancellationToken, Task<ThumbnailResult>>? fetch = ImageFunc;
            if (fetch is null) throw new InvalidOperationException("Core に結ばれていない");
            ThumbnailResult result = await fetch(hash, ct);
            if (serial != _detailSerial) return;
            switch (result.Status)
            {
                case ThumbnailStatus.Ok:
                    _detail.SetImage(ThumbnailLoader.ToThumb(hash, result).Bitmap, null);
                    break;
                case ThumbnailStatus.SourceUnavailable:
                    _detail.SetImage(null, "元のファイルがありません (移動・削除された可能性があります)。または、スキャンのときから変わっています");
                    break;
                default:
                    _detail.SetImage(null, "画像として読み込めませんでした" + (result.Message is { Length: > 0 } m ? ": " + UiText.Plain(m) : ""));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 別の画像に移った、または閉じた
        }
        catch (Exception ex)
        {
            if (serial != _detailSerial) return;
            _log.Error("詳細画面の画像を読めなかった: " + hash, ex);
            _detail.SetImage(null, "画像を読み込めませんでした: " + UiText.Plain(ex));
        }
    }

    /// <summary>開いている詳細を、いまの検索の結果と読み込みの状態から描き直す(タグを編集したあと・読み込みが終わったあと)。</summary>
    private void RenderDetail()
    {
        if (_detailResult is null) return;
        _detail.ShowModel(DetailPresenter.Build(_detailResult, _lastPaths, _detailLoad, DisplayZone, fromSearch: _standalone is null && !_recentActive, fromRecentList: _standalone is null && _recentActive,
            noTextIndexHint: NoTextIndexNow(_detailResult.Hash)));
    }

    // ------------------------------------------------------------------ 手動タグの編集
    //
    // 付けたあとの更新: ①詳細画面の手動タグの欄は、タグを読み直して(Core の GetAsync)その欄だけ描き直す。検索のときの TextDetail は検索のときのままで、
    // 強調はタグを編集すると消える(範囲は検索のときの文字列の上でしか正しくない)。②検索の結果の一覧は**自動では検索し直さない**(順位の入れ替わりや、
    // 開いている詳細の位置が勝手に動くのを避ける)。タグは次の検索から効く。付けたあとの通知でそう伝える。

    /// <summary>一覧で選んだ画像に、手動タグを付ける(名前を聞いてから、1 トランザクションでまとめて付ける)。</summary>
    internal async Task TagSelectedAsync()
    {
        ITagService? tags = TagService;
        if (tags is null) return;
        string[] hashes = [.. _grid.SelectedItems().Select(i => i.Hash)];
        if (hashes.Length == 0) { _notice("手動タグを付ける画像を、一覧で選んでください"); return; }

        IReadOnlyList<TagCandidate> candidates;
        try
        {
            candidates = await tags.GetCandidatesAsync(CandidateLimit, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("タグの候補を読めなかった", ex);
            candidates = [];
        }
        string? input;
        try
        {
            input = await PromptTag(hashes.Length, candidates);
        }
        catch (Exception ex)
        {
            // 名前を聞く画面の失敗。ボタンの押下から投げっぱなしで呼んでいるので、ここで受けないと、何も起きないまま例外が消える
            _log.Error("タグの名前を聞く画面を出せなかった", ex);
            ShowProblem("手動タグの名前を聞く画面を出せませんでした: " + UiText.Plain(ex));
            return;
        }
        if (input is null) return;   // キャンセル

        try
        {
            string name = ManualTagName.Normalize(input);
            int added = await tags.AddToManyAsync(hashes, name, CancellationToken.None);
            _notice(string.Create(CultureInfo.InvariantCulture, $"手動タグ「{name}」を {hashes.Length} 枚に付けました (新しく付いたのは {added} 枚)。検索し直すと、手動タグでも見つけられます"));
        }
        catch (InvalidTagNameException ex)
        {
            ShowProblem("手動タグを付けられませんでした: " + UiText.Plain(ex.Reason));
        }
        catch (Exception ex)
        {
            _log.Error("タグを付けられなかった", ex);
            ShowProblem("手動タグを付けられませんでした: " + UiText.Plain(ex));
        }
    }

    /// <summary>詳細画面で、表示中の画像にタグを足す(拒む名前は、入力欄の下に理由を出して、何も書かない)。</summary>
    internal async Task AddDetailTagAsync(string input)
    {
        if (_detailHash is not { } hash || TagService is not { } tags || _detailLoad.Kind != DetailLoadKind.Loaded) return;
        int serial = _detailSerial;
        string name;
        try
        {
            name = ManualTagName.Normalize(input);
        }
        catch (InvalidTagNameException ex)
        {
            _detail.ShowTagError("手動タグの名前として使えません: " + UiText.Plain(ex.Reason));
            return;
        }
        try
        {
            int added = await tags.AddToManyAsync([hash], name, CancellationToken.None);
            if (serial != _detailSerial) return;   // 書いている間に、別の画像へ移った(書き込みは済んでいる。表示は新しい画像のもの)
            _detail.ShowTagError(null);
            _detail.ClearTagInput();
            await ReloadDetailTagsAsync(hash, serial);
            _notice(added == 1 ? $"手動タグ「{name}」を付けました" : $"手動タグ「{name}」は、すでに付いています");
        }
        catch (InvalidTagNameException ex)
        {
            if (serial == _detailSerial) _detail.ShowTagError("手動タグの名前として使えません: " + UiText.Plain(ex.Reason));
        }
        catch (Exception ex)
        {
            _log.Error("タグを付けられなかった: " + hash, ex);
            if (serial == _detailSerial) _detail.ShowTagError("手動タグを付けられませんでした: " + UiText.Plain(ex));
        }
    }

    /// <summary>詳細画面で、表示中の画像からタグを外す。</summary>
    internal async Task RemoveDetailTagAsync(string name)
    {
        if (_detailHash is not { } hash || TagService is not { } tags || _detailLoad.Kind != DetailLoadKind.Loaded) return;
        int serial = _detailSerial;
        try
        {
            bool removed = await tags.RemoveAsync(hash, name, CancellationToken.None);
            if (serial != _detailSerial) return;
            _detail.ShowTagError(null);
            await ReloadDetailTagsAsync(hash, serial);
            _notice(removed ? $"手動タグ「{name}」を外しました" : $"手動タグ「{name}」は、もう付いていませんでした");
        }
        catch (Exception ex)
        {
            _log.Error("タグを外せなかった: " + hash, ex);
            if (serial == _detailSerial) _detail.ShowTagError("手動タグを外せませんでした: " + UiText.Plain(ex));
        }
    }

    /// <summary>タグを読み直して、詳細の手動タグの欄だけ差し替えて描き直す(ほかの欄・順位・読んだ詳細はそのまま。スクロールの位置も動かさない)。</summary>
    private async Task ReloadDetailTagsAsync(string hash, int serial)
    {
        ITagService? tags = TagService;
        if (tags is null || _detailLoad.Detail is not { } detail) return;
        IReadOnlyList<string> fresh = await tags.GetAsync(hash, CancellationToken.None);
        if (serial != _detailSerial) return;
        _detailLoad = DetailLoad.Loaded(detail with { ManualTags = fresh });
        RenderDetail();
        await RefreshCandidatesAsync(hash, serial, CancellationToken.None);
    }

    /// <summary>既存のタグの候補を読んで、詳細画面に出す(いま付いているタグは除く)。失敗しても、候補が出ないだけ。</summary>
    private async Task RefreshCandidatesAsync(string hash, int serial, CancellationToken ct)
    {
        ITagService? tags = TagService;
        if (tags is null || _detailLoad.Detail is not { } detail) return;
        try
        {
            IReadOnlyList<TagCandidate> candidates = await tags.GetCandidatesAsync(CandidateLimit, detail.ManualTags, ct);
            if (serial == _detailSerial && hash == _detailHash) _detail.SetCandidates(candidates);
        }
        catch (OperationCanceledException)
        {
            // 別の画像に移った、または閉じた
        }
        catch (Exception ex)
        {
            _log.Warning("タグの候補を読めなかった: " + ex.Message);
        }
    }

    private void UpdateTagButton()
    {
        int n = _grid.SelectedCount;
        _tagButton.IsEnabled = n > 0 && TagService is not null;
        _tagButton.Content = n > 0 ? string.Create(CultureInfo.InvariantCulture, $"手動タグを付ける… (選択中 {n} 枚)") : "手動タグを付ける…";
    }

    /// <summary>テスト・確認用: 一覧の「タグを付ける」ボタン。</summary>
    internal Button TagButton => _tagButton;

    internal bool TagRowVisible => _actionRow.Visibility == Visibility.Visible;

    /// <summary>
    /// 詳細画面の画像を押したまま動かしたとき: 表示中の画像のファイルを、外のアプリへ渡す(一覧のドラッグと同じ規則: Copy だけ、実在するファイルだけ、
    /// 無ければ始めずに赤い帯で知らせ、<see cref="MissingFilesDetected"/> でそのフォルダの走査し直しを頼む)。
    /// </summary>
    private void StartDetailDrag()
    {
        if (_detailHash is not { } hash || !_results.TryGetValue(hash, out SearchResult? result)) return;
        string? path = FileLocator.FirstExisting(result.Paths);
        if (path is null)
        {
            ShowProblem("元のファイルが無いので、ドラッグできません (移動・削除された可能性があります)");
            ReportMissing([result.Paths]);
            return;
        }
        DragDropEffects effect = _shell.DragFiles(_detail.DragSource, [path]);
        OnDragFinished(effect, 1, 0, path);
    }

    private void OnDragFinished(DragDropEffects effect, int files, int missing, string? first)
    {
        string what = effect switch
        {
            DragDropEffects.Copy => "ドラッグで他のアプリへ渡しました",
            DragDropEffects.None => "ドラッグは取り消されました",
            _ => "ドラッグが終わりました",
        };
        string text = $"{what} ({files} 枚" + (missing > 0 ? $"、元のファイルが無い {missing} 枚は除きました" : "") + ")";
        if (missing > 0) ShowProblem(text);
        else if (effect == DragDropEffects.None) _log.Info(text);   // 取り消しは画面に出さない(ログだけ。わずかに動いて始まったドラッグを、離しただけで毎回知らせていた)
        else _notice(text);
    }

    /// <summary>
    /// 操作できなかった・一部できなかったことを、<b>目立つ形</b>で知らせる(一覧の上のバナーを数秒出す)。下の状態の行にも同じ文を出す(ログにも残る)。
    /// ドラッグを始めたとき(元のファイルが無い)に、状態の行の小さな文字だけだと「単にできない」ように見える。
    /// コピー・エクスプローラーで表示の「見つからない」も、同じ出し方にそろえた。
    /// </summary>
    internal void ShowProblem(string text)
    {
        _notice(text);
        _bannerText.Text = text;
        _banner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        _banner.Visibility = Visibility.Collapsed;
    }

    /// <summary>テスト・確認用: バナーが出ているか・その文。</summary>
    internal bool BannerVisible => _banner.Visibility == Visibility.Visible;

    internal string BannerText => _bannerText.Text;


    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "エクスプローラーで表示", "E", RevealSelected);
        menu.Items.Add(new Separator());
        AddMenu(menu, "画像をコピー", "Ctrl+C", CopySelectedImage);
        AddMenu(menu, "ファイルとしてコピー", "Ctrl+Shift+C", CopySelectedFiles);
        menu.Items.Add(new Separator());
        AddMenu(menu, "手動タグを付ける…", null, () => _ = TagSelectedAsync());
        _grid.ContextMenu = menu;
        _grid.ContextMenuOpening += (_, e) =>
        {
            if (_grid.SelectedCount == 0) e.Handled = true;   // 何も選んでいないときはメニューを出さない
        };
    }

    /// <summary>メニューの項目を足す。<paramref name="shortcut"/> は右に出すショートカットの表示(キーの処理は <see cref="KeyShortcuts"/>。表示だけ)。</summary>
    private static void AddMenu(ContextMenu menu, string header, string? shortcut, Action onClick)
    {
        var item = new MenuItem { Header = header };
        if (shortcut is not null) item.InputGestureText = shortcut;
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    /// <summary>
    /// ウィンドウの PreviewKeyDown から。処理したら true。判定は <see cref="KeyShortcuts.Resolve"/>(一覧にフォーカスがあるときだけ。検索ボックスでは、E は文字として入る・Ctrl+C は文字列のコピー)。
    /// </summary>
    public bool HandlePreviewKeyDown(KeyEventArgs e)
    {
        if (_detailHash is not null)
            return HandleDetailAction(KeyShortcuts.ResolveDetail(e.Key, e.ImeProcessedKey, Keyboard.Modifiers, _searchBox.IsKeyboardFocused || _detail.TagInputFocused));

        ShortcutAction action = KeyShortcuts.Resolve(e.Key, e.ImeProcessedKey, Keyboard.Modifiers, GridHasFocus, _grid.SelectedCount);
        switch (action)
        {
            case ShortcutAction.CopyImage: CopySelectedImage(); return true;
            case ShortcutAction.CopyFiles: CopySelectedFiles(); return true;
            case ShortcutAction.RevealInExplorer: RevealSelected(); return true;
            default: return false;
        }
    }

    /// <summary>
    /// ウィンドウの PreviewMouseDown から(押されたボタンだけを渡す)。処理したら true。詳細画面が開いているとき、マウスの戻るボタンは「戻る(Esc)」と同じ
    /// (判定は <see cref="MouseShortcuts.ClosesDetail"/>)。それ以外のボタン・一覧が出ているときは、何もしない(false)。
    /// </summary>
    public bool HandleMouseButton(MouseButton button)
    {
        if (!MouseShortcuts.ClosesDetail(button, _detailHash is not null)) return false;
        CloseDetail();
        return true;
    }

    /// <summary>詳細画面が開いているときのキーの操作を行う(判定は <see cref="KeyShortcuts.ResolveDetail"/>)。処理したら true。</summary>
    internal bool HandleDetailAction(DetailKeyAction action)
    {
        switch (action)
        {
            case DetailKeyAction.Close: CloseDetail(); return true;
            case DetailKeyAction.Previous: NavigateDetail(-1); return true;
            case DetailKeyAction.Next: NavigateDetail(+1); return true;
            case DetailKeyAction.CopyImage: CopySelectedImage(); return true;
            case DetailKeyAction.CopyFiles: CopySelectedFiles(); return true;
            case DetailKeyAction.RevealInExplorer: RevealSelected(); return true;
            default: return false;
        }
    }

    /// <summary>元のファイルが無かった画像のパスを、<see cref="MissingFilesDetected"/> で知らせる(そのフォルダを走査し直してもらう)。</summary>
    private void ReportMissing(IEnumerable<IReadOnlyList<string>> candidates)
    {
        string[] paths = candidates.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 0) MissingFilesDetected?.Invoke(paths);
    }

    /// <summary>操作(エクスプローラーで表示・コピー)の対象のパス。詳細画面が開いていれば、表示中の画像 1 枚。そうでなければ一覧の選択。</summary>
    private IEnumerable<IReadOnlyList<string>> SelectedPaths() =>
        _detailHash is { } open ? (_results.TryGetValue(open, out SearchResult? r) ? [r.Paths] : []) : _grid.SelectedItems().Select(PathsOf);

    internal void RevealSelected()
    {
        // 先頭の選択を対象にする。ファイルの実在は、ここで確認する(検索結果は DB の印だけで決まっている)
        IReadOnlyList<string>? candidates = SelectedPaths().FirstOrDefault();
        if (candidates is null) return;
        string? path = FileLocator.FirstExisting(candidates);
        if (path is null) { ShowProblem(FileMissingNotice); ReportMissing([candidates]); return; }
        try
        {
            _shell.RevealInExplorer(path);
            _notice("エクスプローラーで表示しました: " + Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException)
        {
            _log.Error("エクスプローラーで表示に失敗: " + path, ex);
            ShowProblem("エクスプローラーで表示できませんでした: " + UiText.Plain(ex));
        }
    }

    internal async void CopySelectedImage()
    {
        IReadOnlyList<string>? candidates = SelectedPaths().FirstOrDefault();
        if (candidates is null) return;
        string? path = FileLocator.FirstExisting(candidates);
        if (path is null) { ShowProblem(FileMissingNotice); ReportMissing([candidates]); return; }
        try
        {
            await _shell.CopyImageAsync(path);
            _notice("画像をコピーしました: " + Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Error("画像のコピーに失敗: " + path, ex);
            ShowProblem("画像をコピーできませんでした: " + UiText.Plain(ex));
        }
    }

    internal void CopySelectedFiles()
    {
        (string[] paths, int missing) = FileLocator.Resolve(SelectedPaths());
        if (missing > 0) ReportMissing(SelectedPaths().Where(c => FileLocator.FirstExisting(c) is null));
        if (paths.Length == 0) { ShowProblem(FileMissingNotice); return; }
        try
        {
            _shell.CopyFiles(paths);
            string done = $"{paths.Length} 枚をファイルとしてコピーしました";
            if (missing > 0) ShowProblem(done + $" (元のファイルが無い {missing} 枚は除きました)");
            else _notice(done);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _log.Error("ファイルのコピーに失敗", ex);
            ShowProblem("ファイルをコピーできませんでした: " + UiText.Plain(ex));
        }
    }
}
