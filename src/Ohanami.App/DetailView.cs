using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ohanami.Core;
using Ohanami.Core.Tags;

namespace Ohanami.App;

/// <summary>
/// 詳細画面(結果の一覧に重ねる全面の表示)。左に画像(大)、右に経路ごとの順位・テキストの欄(一致の強調)・段階の状態・情報・パス。
/// 上に「戻る(Esc)」と位置(n / 件数)・前後(◀ ▶)。**中身の文字列は <see cref="DetailModel"/>(<see cref="DetailPresenter"/> が作る)を出すだけ**で、Core にも検索にも触らない
/// (<see cref="ResultsPanel"/> が、開く・閉じる・前後・読み込みを持つ)。XAML も Binding も使わない。
/// <para>操作は一覧と同じ部品を使う(<see cref="ResultsPanel"/> の実装を呼ぶ): エクスプローラーで表示、画像をコピー、ファイルとしてコピー、画像のドラッグ。</para>
/// </summary>
internal sealed class DetailView : Grid
{
    private readonly Button _back;
    private readonly Button _prev;
    private readonly Button _next;
    private readonly TextBlock _position;
    private readonly Border _imageHost;
    private readonly Image _image;
    private readonly TextBlock _imageMessage;
    private readonly StackPanel _side;
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, TextBlock> _fieldBlocks = new(StringComparer.Ordinal);

    // 手動タグの編集。入力の欄・エラー・候補は、内容を作り直しても(読み込みが終わる・タグを足す)入力中の文字とフォーカスを失わないよう、1 つを持ち続ける
    private readonly StackPanel _tagEditor = new();
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly DockPanel _tagInputRow = new() { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBox _tagBox = new() { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 3, 6, 3) };
    private readonly Button _tagAdd = new() { Content = "追加", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _tagError = new() { Foreground = AppColors.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _candidatePanel = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<string, Button> _chipRemoveButtons = new(StringComparer.Ordinal);
    private readonly List<TagChip> _chipModels = [];
    private IReadOnlyList<TagCandidate> _candidates = [];
    private bool _tagsEditable;
    private const int MaxShownCandidates = 12;
    private Point _dragStart;
    private bool _dragArmed;

    /// <summary>「戻る」のツールチップ(ADR 0038: ショートカットと補足は、ツールチップに書く。マウスの戻るボタンは <see cref="MouseShortcuts.ClosesDetail"/>)。</summary>
    internal const string BackTooltip = "一覧に戻ります (選択は残ります)。マウスの戻るボタンでも戻れます";

    /// <summary>手動タグの名前の入力欄のツールチップ(詳細画面と、一覧の「手動タグを付ける…」の窓で同じ)。</summary>
    internal const string TagBoxTooltip = "手動タグの名前を入れて Enter を押します (「|」「、」・改行・64 文字を超える名前は使えません)";

    /// <summary>「戻る(Esc)」が押された。</summary>
    public Action? CloseRequested;

    /// <summary>前(-1)・次(+1)へ。</summary>
    public Action<int>? NavigateRequested;

    /// <summary>タグを足す合図(「追加」ボタン・入力欄の Enter・候補のボタン)。引数は、入力された名前(正規化の前)。</summary>
    public Action<string>? TagAddRequested;

    /// <summary>タグを外す合図(札の「×」)。引数は、そのタグの名前。</summary>
    public Action<string>? TagRemoveRequested;

    public Action? RevealRequested;
    public Action? CopyImageRequested;
    public Action? CopyFilesRequested;

    /// <summary>画像のドラッグが始まった(押したまま少し動かした)。ファイルを外へ渡すのは <see cref="ResultsPanel"/>(一覧と同じ部品)。</summary>
    public Action? DragRequested;

    public DetailView()
    {
        Background = AppColors.WindowBackground;
        Focusable = true;
        // 一覧と同じ: 文字を受けないので IME を無効にする(日本語 IME がオンのままだと、E のような単体キーが IME に食われる)
        InputMethod.SetIsInputMethodEnabled(this, false);
        Visibility = Visibility.Collapsed;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _back = new Button { Content = "◀ 戻る (Esc)", Padding = new Thickness(12, 4, 12, 4), ToolTip = BackTooltip };
        _back.Click += (_, _) => CloseRequested?.Invoke();
        _prev = new Button { Content = "◀", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), ToolTip = "前の結果 (←)" };
        _prev.Click += (_, _) => NavigateRequested?.Invoke(-1);
        _next = new Button { Content = "▶", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(4, 0, 0, 0), ToolTip = "次の結果 (→)" };
        _next.Click += (_, _) => NavigateRequested?.Invoke(+1);
        _position = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        BuildTagEditor();

        var top = new DockPanel { Margin = new Thickness(WindowLayout.DetailInnerMargin, WindowLayout.DetailInnerMargin, WindowLayout.DetailInnerMargin, 8), LastChildFill = false };   // 戻る・前・次のボタンの周りに余白(無いと、詳細画面の面の端にボタンが接して見える)
        DockPanel.SetDock(_back, Dock.Left);
        top.Children.Add(_back);
        DockPanel.SetDock(_position, Dock.Left);
        top.Children.Add(_position);
        DockPanel.SetDock(_next, Dock.Right);
        top.Children.Add(_next);
        DockPanel.SetDock(_prev, Dock.Right);
        top.Children.Add(_prev);
        SetRow(top, 0);
        Children.Add(top);

        // 左: 画像(大)。押したまま動かすと、ファイルを外のアプリへ渡すドラッグ
        _image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(6), IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _imageMessage = new TextBlock
        {
            Foreground = AppColors.OnDark, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), IsHitTestVisible = false,
        };
        var imageGrid = new Grid();
        imageGrid.Children.Add(_image);
        imageGrid.Children.Add(_imageMessage);
        _imageHost = new Border { Background = AppColors.ImageHost, Child = imageGrid, ToolTip = "ドラッグで他のアプリへ渡せます" };
        _imageHost.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(_imageHost); _dragArmed = MouseShortcuts.ArmsDrag(e.ClickCount); _imageHost.CaptureMouse(); };
        _imageHost.MouseMove += OnImageMouseMove;
        _imageHost.MouseLeftButtonUp += (_, _) => { _dragArmed = false; if (_imageHost.IsMouseCaptured) _imageHost.ReleaseMouseCapture(); };

