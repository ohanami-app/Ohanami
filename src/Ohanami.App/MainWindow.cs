using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// ウィンドウ(詳細画面は <see cref="ResultsPanel"/> が一覧の上に重ねる)。XAML も Binding も使わず、すべてコードで組む(AGENTS.md「すべて明示的に」)。
/// 左(<see cref="IndexPanel"/>: フォルダ・インデックス)、右(<see cref="ResultsPanel"/>: 検索・一覧)、下(<see cref="StatusBarView"/>: 状態)の 3 つを並べ、
/// Core の開閉と、3 つのつなぎだけを持つ。
/// <list type="bullet">
/// <item>開く: ウィンドウが開いたら <c>OhanamiCore.OpenAsync</c>。失敗したら理由を出して、何も使えない状態で止まる。</item>
/// <item>閉じる: Core の破棄(実行中のジョブ・走査・サムネイルの仕事を止めてから DB を閉じる)を待ってから閉じる。待つあいだに × がもう一度押されても、閉じない。</item>
/// </list>
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly ICoreLog _log;
    private readonly IndexPanel _index;
    private readonly ResultsPanel _results;
    private readonly StatusBarView _status = new();

    private OhanamiCore? _core;
    private bool _closeRequested;      // 閉じる操作があった(Core を開いている最中かもしれない)
    private bool _closing;             // Core の破棄を待っている
    private bool _closeAllowed;        // 破棄が済んだ。次の Closing は通す

    public MainWindow(AppPaths paths, ICoreLog log)
    {
        _paths = paths;
        _log = log;
        Title = "Ohanami";
        Width = 1180;
        Height = 760;
        MinWidth = WindowLayout.MinWindowWidth;
        MinHeight = WindowLayout.MinWindowHeight;
        UseLayoutRounding = true;
        AppTheme.StyleWindow(this);   // 窓の背景とタイトルバーの色を自分で決める(Mica は切ってある)
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _index = new IndexPanel(log, SetNotice, () => _closing || _closeRequested);
        _results = new ResultsPanel(log, SetNotice);
        _index.StateApplied += _ =>
        {
            if (_core is not null) _status.ShowCoreStatus(_core.Status);   // ベクトルの件数は埋め込みで増える(Core の状態の通知は件数の変化では来ない)
        };

        // 移動・削除で元のファイルが無くなった画像: 見つかった時点で、そのフォルダだけを裏で走査し直し(missing_since が付く)、終わったら今の検索をやり直して結果から外す。
        // (監視・定期の走査より先に気づいたときの即時の手段。赤い帯の知らせは、そのまま残る)
        _results.MissingFilesDetected += async paths =>
        {
            try { await _index.RescanFoldersOfAsync(paths); }
            catch (Exception ex) { if (!_closing) _log.Error("元のファイルが見つからなかったフォルダの走査し直しに失敗", ex); }
        };
        // 処理状態の失敗の一覧の「詳細」: 結果の一覧の上に、その画像の詳細画面を開く(検索の結果の外の画像でも開ける)
        _index.OpenDetailRequested += hash =>
        {
            try { _ = _results.OpenDetailByHashAsync(hash); }
            catch (Exception ex) { if (!_closing) _log.Error("失敗の一覧からの詳細画面を開けなかった", ex); }
        };
        _index.ScanFinished += async _ =>
        {
            try { await _results.RefreshSearch(); }
            catch (Exception ex) { if (!_closing) _log.Error("走査のあとの検索のやり直しに失敗", ex); }
        };

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowLayout.LeftColumnWidth) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_index, 0);
        Grid.SetColumn(_results, 1);
        main.Children.Add(_index);
        main.Children.Add(_results);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(main, 0);
        Grid.SetRow(_status, 1);
        root.Children.Add(main);
        root.Children.Add(_status);
        Content = root;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        PreviewKeyDown += (_, e) => { if (_results.HandlePreviewKeyDown(e)) e.Handled = true; };
        PreviewMouseDown += (_, e) => { if (_results.HandleMouseButton(e.ChangedButton)) e.Handled = true; };   // 詳細画面で、マウスの戻るボタン = 戻る(Esc)
    }

    private void SetNotice(string text)
    {
        _status.ShowNotice(text);
        _log.Info("画面: " + text);
    }

    private void ShowCoreStatus(CoreStatus st)
    {
        _status.ShowCoreStatus(st);
        _results.SetSearchEnabled(st.Phase != CorePhase.Failed && _core is not null);
    }

    // ------------------------------------------------------------------ 開く

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _results.SetScale(VisualTreeHelper.GetDpi(this));

        OhanamiCore core;
        try
        {
            core = await OhanamiCore.OpenAsync(new CoreOptions(_paths.DatabasePath, _paths.ThumbnailDirectory, _paths.ModelDirectory, AppContext.BaseDirectory, _log, _paths.ToVlmOptions()));
        }
        catch (Exception ex)
        {
            // DB が開けない・知らないスキーマの版・DLL の自己検査の失敗など。何も使えないので、理由を出して止まる
            _log.Error("Core を開けなかった", ex);
            _status.ShowCoreStatus(new CoreStatus(CorePhase.Failed, ex.Message, false, 0));
            SetNotice("起動できませんでした。詳しくはログをご覧ください: " + _paths.LogDirectory);
            return;
        }

        if (_closeRequested)
        {
            // 開いている間に閉じられた(ウィンドウはもう閉じている)。開き終えた Core だけ破棄する
            await core.DisposeAsync();
            return;
        }

        _core = core;
        core.StatusChanged += st => Dispatcher.BeginInvoke(() => { if (_core is not null) ShowCoreStatus(st); });
        _results.Attach(core);
        _index.Attach(core);
        ShowCoreStatus(core.Status);
        _ = _results.ShowRecentAsync();   // 検索していないときの一覧(新しい順)。走査が終わったら、RefreshSearch が作り直す

        await _index.ScanAtStartupAsync();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _results.HandleDpiChanged(oldDpi, newDpi);
    }

    /// <summary>外から「前に出して」と言われたとき(2 つ目の起動)。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;    // 前面に出す常套手段(Activate だけだと、タスクバーの点滅で終わることがある)
        Topmost = false;
        Focus();
    }

    // ------------------------------------------------------------------ 閉じる

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closeRequested = true;
        if (_closeAllowed) return;            // 破棄が済んだ
        if (_closing) { e.Cancel = true; return; }   // 破棄の最中にもう一度 × が押された: 閉じない(DB を閉じる前にウィンドウだけ閉じない)
        if (_core is not { } core) return;    // Core がまだ無い(開いている最中、または開けなかった): そのまま閉じる

        // Core の破棄(ジョブ・走査・サムネイルの仕事を止めてから DB を閉じる)を待つあいだ、ウィンドウは閉じない
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        _results.Detach();
        _index.Detach();
        _core = null;
        try
        {
            await core.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Core の破棄に失敗", ex);
        }
        _closeAllowed = true;
        // Close をここで直接呼ばない: 破棄がすぐ終わると、この続きは Closing イベントの中(Close の途中)で動き、WPF が InvalidOperationException にする。
        // Dispatcher に載せ直して、今の Close が戻ってから閉じる
        _ = Dispatcher.BeginInvoke(Close);
    }
}
