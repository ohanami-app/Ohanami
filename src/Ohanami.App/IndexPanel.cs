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
using System.Windows.Media;
using Microsoft.Win32;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 左のパネル: フォルダ(追加・外す・有効/無効)、走査、埋め込みと VLM の開始、一時停止・再開、進捗(文章は <see cref="IndexStatusText"/>)。
/// 進捗は <c>Indexer.StateChanged</c> のイベントで受け、最新の 1 つだけを UI スレッドに渡して表示する(<c>Indexer.State</c> は読むたびに DB の件数を数えるので、
/// イベントの外では読まない。ボタンの判断も、最後に表示したスナップショットを使う)。
/// 閉じる途中(<c>isClosing</c>)・Core を手放したあと(<see cref="Detach"/>)は、DB に触る続きを動かさない。
/// </summary>
internal sealed class IndexPanel : DockPanel
{
    private readonly ICoreLog _log;
    private readonly Action<string> _notice;
    private readonly Func<bool> _isClosing;
    private readonly IShellActions _shell;   // 失敗の一覧の「エクスプローラーで表示」。製品は SystemShellActions、テストは偽物

    private OhanamiCore? _core;
    private readonly ListBox _folderList;
    private readonly Button _addFolderButton, _removeFolderButton;
    private readonly Button _runButton;        // 開始 / 一時停止 / 再開(1 つのボタン。状態で文字が替わる)
    private readonly Button _stateButton;      // 処理状態…(窓を開く)
    private readonly TextBlock _summaryText;   // 「処理状態…」の隣の 1 行(0 のものは書かない。全部 0 なら出さない)
    private IndexRunState? _toggleFrom;        // 押した時点の状態。次の状態が届くまで、開始・一時停止・再開のボタンを押せなくする(二度押しで「開始 → すぐ一時停止」にならないように)
    private int? _missingCount;   // 消せる画像(見つからない画像)の数。数えたあとで更新する(RefreshMissingCountAsync)
    private bool _busy;           // 押した操作が、開始の呼び出しに届くまでの間(二度押しで、開始のあとにすぐ別の操作が走らないように)
    private readonly IndexRowsView _indexRows;   // 「インデックス」欄の固定の 7 行(行数は、どの状態でも同じ。ADR 0039)
    private string _statusFull = "";             // 進捗の全文(IndexStatusText.Build。テスト・確認用)
    private readonly FrameworkElement _indexSection;   // 「インデックス」欄の全体(見出し〜まとめの行)。高さは、どの状態でも同じ
    private readonly ProgressBar _progress;

    private IndexState? _latestState;
    private int _stateQueued;
    private IndexState? _shownState;
    private bool _scanning;
    private readonly HashSet<long> _pendingRescan = [];   // 走査の最中に頼まれた、フォルダごとの走査し直し(終わったあとに 1 回だけ)

    /// <summary>進捗の表示を更新したあと(UI スレッド)。MainWindow が Core の状態(ベクトルの件数)の表示を更新する。</summary>
    public event Action<IndexState>? StateApplied;

    /// <summary>走査が終わった(成功したとき。UI スレッド)。MainWindow が、今の検索をやり直して、消えた・動かした画像を結果に反映する。</summary>
    public event Action<ScanSummary>? ScanFinished;

    /// <summary>失敗の一覧の「詳細」が押された(画像のハッシュ。UI スレッド)。MainWindow が、その画像の詳細画面を開く(状態の行を除く窓の全面に重なる。ADR 0050)。</summary>
    public event Action<string>? OpenDetailRequested;

    /// <summary>確認の画面(タイトル, 本文 → OK なら true)。既定は <see cref="MessageBox"/>。テストで差し替える。</summary>
    internal Func<string, string, bool> ConfirmFunc { get; set; }

    /// <summary>確認の窓の持ち主: **処理状態の窓が開いていればそれ**(操作を押した窓の上に出す)、なければメインの画面。</summary>
    internal Window? ConfirmOwner => (Window?)_failureWindow ?? Application.Current?.MainWindow;

    /// <summary>失敗の一覧の画面を出す(既定は、メインの画面を親にして <see cref="Window.Show"/>。テストで差し替える)。</summary>
    internal Action<FailureListWindow> ShowFailureWindow { get; set; } = w =>
    {
        w.Owner = Application.Current?.MainWindow;
        w.Show();
    };

