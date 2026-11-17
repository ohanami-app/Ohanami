using System.Collections.Generic;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class TooltipTests
{
    [Fact]
    public void Tooltip_shows_fused_rank_per_path_rank_and_score_and_the_first_path()
    {
        var result = new SearchResult(
            new string('a', 64),
            ["C:\\shots\\one.png", "D:\\copy\\one.png", "E:\\other\\one.png"],
            Rank: 23.5,
            FusedScore: 0.016129,
            Hits: [new PathHit("visual", "model-1", 23.5, 0.1234, new VisualDetail(0.1234))]);

        string text = ResultsPanel.BuildTooltip(result);

        Assert.Contains("統合: 23.5 位", text, System.StringComparison.Ordinal);                          // 詳細画面と同じ書き方(順位は「n 位」、スコアは出さない)
        Assert.Contains($"{TestWords.VisualWithModel}: 23.5 位 (類似度 0.1234)", text, System.StringComparison.Ordinal);   // 経路名は日本語、類似度は cosine と書かない
        Assert.DoesNotContain("visual", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("cosine", text, System.StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Enter かダブルクリックで詳細を開けます", text, System.StringComparison.Ordinal);
        Assert.Contains("C:\\shots\\one.png", text, System.StringComparison.Ordinal);
        Assert.Contains("ほか 2 件のパス", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Integer_ranks_have_no_decimal_point_and_a_single_path_has_no_extra_line()
    {
        var result = new SearchResult("h", ["C:\\a.png"], 3, 0.5, new List<PathHit>());

        string text = ResultsPanel.BuildTooltip(result);

        Assert.StartsWith("統合: 3 位\n", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ほか", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Building_a_tooltip_needs_no_ui_thread()
    {
        // WPF の型の静的メンバーを呼ぶだけで、要素は作らない。STA でないスレッド(スレッドプール)でも動く(テストがどのスレッドで走っても同じ)
        var result = new SearchResult("h", ["C:\a.png"], 2, 0.5, new List<PathHit>());

        string text = await System.Threading.Tasks.Task.Run(() => ResultsPanel.BuildTooltip(result), TestContext.Current.CancellationToken);

        Assert.StartsWith("統合: 2 位\n", text, System.StringComparison.Ordinal);
    }
}
