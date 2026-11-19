using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>偽のタイマー(<see cref="Fire"/> で、待ちが終わったことにする)。実際の時間を待たない。</summary>
internal sealed class FakeTooltipTimer : ITooltipTimer
{
    private Action? _tick;

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public bool Running => _tick is not null;

    public void Start(Action tick) { Starts++; _tick = tick; }

    public void Stop() { Stops++; _tick = null; }

    /// <summary>待ちが終わったことにする(動いていなければ何もしない)。</summary>
    public void Fire()
    {
        Action? tick = _tick;
        _tick = null;
        tick?.Invoke();
    }
}

/// <summary>偽の表示の面(開いた文字列と、閉じた回数を覚える)。本物の ToolTip もウィンドウも使わない。</summary>
internal sealed class FakeTooltipSurface : ITooltipSurface
{
    public List<string> Shown { get; } = [];

    public int Hides { get; private set; }

    public bool IsOpen { get; private set; }

    public string? Text { get; private set; }

    public void Show(string text) { Shown.Add(text); IsOpen = true; Text = text; }

    public void Hide() { Hides++; IsOpen = false; Text = null; }
}

/// <summary>
/// 一覧のホバーのツールチップ(ADR 0041): グリッドが自分で管理する(WPF の ToolTipService の持ち主の探索に頼らない)。待ち → そのとき内容を作って開く → 閉じる条件、
/// 同じサムネイルの上では開き直さない、結果の差し替え(マウスが動かないまま)のあとでも出ること、検索の結果と新しい順の一覧の両方。
/// タイマーと表示の面は偽物で、実際のマウス・ウィンドウは使わない(マウスのイベントを送る確認は、目で見て行う)。
/// </summary>
public sealed class HoverTooltipTests
{
    private static string H(char c) => new(c, 64);

    // ---------------------------------------------------------------- 状態の流れ(HoverTooltip)

    private static (HoverTooltip Tip, FakeTooltipSurface Surface, FakeTooltipTimer Timer, List<int> Asked) New(Func<int, string?>? content = null)
    {
        var surface = new FakeTooltipSurface();
        var timer = new FakeTooltipTimer();
        var asked = new List<int>();
        var tip = new HoverTooltip(i => { asked.Add(i); return content is null ? "tip " + i : content(i); }, surface, timer);
        return (tip, surface, timer, asked);
    }

    [Fact]
    public void Hovering_waits_and_only_then_builds_the_content_and_opens_it()
    {
        var (tip, surface, timer, asked) = New();

        tip.Hover(3);

        Assert.True(timer.Running);
        Assert.Empty(surface.Shown);          // 待ちの間は出さない
        Assert.Empty(asked);                   // 内容も、開く直前まで作らない(差し替わった結果や、遅れて来た値を反映するため)
        Assert.Equal(3, tip.HoverIndex);

        timer.Fire();

        Assert.Equal(["tip 3"], surface.Shown);
        Assert.Equal([3], asked);
        Assert.True(tip.IsShown);
        Assert.False(timer.Running);
    }

    [Fact]
    public void The_content_is_built_at_the_moment_it_opens_so_a_value_that_arrived_during_the_wait_is_used()
    {
        string text = "before";
        var (tip, surface, timer, _) = New(_ => text);

        tip.Hover(0);
        text = "after (文字の索引の引き込みが済んだ)";
        timer.Fire();

        Assert.Equal(["after (文字の索引の引き込みが済んだ)"], surface.Shown);
    }

    [Fact]
    public void Moving_to_another_thumbnail_closes_the_open_tip_and_starts_the_wait_again()
    {
        var (tip, surface, timer, _) = New();
        tip.Hover(1);
        timer.Fire();
        Assert.True(surface.IsOpen);

        tip.Hover(2);

        Assert.False(surface.IsOpen);          // 前のサムネイルのツールチップは、すぐ閉じる
        Assert.True(timer.Running);            // 新しい待ち
        Assert.False(tip.IsShown);
        timer.Fire();
        Assert.Equal(["tip 1", "tip 2"], surface.Shown);
    }

    [Fact]
    public void Leaving_every_thumbnail_closes_and_stops_the_wait()
    {
        var (tip, surface, timer, _) = New();
        tip.Hover(1);
        timer.Fire();

        tip.Hover(-1);

        Assert.False(surface.IsOpen);
        Assert.False(timer.Running);
        Assert.Equal(-1, tip.HoverIndex);
        timer.Fire();
        Assert.Single(surface.Shown);          // 待ちは止まっているので、これ以上出ない
    }

    [Fact]
    public void A_pending_wait_is_cancelled_when_the_mouse_leaves_before_it_ends()
    {
        var (tip, surface, timer, asked) = New();
        tip.Hover(1);

        tip.Hover(-1);
        timer.Fire();

        Assert.Empty(surface.Shown);
        Assert.Empty(asked);
    }