    /// <summary>
    /// 開いたままの失敗の一覧の画面を、前に出す(最小化されていれば戻す。既定。テストで差し替える=画面を実際には触らない)。
    /// ShellBoundaryGuardTests の許可の表に、この呼び出しのために IndexPanel.cs を足した。
    /// </summary>
    internal Action<FailureListWindow> ActivateFailureWindow { get; set; } = w =>
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    };

    /// <summary>開いている失敗の一覧の画面(1 つだけ。閉じたら null)。左パネルのボタンをもう一度押しても、増やさずにこれを使う。</summary>
    private FailureListWindow? _failureWindow;

    /// <param name="shell">失敗の一覧の「エクスプローラーで表示」の先。null なら本物のシェル(<see cref="SystemShellActions"/>)。テストは偽物を渡す。</param>
    public IndexPanel(ICoreLog log, Action<string> notice, Func<bool> isClosing, IShellActions? shell = null)
    {
        _log = log;
        _notice = notice;
        _isClosing = isClosing;
        _shell = shell ?? SystemShellActions.Instance;
        ConfirmFunc = (title, text) => MessageBox.Show(ConfirmOwner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        Margin = new Thickness(12, 12, 6, 12);
        LastChildFill = true;

        _folderList = UiParts.TextList(new ListBox { MinHeight = 120, SelectionMode = SelectionMode.Single });
        ScrollViewer.SetHorizontalScrollBarVisibility(_folderList, ScrollBarVisibility.Disabled);   // 長いパスは折り返す(横スクロールにしない)
        _folderList.SelectionChanged += (_, _) => UpdateButtons();
        // 左パネルのボタンは 4 つ(ADR 0039): 追加・外す(フォルダ)、開始/一時停止/再開(インデックス)、インデックスの状態。
        // 取り込みを止める/再開する・今すぐ変化を確かめるは、フォルダの行の右クリック。まれな操作(失敗のやり直し・要再作成・記録を消す)は、インデックスの状態の窓へ。文字は ProcessingWords
        _addFolderButton = UiParts.Button(ProcessingWords.AddFolderLabel, OnAddFolder);
        _removeFolderButton = UiParts.Button(ProcessingWords.RemoveFolderLabel, OnRemoveFolder);   // 確認の画面が出るボタンには「…」を付ける(ADR 0038)
        _runButton = UiParts.Button(ProcessingWords.StartLabel, OnRunButton);
        _runButton.ToolTip = ProcessingWords.StartTooltip();   // 時間の見込みは、要再作成の確認と同じ数(ProcessingText.VlmSecondsPerImage)から作る
        _stateButton = UiParts.Button(ProcessingWords.ProcessingStateLabel, (_, _) => OpenFailureList());
        _stateButton.ToolTip = ProcessingWords.ProcessingStateTooltip;
        _indexRows = new IndexRowsView();
        _progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, Value = 0, Margin = new Thickness(0, 4, 0, 0) };
        // まとめの行: 1 行で切る(入りきらなければ末尾を「…」に。全文はヒント)。空でも、行は残す(高さを変えない)
        _summaryText = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6) };

        var folderButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (Button b in new[] { _addFolderButton, _removeFolderButton }) folderButtons.Children.Add(b);
        var runRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        runRow.Children.Add(_runButton);
        var stateRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        SetDock(_stateButton, Dock.Left);
        stateRow.Children.Add(_stateButton);
        stateRow.Children.Add(_summaryText);

        var top = new StackPanel();
        top.Children.Add(UiParts.Header(ProcessingWords.FolderHeader));
        SetDock(top, Dock.Top);
        Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _indexSection = bottom;
        bottom.Children.Add(folderButtons);
        bottom.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6), Height = 1 });   // Fluent の Separator は、高さが 19px になる。明示して 1px にする
        bottom.Children.Add(UiParts.Header(ProcessingWords.IndexHeader));
        bottom.Children.Add(runRow);
        // 「インデックス」欄の固定の 7 行と進捗バーは、白い下地と枠の 1 枚の面(カード)に入れる。文字の行が、ほかの UI(ボタン・見出し)と区切れて見える
        var indexCard = new Border
        {
            Background = AppColors.ListSurface, BorderBrush = AppColors.ListBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 6), Margin = new Thickness(0, 6, 0, 0),
        };
        var indexCardBody = new StackPanel();
        indexCardBody.Children.Add(_indexRows);
        indexCardBody.Children.Add(_progress);
        indexCard.Child = indexCardBody;
        bottom.Children.Add(indexCard);
        bottom.Children.Add(stateRow);
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        Children.Add(_folderList);

        UpdateButtons();
    }

    /// <summary>最後に表示した状態の、ジョブの実行状態。</summary>
    public IndexRunState Run => _shownState?.Run ?? IndexRunState.Idle;

    // ------------------------------------------------------------------ Core との接続

    public void Attach(OhanamiCore core)
    {
        _core = core;
        core.Indexer.StateChanged += OnIndexerStateChanged;
        core.Indexer.ScanCompleted += OnIndexerScanCompleted;
        core.FolderWatchChanged += OnFolderWatchChanged;
        RefreshFolders();
        ApplyState(core.Indexer.State);   // 起動時の 1 回だけ読む(以降はイベント)
        UpdateButtons();
    }

    /// <summary>Core を手放す(閉じる前)。以降、このパネルは DB に触らない。</summary>
    public void Detach()
    {
        if (_core is { } core)
        {
            core.Indexer.StateChanged -= OnIndexerStateChanged;
            core.Indexer.ScanCompleted -= OnIndexerScanCompleted;
            core.FolderWatchChanged -= OnFolderWatchChanged;
        }
        _core = null;
        UpdateButtons();
    }

    /// <summary>起動時に 1 度、登録済みのフォルダがあれば走査する(差分だけなので軽い。新しい画像・消えた画像を拾う)。</summary>
    public async Task ScanAtStartupAsync()
    {
        if (_core is { } core && core.Folders.List().Count > 0) await ScanAsync(null, ScanTrigger.Startup);
    }

    /// <summary>閉じる途中、または Core を手放した(DB に触る続きを動かさない)。</summary>
    private bool Gone => _isClosing() || _core is null;

    // ------------------------------------------------------------------ 進捗(イベントで受ける)

    private void OnIndexerStateChanged(IndexState state)
    {
        // どのスレッドからでも呼ばれる。最新の 1 つだけを UI スレッドに渡す(溜めない)
        _latestState = state;
        if (Interlocked.Exchange(ref _stateQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _stateQueued, 0);   // 先に印を戻してから最新を読む(読んだあとに来た更新を取りこぼさない)
                if (!Gone && _latestState is { } s) ApplyState(s);
            });
        }
    }

    private void ApplyState(IndexState s)
    {
        _shownState = s;
        if (_toggleFrom is { } from && s.Run != from) _toggleFrom = null;   // 次の状態が届いた: また押せる
        _statusFull = IndexStatusText.Build(s);
        _indexRows.Show(IndexStatusText.BuildRows(s));
        _progress.Value = IndexStatusText.Progress(s);
        UpdateButtons();
        StateApplied?.Invoke(s);
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours} 時間 {t.Minutes} 分")
        : t.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{t.Minutes} 分 {t.Seconds} 秒")
        : string.Create(CultureInfo.InvariantCulture, $"{t.Seconds} 秒");

    private void UpdateButtons()
    {
        bool open = _core is not null;
        IndexRunState run = Run;
        _addFolderButton.IsEnabled = open;
        _removeFolderButton.IsEnabled = open && _folderList.SelectedItem is not null;

        // 開始 / 一時停止 / 再開の 1 つのボタン(開始と一時停止・再開は同時に押せる時が無い。ADR 0039)。
        // 止まっている・失敗 → 開始。実行中・待機中 → 一時停止。一時停止の途中 → 押せない(状態の文が「一時停止の途中…」と言う)。一時停止中 → 再開。
        // 押したら、次の状態が届くまで(_toggleFrom)・別の操作の最中(_busy)は押せない
        (string label, bool allowed) = run switch
        {
            IndexRunState.Running or IndexRunState.Waiting => (ProcessingWords.PauseLabel, true),
            IndexRunState.Pausing => (ProcessingWords.PauseLabel, false),
            IndexRunState.Paused => (ProcessingWords.ResumeLabel, true),
            _ => (ProcessingWords.StartLabel, true),   // Idle・Failed
        };
        _runButton.Content = label;
        _runButton.IsEnabled = open && allowed && _toggleFrom is null && !_busy;

        // 処理状態の可否・数は ProcessingSnapshot(純粋な関数)。処理状態の窓にも同じものを届ける
        ProcessingSnapshot snap = ProcessingSnapshot.Build(_shownState, _missingCount, open, _busy);
        _stateButton.IsEnabled = open;
        string summary = ProcessingWords.LeftSummary(snap);
        _summaryText.Text = summary;
        _summaryText.ToolTip = summary.Length == 0 ? null : summary;   // 切れたときの全文
        _failureWindow?.Apply(snap);   // 開いている処理状態の窓にも、同じ数と可否を届ける(表示中の種類の数が変われば、窓が自動で読み直す)
    }

    /// <summary>いまの数と操作の可否(最後に表示した状態から)。処理状態の窓に渡す。</summary>
    internal ProcessingSnapshot CurrentSnapshot => ProcessingSnapshot.Build(_shownState, _missingCount, _core is not null, _busy);

    /// <summary>処理状態の窓の操作を、Core につなぐ(窓は Core を知らない)。</summary>
    private Task PerformProcessingActionAsync(ProcessingAction action) => action switch
    {
        ProcessingAction.RetryEmbed => RetryFailedAsync(JobKind.Embed),
        ProcessingAction.RetryVlm => RetryFailedAsync(JobKind.Vlm),
        ProcessingAction.RetrySkipped => RetrySkippedAsync(),
        ProcessingAction.RequeueEmbed => RequeueAsync(JobKind.Embed),
        ProcessingAction.RequeueVlm => RequeueAsync(JobKind.Vlm),
        ProcessingAction.PurgeMissing => PurgeMissingAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "知らない操作"),
    };

    /// <summary>テスト・確認用: 「押した直後」の状態にする(<paramref name="from"/> と違う状態が届くまで、開始 / 一時停止 / 再開のボタンが押せない)。本物の操作は、状態が替わる前に終わってしまうことがあるので、鍵の外し方だけを確かめるために使う。</summary>
    internal void LockRunButtonForTests(IndexRunState from)
    {
        _toggleFrom = from;
        UpdateButtons();
    }

    /// <summary>テスト・確認用: 開始 / 一時停止 / 再開のボタンの文字。</summary>
    internal string RunButtonText => (string)_runButton.Content;

    /// <summary>テスト・確認用: 開始 / 一時停止 / 再開のボタンが押せるか。</summary>
    internal bool CanRun => _runButton.IsEnabled;

    /// <summary>テスト・確認用: 「処理状態…」の隣の 1 行(0 のものは書かない。全部 0 なら空)。</summary>
    internal string SummaryText => _summaryText.Text;

    /// <summary>テスト・確認用: 「処理状態…」が押せるか。</summary>
    internal bool CanOpenProcessingState => _stateButton.IsEnabled;

    /// <summary>テスト・確認用: 数えたフォルダに無い画像の数(まだ数えていなければ null)。</summary>
    internal int? MissingCount => _missingCount;

    /// <summary>テスト・確認用: フォルダの各行の右クリックのメニューの、取り込みを止める / 再開するの文字(行ごとに 1 つ)。</summary>
    internal IReadOnlyList<string> FolderMenuHeaders => _folderList.Items.OfType<ListBoxItem>().Select(i => (string)((MenuItem)i.ContextMenu!.Items[1]!).Header).ToList();

    /// <summary>
    /// テスト・確認用: フォルダの各行の右クリックの「今すぐ変化を確かめる」(行ごとに 1 つ)。メニューを開いたときと同じ更新をしてから返す(押せるか・ヒントが、いまの状態を映す)。
    /// </summary>
    internal IReadOnlyList<MenuItem> FolderCheckNowItems()
    {
        var items = new List<MenuItem>();
        foreach (ListBoxItem row in _folderList.Items.OfType<ListBoxItem>())
        {
            var item = (MenuItem)row.ContextMenu!.Items[0]!;
            UpdateCheckNowItem(item, (FolderInfo)row.Tag);
            items.Add(item);
        }
        return items;
    }

    // ------------------------------------------------------------------ フォルダ

    /// <summary>
    /// フォルダ一覧の 1 行のツールチップ: パス、直近の走査で到達できなかった場所の記録(あれば)、監視できない理由と監視の説明(あれば)。
    /// 到達できなかった場所の記録は Core の文で、自分で「到達できなかった場所 {n} 件(…):」と言い出すので、接頭は付けない(付けると「到達できなかった場所: 到達できなかった場所 …」と 2 回続く)。
    /// Core の文の中の「{ディレクトリ} — {理由}」の区切りは、<see cref="UiText.Plain(string?)"/> が「: 」にする(失敗の文の形にそろえる。ADR 0038)。
    /// </summary>
    internal static string FolderTooltip(string path, string? lastError, string? unwatchedReason) =>
        path + (lastError is not null ? "\n" + UiText.Plain(lastError) : "")
            + (unwatchedReason is not null ? "\n監視できません: " + UiText.Plain(unwatchedReason) + "\n(監視は、フォルダの変化を見張って、新しい画像を自動で拾う仕組みです。ここは、15 分ごとのスキャンだけで拾います)" : "");

    private void RefreshFolders()
    {
        if (_core is null) return;
        long? keep = (_folderList.SelectedItem as ListBoxItem)?.Tag is FolderInfo cur ? cur.Id : null;
        _folderList.Items.Clear();
        IReadOnlyDictionary<long, string> unwatched = _core.UnwatchedFolders;   // 監視を作れなかったフォルダ(定期の走査だけ。ADR 0009)
        foreach (FolderInfo f in _core.Folders.List())
        {
            bool noWatch = f.Enabled && unwatched.ContainsKey(f.Id);
            var item = new ListBoxItem
            {
                Tag = f,
                Padding = UiParts.ListRowPadding,
                Content = new TextBlock
                {
                    Text = (f.Enabled ? "" : ProcessingWords.FolderStoppedMark) + f.Path + "\n" + f.ImageCount.ToString(CultureInfo.InvariantCulture) + " 枚"
                        + (noWatch ? "\n監視できません (定期のスキャンだけです)" : ""),
                    TextWrapping = TextWrapping.Wrap,
                },
                ToolTip = FolderTooltip(f.Path, f.LastError, noWatch ? unwatched[f.Id] : null),
            };
            if (f.LastError is not null) item.Foreground = AppColors.Danger;
            // 行の右クリック: 取り込みを止める / 再開する(有効/無効のボタンは無い。ADR 0039)。右クリックした行を選んでから、メニューを出す
            FolderInfo info = f;
            var menuItem = new MenuItem { Header = f.Enabled ? ProcessingWords.FolderStopImport : ProcessingWords.FolderResumeImport, ToolTip = ProcessingWords.FolderImportHint };
            menuItem.Click += async (_, _) => await ToggleFolderAsync(info);
            // 今すぐ変化を確かめる: そのフォルダだけを走査する。Core は、指定されたフォルダなら、取り込みを止めていても走査してしまう(Scanner.LoadFolders)ので、
            // 止めているフォルダでは押せなくする(理由はヒントに)。走査の最中も押せない。メニューを開くたびに、押せるかを決める
            var checkItem = new MenuItem { Header = ProcessingWords.FolderCheckNow };
            checkItem.Click += async (_, _) => await ScanFolderNowAsync(info);
            var menu = new ContextMenu { Items = { checkItem, menuItem } };
            menu.Opened += (_, _) => UpdateCheckNowItem(checkItem, info);
            UpdateCheckNowItem(checkItem, info);
            item.ContextMenu = menu;
            item.PreviewMouseRightButtonDown += (_, _) => item.IsSelected = true;
            _folderList.Items.Add(item);
            if (f.Id == keep) item.IsSelected = true;
        }
        UpdateButtons();
        _ = RefreshMissingCountAsync();   // 走査・ジョブ・フォルダの操作のあとに、消せる画像の数を取り直す(裏で数える)
    }

    /// <summary>「今すぐ変化を確かめる」を押せるか(Core を開いていて、走査の最中でなく、このフォルダの取り込みを止めていない)と、ヒント(押せない理由)を決める。</summary>
    private void UpdateCheckNowItem(MenuItem item, FolderInfo folder)
    {
        bool allowed = _core is not null && !_scanning && folder.Enabled;
        item.IsEnabled = allowed;
        item.ToolTip = folder.Enabled ? ProcessingWords.FolderCheckNowHint : ProcessingWords.FolderCheckNowStoppedReason;
    }

    /// <summary>「今すぐ変化を確かめる」の本体(メニューの処理。テストから呼べる)。そのフォルダだけを走査する。取り込みを止めているフォルダ・走査の最中は、何もしない。</summary>
    internal async Task ScanFolderNowAsync(FolderInfo folder)
    {
        if (_core is null || _scanning || !folder.Enabled) return;
        await ScanAsync([folder.Id]);
    }

    private FolderInfo? SelectedFolder() => (_folderList.SelectedItem as ListBoxItem)?.Tag as FolderInfo;

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (_core is not { } core) return;
        var dialog = new OpenFolderDialog { Title = NoticeWords.AddFolderDialogTitle, Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            FolderInfo added = await core.Folders.AddAsync(dialog.FolderName);
            _log.Info("フォルダを登録: " + added.Path);
        }
        catch (FolderRegistrationException ex)
        {
            MessageBox.Show(Window.GetWindow(this), UiText.Plain(ex), NoticeWords.AddFolderFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Gone) return;
        RefreshFolders();
        await ScanAsync();
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (_core is not { } core || SelectedFolder() is not { } folder) return;
        MessageBoxResult answer = MessageBox.Show(Window.GetWindow(this),
            NoticeWords.RemoveFolderConfirm(folder.Path),
            NoticeWords.RemoveFolderTitle, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        await RemoveFolderAsync(folder);
    }

    /// <summary>「外す」の本体(確認のあと。テストから呼べる)。外したあとは、フォルダの一覧と、待ちの件数の表示(<see cref="RefreshAfterFolderChange"/>)を取り直す。</summary>
    internal async Task RemoveFolderAsync(FolderInfo folder)
    {
        if (_core is not { } core) return;
        try
        {
            await core.Folders.RemoveAsync(folder.Id);
            _log.Info("フォルダを外した: " + folder.Path);
        }
        catch (Exception ex) when (ex is FolderRegistrationException or InvalidOperationException)
        {
            _notice(NoticeWords.RemoveFolderFailed(UiText.Plain(ex)));
        }
        RefreshAfterFolderChange();
    }

    /// <summary>「取り込みを止める / 再開する」の本体(行の右クリック。テストから呼べる)。切り替えたあとは、<see cref="RemoveFolderAsync"/> と同じに件数の表示も取り直す。</summary>
    internal async Task ToggleFolderAsync(FolderInfo folder)
    {
        if (_core is not { } core) return;
        try
        {
            await core.Folders.SetEnabledAsync(folder.Id, !folder.Enabled);
        }
        catch (Exception ex) when (ex is FolderRegistrationException or InvalidOperationException)
        {
            _notice(NoticeWords.ToggleImportFailed(folder.Enabled, UiText.Plain(ex)));
        }
        RefreshAfterFolderChange();
    }

    /// <summary>
    /// フォルダを外した・有効/無効を切り替えたあと: フォルダの一覧に加えて、待ちの件数の表示(VLM の残りなど)も取り直す。
    /// 件数は <c>Indexer.State</c> を読むたびに DB から数える値で、ジョブや走査の状態の変わり目の<b>イベント</b>でしか更新されない。外す・切り替えはどちらのイベントも起こさないので、
    /// ここで 1 回読まないと、別の操作で更新されるまで古い件数(外したフォルダの分を含む)が表示に残る。
    /// </summary>
    private void RefreshAfterFolderChange()
    {
        if (Gone) return;
        RefreshFolders();
        ApplyState(_core!.Indexer.State);
    }

    // ------------------------------------------------------------------ 走査・埋め込み

    /// <summary>
    /// 「元のファイルが見つからない」と分かった画像のパスから、それを含む登録済みのフォルダだけを裏で走査し直す(<c>missing_since</c> を付けて、検索の母集団から外す)。
    /// 走査の最中なら、終わったあとに 1 回だけやる。走査は 1030 枚で約 1 秒。フォルダの監視と定期的な走査があっても、監視の前に気づいたときの即時の手段として残す。
    /// </summary>
    public async Task RescanFoldersOfAsync(IReadOnlyList<string> missingPaths)
    {
        if (_core is not { } core) return;
        IReadOnlyList<long> ids = FolderMatcher.FolderIdsContaining(core.Folders.List(), missingPaths);
        if (ids.Count == 0) return;
        if (_scanning)
        {
            foreach (long id in ids) _pendingRescan.Add(id);
            return;
        }
        await ScanAsync(ids, ScanTrigger.Missing);
    }

    /// <summary>
    /// Core の中で起きた走査(監視・定期・復帰・再試行)が終わった(Indexer.ScanCompleted。どのスレッドからでも来る)。UI のスレッドに渡して、フォルダの一覧と状態を取り直し、
    /// <see cref="ScanFinished"/> で検索をやり直させる(消えた・動かした画像を結果から外す)。このパネルが頼んだ走査(起動・手動・見つからない)は、<see cref="ScanAsync"/> の続きで
    /// 同じ処理をするので、ここでは受けない。
    /// </summary>
    private void OnIndexerScanCompleted(ScanCompletedInfo info)
    {
        if (info.Trigger is ScanTrigger.Startup or ScanTrigger.Manual or ScanTrigger.Missing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Gone) return;
            try { HandleBackgroundScan(info); }
            catch (Exception ex) { _log.Error("自動の走査のあとの画面の更新に失敗", ex); }
        });
    }

    /// <summary>
    /// 自動の走査の完了を画面に反映する(UI のスレッド)。フォルダの一覧と状態は毎回取り直し、検索のやり直し(<see cref="ScanFinished"/>)と通知は、画像が増えた・変わった・消えたときだけ
    /// (自動の走査は何度も起きる。毎回やり直すと、見ている結果の並びが動き、状態の行も埋まる)。テストから呼べる。
    /// </summary>
    internal void HandleBackgroundScan(ScanCompletedInfo info)
    {
        if (Gone) return;
        RefreshFolders();
        ApplyState(_core!.Indexer.State);
        ScanSummary s = info.Summary;
        if (s.Added + s.Changed + s.Missing == 0) return;   // 何も変わらなかった自動の走査では、検索をやり直さない(15 分ごとに、見ている結果の並びが動かないように)
        ScanFinished?.Invoke(s);
        _notice(NoticeWords.AutoScanFinished(info.Trigger, s));
    }

    /// <summary>走査の結果の文章(「新規 n、変更 n、消えた n」+ 0 でないもの)。文は <see cref="NoticeWords.DescribeScan"/>。</summary>
    internal static string DescribeScan(ScanSummary s) => NoticeWords.DescribeScan(s);

    /// <summary>「監視できない」の一覧が変わった(どのスレッドからでも来る)。フォルダの一覧を取り直す。</summary>
    private void OnFolderWatchChanged() =>
        Dispatcher.BeginInvoke(() =>
        {
            if (Gone) return;
            try { RefreshFolders(); }
            catch (Exception ex) { _log.Error("監視の状態の変更のあとの画面の更新に失敗", ex); }
        });

    private async Task ScanAsync(IReadOnlyCollection<long>? folderIds = null, ScanTrigger trigger = ScanTrigger.Manual)
    {
        if (_core is not { } core || _scanning) return;
        _scanning = true;
        UpdateButtons();
        bool succeeded = false;
        try
        {
            ScanSummary s = await core.Indexer.ScanAsync(folderIds, trigger);
            if (Gone) return;
            succeeded = true;
            ScanFinished?.Invoke(s);
            _notice(NoticeWords.ScanFinished(s));
        }
        catch (Exception ex)
        {
            // 閉じる途中の失敗(Core の破棄に止められた・破棄済み)は、黙って終わる
            if (!Gone)
            {
                _log.Error("走査に失敗", ex);
                _notice(NoticeWords.ScanFailed(UiText.Plain(ex)));
            }
        }
        finally
        {
            _scanning = false;
            if (!Gone)
            {
                RefreshFolders();
                ApplyState(_core!.Indexer.State);   // 走査の終わりに 1 度だけ読む(イベントは、走査の終了の印を間引くことがある)
            }
        }
        // 走査の最中に頼まれたフォルダの走査し直しを、終わったあとに 1 回だけやる(成功したときだけ。連続で失敗して止まらなくなるのを避ける)
        if (succeeded && !Gone && _pendingRescan.Count > 0)
        {
            long[] pending = _pendingRescan.ToArray();
            _pendingRescan.Clear();
            await ScanAsync(pending, ScanTrigger.Missing);
        }
    }

    /// <summary>
    /// **ジョブの指定は、いつもこの 1 通り**(埋め込みを先に、そのあと VLM): <c>Indexer.StartAsync</c> に渡す指定が 1 通りなら、待機中に覚えている指定が、
    /// 画面から見えない状態で狭まることが無い(開始の指定が 2 通りだと起きる罠。ADR 0039)。VLM の途中で新しい画像が増えたら、埋め込みに戻って先に済ませる(ADR 0027)。
    /// <c>StartAsync</c> を呼ぶのは <see cref="StartProcessingAsync"/> の 1 か所だけ(StartAsyncSingleCallSiteTests が、ソースを読んで見張る)。
    /// </summary>
    internal static readonly IReadOnlyList<JobKind> AllJobs = [JobKind.Embed, JobKind.Vlm];

    /// <summary>
    /// 「処理を始める」の本体(ボタンの処理。テストから呼べる)。Task はジョブが終わる(待ちが無くなる・一時停止する・失敗する)まで続く。
    /// 失敗のやり直し・要再作成・飛ばした画像のもう一度も、戻したあと、ここから始める(**<c>StartAsync</c> を呼ぶ唯一の場所**)。
    /// </summary>
    internal Task StartProcessingAsync(string what = NoticeWords.StartWhat, Func<string, string>? failed = null)
    {
        if (_core is not { } core) return Task.CompletedTask;
        return RunJobAsync(() => core.Indexer.StartAsync(AllJobs), what, failed ?? NoticeWords.StartFailed);
    }

    /// <summary>テスト用: 指定した状態を「最後に表示した状態」にして表示する(本物のジョブを動かさずに、実行中のボタンの処理を試すため)。</summary>
    internal void ShowForTests(IndexState state, int? missing = null)
    {
        if (missing is not null) _missingCount = missing;
        ApplyState(state);
    }

    /// <summary>テスト・確認用: 表示中の進捗の文章。</summary>
    internal string StatusText => _statusFull;

    /// <summary>テスト・確認用: 「インデックス」欄の固定の行(7 行)。</summary>
    internal IndexRowsView IndexRowsForTests => _indexRows;

    /// <summary>テスト・確認用: 「インデックス」欄の全体(見出し〜まとめの行)。高さは、どの状態でも同じ。</summary>
    internal FrameworkElement IndexSectionForTests => _indexSection;

    /// <summary>
    /// 「失敗をやり直す」の本体(ボタンの処理。テストから呼べる)。失敗した画像を待ちに戻し(<c>Indexer.RetryFailedAsync</c>。stage_status の failed の行を消すだけ)、**そのまま始める**
    /// (<see cref="StartProcessingAsync"/>。始まるジョブは、失敗のある段階によらず、いつも <see cref="AllJobs"/>)。<paramref name="only"/> が null でなければ、そのジョブの失敗だけを戻す
    /// (処理状態の窓の「この n 枚をやり直す」は、いま見ている一覧の分だけ)。失敗の数は、押した時点の状態を 1 回だけ読んで決める。
    /// </summary>
    internal async Task RetryFailedAsync(JobKind? only = null)
    {
        if (_core is not { } core || _busy) return;   // 操作の最中(二度押しの 2 回目)は何もしない
        RetryPlan plan = IndexStatusText.PlanRetry(core.Indexer.State);
        IReadOnlyList<JobKind> kinds = [.. plan.RetryKinds.Where(k => only is null || k == only)];
        if (kinds.Count == 0)
        {
            _notice(NoticeWords.NoFailures);
            return;
        }
        SetBusy(true);
        try
        {
            try
            {
                foreach (JobKind kind in kinds) await core.Indexer.RetryFailedAsync(kind);
            }
            catch (Exception ex)
            {
                if (!Gone)
                {
                    _log.Error("失敗した画像を待ちに戻せなかった", ex);
                    _notice(NoticeWords.RetryResetFailed(UiText.Plain(ex)));
                }
                return;
            }
            if (Gone) return;
            int embed = kinds.Contains(JobKind.Embed) ? plan.EmbedFailed : 0;
            int vlm = kinds.Contains(JobKind.Vlm) ? plan.VlmFailed : 0;
            _notice(NoticeWords.RetryRestarted(embed, vlm));
            await StartProcessingAsync("失敗をやり直す", NoticeWords.RetryFailedStart);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 「もう一度試す」(飛ばした画像)の本体。飛ばした画像は記録していない(次に始めたとき、もう一度試す)ので、**始める**だけ(Core の <c>StartAsync</c> が、飛ばした集合を空にする。
    /// 自動の再開は空にしない)。開始が 1 つになったので、待機中に「始める」を押して飛ばした集合を空にする道の代わり(ADR 0039)。
    /// </summary>
    internal async Task RetrySkippedAsync()
    {
        if (_core is null || _busy) return;
        SetBusy(true);
        try
        {
            await StartProcessingAsync("飛ばした画像をもう一度試す", NoticeWords.RetrySkippedFailed);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>操作の最中の印を替えて、ボタンの押せる・押せないに反映する(<see cref="_busy"/>)。</summary>
    private void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        if (!Gone) UpdateButtons();
    }

    /// <summary>テスト・確認用: フォルダの一覧の各行の文字(パス・枚数・「監視できない」など)。</summary>
    internal IReadOnlyList<string> FolderTexts => _folderList.Items.OfType<ListBoxItem>().Select(i => ((TextBlock)i.Content).Text).ToList();

    /// <summary>テスト・確認用: フォルダの一覧の各行のツールチップ。</summary>
    internal IReadOnlyList<string> FolderTooltips => _folderList.Items.OfType<ListBoxItem>().Select(i => (string)i.ToolTip).ToList();

    /// <summary>テスト・確認用: 開始のボタンのツールチップ。</summary>
    internal string StartToolTip => (string)_runButton.ToolTip;

    // ------------------------------------------------------------------ 処理状態(失敗の一覧・要再作成・見つからない画像の記録を消す)

    /// <summary>
    /// 失敗の一覧(または飛ばした画像の一覧)の画面を出す。中身は Core から読む(<c>Images.ListFailuresAsync</c>、<c>Indexer.ListSkippedAsync</c>)。
    /// 「詳細」は、画面を閉じて、<see cref="OpenDetailRequested"/> で、メインの画面に詳細画面を開かせる。
    /// **画面は 1 つだけ**: すでに開いていれば、新しく作らずに、それを前に出して(<see cref="ActivateFailureWindow"/>)指定の種類に切り替える(<see cref="FailureListWindow.SwitchTo"/>)。
    /// 閉じたあとは、また新しく作る。
    /// </summary>
    internal FailureListWindow? OpenFailureList(ProblemKind? requested = null)
    {
        if (_core is not { } core) return null;
        ProcessingSnapshot snap = CurrentSnapshot;
        ProblemKind kind = requested ?? snap.FirstKind();   // 指定が無ければ、数が 1 以上の最初の種類(全部 0 なら 1 つ目)
        if (_failureWindow is { } open)
        {
            open.SwitchTo(kind);
            ActivateFailureWindow(open);
            return open;
        }
        var window = new FailureListWindow(kind, async (k, ct) =>
        {
            TimeZoneInfo zone = TimeZoneInfo.Local;
            return k switch
            {
                ProblemKind.EmbedFailures => ProcessingText.FailureRows(JobKind.Embed, await core.Images.ListFailuresAsync(Stage.Embed, ct: ct), zone),
                ProblemKind.VlmFailures => ProcessingText.FailureRows(JobKind.Vlm, await core.Images.ListFailuresAsync(Stage.Caption, ct: ct), zone),
                ProblemKind.Skipped => ProcessingText.SkippedRows(await core.Indexer.ListSkippedAsync(ct)),
                ProblemKind.MissingImages => ProcessingText.MissingRows(await core.Indexer.ListMissingImagesAsync(ct: ct), zone),
                ProblemKind.Outdated => ProcessingText.OutdatedList(_shownState is { } st ? ProcessingText.PlanRequeue(st) : new RequeuePlan(0, 0)),
                _ => throw new ArgumentOutOfRangeException(nameof(k), k, "知らない種類"),
            };
        }, _shell, snap, PerformProcessingActionAsync);
        window.OpenDetailRequested += hash => OpenDetailRequested?.Invoke(hash);
        window.Closed += (_, _) => { if (ReferenceEquals(_failureWindow, window)) _failureWindow = null; };
        _failureWindow = window;
        ShowFailureWindow(window);
        return window;
    }

    /// <summary>
    /// 「再作成を始める」の本体(ボタンの処理。テストから呼べる)。件数と見込みの時間を出して確認し(<see cref="ConfirmFunc"/>)、OK なら、要再作成の画像を待ちに戻して
    /// (<c>Indexer.RequeueOutdatedAsync</c>。古い結果は残る)、そのまま始める(<see cref="StartProcessingAsync"/>。始まるジョブは、いつも <see cref="AllJobs"/>)。件数は押した時点の状態を 1 回読んで決める。
    /// </summary>
    internal async Task RequeueAsync(JobKind job)
    {
        if (_core is not { } core || _shownState is not { } state || _busy) return;
        int count = ProcessingText.PlanRequeue(state).For(job);
        if (count == 0)
        {
            _notice(NoticeWords.NoOutdated);
            return;
        }
        if (!ConfirmFunc(ProcessingText.RequeueConfirmTitle(job), ProcessingText.RequeueConfirmText(job, count))) return;
        SetBusy(true);
        try
        {
            try
            {
                await core.Indexer.RequeueOutdatedAsync(job);
            }
            catch (Exception ex)
            {
                if (!Gone)
                {
                    _log.Error("要再作成の画像を待ちに戻せなかった", ex);
                    _notice(NoticeWords.RequeueResetFailed(UiText.Plain(ex)));
                }
                return;
            }
            if (Gone) return;
            _notice(NoticeWords.RequeueReset(job, count));
            await StartProcessingAsync(NoticeWords.RequeueWhat(job), reason => NoticeWords.RequeueStartFailed(job, reason));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 「見つからない画像の記録を消す」の本体(ボタンの処理。テストから呼べる)。消せる枚数を数え直して確認し(<see cref="ConfirmFunc"/>)、OK なら消す(<c>Indexer.PurgeMissingAsync</c>)。
    /// ジョブが動いているときは Core が拒む(理由を通知に出す)。
    /// </summary>
    internal async Task PurgeMissingAsync()
    {
        if (_core is null || _busy) return;
        SetBusy(true);   // 数える・確認・消すの間は、どの操作も押せない
        try
        {
            await PurgeMissingCoreAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PurgeMissingCoreAsync()
    {
        if (_core is not { } core) return;
        int count;
        int withManualTags;
        try
        {
            // 件数と、手動タグのある枚数(確認に添える)。一覧の中身は要らないので limit: 1
            var list = await core.Indexer.ListMissingImagesAsync(limit: 1);
            count = list.Total;
            withManualTags = list.WithManualTags;
        }
        catch (Exception ex)
        {
            if (!Gone)
            {
                _log.Error("フォルダに無い画像を数えられなかった", ex);
                _notice(NoticeWords.MissingCountFailed(UiText.Plain(ex)));
            }
            return;
        }
        if (Gone) return;
        _missingCount = count;
        UpdateButtons();
        if (count == 0)
        {
            _notice(NoticeWords.NoMissing);
            return;
        }
        if (!ConfirmFunc(ProcessingText.PurgeConfirmTitle, ProcessingText.PurgeConfirmText(count, withManualTags))) return;
        try
        {
            int removed = await core.Indexer.PurgeMissingAsync();
            if (!Gone) _notice(NoticeWords.Purged(removed));
        }
        catch (InvalidOperationException ex)
        {
            if (!Gone) _notice(NoticeWords.PurgeFailed(UiText.Plain(ex)));   // ジョブの実行中、など
        }
        catch (Exception ex)
        {
            if (!Gone)
            {
                _log.Error("フォルダに無い画像の記録を消せなかった", ex);
                _notice(NoticeWords.PurgeFailed(UiText.Plain(ex)));
            }
        }
        if (Gone) return;
        RefreshFolders();                          // フォルダの一覧の枚数・消せる枚数を取り直す
        ApplyState(_core!.Indexer.State);          // ベクトルの件数・待ちの件数の表示(StateApplied で、ステータスバーも)
    }

    /// <summary>消せる画像の数を数え直して、「見つからない画像の記録を消す」ボタンに反映する(走査・ジョブ・フォルダの操作のあと。DB の読み出しは裏のスレッドで)。</summary>
    internal async Task RefreshMissingCountAsync()
    {
        if (_core is not { } core) return;
        int count;
        try
        {
            count = await core.Indexer.CountMissingImagesAsync();
        }
        catch (ObjectDisposedException)
        {
            return;   // 閉じる途中
        }
        catch (Exception ex)
        {
            if (!Gone) _log.Error("見つからない画像の数を数えられなかった", ex);
            return;
        }
        if (Gone) return;
        _missingCount = count;
        UpdateButtons();
    }

    private async void OnRunButton(object sender, RoutedEventArgs e) => await ToggleRunAsync();

    /// <summary>
    /// 開始 / 一時停止 / 再開の 1 つのボタンの本体(ボタンの処理。テストから呼べる)。最後に表示した状態で決める(DB を読まない):
    /// 止まっている・失敗 → 処理を始める、実行中・待機中 → 一時停止、一時停止中 → 再開。**押したら、次の状態が届くまで押せなくする**(<see cref="_toggleFrom"/>。二度押しの 2 回目は何もしない)。
    /// 押したことは診断ログに残す(開始・再開は <see cref="RunJobAsync"/> が「ジョブ: …」で出す。一時停止も同じ形)。
    /// </summary>
    internal async Task ToggleRunAsync()
    {
        if (_core is not { } core || _toggleFrom is not null || _busy) return;
        IndexRunState run = Run;
        if (run is IndexRunState.Pausing) return;   // 一時停止の途中は押せない
        _toggleFrom = run;
        UpdateButtons();
        try
        {
            if (run is IndexRunState.Running or IndexRunState.Waiting)
            {
                try
                {
                    _log.Info("ジョブ: 一時停止");
                    await core.Indexer.PauseAsync();
                }
                catch (Exception ex)
                {
                    if (!Gone)
                    {
                        _log.Error("一時停止に失敗", ex);
                        _notice(NoticeWords.PauseFailed(UiText.Plain(ex)));
                    }
                }
            }
            else if (run == IndexRunState.Paused)
            {
                await RunJobAsync(() => core.Indexer.ResumeAsync(), "再開", NoticeWords.ResumeFailed);
            }
            else
            {
                await StartProcessingAsync();
            }
        }
        finally
        {
            // 状態が替わらないまま終わった(開始・一時停止・再開が失敗した)ときも、押せない印を残さない
            _toggleFrom = null;
            if (!Gone) UpdateButtons();
        }
    }

    /// <summary>ジョブの開始・再開。Task はジョブが終わる(待ちが無くなる・一時停止する)まで続く。<paramref name="what"/> はログの操作名、<paramref name="failed"/> は、失敗の通知の文を作る関数(理由を渡す。文は <see cref="NoticeWords"/>)。</summary>
    private async Task RunJobAsync(Func<Task> start, string what, Func<string, string> failed)
    {
        try
        {
            _log.Info("ジョブ: " + what);
            Task started = start();   // StartAsync は、状態を Running にしてから Task を返す。ここまでが「操作の最中」
            SetBusy(false);
            await started;
        }
        catch (Exception ex)
        {
            if (!Gone)
            {
                _log.Error("ジョブに失敗: " + what, ex);
                _notice(failed(UiText.Plain(ex)));
            }
        }
        finally
        {
            // 閉じる途中・Core を手放したあとは、DB を読まない(破棄が DB を閉じたあとに、この続きが UI スレッドで動くため)
            if (!Gone)
            {
                RefreshFolders();
                ApplyState(_core!.Indexer.State);   // ジョブの終わりに 1 度だけ読む
            }
        }
    }
}
