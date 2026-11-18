using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 処理状態の窓(ADR 0039)。一覧の種類は 5 つ(ラジオ。数を添える): {段階}のインデックス作成に失敗(2 つ)・飛ばした画像・要再作成・フォルダに無い画像。
/// <list type="bullet">
/// <item><b>操作は見出しの下(数の隣)</b>に置く: やり直す・もう一度試す・再作成を始める…(要再作成は段階ごとに 1 行)・記録を消す…。いま見ている一覧の分だけ。
///   <b>押せないときは、その操作の隣に 1 行で理由を書く</b>(ツールチップに頼らない)。可否と理由は <see cref="ProcessingSnapshot"/>(純粋な関数)が決める。</item>
/// <item>下の行は、行を選んで「エクスプローラーで表示」(現存するパスがあるとき)・「詳細」(その画像の詳細画面を、メインの画面で開く。フォルダに無い画像でも開ける)・「読み直す」・「閉じる」。</item>
/// <item><b>自動の読み直し</b>: <see cref="Apply"/> で新しい <see cref="ProcessingSnapshot"/> が届き、いま見ている種類の数が変わったら、一覧を読み直す(選択は残す)。
///   窓はモーダルにしない(一時停止をいつでも押せるように)。</item>
/// <item>操作は <c>perform</c>(<see cref="IndexPanel"/> が Core につなぐ)に渡すだけ。窓は Core を知らない(見せる側)。二度押しは、<see cref="Apply"/> で届く「操作の最中」で押せなくなる(押す前にも、いまの可否を確かめる)。</item>
/// </list>
/// 一覧の中身は <paramref name="load"/>(Core の <c>Images.ListFailuresAsync</c> / <c>Indexer.ListSkippedAsync</c> / <c>Indexer.ListMissingImagesAsync</c> を App が結ぶ)で読む。
/// 「詳細」を押すと、この画面を閉じて <see cref="OpenDetailRequested"/> を起こす(詳細画面はメインの窓の全面に重ねる作りなので、この画面は閉じる)。語は <see cref="ProcessingWords"/> から。
/// </summary>
internal sealed class FailureListWindow : Window
{
    private readonly Func<ProblemKind, CancellationToken, Task<ProblemList>> _load;
    private readonly IShellActions _shell;   // 「エクスプローラーで表示」(製品は SystemShellActions、テストは偽物)
    private readonly Func<ProcessingAction, Task>? _perform;   // 操作を実際に行う(IndexPanel)。null なら、操作の行は出さない(一覧を見るだけ)
    private readonly ListBox _list = UiParts.TextList(new ListBox { SelectionMode = SelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    private readonly TextBlock _heading = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<ProblemKind, RadioButton> _radios = [];
    private readonly TextBlock _modelNote = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, Foreground = AppColors.Secondary, Visibility = Visibility.Collapsed };
    private readonly StackPanel _actionHost = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly List<ActionRow> _actionRows = [];
    private readonly Button _revealButton, _detailButton, _reloadButton;
    private readonly ContentControl _headerHost = new();   // 列の見出しの行(一覧の種類で列が替わるので、読み込むたびに作り直す)
    private CancellationTokenSource? _cts;
    private int _serial;
    private ProblemList? _current;
    private ProblemKind _kind;
    private ProcessingSnapshot _snapshot;
    private int? _loadedCount;   // 最後に読み込みを始めたときの、その種類の数(これと違う数が届いたら、読み直す)

    /// <summary>操作の 1 行(要再作成の前置きの文・ボタン・押せない理由)。</summary>
    private sealed record ActionRow(ProcessingAction Action, TextBlock? Lead, Button Button, TextBlock Reason);

    /// <summary>ファイルの実在の確認(既定は <see cref="File.Exists"/>。テストで差し替える)。</summary>
    internal Func<string, bool> FileExists { get; set; } = File.Exists;

    /// <summary>「詳細」のあとで、この画面を閉じる処理(既定は <see cref="Window.Close"/>。テストで差し替える=画面を実際に表示せずに確かめる)。</summary>
    internal Action CloseWindow { get; set; }

    /// <summary>「詳細」を押した(画像のハッシュ)。この画面は、起こしたあとで閉じる。</summary>
    public event Action<string>? OpenDetailRequested;

    /// <param name="snapshot">いまの数と操作の可否(省略すると、すべて 0・起動前)。以後は <see cref="Apply"/> で受ける。</param>
    /// <param name="perform">操作を行う(<see cref="IndexPanel"/>)。省略すると、操作の行は出さない。</param>
    public FailureListWindow(
        ProblemKind first, Func<ProblemKind, CancellationToken, Task<ProblemList>> load, IShellActions shell,
        ProcessingSnapshot? snapshot = null, Func<ProcessingAction, Task>? perform = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(shell);
        _load = load;
        _shell = shell;
        _perform = perform;
        _snapshot = snapshot ?? ProcessingSnapshot.Closed;
        CloseWindow = Close;
        Title = ProcessingWords.WindowTitle;
        Width = 960;
        Height = 560;
        MinWidth = 640;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        AppTheme.StyleWindow(this);
        FontSize = 13;

        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => { if (CanOpenDetail) OpenDetail(); };

        _revealButton = Button("エクスプローラーで表示", () => Reveal());
        _detailButton = Button("詳細", () => OpenDetail());
        _reloadButton = Button("読み直す", () => _ = ReloadAsync());
        var close = Button("閉じる", Close);
        close.IsCancel = true;

        var radios = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (ProblemKind kind in Enum.GetValues<ProblemKind>())
        {
            var radio = new RadioButton { Margin = new Thickness(0, 0, 14, 4), GroupName = "kind" };
            ProblemKind k = kind;
            radio.Checked += (_, _) => KindChosen(k);
            _radios[kind] = radio;
            radios.Children.Add(radio);
        }
        RefreshRadioTexts();
        RefreshModelNote();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (Button b in new[] { _revealButton, _detailButton, _reloadButton, close }) buttons.Children.Add(b);

        var top = new StackPanel();
        top.Children.Add(_modelNote);
        top.Children.Add(radios);
        top.Children.Add(_heading);
        top.Children.Add(_actionHost);
        top.Children.Add(_headerHost);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        _kind = first;
        BuildActionRows();
        _radios[first].IsChecked = true;
        // 必ずどれかを選んだ状態で開く(選んでいないと、ラジオが空に見える。上で選んでいるが、表示されるときにも確かめる。何も選ばれない状態は作らない)
        Loaded += (_, _) => EnsureKindChecked();
        Closed += (_, _) => _cts?.Cancel();
        UpdateButtons();
    }

    /// <summary>いまの種類のラジオが選ばれていなければ、選ぶ(Checked が <see cref="KindChosen"/> を呼び、読み直す)。</summary>
    private void EnsureKindChecked()
    {
        if (_radios[_kind].IsChecked != true) _radios[_kind].IsChecked = true;
    }

    /// <summary>テスト用: 選ばれているラジオの数(いつも 1。何も選ばれない状態・2 つ選ばれた状態は無い)。</summary>
    internal int CheckedRadioCount => _radios.Values.Count(r => r.IsChecked == true);

    /// <summary>テスト用: 表示の直前の確かめ(<see cref="Loaded"/> と同じ)を、画面を出さずに行う。</summary>
    internal void EnsureKindCheckedForTests() => EnsureKindChecked();

    /// <summary>列の定義(<see cref="ProblemColumn"/>。固定の幅または残り)に沿った 1 行。XAML も Binding も使わず、列の幅を決めて並べる。</summary>
    private static Grid RowGrid(IReadOnlyList<ProblemColumn> columns, IReadOnlyList<TextBlock> cells)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (ProblemColumn c in columns)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width is { } w ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < cells.Count && i < columns.Count; i++)
        {
            cells[i].Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------------------------------------------------------- 数と操作(ProcessingSnapshot)

    /// <summary>
    /// 新しい数と操作の可否を受ける(IndexPanel から。UI スレッド)。種類のラジオの数と、操作の行(ボタンの押せる・押せない・理由)を更新する。
    /// **いま見ている種類の数が、最後に読み込みを始めたときと違えば、自動で読み直す**(選択は残す。状態の行は消さない)。ほかの種類の数が変わっても、読み直さない。
    /// </summary>
    internal void Apply(ProcessingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        RefreshRadioTexts();
        RefreshModelNote();
        RefreshActionRows();
        if (_loadedCount is { } loaded && loaded != snapshot.Count(_kind)) _ = ReloadAsync(auto: true);
    }

    /// <summary>VLM のモデルのファイルが無いとき、一覧の上に 1 行で言う(無ければ隠す)。</summary>
    private void RefreshModelNote()
    {
        bool missing = _snapshot.VlmMissingFiles.Count > 0;
        _modelNote.Text = missing ? ProcessingWords.ModelNotFetchedWindowLine(JobKind.Vlm) : "";
        _modelNote.ToolTip = missing ? ProcessingWords.ModelNotFetchedHint(JobKind.Vlm, _snapshot.VlmMissingFiles) : null;
        _modelNote.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>窓の上の、モデルが未取得だと言う 1 行(出ていなければ空)。</summary>
    internal string ModelNoteText => _modelNote.Visibility == Visibility.Visible ? _modelNote.Text : "";

    private void RefreshRadioTexts()
    {
        foreach ((ProblemKind kind, RadioButton radio) in _radios) radio.Content = ProcessingWords.KindRadioText(kind, _snapshot);
    }

    /// <summary>いまの種類の操作の行を作る(種類が替わるたび)。中身(文字・押せるか・理由)は <see cref="RefreshActionRows"/>。</summary>
    private void BuildActionRows()
    {
        _actionHost.Children.Clear();
        _actionRows.Clear();
        if (_perform is null) return;
        foreach (ProcessingAction action in ProcessingWords.ActionsOf(_kind))
        {
            var lead = ProcessingWords.ActionLead(action, _snapshot) is null ? null : new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var button = new Button { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var reason = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = AppColors.Secondary };
            ProcessingAction a = action;
            button.Click += (_, _) => _ = PerformAsync(a);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            if (lead is not null) row.Children.Add(lead);
            row.Children.Add(button);
            row.Children.Add(reason);
            _actionHost.Children.Add(row);
            _actionRows.Add(new ActionRow(action, lead, button, reason));
        }
        RefreshActionRows();
    }

    private void RefreshActionRows()
    {
        foreach (ActionRow r in _actionRows)
        {
            ActionState st = _snapshot.Action(r.Action);
            r.Button.Content = ProcessingWords.ActionLabel(r.Action, _snapshot);
            r.Button.IsEnabled = st.Enabled;
            r.Reason.Text = st.Reason ?? "";     // 押せないときは、隣に 1 行で理由(ツールチップに頼らない)
            if (r.Lead is not null)
            {
                r.Lead.Text = ProcessingWords.ActionLead(r.Action, _snapshot);
                // 要再作成の前置きには、段階ごとのヒント(作り直すまで検索に出ない / 古い結果のまま使われる。ADR 0006)
                JobKind? job = r.Action switch { ProcessingAction.RequeueEmbed => JobKind.Embed, ProcessingAction.RequeueVlm => JobKind.Vlm, _ => null };
                r.Lead.ToolTip = job is { } j ? ProcessingWords.OutdatedHint(j) : null;
            }
        }
    }

    /// <summary>
    /// 操作を行う(ボタンの処理。テストから呼べる)。**いまの可否を確かめてから** <c>perform</c> に渡す(押せない=操作の最中・数が 0・ジョブの状態のときは何もしない。二度押しの 2 回目)。
    /// 返す Task は <c>perform</c> が終わるまで(ジョブを始める操作は、ジョブが終わるまで続く。ボタンの処理は待たない)。
    /// </summary>
    internal async Task PerformAsync(ProcessingAction action)
    {
        if (_perform is null || !_snapshot.Action(action).Enabled) return;
        try
        {
            await _perform(action);
        }
        catch (Exception ex)
        {
            _status.Text = "操作できませんでした: " + UiText.Plain(ex);
        }
    }

    /// <summary>テスト・確認用: 種類のラジオの文字(並びの順)。</summary>
    internal IReadOnlyList<string> RadioTexts => [.. Enum.GetValues<ProblemKind>().Select(k => (string)_radios[k].Content)];

    /// <summary>テスト・確認用: 操作の行(操作・前置きの文・ボタンの文字・押せるか・押せない理由)。</summary>
    internal IReadOnlyList<(ProcessingAction Action, string? Lead, string Label, bool Enabled, string Reason)> ActionRows =>
        [.. _actionRows.Select(r => (r.Action, r.Lead?.Text, (string)r.Button.Content, r.Button.IsEnabled, r.Reason.Text))];

    // ---------------------------------------------------------------- 読み込み

    private void KindChosen(ProblemKind kind)
    {
        _kind = kind;
        BuildActionRows();
        _ = ReloadAsync();
    }

    /// <summary>いま出している(選んでいる)一覧の種類。</summary>
    internal ProblemKind Kind => _kind;

    /// <summary>
    /// 開いたままのこの画面で、指定の種類の一覧に切り替える(左パネルのボタンをもう一度押したとき。画面を増やさない)。
    /// 違う種類なら、その種類を選んで読み直す。同じ種類なら、いまの一覧を読み直す(押した時点の中身にする)。
    /// </summary>
    internal void SwitchTo(ProblemKind kind)
    {
        RadioButton radio = _radios[kind];
        if (radio.IsChecked == true) _ = ReloadAsync();
        else radio.IsChecked = true;   // Checked が KindChosen を呼び、読み直す
    }

    /// <summary>いま選んでいる種類の一覧を読み直して出す(テストが待てるように、Task を返す)。<paramref name="auto"/> は、数が変わったことによる自動の読み直し(見出し・選択・状態の行を動かさない)。</summary>
    internal async Task ReloadAsync(bool auto = false)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int serial = ++_serial;
        ProblemKind kind = _kind;
        _loadedCount = _snapshot.Count(kind);
        string? keepHash = auto ? SelectedRow?.Hash : null;
        if (!auto)
        {
            _heading.Text = "読み込み中…";
            _status.Text = "";
        }
        try
        {
            ProblemList list = await _load(kind, cts.Token);
            if (serial != _serial) return;
            Show(list, keepHash);
        }
        catch (OperationCanceledException)
        {
            // 別の種類に切り替えた、または閉じた
        }
        catch (Exception ex)
        {
            if (serial != _serial) return;
            _current = null;
            _list.Items.Clear();
            _heading.Text = "一覧を読み込めませんでした: " + UiText.Plain(ex);
            UpdateButtons();
        }
    }

    private void Show(ProblemList list, string? keepHash = null)
    {
        _current = list;
        _heading.Text = list.Heading;
        bool hasList = list.Columns.Count > 0;      // 要再作成は一覧が無い(見出しと操作の行だけ)
        _headerHost.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;
        // 見出しの行は、一覧の枠と行の余白の分(左右 7)だけ内側に寄せて、列をそろえる(縦のスクロールバーが出ているときは、右の列が 17 ほどずれる=未対応)
        _headerHost.Margin = new Thickness(7, 0, 7, 0);
        _headerHost.Content = hasList ? RowGrid(list.Columns, [.. list.Columns.Select(c => UiParts.HeaderCell(c.Header))]) : null;
        _list.Items.Clear();
        foreach (ProblemRow row in list.Rows)
        {
            // 先頭の列がファイル(長いパスは省略して、ツールチップに全体)。ほかの列は折り返す
            var cells = new List<TextBlock>
            {
                new() { Text = row.Cells[0], TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.PathText },
            };
            for (int c = 1; c < row.Cells.Count; c++) cells.Add(new TextBlock { Text = row.Cells[c], TextWrapping = TextWrapping.Wrap });
            var item = new ListBoxItem { Tag = row, Padding = UiParts.ListRowPadding, Content = RowGrid(list.Columns, cells), ToolTip = row.Tooltip };
            if (row.Path is null && _kind != ProblemKind.MissingImages) item.Foreground = AppColors.Faint;   // 元のファイルが無い画像(フォルダに無い画像の一覧では、全部がそうなので灰色にしない)
            _list.Items.Add(item);
            if (keepHash is not null && row.Hash == keepHash) item.IsSelected = true;
        }
        UpdateButtons();
    }

    // ---------------------------------------------------------------- 操作

    /// <summary>テスト・確認用: 出している一覧(読み込み中・読めなかったときは null)。</summary>
    internal ProblemList? CurrentList => _current;

    /// <summary>テスト・確認用: 選んでいる行。</summary>
    internal ProblemRow? SelectedRow => (_list.SelectedItem as ListBoxItem)?.Tag as ProblemRow;

    /// <summary>テスト・確認用: 行を選ぶ。</summary>
    internal void SelectRow(int index) => _list.SelectedIndex = index;

    internal string StatusText => _status.Text;

    internal string HeadingText => _heading.Text;

    /// <summary>「エクスプローラーで表示」を押せるか(現存するパスがある行を選んでいるとき)。</summary>
    internal bool CanReveal => SelectedRow?.Path is not null;

    /// <summary>「詳細」を押せるか(行を選んでいるとき)。</summary>
    internal bool CanOpenDetail => SelectedRow is not null;

    private void UpdateButtons()
    {
        _revealButton.IsEnabled = CanReveal;
        _detailButton.IsEnabled = CanOpenDetail;
        _revealButton.ToolTip = SelectedRow is { Path: null } ? ProcessingWords.RevealUnavailable : null;
        // 押せない理由は窓の中に 1 行で(ツールチップに頼らない): 元のファイルが無い行を選んだとき、状態の行に書く
        if (SelectedRow is { Path: null }) _status.Text = ProcessingWords.RevealUnavailable;
        else if (_status.Text == ProcessingWords.RevealUnavailable) _status.Text = "";
    }

    /// <summary>選んでいる行のファイルを、エクスプローラーで表示する。現存するパスが無い・ファイルが無い・実行に失敗したときは、画面の下に理由を出す。</summary>
    internal void Reveal()
    {
        if (SelectedRow is not { } row) return;
        if (row.Path is not { } path)
        {
            _status.Text = ProcessingWords.RevealUnavailable;
            return;
        }
        if (!FileExists(path))
        {
            _status.Text = "元のファイルがありません (移動・削除された可能性があります): " + path;
            return;
        }
        try
        {
            _shell.RevealInExplorer(path);
            _status.Text = "エクスプローラーで表示しました: " + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException)
        {
            _status.Text = "エクスプローラーで表示できませんでした: " + UiText.Plain(ex);
        }
    }

    /// <summary>選んでいる行の画像の詳細を、メインの画面で開く(この画面は閉じる)。</summary>
    internal void OpenDetail()
    {
        if (SelectedRow is not { } row) return;
        OpenDetailRequested?.Invoke(row.Hash);
        CloseWindow();
    }
}
