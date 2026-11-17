using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ohanami.Core;
using Ohanami.Core.Tags;

namespace Ohanami.App;

/// <summary>
/// 一覧で選んだ複数の画像に付けるタグの名前を聞く小さな画面。名前を入れて「付ける」(Enter)、または既存のタグの候補を押す。
/// 名前が決まりに合わなければ(<see cref="ManualTagName.Normalize"/>)、閉じずに理由を出す。結果は正規化した名前(キャンセルは null)。
/// 文言は ADR 0038 に沿う(手動のタグは必ず「手動タグ」と書く)。
/// </summary>
internal sealed class TagPromptWindow : Window
{
    private const int MaxShownCandidates = 16;
    private readonly TextBox _box = new() { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBlock _error = new() { Foreground = AppColors.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly WrapPanel _candidatePanel = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly IReadOnlyList<TagCandidate> _candidates;

    /// <summary>正規化した名前。「付ける」で閉じたときだけ入る。</summary>
    public string? TagName { get; private set; }

    public TagPromptWindow(int imageCount, IReadOnlyList<TagCandidate> candidates)
    {
        _candidates = candidates;
        Title = "手動タグを付ける";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;
        AppTheme.StyleWindow(this);
        FontSize = 13;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"選んだ {imageCount} 枚に付ける手動タグを入力してください"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        _box.ToolTip = DetailView.TagBoxTooltip;
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        _box.TextChanged += (_, _) => { _error.Visibility = Visibility.Collapsed; RenderCandidates(); };
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        panel.Children.Add(_candidatePanel);

        var ok = new Button { Content = "付ける", IsDefault = false, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(16, 4, 16, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        RenderCandidates();
        Loaded += (_, _) => _box.Focus();
    }

    private void Accept()
    {
        try
        {
            TagName = ManualTagName.Normalize(_box.Text);
        }
        catch (InvalidTagNameException e)
        {
            _error.Text = "手動タグの名前として使えません: " + UiText.Plain(e.Reason);
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void RenderCandidates()
    {
        _candidatePanel.Children.Clear();
        string typed = _box.Text.Trim();
        bool any = false;
        foreach (TagCandidate c in _candidates.Where(c => typed.Length == 0 || c.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)).Take(MaxShownCandidates))
        {
            if (!any) _candidatePanel.Children.Add(new TextBlock { Text = "候補:", Foreground = AppColors.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
            any = true;
            string name = c.Name;
            var button = new Button
            {
                Content = string.Create(CultureInfo.InvariantCulture, $"{c.Name} ({c.ImageCount})"),
                Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 4), ToolTip = "この手動タグの名前を入れます",
            };
            button.Click += (_, _) => { _box.Text = name; _box.CaretIndex = name.Length; _box.Focus(); };
            _candidatePanel.Children.Add(button);
        }
    }

    /// <summary>名前を聞く(モーダル)。キャンセルは null。親は、アプリのメインのウィンドウ。</summary>
    public static string? Ask(int imageCount, IReadOnlyList<TagCandidate> candidates)
    {
        var window = new TagPromptWindow(imageCount, candidates);
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.FontFamily = owner.FontFamily;
        }
        return window.ShowDialog() == true ? window.TagName : null;
    }
}
