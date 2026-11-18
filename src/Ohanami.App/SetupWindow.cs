using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// モデルのセットアップの画面。起動の最初(Core を開く前)にモデルのファイルを見て、特徴ベクトル検索のモデルがそろっていなければ、この画面を出す。
/// モデルごとに、要るファイルの一覧(名前・大きさ・状態)・置き場・入手先を見せる。ファイルは、手で置く(「フォルダを開く」と「確かめ直す」)か、「ダウンロード」で取る。
/// <list type="bullet">
/// <item><b>ダウンロード</b>: 押す前に、何を・どこから・どこへ・空きがどれだけかを、1 行で言う(<see cref="IModelDownloads.Plan"/>。ネットワークには出ない)。押したときだけ取る。
///   ダウンロードの領域は 3 つある: 区画ごとの領域(特徴ベクトル検索のモデル・テキスト検索のモデル)と、導入の文の下の一括の領域(「2 つともダウンロード」)。
///   一括の領域は、どちらかの区画がそろっていない間は、ずっと出ている(押すと、まだそろっていない組だけを取る。片方がそろっていれば「残りをダウンロード」)。
///   領域の鍵は、一括の領域が <see cref="BothSets"/>、区画の領域がその区画の組。押した領域を覚えて(<see cref="_activeArea"/>)、進み具合・「中止」・失敗・中止の文は、押した領域に出す。
///   一括の領域で残り 1 組を取るときも、区画の領域とぶつからない(区画の側は「ダウンロードしている間は、押せません」になり、進み具合は出ない)。
///   一括は、取る組を 1 回の呼び出しで渡す(Core が、特徴ベクトル検索のモデル → テキスト検索のモデルの順に取り、空きをドライブごとの合計で先に見て、
///   スリープの抑止・中止・失敗を 1 つにまとめ、進み具合を全ファイルの通しで出す)。区画ごとの取得を 2 回呼ぶ形にすると、
///   1 つ目の中止・失敗・「終了」のあとに、2 つ目が押していないのに始まるので採らない。
///   取っている間は、ほかのダウンロードのボタンも「確かめ直す」も「このまま始める」も押せない。
///   中止しても途中までのファイルは残り(ファイルごとの .part なので、どの領域からでも次は続きから取る)、失敗は、種類ごとの平らな文と、手で置く方法の案内。終わったら自動で確かめ直す。
///   取っている間は、ファイルを取り終えて置くたびに、その行の状態を「あり」に替え、区画の見出しの数を減らす(いま取っている行は「取得中」「確かめ中」)。
///   取っていない領域は、その組がそろったら、中身を消す(押せない「ダウンロード」を残さない。高さは残す)。
///   どの領域も、高さは、場面(押す前・取っている最中・失敗・中止・空きが足りない)によらず一定(区画の領域は <see cref="DownloadAreaHeight"/>、一括の領域は <see cref="BothAreaHeight"/>)。窓の高さが、取っている間に伸び縮みしない。
///   入りきらない文は、末尾を「…」で切って、全文を行のヒントで見せる。</item>
/// <item>窓が前に出たとき(エクスプローラーから戻ったとき)も、自動で確かめ直す(取っている間は確かめない)。そろったら、結果(<see cref="Choice"/>)を <see cref="SetupChoice.Ready"/> にして閉じる。</item>
/// <item>テキスト検索のモデルだけが無いときは、「このまま始める」で、<see cref="SetupChoice.WithoutVlm"/> を返して閉じる。窓を閉じる(✕・終了)と、アプリを終了する(<see cref="SetupChoice.Exit"/>)。
///   取っている間に閉じようとすると、確かめて(<see cref="ConfirmAbort"/>)、中止してから閉じる。</item>
/// </list>
/// 調べる口(<c>inspect</c>)・エクスプローラーを開く口(<see cref="IShellActions"/>)・ダウンロードの口(<see cref="IModelDownloads"/>)は、引数で受ける(テストは偽物を渡す)。
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly Func<SetupState> _inspect;
    private readonly IShellActions _shell;
    private readonly IModelDownloads _downloads;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = AppColors.Secondary };
    private SetupState _state;

    // ---- ダウンロードの状態(UI スレッドだけが触る)
    /// <summary>「2 つともダウンロード」で取る組。この組は、その領域の鍵にもなる。</summary>
    internal const ModelSets BothSets = ModelSets.FeatureVector | ModelSets.Vlm;

    private ModelSets? _active;                         // いま取っている組(取っていなければ null)
    private ModelSets? _activeArea;                     // 押した領域の鍵(一括の領域なら BothSets。取っていなければ null)
    private CancellationTokenSource? _cts;
    private bool _pendingClose;                         // 取っている最中に閉じると言われた(中止が済んだら閉じる)
    private (ModelSets Area, ModelDownloadException Error)? _failure;   // 直近の失敗(押した領域に出す)
    private ModelSets? _cancelledArea;                  // 直近に中止したときの、押した領域
    private ModelDownloadProgress? _lastProgress;
    private TransferRate _rate = new();
    private TextBlock? _progressLine, _amountLine;
    private ProgressBar? _bar;
    private Button? _cancelButton;
    // 今回の取得で、取り終えて置き場に置いたファイル(いま取っている組の)。名前(モデルのフォルダからの相対パス)だけで持つ。
    // 2 つのカタログに同じ相対パスは無い(ModelDownloaderTests が見張る)ので、「2 つとも」でも、どちらの組のファイルかを取り違えない
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<(ModelSets Set, string File), TextBlock> _fileStateCells = [];
    private readonly Dictionary<ModelSets, TextBlock> _setStateCells = [];
    private readonly Dictionary<ModelSets, (StackPanel Text, Grid Buttons)> _areaParts = [];   // ダウンロードの領域の、文の段とボタンの行(鍵 = 領域の組)

    /// <summary>窓を閉じたあとの結果。既定は終了(✕ で閉じた場合も)。</summary>
    public SetupChoice Choice { get; private set; } = SetupChoice.Exit;

    /// <summary>窓を閉じる処理(既定は <see cref="Window.Close"/>。テストで差し替える=画面を実際に表示せずに確かめる)。</summary>
    internal Action CloseWindow { get; set; }

    /// <summary>取っている最中に閉じようとしたとき、「中止して終了するか」を利用者に聞く。既定はメッセージボックス。テストは偽物に替える。</summary>
    internal Func<bool> ConfirmAbort { get; set; }

    /// <summary>いまの時刻(取る速さを測る。テストは固定の時刻を返す)。</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <param name="inspect">置き場を調べる(<see cref="SetupState.Inspect"/>)。</param>
    /// <param name="shell">エクスプローラーを開く口。</param>
    /// <param name="downloads">ダウンロードの口。</param>
    /// <param name="initial">最初に見せる状態。</param>
    public SetupWindow(Func<SetupState> inspect, IShellActions shell, IModelDownloads downloads, SetupState initial)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(initial);
        _inspect = inspect;
        _shell = shell;
        _downloads = downloads;
        _state = initial;
        CloseWindow = Close;
        ConfirmAbort = AskAbort;
        Title = SetupWords.WindowTitle;
        Width = 820;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        AppTheme.StyleWindow(this);
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(_body);
        // 窓の高さは中身で決まる(SizeToContent)。画面の作業領域より高いときは、窓を作業領域に収め、中身をスクロールで見せる(下のボタンまで届く)
        MaxHeight = SystemParameters.WorkArea.Height;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Rebuild();
        Activated += (_, _) => Recheck(manual: false);
    }

    /// <summary>前に出す(2 つ目の起動のとき)。最小化していれば戻す。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;    // 前面に出す常套手段(Activate だけだと、タスクバーの点滅で終わることがある)
        Topmost = false;
        Focus();
    }

    /// <summary>いまの状態(テスト用)。</summary>
    internal SetupState State => _state;

    /// <summary>確かめ直した 1 行(テスト用。出ていなければ空)。</summary>
    internal string StatusText => _status.Text;

    /// <summary>取っている最中か(テスト用)。</summary>
    internal bool IsDownloading => _active is not null;

    /// <summary>この組を、いま取っているか(「2 つとも」を取っているときは、どちらの組も取っている)。</summary>
    private bool IsTaking(ModelSets sets) => _active is { } active && active.HasFlag(sets);

    /// <summary>窓の中の文字を、上から順に集める(テスト用)。</summary>
    internal string[] TextsForTests() => Walk(_body).ToArray();

    /// <summary>ボタンの文字(テスト用)。</summary>
    internal string[] ButtonTextsForTests() => FindAll<Button>(_body).Select(b => (string)b.Content).ToArray();

    /// <summary>押せるボタンの文字(テスト用)。</summary>
    internal string[] EnabledButtonTextsForTests() => FindAll<Button>(_body).Where(b => b.IsEnabled).Select(b => (string)b.Content).ToArray();

    /// <summary>ファイルの行の状態の文字(テスト用。表の上から順)。</summary>
    internal string[] FileStatesForTests(ModelSets sets) =>
        [.. (sets == ModelSets.FeatureVector ? _state.Embedding : _state.Vlm).Files.Select(f => _fileStateCells[(sets, f.RelativePath)].Text)];

    /// <summary>区画の見出しの右の状態の文字(テスト用)。</summary>
    internal string SetStateForTests(ModelSets sets) => _setStateCells[sets].Text;

    /// <summary>窓の中の文字のヒント(ツールチップが文字のもの)を、上から順に集める(テスト用。切れた文の全文)。</summary>
    internal string[] HintsForTests() => [.. FindAll<TextBlock>(_body).Select(t => t.ToolTip).OfType<string>()];

    /// <summary>ダウンロードの領域(鍵 = 領域が扱う組)があるか(テスト用)。</summary>
    internal bool HasAreaForTests(ModelSets key) => _areaParts.ContainsKey(key);

    /// <summary>ダウンロードの領域の、文の段の文字(テスト用。領域が無い・中身を消したあとは空)。</summary>
    internal string[] AreaTextsForTests(ModelSets key) => _areaParts.TryGetValue(key, out var part) ? Walk(part.Text).ToArray() : [];

    /// <summary>ダウンロードの領域の、ボタンの行のボタンの文字(テスト用)。</summary>
    internal string[] AreaButtonsForTests(ModelSets key) => _areaParts.TryGetValue(key, out var part) ? FindAll<Button>(part.Buttons).Select(b => (string)b.Content).ToArray() : [];

    /// <summary>ダウンロードの領域の、ボタンの行のボタンが押せるか(テスト用。ボタンが無ければ空)。</summary>
    internal bool[] AreaButtonsEnabledForTests(ModelSets key) => _areaParts.TryGetValue(key, out var part) ? FindAll<Button>(part.Buttons).Select(b => b.IsEnabled).ToArray() : [];

    /// <summary>進みのバーの割合(テスト用。バーが出ていなければ null)。</summary>
    internal double? ProgressFraction => _bar?.Value;

    /// <summary>
    /// 置き場を調べ直す。<paramref name="manual"/> は「確かめ直す」を押した場合(まだそろっていなければ、1 行で知らせる)。
    /// そろっていれば、結果を決めて閉じる。取っている間は、何もしない(取りかけのファイルを、そろっていないと見てしまうため)。
    /// </summary>
    internal void Recheck(bool manual)
    {
        if (_active is not null) return;
        SetupState state = _inspect();
        _state = state;
        if (state.Kind == SetupKind.Ready)
        {
            Choice = SetupChoice.Ready;
            CloseWindow();
            return;
        }
        Rebuild();
        if (manual) _status.Text = SetupWords.StillIncomplete(state.ProblemFileCount);
    }

    private void ContinueWithoutVlm()
    {
        Choice = SetupChoice.WithoutVlm;
        CloseWindow();
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _shell.OpenFolder(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            _status.Text = SetupWords.OpenFolderFailed(UiText.Plain(e));
        }
    }

    // ---------------------------------------------------------------- ダウンロード

    /// <summary>ボタンのハンドラ(押したときだけ取る)。例外のうち、知っているもの(失敗・中止)は <see cref="StartDownloadAsync"/> が画面に出す。ほかは、握りつぶさず、そのまま落とす。</summary>
    private async void OnDownloadClicked(ModelSets sets, ModelSets area) => await StartDownloadAsync(sets, area);

    /// <summary>
    /// <paramref name="sets"/> を取る(<see cref="BothSets"/> なら、2 つとも。特徴ベクトル検索のモデルが先)。取っている最中は何もしない。
    /// <paramref name="area"/> は押した領域の鍵(省略すると、<paramref name="sets"/> の領域。一括の領域は <see cref="BothSets"/>)。進み具合・失敗・中止の文は、この領域に出す。
    /// 終わったら自動で確かめ直す(そろえば、閉じて先へ進む)。中止は途中までのファイルを残す。
    /// </summary>
    internal async Task StartDownloadAsync(ModelSets sets, ModelSets? area = null)
    {
        if (_active is not null) return;
        _failure = null;
        _cancelledArea = null;
        _active = sets;
        _activeArea = area ?? sets;
        _lastProgress = null;
        _completed.Clear();
        _rate = new TransferRate();
        _cts = new CancellationTokenSource();
        Rebuild();
        try
        {
            await _downloads.RunAsync(_state.Embedding.Directory, _state.Vlm.Directory, sets, new DirectProgress(OnProgress), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _cancelledArea = _activeArea;
        }
        catch (ModelDownloadException e)
        {
            _failure = (_activeArea!.Value, e);
        }
        finally
        {
            _active = null;
            _activeArea = null;
            _completed.Clear();
            _cts?.Dispose();
            _cts = null;
            ClearProgressElements();
        }

        if (_pendingClose)
        {
            _pendingClose = false;
            Choice = SetupChoice.Exit;
            CloseWindow();
            return;
        }
        // 置き場を読み直す(そろえば閉じる。SigLIP2 だけそろったときは、画面が「テキスト検索のモデル」の状態に替わる。
        // 失敗・中止のときも、途中までに置いたファイルが「あり」で出るように、読み直す)
        Recheck(manual: false);
    }

    /// <summary>進み具合を受ける(取る側のスレッドから呼ばれうる。画面のスレッドに移して反映する)。</summary>
    private sealed class DirectProgress(Action<ModelDownloadProgress> onReport) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => onReport(value);
    }

    private void OnProgress(ModelDownloadProgress p)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)(() => OnProgress(p)));
            return;
        }
        if (_active is null) return;   // 取り終わったあとに届いた分
        if (_lastProgress is null || _lastProgress.Phase != p.Phase) _rate.Reset();
        bool fileOrPhaseChanged = _lastProgress is null || _lastProgress.Phase != p.Phase || _lastProgress.FileName != p.FileName;
        _lastProgress = p;
        if (p.Phase == ModelDownloadPhase.Completed) _completed.Add(p.FileName);
        if (p.Phase == ModelDownloadPhase.Downloading) _rate.Add(Now(), p.BytesDone);
        if (fileOrPhaseChanged) RefreshStates();
        ShowProgress();
    }

    private void ShowProgress()
    {
        if (_lastProgress is not { } p) return;
        if (_progressLine is not null) _progressLine.Text = SetupWords.ProgressLine(p);
        if (_amountLine is not null) _amountLine.Text = SetupWords.AmountLine(p, _rate.BytesPerSecond);
        if (_bar is not null) _bar.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
    }

    private void ClearProgressElements()
    {
        _progressLine = null;
        _amountLine = null;
        _bar = null;
        _cancelButton = null;
    }

    private void CancelDownload()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        if (_cancelButton is not null) { _cancelButton.IsEnabled = false; _cancelButton.Content = SetupWords.Cancelling; }
    }

    /// <summary>閉じてよいか。取っている最中なら、確かめて(<see cref="ConfirmAbort"/>)、中止だけ始める(閉じるのは、中止が済んでから)。</summary>
    private bool AllowClose()
    {
        if (_active is null) return true;
        if (_pendingClose) return false;
        if (!ConfirmAbort()) return false;
        // 確かめの窓を出している間に、取り終わっていることがある(窓の裏でも取るのは進む)。中止するものが無いので、そのまま閉じる。
        // 旗を立てると、閉じる処理は通り過ぎているので窓が閉じないまま旗が残り、次に取り終えたときに閉じてしまう
        if (_active is null) return true;
        _pendingClose = true;
        CancelDownload();
        return false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>「終了」のボタン。取っている最中は確かめる(中止が済んでから閉じる)。</summary>
    internal void RequestExit()
    {
        Choice = SetupChoice.Exit;
        if (AllowClose()) CloseWindow();
    }

    private bool AskAbort() =>
        MessageBox.Show(this, SetupWords.CloseConfirmText, SetupWords.CloseConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ---------------------------------------------------------------- 組み立て

    private void Rebuild()
    {
        _status.Text = "";
        ClearProgressElements();
        _fileStateCells.Clear();
        _setStateCells.Clear();
        _areaParts.Clear();
        _body.Children.Clear();
        bool vlmOnly = _state.Kind == SetupKind.VlmIncomplete;

        _body.Children.Add(new TextBlock
        {
            Text = vlmOnly ? SetupWords.HeadingVlmMissing : SetupWords.HeadingMissing,
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(new TextBlock
        {
            Text = vlmOnly ? SetupWords.LeadVlmMissing : SetupWords.LeadMissing,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });

        // 一括の領域は、どちらかの区画がそろっていない間は出しっぱなし(片方がそろっていれば、残りだけを取る)
        if (Remaining(_state) != ModelSets.None) _body.Children.Add(BothSection());
        _body.Children.Add(Section(JobKind.Embed, _state.Embedding, ModelSets.FeatureVector));
        _body.Children.Add(Section(JobKind.Vlm, _state.Vlm, ModelSets.Vlm));

        // 約束の文(ダウンロードのボタンの近く。AGENTS.md と同じ文)
        _body.Children.Add(new TextBlock
        {
            Text = SetupWords.Promise, TextWrapping = TextWrapping.Wrap, Foreground = AppColors.Secondary, Margin = new Thickness(0, 2, 0, 12),
        });

        _body.Children.Add(Buttons(vlmOnly));
        RefreshStates();
        ShowProgress();
    }

    // ---------------------------------------------------------------- 行の状態(取っている間に、ファイルごとに替わる)

    /// <summary>まだそろっていない組(調べたとおり。取っている間の置いたファイルは見ない)。</summary>
    private static ModelSets Remaining(SetupState state) =>
        (state.Embedding.IsComplete ? ModelSets.None : ModelSets.FeatureVector) | (state.Vlm.IsComplete ? ModelSets.None : ModelSets.Vlm);

    /// <summary>取っている間に取り終えたファイルを「あり」と見た、組の状態(取っていない組・取っていない間は、調べたとおり)。</summary>
    private ModelSetStatus Effective(ModelSets sets, ModelSetStatus set)
    {
        if (!IsTaking(sets) || _completed.Count == 0) return set;
        return set with { Files = [.. set.Files.Select(f => _completed.Contains(f.RelativePath) ? f with { State = ModelFileState.Present, ActualSize = f.ExpectedSize } : f)] };
    }

    /// <summary>区画の見出しの状態と、ファイルの行の状態を、いまの状態に合わせる(部品は作り直さない)。</summary>
    private void RefreshStates()
    {
        foreach ((ModelSets sets, ModelSetStatus set) in new[] { (ModelSets.FeatureVector, _state.Embedding), (ModelSets.Vlm, _state.Vlm) })
        {
            ModelSetStatus effective = Effective(sets, set);
            if (_setStateCells.TryGetValue(sets, out TextBlock? header))
            {
                header.Text = SetupWords.SetState(effective);
                header.Foreground = effective.IsComplete ? AppColors.Success : AppColors.Warn;
            }
            foreach (ModelFileStatus file in effective.Files)
                if (_fileStateCells.TryGetValue((sets, file.RelativePath), out TextBlock? cell)) ShowFileState(cell, sets, file);
        }

        // 取っていない領域の組が(取っている間に)そろったら、その領域の中身を消す。押せない「ダウンロード」を残さない(高さは残す)
        foreach ((ModelSets key, (StackPanel text, Grid buttons)) in _areaParts)
        {
            if (_activeArea == key) continue;
            bool done = key == BothSets
                ? Effective(ModelSets.FeatureVector, _state.Embedding).IsComplete && Effective(ModelSets.Vlm, _state.Vlm).IsComplete   // 一括: 残りがすべてそろった
                : Effective(key, key == ModelSets.FeatureVector ? _state.Embedding : _state.Vlm).IsComplete;
            if (!done) continue;
            text.Children.Clear();
            buttons.Children.Clear();
        }
    }

    /// <summary>ファイル 1 行の状態。いま取っているファイルは「取得中」「確かめ中」(太字)。取り終えたファイルは、ふつうの「あり」。</summary>
    private void ShowFileState(TextBlock cell, ModelSets sets, ModelFileStatus file)
    {
        if (IsTaking(sets) && file.State != ModelFileState.Present && _lastProgress is { Phase: not ModelDownloadPhase.Completed } p && p.FileName == file.RelativePath)
        {
            cell.Text = SetupWords.FileStateWhileDownloading(p.Phase);
            cell.ClearValue(TextBlock.ForegroundProperty);
            cell.FontWeight = FontWeights.SemiBold;
            return;
        }
        cell.Text = SetupWords.FileState(file);
        cell.FontWeight = FontWeights.Normal;
        if (file.State == ModelFileState.SizeMismatch) cell.Foreground = AppColors.Warn;
        else if (file.State == ModelFileState.Present) cell.Foreground = AppColors.Secondary;
        else cell.ClearValue(TextBlock.ForegroundProperty);
    }

    private UIElement Section(JobKind job, ModelSetStatus set, ModelSets sets)
    {
        var panel = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center };   // 文字と色は RefreshStates が付ける
        _setStateCells[sets] = state;
        DockPanel.SetDock(state, Dock.Right);
        header.Children.Add(state);
        header.Children.Add(new TextBlock { Text = SetupWords.SectionTitle(job), FontWeight = FontWeights.SemiBold, ToolTip = ProcessingWords.StageHint(job) });
        panel.Children.Add(header);

        panel.Children.Add(new TextBlock
        {
            Text = SetupWords.SectionPurpose(job) + "。" + SetupWords.SetSummary(set) + "です",
            TextWrapping = TextWrapping.Wrap, Foreground = AppColors.Secondary, Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(FileTable(set, sets));

        // 置き場と入手先(選んでコピーできる文字)
        var info = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        AddInfoRow(info, row++, SetupWords.FolderLabel, set.Directory, () =>
        {
            var open = UiParts.Button(SetupWords.OpenFolderLabel, (_, _) => OpenFolder(set.Directory));
            open.ToolTip = SetupWords.OpenFolderHint;
            return open;
        });
        if (set.SourceUrl is { } url) AddInfoRow(info, row, SetupWords.SourceLabel, url, null);
        panel.Children.Add(info);

        if (!set.IsComplete) panel.Children.Add(DownloadArea(sets));

        return new Border
        {
            Child = panel, Padding = new Thickness(14, 10, 14, 6), Margin = new Thickness(0, 0, 0, 12),
            Background = AppColors.ListSurface, BorderBrush = AppColors.ListBorder, BorderThickness = new Thickness(1),
        };
    }

    /// <summary>領域の中の 1 行の高さ(DIP)。文字の高さ(Yu Gothic UI 13 で約 17.3)より少し大きい固定値(左パネルの「インデックス」欄の行と同じ)。</summary>
    internal const double AreaLineHeight = 18;

    /// <summary>文の段の行数(主の文 2 行 + 添える 1 行。取っている最中は、進み具合の 2 行 + バー)。</summary>
    private const int AreaTextLines = 3;

    /// <summary>ボタンの行の高さ(DIP)。</summary>
    internal const double AreaButtonRowHeight = 34;

    /// <summary>ダウンロードの領域の高さ(文の段 + ボタンの行。余白は含まない)。どの場面でも同じ。</summary>
    internal const double DownloadAreaHeight = AreaTextLines * AreaLineHeight + AreaButtonRowHeight;

    /// <summary>「2 つとも」の領域の高さ(文の段だけ。ボタンは文の右に並べる)。どの場面でも同じ。</summary>
    internal const double BothAreaHeight = AreaTextLines * AreaLineHeight;

    /// <summary>1 行の高さを固定した文字(折り返すのは <paramref name="lines"/> が 2 以上のときだけ。入りきらなければ末尾を「…」で切る)。</summary>
    private static TextBlock AreaText(int lines, Brush? foreground = null)
    {
        var text = new TextBlock
        {
            TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = AreaLineHeight,
            Height = AreaLineHeight * lines,
        };
        if (foreground is not null) text.Foreground = foreground;
        return text;
    }

    /// <summary>文を入れる。切れたときのために、全文をヒントで読めるようにする(<paramref name="hint"/> が無ければ、文そのもの)。</summary>
    private static void Say(TextBlock block, string text, Brush? foreground = null, string? hint = null)
    {
        block.Text = text;
        if (foreground is not null) block.Foreground = foreground;
        block.ToolTip = text.Length == 0 ? null : hint ?? text;
    }

    /// <summary>
    /// 導入の文の下の、一括の領域(区画と同じ枠)。どちらかの区画がそろっていない間は置く。
    /// </summary>
    private UIElement BothSection() => new Border
    {
        Child = DownloadArea(BothSets), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 12),
        Background = AppColors.ListSurface, BorderBrush = AppColors.ListBorder, BorderThickness = new Thickness(1),
    };

    /// <summary>
    /// ダウンロードの領域(区画の下と、「2 つとも」の領域。<paramref name="sets"/> は、その領域が取る組で、領域の鍵)。<b>高さは、場面によらず一定</b>(区画の領域は <see cref="DownloadAreaHeight"/>、「2 つとも」の領域は <see cref="BothAreaHeight"/>)。区画の領域は文の段(3 行)の下にボタンの行を置き、「2 つとも」の領域は文の段の右にボタンを並べる。場面ごとに、同じ段の中身だけを替える。
    /// <list type="table">
    /// <item><term>押す前</term><description>文: 見積もり(何を・どこから・どこへ・空き) / ボタン: 「ダウンロード」</description></item>
    /// <item><term>失敗の直後</term><description>文: 失敗の理由(平らな文。詳細はヒント) + 手で置く方法 / ボタン: 「ダウンロード」(押し直せる)</description></item>
    /// <item><term>中止の直後</term><description>文: 中止したことと、続きから取れること / ボタン: 「ダウンロード」</description></item>
    /// <item><term>空きが足りない・ほかを取っている</term><description>文: 押せない理由 / ボタン: 「ダウンロード」(押せない)</description></item>
    /// <item><term>取っている最中</term><description>文: 進み具合の 2 行 + バー / ボタン: 「中止」</description></item>
    /// </list>
    /// 押す前の見積もりの文は、場面が替わって文の段から外れても、「ダウンロード」のボタンのヒントで読める。
    /// </summary>
    private UIElement DownloadArea(ModelSets key)
    {
        // 一括の領域が取るのは、まだそろっていない組(両方なら 2 つとも、片方がそろっていれば残り)
        ModelSets sets = key == BothSets ? Remaining(_state) : key;
        // 「2 つとも」の領域は、文を左、ボタンを右に並べる(高さは、文の段だけ)。区画の領域は、文の段の下にボタンの行を置く
        bool sideBySide = key == BothSets;
        var text = new StackPanel { Height = AreaTextLines * AreaLineHeight };
        var buttons = new Grid();
        UIElement area;
        if (sideBySide)
        {
            var row = new Grid { Height = BothAreaHeight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.Margin = new Thickness(16, 0, 0, 0);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons, 1);
            row.Children.Add(text);
            row.Children.Add(buttons);
            area = row;
        }
        else
        {
            buttons.Height = AreaButtonRowHeight;
            var column = new StackPanel { Margin = new Thickness(0, 6, 0, 4), Height = DownloadAreaHeight };
            column.Children.Add(text);
            column.Children.Add(buttons);
            area = column;
        }
        _areaParts[key] = (text, buttons);

        if (_activeArea == key)
        {
            _progressLine = AreaText(1);
            _amountLine = AreaText(1, AppColors.Secondary);
            _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 6, 0, 6) };
            _cancelButton = UiParts.Button(_cts is { IsCancellationRequested: true } ? SetupWords.Cancelling : SetupWords.CancelLabel, (_, _) => CancelDownload());
            _cancelButton.ToolTip = SetupWords.CancelHint;
            _cancelButton.IsEnabled = _cts is not { IsCancellationRequested: true };
            _cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
            _cancelButton.VerticalAlignment = VerticalAlignment.Center;
            _cancelButton.Margin = new Thickness(0);
            _progressLine.Text = SetupWords.Starting;
            text.Children.Add(_progressLine);
            text.Children.Add(_amountLine);
            text.Children.Add(_bar);
            buttons.Children.Add(_cancelButton);
            return area;
        }

        ModelDownloadPlan plan = _downloads.Plan(_state.Embedding.Directory, _state.Vlm.Directory, sets);
        // 空きは、置き場のドライブごとに見る(2 つのモデルの置き場が別のドライブのことがある。Core の取る前の確かめと同じ数え方)
        DriveNeed[] drives = DriveNeeds(plan);
        int lacking = Array.FindIndex(drives, d => d.Free is { } f && d.Required > f);
        bool noSpace = lacking >= 0;
        bool otherBusy = _active is not null;

        // 文の段: 場面ごとに、主の文は 1 つだけ(失敗 > 空きが足りない > ほかを取っている > 中止 > 見積もり)。長いときは切って、全文をヒントで読む
        TextBlock message = AreaText(AreaTextLines - 1), extra = AreaText(1, AppColors.Secondary);
        // 一括の領域で、片方がそろっているときの文の頭(どちらのモデルがそろっているかを言う)
        JobKind? readyJob = key == BothSets && sets != BothSets ? (sets == ModelSets.FeatureVector ? JobKind.Vlm : JobKind.Embed) : null;
        string? planText = plan.IsEmpty ? null
            : readyJob is { } ready ? SetupWords.RemainingPlanLine(ready, plan, [.. drives.Select(d => (d.Root, d.Free))])
            : SetupWords.PlanLine(plan, [.. drives.Select(d => (d.Root, d.Free))]);
        if (_failure is { } failure && failure.Area == key)
        {
            string line = SetupWords.FailureHead + ": " + SetupWords.FailureSummary(failure.Error);
            Say(message, line, AppColors.Warn, line + "\n" + SetupWords.FailureDetail(failure.Error));
            Say(extra, key == BothSets ? SetupWords.BulkManualGuidance : SetupWords.ManualGuidance);
        }
        else if (noSpace) Say(message, SetupWords.NoSpaceReason(drives[lacking].Required, drives[lacking].Free!.Value, drives[lacking].Root), AppColors.Warn);
        else if (otherBusy) Say(message, SetupWords.BusyReason, AppColors.Secondary);
        else
        {
            if (_cancelledArea == key) Say(message, SetupWords.Cancelled, AppColors.Secondary);
            else if (planText is not null) Say(message, planText, AppColors.Secondary);
            // 2 つとも取るときは、取る順を 1 行添える(Core の順。テストが見張る)
            if (key == BothSets && sets == BothSets && message.Text.Length > 0) Say(extra, SetupWords.BothOrder, AppColors.Secondary);
        }
        text.Children.Add(message);
        text.Children.Add(extra);

        // ボタンの行(見積もりの文が文の段に出ていない場面では、ヒントに見積もりの文を入れる)
        if (!plan.IsEmpty)
        {
            string label = key != BothSets ? SetupWords.DownloadButton(plan) : sets == BothSets ? SetupWords.DownloadBothButton(plan) : SetupWords.DownloadRemainingButton(plan);
            var download = UiParts.Button(label, (_, _) => OnDownloadClicked(sets, key));
            download.ToolTip = planText is not null && message.Text != planText ? planText + "。" + SetupWords.DownloadButtonHint : SetupWords.DownloadButtonHint;
            ToolTipService.SetShowOnDisabled(download, true);
            download.IsEnabled = !noSpace && !otherBusy;
            download.HorizontalAlignment = HorizontalAlignment.Right;
            download.VerticalAlignment = VerticalAlignment.Center;
            download.Margin = new Thickness(0);
            buttons.Children.Add(download);
        }
        return area;
    }

    /// <summary>置き場のドライブ 1 つぶん: ドライブのルート・空き(分からなければ null)・要る空き(そこに置くぶんの残り + 余裕)。</summary>
    private readonly record struct DriveNeed(string Root, long? Free, long Required);

    /// <summary>
    /// 取るファイルを置き場のドライブごとに分けて、要る空きを数える(Core の取る前の確かめと同じ: ドライブごとに、残りの合計 + 余裕。残りが無いドライブは数えない)。
    /// 見積もり全体の <see cref="ModelDownloadPlan.RequiredFreeBytes"/> は 1 つの合計なので、置き場が別のドライブのときは使えない。
    /// </summary>
    private DriveNeed[] DriveNeeds(ModelDownloadPlan plan)
    {
        string DirectoryOf(ModelDownloadFile f) => f.Set switch
        {
            ModelSets.FeatureVector => plan.EmbeddingDirectory,
            ModelSets.Vlm => plan.VlmDirectory,
            _ => throw new InvalidOperationException($"取るファイルの組が分からない: {f.Set}({f.RelativePath})"),
        };
        return [.. plan.Files
            .GroupBy(f => Path.GetPathRoot(Path.GetFullPath(DirectoryOf(f))) ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => (Root: g.Key, Directory: DirectoryOf(g.First()), Remaining: g.Sum(f => f.Size - Math.Min(f.PartialBytes, f.Size))))
            .Where(g => g.Remaining > 0)
            .Select(g => new DriveNeed(g.Root, _downloads.FreeSpace(g.Directory), g.Remaining + ModelDownloadPlan.FreeSpaceMargin))];
    }

    private UIElement FileTable(ModelSetStatus set, ModelSets sets)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 200 });

        void Add(int row, int col, UIElement e)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(0, 0, Cell(SetupWords.ColumnFile, header: true));
        Add(0, 1, Cell(SetupWords.ColumnSize, header: true, alignRight: true));
        Add(0, 2, Cell(SetupWords.ColumnState, header: true));
        int r = 1;
        foreach (ModelFileStatus file in set.Files)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(r, 0, Cell(file.RelativePath));
            TextBlock size = Cell(SetupWords.FormatSize(file.ExpectedSize), alignRight: true);
            size.ToolTip = SetupWords.ExactSize(file.ExpectedSize);
            Add(r, 1, size);
            TextBlock state = Cell("");   // 文字と色は RefreshStates が付ける
            _fileStateCells[(sets, file.RelativePath)] = state;
            Add(r, 2, state);
            r++;
        }
        return grid;
    }

    private static TextBlock Cell(string text, bool header = false, bool alignRight = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 2, 12, 2),
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
        };
        if (header) cell.Foreground = AppColors.Secondary;
        return cell;
    }

    private static void AddInfoRow(Grid grid, int row, string label, string value, Func<UIElement>? action)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Foreground = AppColors.Secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 4) };
        Grid.SetRow(l, row);
        grid.Children.Add(l);
        if (value.Length > 0)
        {
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = AppColors.Transparent, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(0),
            };
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(v);
        }
        if (action is not null)
        {
            UIElement a = action();
            Grid.SetRow(a, row);
            Grid.SetColumn(a, 2);
            grid.Children.Add(a);
        }
    }

    private UIElement Buttons(bool vlmOnly)
    {
        var bar = new DockPanel { LastChildFill = true };
        bool busy = _active is not null;

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var recheck = UiParts.Button(SetupWords.RecheckLabel, (_, _) => Recheck(manual: true));
        recheck.ToolTip = SetupWords.RecheckHint;
        recheck.IsEnabled = !busy;
        right.Children.Add(recheck);
        if (vlmOnly)
        {
            var go = UiParts.Button(SetupWords.ContinueLabel, (_, _) => ContinueWithoutVlm());
            go.ToolTip = SetupWords.ContinueHint;
            go.IsEnabled = !busy;
            right.Children.Add(go);
        }
        var exit = UiParts.Button(SetupWords.ExitLabel, (_, _) => RequestExit());
        exit.IsCancel = true;
        right.Children.Add(exit);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        (_status.Parent as Panel)?.Children.Remove(_status);   // 作り直すたびに、前の行から外してから載せる
        bar.Children.Add(_status);
        return bar;
    }

    // ---------------------------------------------------------------- テスト用の走査

    private static System.Collections.Generic.IEnumerable<string> Walk(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock t && t.Text.Length > 0) yield return t.Text;
            else if (child is TextBox b && b.Text.Length > 0) yield return b.Text;
            foreach (string s in Walk(child)) yield return s;
        }
    }

    private static System.Collections.Generic.IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (T inner in FindAll<T>(child)) yield return inner;
        }
    }
}
