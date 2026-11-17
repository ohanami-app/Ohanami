using System;
using System.Collections.Generic;

namespace Ohanami.App;

/// <summary>クリックの結果(<see cref="GridSelectionRules.Click"/>)。<paramref name="Selected"/> は選択のあとの添字の集合、<paramref name="Anchor"/> は範囲選択の起点(無ければ -1)。</summary>
internal readonly record struct ClickOutcome(IReadOnlySet<int> Selected, int Anchor, bool PendingSingleSelect, bool Changed);

/// <summary>
/// サムネイルの一覧の、クリックによる選択の規則(エクスプローラーと同じ)。**純粋な関数**(WPF もマウスも使わない)。ThumbnailGridControl のマウスのハンドラと、
/// 試験用の入口(<see cref="ThumbnailGridControl.ApplyClick"/>。ShellBoundaryGuardTests がマウスのイベントの合成を禁じているので、試験は規則に直接当てる)が使う。
/// <list type="bullet">
/// <item>起点(アンカー) = 最後に、普通のクリックか Ctrl+クリックで押した項目。Shift+クリックでは動かない(続けて Shift+クリックすると、同じ起点から範囲が替わる)。</item>
/// <item>普通のクリック: 押した項目だけを選ぶ。すでに選んでいる複数のうちの 1 つを押したときは、押しただけでは選択を変えず(群ごとドラッグできる)、離したとき単独選択にする(<see cref="ClickOutcome.PendingSingleSelect"/>)。</item>
/// <item>Ctrl+クリック: その項目の選択を切り替える。起点はその項目になる(外したときも)。</item>
/// <item>Shift+クリック: 起点からクリックした項目までを選ぶ(今の選択は捨てる)。範囲は一覧の並び順(添字の小さい方から大きい方まで)。起点が無いときは、普通のクリックと同じ(その項目が新しい起点)。</item>
/// <item>Ctrl+Shift+クリック: 起点からクリックした項目までを、今の選択に足す(外しはしない)。起点は動かない。起点が無いときは、その項目を足してそれを起点にする。</item>
/// <item>空きの場所(index &lt; 0): 何も押していなければ選択を全部外す。Ctrl・Shift を押しているときは何も変えない(うっかり選択を失わない)。起点はどの場合も変えない。</item>
/// </list>
/// </summary>
internal static class GridSelectionRules
{
    public static ClickOutcome Click(IReadOnlySet<int> selected, int anchor, int index, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (index < 0)
        {
            return !ctrl && !shift && selected.Count > 0
                ? new ClickOutcome(new HashSet<int>(), anchor, false, true)
                : new ClickOutcome(selected, anchor, false, false);
        }

        if (shift)
        {
            if (anchor < 0)
            {
                // 起点が無い: 普通のクリックと同じ(Ctrl+Shift なら足すだけ)。その項目が新しい起点
                var one = ctrl ? new HashSet<int>(selected) { index } : new HashSet<int> { index };
                return new ClickOutcome(one, index, false, !one.SetEquals(selected));
            }
            var range = ctrl ? new HashSet<int>(selected) : new HashSet<int>();
            for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) range.Add(i);
            return new ClickOutcome(range, anchor, false, !range.SetEquals(selected));
        }

        if (ctrl)
        {
            var toggled = new HashSet<int>(selected);
            if (!toggled.Remove(index)) toggled.Add(index);
            return new ClickOutcome(toggled, index, false, true);
        }

        if (!selected.Contains(index)) return new ClickOutcome(new HashSet<int> { index }, index, false, true);
        // すでに選んでいる項目を押した: 複数なら、離したときに単独選択にする(それまでは群のまま)
        return new ClickOutcome(selected, index, selected.Count > 1, false);
    }
}