    [Fact]
    public void A_thumbnail_without_content_opens_nothing()
    {
        var (tip, surface, timer, _) = New(i => i == 0 ? null : i == 1 ? "" : "x");

        tip.Hover(0);
        timer.Fire();
        tip.Hover(1);
        timer.Fire();

        Assert.Empty(surface.Shown);
        Assert.False(tip.IsShown);
    }

    [Fact]
    public void Dismiss_closes_stops_the_wait_and_is_safe_when_nothing_is_open()
    {
        var (tip, surface, timer, _) = New();
        tip.Dismiss();                         // 何も無くても安全
        Assert.Equal(0, surface.Hides);

        tip.Hover(1);
        timer.Fire();
        tip.Dismiss();
        Assert.False(surface.IsOpen);

        tip.Hover(2);                          // 待ちの途中で閉じる
        tip.Dismiss();
        timer.Fire();
        Assert.Single(surface.Shown);          // 閉じたあとは、待ちが終わっても開かない
        Assert.Equal(2, tip.HoverIndex);       // ホバーの添字は覚えている(同じサムネイルでは、開き直さない。グリッドの SetHover が同じ添字を無視する)
    }

    [Fact]
    public void A_timer_that_fires_twice_or_after_the_wait_was_replaced_opens_once()
    {
        var (tip, surface, timer, _) = New();
        tip.Hover(1);
        timer.Fire();
        timer.Fire();
        Assert.Single(surface.Shown);
    }

