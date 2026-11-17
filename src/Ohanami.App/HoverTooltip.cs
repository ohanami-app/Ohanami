using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Ohanami.App;

/// <summary>ツールチップを開くまでの待ちのタイマー(1 回だけ鳴る。<see cref="Start"/> は動かし直し)。製品は <see cref="DispatcherTooltipTimer"/>、テストは偽物。</summary>
internal interface ITooltipTimer
{
    /// <summary>待ちを始める(動いていれば、最初からやり直す)。時間が来たら <paramref name="tick"/> を 1 回呼ぶ。</summary>
    void Start(Action tick);

    /// <summary>待ちをやめる(動いていなければ何もしない)。</summary>
    void Stop();
}

/// <summary>ツールチップの表示の面(開く・閉じる)。製品は <see cref="WpfToolTipSurface"/>、テストは偽物。</summary>
internal interface ITooltipSurface
{
    void Show(string text);

    void Hide();
}

/// <summary>
/// 一覧(<see cref="ThumbnailGridControl"/>)のサムネイルの上に出すツールチップの状態の流れ(グリッドが自分で管理する。理由は ADR 0041)。
/// <para>
/// マウスが動くたびにグリッド自身の <c>ToolTip</c> に文字列を代入する方式だと、WPF のツールチップの持ち主の探索(<c>PopupControlService.OnMouseMove</c>)は
/// 「マウスの下の要素が変わったとき」にしか走らず、グリッドは 1 つの要素なので、外から入った最初の動きがサムネイルに当たったときしか、ツールチップが拾われない
/// (マウスが一覧の中にあるまま結果が差し替わると、出ない)。なので、WPF の探索に頼らず、グリッドが自分で管理する:
/// </para>
/// <list type="bullet">
/// <item>ホバーしているサムネイルが変わるたびに、前のツールチップを閉じ、待ち(<see cref="DelayMs"/> ミリ秒)を始め直す。</item>
/// <item>待ちが終わったとき、まだそのサムネイルの上にあれば、**そのとき**内容を作って(<c>contentOf</c>。差し替わった結果や、遅れて来た値も反映される)開く。内容が無ければ開かない。</item>
/// <item><see cref="Dismiss"/>(ボタン・ホイール・スクロール・キー・ウィンドウが非アクティブ・項目の入れ替え)で閉じて待ちもやめる。**同じサムネイルの上に居る間は、開き直さない**
///   (クリックのあとにマウスをわずかに動かしても、ツールチップが戻ってこない)。別のサムネイルに動けば、また待ちから始まる。</item>
/// </list>
/// マウスのホバーだけが対象で、キーボードの操作(矢印で選択を動かす)では出さない。WPF の型を直接使わず、タイマーと表示の面は差し替えられる(実際のマウス・ウィンドウなしでテストできる)。
/// </summary>
internal sealed class HoverTooltip
{
    /// <summary>ホバーしてから開くまでの待ち(ミリ秒)。WPF の既定の <c>ToolTipService.InitialShowDelay</c> と同じ。</summary>
    public const int DelayMs = 500;

    private readonly Func<int, string?> _contentOf;
    private readonly ITooltipSurface _surface;
    private readonly ITooltipTimer _timer;
    private int _index = -1;
    private bool _shown;

    /// <param name="contentOf">サムネイルの添字から、ツールチップの文字列を作る(無ければ null か空)。開く直前に呼ぶ。</param>
    public HoverTooltip(Func<int, string?> contentOf, ITooltipSurface surface, ITooltipTimer timer)
    {
        ArgumentNullException.ThrowIfNull(contentOf);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(timer);
        _contentOf = contentOf;
        _surface = surface;
        _timer = timer;
    }

    /// <summary>いま開いているか。</summary>
    public bool IsShown => _shown;

    /// <summary>いま、ホバーしている(待っている・開いている)サムネイルの添字(無ければ -1)。</summary>
    public int HoverIndex => _index;

    /// <summary>ホバーしているサムネイルが変わった(<paramref name="index"/> が -1 なら、どのサムネイルの上でもない)。前のを閉じて、待ちを始め直す。</summary>
    public void Hover(int index)
    {
        _timer.Stop();
        HideIfShown();
        _index = index;
        if (index >= 0) _timer.Start(OnTimerTick);
    }

    /// <summary>
    /// 閉じて、待ちもやめる(ボタン・ホイール・スクロール・キー・ウィンドウが非アクティブ)。ホバーの添字は覚えておく(同じサムネイルの上では、開き直さない。
    /// 呼び出し側は、同じ添字の <see cref="Hover"/> を呼ばない=グリッドの SetHover は、添字が同じなら何もしない)。
    /// </summary>
    public void Dismiss()
    {
        _timer.Stop();
        HideIfShown();
    }

    private void OnTimerTick()
    {
        if (_index < 0 || _shown) return;
        string? text = _contentOf(_index);
        if (string.IsNullOrEmpty(text)) return;
        _surface.Show(text);
        _shown = true;
    }

    private void HideIfShown()
    {
        if (!_shown) return;
        _shown = false;
        _surface.Hide();
    }
}

/// <summary>製品のタイマー(<see cref="DispatcherTimer"/>。UI のスレッドで鳴る)。</summary>
internal sealed class DispatcherTooltipTimer : ITooltipTimer
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(HoverTooltip.DelayMs) };
    private Action? _tick;

    public DispatcherTooltipTimer()
    {
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();   // 1 回だけ
            _tick?.Invoke();
        };
    }

    public void Start(Action tick)
    {
        _tick = tick;
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}

/// <summary>
/// 製品の表示の面: 1 つの <see cref="ToolTip"/> をマウスの位置の近くに開く(<see cref="PlacementMode.Mouse"/>)。WPF の <c>ToolTipService</c> は使わない(持ち主の探索に頼らない)。
/// マウスの上に重ならないので、マウスの動き・クリックを邪魔しない(<c>IsHitTestVisible</c> は false)。
/// </summary>
internal sealed class WpfToolTipSurface : ITooltipSurface
{
    private readonly FrameworkElement _owner;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse, IsHitTestVisible = false };

    public WpfToolTipSurface(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public void Show(string text)
    {
        _tip.Content = text;
        _tip.PlacementTarget = _owner;
        _tip.IsOpen = true;
    }

    public void Hide() => _tip.IsOpen = false;
}
