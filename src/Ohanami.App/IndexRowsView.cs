using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 左パネルの「インデックス」欄の**固定の 7 行**を並べる部品(欄の高さを、どの状態でも同じにする。ADR 0039)。
/// 行の中身は <see cref="IndexRows"/>(<see cref="IndexStatusText.BuildRows"/>)。どの行も、1 行の高さを固定し(<see cref="LineHeightPx"/>。行ごとの文字の違いで、1px も変わらない)、
/// 折り返さず、入りきらなければ末尾を「…」で切る。切れた文の全文は、その行のヒントで見る。空の行は空のまま、高さを保つ。
/// 行の並び: 特徴ベクトル検索の件数・内訳 / テキスト検索の件数・内訳 / 状態 / 速さと残り / 補足。
/// </summary>
internal sealed class IndexRowsView : StackPanel
{
    /// <summary>1 行の高さ(DIP)。文字の高さ(Yu Gothic UI 13 で約 17.3)より少し大きい固定値。フォントの違い(外字の代替フォントなど)で行の高さが動かないよう、行の高さを固定する。</summary>
    public const double LineHeightPx = 18;

    /// <summary>内訳の行・補足の行(副の情報)の高さ。文字を 12 にして灰色にし、件数の行・状態の行より控えめにする。</summary>
    public const double SecondaryLineHeightPx = 16;

    /// <summary>内訳の行(「未作成 218、失敗 1、要再作成 2」)の字下げ(件数の行より少し右)。</summary>
    private const double ExtraIndent = 12;

    private readonly TextBlock _stage1Count = Row(0, secondary: false), _stage1Extra = Row(ExtraIndent, secondary: true), _stage2Count = Row(0, secondary: false), _stage2Extra = Row(ExtraIndent, secondary: true);
    private readonly TextBlock _state = Row(0, secondary: false), _progress = Row(0, secondary: false), _note = Row(0, secondary: true);

    public IndexRowsView()
    {
        foreach (TextBlock row in Rows) Children.Add(row);
        Show(IndexRows.Empty);
    }

    /// <summary>7 行(上から)。</summary>
    public IReadOnlyList<TextBlock> Rows => [_stage1Count, _stage1Extra, _stage2Count, _stage2Extra, _state, _progress, _note];

    private static TextBlock Row(double indent, bool secondary)
    {
        double height = secondary ? SecondaryLineHeightPx : LineHeightPx;
        var row = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = height,
            Height = height,
            Margin = new Thickness(indent, 0, 0, 0),
        };
        if (secondary)
        {
            row.FontSize = 12;
            row.Foreground = AppColors.Secondary;
        }
        return row;
    }

    /// <summary>行の中身を替える。ヒント: 段階の行は正確な技術の名前(<see cref="ProcessingWords.StageHint"/>)、ほかは、その行の全文(補足の行は、同時にあるもの全部)。</summary>
    public void Show(IndexRows rows)
    {
        string embedHint = ProcessingWords.StageHint(JobKind.Embed);
        string vlmHint = ProcessingWords.StageHint(JobKind.Vlm);
        Set(_stage1Count, rows.Stage1Count, embedHint);
        Set(_stage1Extra, rows.Stage1Extra, embedHint);
        Set(_stage2Count, rows.Stage2Count, vlmHint);
        Set(_stage2Extra, rows.Stage2Extra, vlmHint);
        Set(_state, rows.State, rows.StateFull);
        Set(_progress, rows.Progress, rows.Progress);
        Set(_note, rows.Note, rows.NoteFull);
    }

    private static void Set(TextBlock block, string text, string tip)
    {
        block.Text = text;
        block.ToolTip = text.Length == 0 ? null : tip;   // 空の行にヒントは出さない
    }

    /// <summary>テスト・確認用: 7 行の文字(上から)。</summary>
    public IReadOnlyList<string> Texts => Rows.Select(r => r.Text).ToList();
}