    [Fact]
    public void Constructor_arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => new HoverTooltip(null!, new FakeTooltipSurface(), new FakeTooltipTimer()));
        Assert.Throws<ArgumentNullException>(() => new HoverTooltip(_ => "", null!, new FakeTooltipTimer()));
        Assert.Throws<ArgumentNullException>(() => new HoverTooltip(_ => "", new FakeTooltipSurface(), null!));
        Assert.Equal(500, HoverTooltip.DelayMs);   // ToolTipService.InitialShowDelay の既定と同じ
    }

    // ---------------------------------------------------------------- グリッドにつないだ流れ

    private static (ThumbnailGridControl Grid, FakeTooltipSurface Surface, FakeTooltipTimer Timer) NewGrid(int items = 50)
    {
        var grid = new ThumbnailGridControl(256, 144, 8, 2, _ => []);
        var surface = new FakeTooltipSurface();
        var timer = new FakeTooltipTimer();
        grid.UseTooltipParts(surface, timer);
        grid.TooltipOf = item => "tip " + item.Hash[0];
        grid.SetItems([.. Enumerable.Range(0, items).Select(i => new GridItem(new string((char)('a' + i % 26), 64)))]);
        return (grid, surface, timer);
    }

    [Fact]
    public async Task The_grid_opens_the_tooltip_of_the_hovered_thumbnail_without_touching_the_ToolTip_property()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();

            grid.SetHover(2);
            timer.Fire();

            Assert.Equal(["tip c"], surface.Shown);
            Assert.Null(grid.ToolTip);          // WPF の ToolTipService の持ち主の探索に頼らない(これが、一覧のツールチップが出なかった原因)
        });
    }

    [Fact]
    public async Task Hovering_the_same_thumbnail_again_does_not_restart_the_wait_but_another_one_does()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();
            grid.SetHover(2);
            int starts = timer.Starts;

            grid.SetHover(2);                   // マウスが同じサムネイルの上を動いた(OnMouseMove は毎回 SetHover を呼ぶ)
            Assert.Equal(starts, timer.Starts);

            timer.Fire();
            grid.SetHover(2);
            Assert.Single(surface.Shown);       // 開いたあと、同じサムネイルの上では開き直さない
            Assert.True(surface.IsOpen);

            grid.SetHover(3);
            Assert.False(surface.IsOpen);
            Assert.Equal(starts + 1, timer.Starts);
        });
    }

    [Fact]
    public async Task Replacing_the_items_under_a_still_mouse_closes_the_tooltip_and_the_next_move_over_the_same_cell_opens_one_again()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();
            grid.SetHover(2);
            timer.Fire();
            Assert.True(surface.IsOpen);

            grid.SetItems([.. Enumerable.Range(0, 10).Select(i => new GridItem(new string((char)('p' + i), 64)))]);   // 検索の結果の差し替え。マウスは動いていない

            Assert.False(surface.IsOpen);       // 古い項目のツールチップは閉じる
            Assert.False(timer.Running);
            grid.SetHover(2);                   // マウスがわずかに動いて、同じ位置(添字 2)の新しい項目の上になった
            Assert.True(timer.Running);         // 新しい項目のツールチップの待ちが始まる(WPF の探索は、要素が変わらないと走らないので、頼らない)
            timer.Fire();
            Assert.Equal("tip r", surface.Text);
        });
    }

    [Fact]
    public async Task Scrolling_closes_the_tooltip_and_does_not_reopen_it_over_the_same_index()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();
            grid.SetHover(2);
            timer.Fire();

            grid.Offset = 200;                  // スクロール(バー・キー・ホイールのどれでも、Offset を通る)

            Assert.True(grid.Offset > 0);
            Assert.False(surface.IsOpen);
            Assert.False(timer.Running);
            grid.SetHover(2);                   // 添字が同じなら、何も起きない(次にマウスが別のサムネイルへ動くまで)
            Assert.False(timer.Running);
            grid.SetHover(5);
            Assert.True(timer.Running);
        });
    }

    [Fact]
    public async Task Dismissing_for_a_press_a_key_or_the_window_going_inactive_closes_and_waits_for_another_thumbnail()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();
            grid.SetHover(2);
            timer.Fire();

            grid.DismissTooltip();

            Assert.False(surface.IsOpen);
            grid.SetHover(2);                   // クリックのあとでマウスをわずかに動かしても、ツールチップは戻ってこない
            Assert.False(timer.Running);
            grid.SetHover(4);
            Assert.True(timer.Running);
            timer.Fire();
            Assert.Equal("tip e", surface.Text);
        });
    }

    [Fact]
    public async Task Leaving_the_grid_closes_the_tooltip()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid();
            grid.SetHover(2);
            timer.Fire();

            grid.SetHover(-1);                  // OnMouseLeave が呼ぶ

            Assert.False(surface.IsOpen);
            grid.SetHover(2);                   // 入り直した(同じ添字でも、外に出たので、待ちから始まる)
            Assert.True(timer.Running);
        });
    }

    [Fact]
    public async Task A_tooltip_that_cannot_be_built_because_the_item_is_gone_opens_nothing()
    {
        await Sta.RunAsync(() =>
        {
            var (grid, surface, timer) = NewGrid(3);
            grid.SetHover(2);
            grid.TooltipOf = _ => null;         // 項目の内容が引けなくなった(結果が差し替わった直後など)

            timer.Fire();

            Assert.Empty(surface.Shown);
        });
    }

    // ---------------------------------------------------------------- 検索の結果と新しい順の一覧の両方

    private static ResultsPanel NewPanel(out FakeTooltipSurface surface, out FakeTooltipTimer timer)
    {
        var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell())
        {
            DisplayZone = TimeZoneInfo.Utc,
            SearchFunc = (q, _) => Task.FromResult(new SearchResponse(q.Text, [new SearchResult(H('a'), [@"C:\shots\a.png"], 1, 0.5, [new PathHit("visual", "m", 1, 0.2, new VisualDetail(0.2, 0.2))])], [], 1, 1, TimeSpan.Zero)),
            RecentFunc = (_, _) => Task.FromResult(new RecentResponse([new RecentImage(H('b'), [@"C:\shots\b.png"], new DateTimeOffset(2026, 11, 10, 12, 5, 0, TimeSpan.Zero))], 1, TimeSpan.Zero)),
        };
        surface = new FakeTooltipSurface();
        timer = new FakeTooltipTimer();
        panel.ResultGrid.UseTooltipParts(surface, timer);
        return panel;
    }

    [Fact]
    public async Task Both_a_search_result_and_the_recent_list_get_their_tooltips_through_the_same_path()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            ResultsPanel panel = NewPanel(out FakeTooltipSurface surface, out FakeTooltipTimer timer);

            await panel.ShowRecentAsync();
            panel.ResultGrid.SetHover(0);
            timer.Fire();
            Assert.StartsWith("更新: 2026-11-10 12:05\n", surface.Text, StringComparison.Ordinal);       // 新しい順の一覧

            panel.SearchBox.Text = "猫";
            panel.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (panel.ResultGrid.Items.Count == 0 || panel.ResultGrid.Items[0].Hash != H('a'))
            {
                if (sw.ElapsedMilliseconds > 5000) throw new TimeoutException("検索の結果が出ない");
                await Task.Delay(10);
            }
            Assert.False(surface.IsOpen);                                                                    // 結果が差し替わって、前のツールチップは閉じた

            panel.ResultGrid.SetHover(0);                                                                    // 結果が差し替わった直後、同じ添字(0)の上でも出る
            timer.Fire();
            Assert.StartsWith("特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)\n統合: 1 位", surface.Text, StringComparison.Ordinal);            // 検索の結果(理由が先頭)
            Assert.Null(panel.ResultGrid.ToolTip);
        });
    }
}
