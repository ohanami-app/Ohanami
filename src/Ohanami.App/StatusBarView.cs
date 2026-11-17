using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>下の状態の行。左: 起動の状態(起動中 / 準備完了 / 起動できなかった理由)、右: 最後の操作の結果。</summary>
internal sealed class StatusBarView : Border
{
    private readonly TextBlock _coreText = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _noticeText = new() { TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };

    public StatusBarView()
    {
        var row = new Grid { Margin = new Thickness(12, 6, 12, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_coreText, 0);
        Grid.SetColumn(_noticeText, 1);
        row.Children.Add(_coreText);
        row.Children.Add(_noticeText);
        Background = AppColors.StatusBarBackground;
        BorderBrush = AppColors.StatusBarBorder;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Child = row;
    }

    public void ShowCoreStatus(CoreStatus st)
    {
        switch (st.Phase)
        {
            case CorePhase.Starting:
                _coreText.Text = "起動中 (モデルの確認など)…";
                _coreText.ClearValue(TextBlock.ForegroundProperty);   // ふだんは色を指定しない(窓の文字色を継承)
                break;
            case CorePhase.Ready:
                // 「検索準備完了」は、検索の両方の経路が答えられるときだけ(ADR 0038。「準備完了」だけだと、インデックス作成の完了に読める)。読み込み中は「起動中」のまま
                string vectorSearch = ProcessingWords.StageName(JobKind.Embed);
                _coreText.Text = st.VectorsLoaded
                    ? $"検索準備完了 ({vectorSearch}の対象 {st.VectorCount} 枚)"
                    : $"起動中 (特徴ベクトルを読み込み中です。{vectorSearch}はまだ使えません)";
                _coreText.ClearValue(TextBlock.ForegroundProperty);   // ふだんは色を指定しない(窓の文字色を継承)
                break;
            default:
                _coreText.Text = "起動できませんでした: " + UiText.Plain(st.Reason);
                _coreText.Foreground = AppColors.Danger;
                break;
        }
        _coreText.ToolTip = _coreText.Text;
    }

    public void ShowNotice(string text)
    {
        _noticeText.Text = text;
        _noticeText.ToolTip = text;
    }

    /// <summary>テスト・確認用: 表示中の文字。</summary>
    public string CoreText => _coreText.Text;

    public string NoticeText => _noticeText.Text;
}