        // 右: 操作と内容(縦に並べる。収まらなければスクロール)
        _side = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _side, Focusable = false, Margin = new Thickness(12, 0, 0, 0) };

        var body = new Grid { Margin = new Thickness(WindowLayout.DetailInnerMargin, 0, WindowLayout.DetailInnerMargin, WindowLayout.DetailInnerMargin) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowLayout.DetailSideWidth) });
        SetColumn(_imageHost, 0);
        SetColumn(_scroll, 1);
        body.Children.Add(_imageHost);
        body.Children.Add(_scroll);
        SetRow(body, 1);
        Children.Add(body);
    }

    // ------------------------------------------------------------------ 表示

    /// <summary>位置(1 始まり)と、前後に動けるか。</summary>
    public void SetPosition(int position, int total, bool canPrevious, bool canNext)
    {
        _position.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{position} / {total}");
        _prev.IsEnabled = canPrevious;
        _next.IsEnabled = canNext;
    }

    /// <summary>右側のスクロールを先頭に戻す(別の画像に移ったとき。同じ画像の読み込みが終わって内容を作り直すときは呼ばない=読んでいる位置を動かさない)。</summary>
    public void ResetScroll() => _scroll.ScrollToTop();

    /// <summary>右側の内容を、<paramref name="model"/> で作り直す。</summary>
    public void ShowModel(DetailModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        bool tagInputHadFocus = _tagBox.IsKeyboardFocused;   // 内容を作り直すと、入力欄は一度、木から外れてフォーカスを失う。終わったら戻す
        _side.Children.Clear();
        _fieldBlocks.Clear();

        _side.Children.Add(new TextBlock { Text = model.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        if (model.RecordNotice is { } notice)
            _side.Children.Add(new TextBlock { Text = notice, Foreground = model.RecordNotice.StartsWith("読み込み中", StringComparison.Ordinal) ? AppColors.Muted : AppColors.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(WithTip(UiParts.Button("エクスプローラーで表示", (_, _) => RevealRequested?.Invoke()), "ショートカット: E"));
        actions.Children.Add(WithTip(UiParts.Button("画像をコピー", (_, _) => CopyImageRequested?.Invoke()), "ショートカット: Ctrl+C"));
        actions.Children.Add(WithTip(UiParts.Button("ファイルとしてコピー", (_, _) => CopyFilesRequested?.Invoke()), "ショートカット: Ctrl+Shift+C"));
        _side.Children.Add(actions);

        if (model.ReasonLines.Count > 0) AddSection("出た理由", model.ReasonLines.Select(l => (UIElement)Line(l)));
        AddSection("順位", model.RankLines.Select((l, i) =>
        {
            TextBlock line = Line(l);
            if (i < model.RankTips.Count && model.RankTips[i] is { } tip) line.ToolTip = tip;   // 類似度の数字は、ここ(ツールチップ)に置く
            return (UIElement)line;
        }));

        // 手動タグは短いので、長い OCR の文章より前に置く(編集するのに、長い文章の下までスクロールしなくてよい)
        ShowManualTags(model.ManualTags);
        AddSection("手動タグ", [_tagEditor]);

        var fields = new List<UIElement>();
        foreach (DetailField f in model.Fields)
        {
            fields.Add(new TextBlock { Text = f.Label, Foreground = AppColors.Muted, Margin = new Thickness(0, 4, 0, 0) });
            TextBlock block = FieldBlock(f);
            _fieldBlocks[f.Label] = block;
            fields.Add(block);
        }
        if (model.FieldsNote is { } fieldsNote) fields.Insert(0, new TextBlock { Text = fieldsNote, Foreground = AppColors.Muted, TextWrapping = TextWrapping.Wrap });
        if (fields.Count > 0) AddSection(model.FieldsHeader, fields);

        if (model.StageLines.Count > 0) AddSection("処理状態", model.StageLines.Select(l => (UIElement)Line(l)));
        if (model.InfoLines.Count > 0) AddSection("画像の情報", model.InfoLines.Select(l => (UIElement)Line(l)));

        var paths = new List<UIElement>();
        foreach (DetailPathLine p in model.PathLines)
        {
            var pathText = new TextBlock { Text = p.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            if (p.Missing) pathText.Foreground = AppColors.Warn;   // ふだんは色を指定しない(窓の文字色を継承)。欠けたときだけ赤
            paths.Add(pathText);
            if (p.Note is { } note) paths.Add(new TextBlock { Text = note, Foreground = AppColors.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
        if (paths.Count > 0) AddSection("パス", paths);

        if (tagInputHadFocus)
        {
            // 作り直しの間に外れたフォーカスを、入力欄に戻す(続けて次のタグを打てる)。レイアウトが済んでから
            Dispatcher.BeginInvoke(() =>
            {
                if (_tagBox.IsVisible && _tagsEditable) { _tagBox.Focus(); _tagBox.CaretIndex = _tagBox.Text.Length; }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ------------------------------------------------------------------ 手動タグの編集

    private void BuildTagEditor()
    {
        // 検索ボックスと同じ: 日本語を入力するので、IME は有効にする(詳細画面全体の IME の無効は、この欄だけ上書きする)
        InputMethod.SetIsInputMethodEnabled(_tagBox, true);
        _tagBox.ToolTip = TagBoxTooltip;
        _tagBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { RequestAdd(_tagBox.Text); e.Handled = true; }
        };
        _tagBox.TextChanged += (_, _) =>
        {
            _tagError.Visibility = Visibility.Collapsed;   // 入れ直したら、前のエラーは消す
            RenderCandidates();
        };
        _tagAdd.Click += (_, _) => RequestAdd(_tagBox.Text);
        DockPanel.SetDock(_tagAdd, Dock.Right);
        _tagInputRow.Children.Add(_tagAdd);
        _tagInputRow.Children.Add(_tagBox);
        _tagEditor.Children.Add(_chips);
        _tagEditor.Children.Add(_tagInputRow);
        _tagEditor.Children.Add(_tagError);
        _tagEditor.Children.Add(_candidatePanel);
    }

    private void RequestAdd(string text)
    {
        if (!_tagsEditable) return;
        TagAddRequested?.Invoke(text);
    }

    private void ShowManualTags(ManualTagsModel model)
    {
        _tagsEditable = model.Editable;
        _chips.Children.Clear();
        _chipRemoveButtons.Clear();
        _chipModels.Clear();
        _chipModels.AddRange(model.Chips);
        if (model.Chips.Count == 0)
            _chips.Children.Add(new TextBlock { Text = DetailPresenter.NoValue, Foreground = AppColors.Muted, Margin = new Thickness(0, 0, 0, 2) });
        foreach (TagChip chip in model.Chips)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            foreach (TextSegment seg in chip.Segments)
            {
                var run = new Run(seg.Text);
                if (seg.Highlight) run.Background = AppColors.Highlight;
                text.Inlines.Add(run);
            }
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text);
            if (model.Editable)
            {
                string name = chip.Name;
                var remove = new Button
                {
                    Content = "×", Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(0),
                    Background = AppColors.Transparent, Foreground = AppColors.Muted, ToolTip = "この手動タグを外します", Cursor = Cursors.Hand,
                };
                remove.Click += (_, _) => TagRemoveRequested?.Invoke(name);
                _chipRemoveButtons[name] = remove;
                panel.Children.Add(remove);
            }
            _chips.Children.Add(new Border
            {
                Child = panel, Background = AppColors.ChipBackground, BorderBrush = AppColors.ChipBorder,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 4, 2), Margin = new Thickness(0, 0, 6, 4),
            });
        }
        _tagInputRow.Visibility = model.Editable ? Visibility.Visible : Visibility.Collapsed;
        RenderCandidates();
    }

    /// <summary>既存のタグの候補(使われている数の多い順)を出す。いま付いているタグは出さない。入力欄に文字があれば、それを含む名前だけ。ボタンを押すと、そのタグを足す。</summary>
    public void SetCandidates(IReadOnlyList<TagCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _candidates = candidates;
        RenderCandidates();
    }

    private void RenderCandidates()
    {
        _candidatePanel.Children.Clear();
        if (!_tagsEditable) return;
        string typed = _tagBox.Text.Trim();
        var attached = new HashSet<string>(_chipModels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        IEnumerable<TagCandidate> shown = _candidates
            .Where(c => !attached.Contains(c.Name) && (typed.Length == 0 || c.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxShownCandidates);
        bool any = false;
        foreach (TagCandidate c in shown)
        {
            if (!any) _candidatePanel.Children.Add(new TextBlock { Text = "候補:", Foreground = AppColors.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
            any = true;
            string name = c.Name;
            var button = new Button
            {
                Content = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c.Name} ({c.ImageCount})"),
                Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 4), ToolTip = "この手動タグを付けます",
            };
            button.Click += (_, _) => RequestAdd(name);
            _candidatePanel.Children.Add(button);
        }
    }

    /// <summary>入力欄の下に、拒んだ理由・失敗の理由を出す(null で消す)。</summary>
    public void ShowTagError(string? message)
    {
        _tagError.Text = message ?? "";
        _tagError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>入力欄を空にする(足せたとき)。</summary>
    public void ClearTagInput() => _tagBox.Clear();

    /// <summary>入力欄にキーボードのフォーカスがあるか(←・→・E・Ctrl+C は、入力の文字として扱う)。</summary>
    public bool TagInputFocused => _tagBox.IsKeyboardFocused;

    /// <summary>ボタンにツールチップ(ショートカットの案内)を付けて返す。</summary>
    private static Button WithTip(Button b, string tip)
    {
        b.ToolTip = tip;
        return b;
    }

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };

    private TextBlock FieldBlock(DetailField f)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        if (f.IsEmpty)
        {
            block.Inlines.Add(new Run(DetailPresenter.NoValue) { Foreground = AppColors.Muted });
            return block;
        }
        foreach (TextSegment s in f.Segments)
        {
            var run = new Run(s.Text);
            if (s.Highlight) run.Background = AppColors.Highlight;
            block.Inlines.Add(run);
        }
        return block;
    }

    private void AddSection(string header, IEnumerable<UIElement> content)
    {
        _side.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        foreach (UIElement e in content) _side.Children.Add(e);
    }

    /// <summary>画像を出す(null なら <paramref name="message"/> を出す: 読み込み中・画像なし・読めない)。</summary>
    public void SetImage(BitmapSource? bitmap, string? message)
    {
        _image.Source = bitmap;
        _imageMessage.Text = bitmap is null ? message ?? "" : "";
        _imageMessage.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ 画像のドラッグ

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition(_imageHost);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        _imageHost.ReleaseMouseCapture();
        DragRequested?.Invoke();
    }

    /// <summary>ドラッグ元の要素(<c>DragDrop.DoDragDrop</c> に渡す)。</summary>
    internal DependencyObject DragSource => _imageHost;

    // ------------------------------------------------------------------ テスト・確認用

    internal string PositionText => _position.Text;

    internal bool PreviousEnabled => _prev.IsEnabled;

    internal bool NextEnabled => _next.IsEnabled;

    internal string ImageMessage => _imageMessage.Visibility == Visibility.Visible ? _imageMessage.Text : "";

    internal bool HasImage => _image.Source is not null;

    /// <summary>右側に出ている全部のテキスト(行ごと。強調の有無は <see cref="HighlightedTexts"/>)。</summary>
    internal string SideText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (UIElement child in _side.Children) AppendText(child, sb);
            return sb.ToString();
        }
    }

    private static void AppendText(UIElement e, StringBuilder sb)
    {
        switch (e)
        {
            case TextBlock tb: sb.AppendLine(string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } t ? t : tb.Text); break;
            case Panel p: foreach (UIElement c in p.Children) AppendText(c, sb); break;
            case Button b: sb.AppendLine("[" + b.Content + "]"); break;
        }
    }

    /// <summary>欄の名前から、その欄で強調されている文字列(順に)。欄が無ければ null。</summary>
    internal IReadOnlyList<string>? HighlightedTexts(string label) =>
        _fieldBlocks.TryGetValue(label, out TextBlock? block) ? block.Inlines.OfType<Run>().Where(r => r.Background is not null).Select(r => r.Text).ToArray() : null;

    /// <summary>欄の名前から、その欄の文字列全体(区間をつないだもの)。欄が無ければ null。</summary>
    internal string? FieldText(string label) =>
        _fieldBlocks.TryGetValue(label, out TextBlock? block) ? string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)) : null;

    /// <summary>右側の行のうち、<paramref name="startsWith"/> で始まる行のツールチップ(無ければ null)。順位の行の類似度の確認用。</summary>
    internal string? TooltipOfLine(string startsWith) =>
        _side.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<TextBlock>()).Concat(_side.Children.OfType<TextBlock>())
            .FirstOrDefault(tb => tb.Text.StartsWith(startsWith, StringComparison.Ordinal))?.ToolTip as string;

    internal string TitleText => (_side.Children.Count > 0 && _side.Children[0] is TextBlock tb) ? tb.Text : "";

    /// <summary>手動タグの札の名前(順に)。</summary>
    internal IReadOnlyList<string> ChipNames => _chipModels.Select(c => c.Name).ToArray();

    /// <summary>強調されている札の文字列(順に)。</summary>
    internal IReadOnlyList<string> HighlightedChipTexts => _chips.Children.OfType<Border>()
        .SelectMany(b => ((StackPanel)b.Child).Children.OfType<TextBlock>())
        .SelectMany(t => t.Inlines.OfType<Run>().Where(r => r.Background is not null).Select(r => r.Text)).ToArray();

    internal bool TagsEditable => _tagsEditable;

    internal bool TagInputVisible => _tagInputRow.Visibility == Visibility.Visible;

    internal string TagInputText { get => _tagBox.Text; set => _tagBox.Text = value; }

    internal string TagError => _tagError.Visibility == Visibility.Visible ? _tagError.Text : "";

    /// <summary>いま出ている候補のボタンの文字(順に)。</summary>
    internal IReadOnlyList<string> CandidateLabels => _candidatePanel.Children.OfType<Button>().Select(b => (string)b.Content).ToArray();

    internal void PressAdd() => _tagAdd.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal void PressRemove(string name) => _chipRemoveButtons[name].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal void PressCandidate(string name) =>
        _candidatePanel.Children.OfType<Button>().Single(b => ((string)b.Content).StartsWith(name + " (", StringComparison.Ordinal))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal bool RemoveButtonExists(string name) => _chipRemoveButtons.ContainsKey(name);

    internal IEnumerable<Button> Buttons() => FindButtons(this);

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button b) yield return b;
            if (child is DependencyObject d) foreach (Button inner in FindButtons(d)) yield return inner;
        }
    }
}
