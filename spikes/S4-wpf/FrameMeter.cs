using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace S4Wpf;

/// <summary>1フレームぶんの記録。「間隔」は直前の Rendering イベントからの時間。</summary>
public sealed class FrameRec
{
    public double TMs;              // ベンチ開始からの時刻
    public double IntervalMs;       // RenderingTime の差
    public double WallMs;           // Stopwatch の差(参考。取れなければ NaN)
    public double Offset;
    public double PrevHandlerMs;    // 直前のフレームの Rendering ハンドラ(シナリオ+グリッド更新)の所要時間
    public double RenderMs;         // 直前のイベントからこのイベントまでの間の OnRender(描画コマンドの記録)の所要時間
    public int Drained;             // その間に取り込んだデコード結果の数
    public int Gc0, Gc1, Gc2;       // その間に起きた GC の回数
    public bool Startup;            // 立ち上がり(最初の 30 フレームまたは 0.5 秒)
}

/// <summary>1フェーズ(例: pass-down)ぶんの記録。</summary>
public sealed class PhaseData
{
    public string Name = "";
    public List<FrameRec> Frames = new();
    public List<double> UiLagMs = new();           // UI スレッドの応答遅延(heartbeat)
    public List<double> TtfpMs = new();            // 画面に入ってから画像が出るまで
    public List<double> JumpSettleMs = new();      // jump 後、見えているセルが全部そろうまで
    public double DurationMs;
    public long StartTs;
}

/// <summary>
/// フレーム時間の記録。CompositionTarget.Rendering の RenderingTime の間隔を主指標にする。
/// 同じ RenderingTime で複数回呼ばれることがあるので、重複は数えず、回数だけ記録する。
/// 立ち上がり(最初の 30 フレームまたは最初の 0.5 秒)は判定から外し、別に数える。
/// 遅いフレームは「ハンドラ(更新)・OnRender(描画の記録)・GC」のどれが原因かを大まかに分類する。
/// </summary>
public sealed class FrameMeter
{
    private const int StartupFrames = 30;
    private const double StartupMs = 500;

    private readonly List<PhaseData> _phases = new();
    private PhaseData? _cur;
    private TimeSpan _lastRendering = TimeSpan.MinValue;
    private long _lastWallTs;
    private long _duplicateRenderingEvents;
    private long _totalRenderingEvents;
    private long _frameNo;
    private double _firstFrameMs = -1;
    private int _prevGc0, _prevGc1, _prevGc2;

    private readonly StringBuilder _frameCsv = new();
    private readonly StringBuilder _secondCsv = new();
    private double _lastSecondMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _proc = Process.GetCurrentProcess();
    private int _gc0, _gc1, _gc2;
    private double _maxWorkingSetMiB, _maxPrivateMiB, _startWorkingSetMiB;
    private readonly List<string> _notes = new();

    // UI スレッドの応答遅延の計測(別スレッドが Dispatcher に投げ、実行されるまでの時間を測る)
    private Thread? _lagThread;
    private volatile bool _lagStop;
    private volatile bool _lagPending;

    public FrameMeter()
    {
        _frameCsv.AppendLine("phase,t_ms,interval_ms,wall_interval_ms,startup,offset,prev_handler_ms,render_ms,drained,gc0,gc1,gc2,inflight,lru_count,loaded_total");
        _secondCsv.AppendLine("t_s,phase,working_set_mib,private_mib,gc0,gc1,gc2,lru_count,inflight,loaded_total");
        _proc.Refresh();
        _startWorkingSetMiB = _proc.WorkingSet64 / 1048576.0;
        _gc0 = _prevGc0 = GC.CollectionCount(0);
        _gc1 = _prevGc1 = GC.CollectionCount(1);
        _gc2 = _prevGc2 = GC.CollectionCount(2);
    }

    public bool Recording => _cur != null;
    public string CurrentPhase => _cur?.Name ?? "";
    public IReadOnlyList<PhaseData> Phases => _phases;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public void AddNote(string note) { _notes.Add(note); }

    public void BeginPhase(string name)
    {
        EndPhase();
        _cur = new PhaseData { Name = name, StartTs = Stopwatch.GetTimestamp() };
        _phases.Add(_cur);
        _lastRendering = TimeSpan.MinValue;   // フェーズ境界をまたぐ間隔は記録しない
        _lastWallTs = 0;
    }

    public void EndPhase()
    {
        if (_cur != null)
        {
            _cur.DurationMs = (Stopwatch.GetTimestamp() - _cur.StartTs) * 1000.0 / Stopwatch.Frequency;
            _cur = null;
        }
    }

    public void AddTtfp(double ms) { _cur?.TtfpMs.Add(ms); }
    public void AddJumpSettle(double ms) { _cur?.JumpSettleMs.Add(ms); }

    public void StartLagProbe(Dispatcher dispatcher)
    {
        _lagStop = false;
        _lagThread = new Thread(() =>
        {
            while (!_lagStop)
            {
                if (!_lagPending)
                {
                    _lagPending = true;
                    long posted = Stopwatch.GetTimestamp();
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        double lag = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
                        _cur?.UiLagMs.Add(lag);
                        _lagPending = false;
                    }));
                }
                Thread.Sleep(2);
            }
        }) { IsBackground = true, Name = "ui-lag-probe" };
        _lagThread.Start();
    }

    public void StopLagProbe()
    {
        _lagStop = true;
        _lagThread?.Join(1000);
    }

    /// <summary>Rendering イベントごとに呼ぶ(UI スレッド)。</summary>
    public void OnRendering(TimeSpan renderingTime, double offset, int inflight, int lruCount, long loadedTotal,
        double prevHandlerMs, double renderMsSincePrev, int drainedSincePrev)
    {
        _totalRenderingEvents++;
        if (renderingTime == _lastRendering)
        {
            _duplicateRenderingEvents++;
            return;
        }
        double nowMs = NowMs;
        _frameNo++;
        if (_firstFrameMs < 0) _firstFrameMs = nowMs;
        bool startup = _frameNo <= StartupFrames || nowMs - _firstFrameMs < StartupMs;

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        int d0 = g0 - _prevGc0, d1 = g1 - _prevGc1, d2 = g2 - _prevGc2;
        _prevGc0 = g0; _prevGc1 = g1; _prevGc2 = g2;

        long wallTs = Stopwatch.GetTimestamp();
        if (_cur != null && _lastRendering != TimeSpan.MinValue)
        {
            double interval = (renderingTime - _lastRendering).TotalMilliseconds;
            double wall = _lastWallTs == 0 ? double.NaN : (wallTs - _lastWallTs) * 1000.0 / Stopwatch.Frequency;
            var rec = new FrameRec
            {
                TMs = nowMs, IntervalMs = interval, WallMs = wall, Offset = offset,
                PrevHandlerMs = prevHandlerMs, RenderMs = renderMsSincePrev, Drained = drainedSincePrev,
                Gc0 = d0, Gc1 = d1, Gc2 = d2, Startup = startup,
            };
            _cur.Frames.Add(rec);
            _frameCsv.Append(_cur.Name).Append(',')
                .Append(nowMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(interval.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(double.IsNaN(wall) ? "" : wall.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(startup ? "1" : "0").Append(',')
                .Append(offset.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                .Append(prevHandlerMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(renderMsSincePrev.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(drainedSincePrev.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(d0.ToString(CultureInfo.InvariantCulture)).Append(',').Append(d1.ToString(CultureInfo.InvariantCulture)).Append(',').Append(d2.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(inflight.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(lruCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(loadedTotal.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        _lastRendering = renderingTime;
        _lastWallTs = wallTs;

        if (nowMs - _lastSecondMs >= 1000)
        {
            _lastSecondMs = nowMs;
            _proc.Refresh();
            double ws = _proc.WorkingSet64 / 1048576.0;
            double priv = _proc.PrivateMemorySize64 / 1048576.0;
            _maxWorkingSetMiB = Math.Max(_maxWorkingSetMiB, ws);
            _maxPrivateMiB = Math.Max(_maxPrivateMiB, priv);
            _secondCsv.Append((nowMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                .Append(CurrentPhase).Append(',')
                .Append(ws.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(priv.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(g0).Append(',').Append(g1).Append(',').Append(g2).Append(',')
                .Append(lruCount).Append(',').Append(inflight).Append(',').Append(loadedTotal).AppendLine();
        }
    }

    // ---------------------------------------------------------------- 出力

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        double pos = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static string Stat(IEnumerable<double> values, string unit = "ms")
    {
        double[] a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (a.Length == 0) return "(なし)";
        return string.Create(CultureInfo.InvariantCulture,
            $"n={a.Length} mean={a.Average():0.00} p50={Percentile(a, 50):0.00} p95={Percentile(a, 95):0.00} p99={Percentile(a, 99):0.00} max={a[^1]:0.00} {unit}");
    }

    /// <summary>フレーム間隔の要約と、合格線(案)での判定を書く。nominalHz はモニタのリフレッシュレート(参考表示)。</summary>
    public string BuildSummary(string header, LoaderStats? loader, BenchThresholds th, string kind, double nominalHz)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Rendering イベント: 全 {_totalRenderingEvents} 回(同じ RenderingTime での重複 {_duplicateRenderingEvents} 回は間隔の計算から除いた)"));
        sb.AppendLine($"立ち上がり(最初の {StartupFrames} フレームまたは最初の {StartupMs:0} ミリ秒)のフレームは判定・統計から外し、フェーズごとに別に数える。");
        foreach (PhaseData p in _phases)
        {
            List<FrameRec> main = p.Frames.Where(f => !f.Startup).ToList();
            List<FrameRec> startup = p.Frames.Where(f => f.Startup).ToList();
            sb.AppendLine();
            sb.AppendLine($"=== フェーズ: {p.Name}(経過 {p.DurationMs / 1000:0.0} 秒)===");
            if (startup.Count > 0)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"立ち上がりのフレーム(判定から除外): {startup.Count} 個、最大 {startup.Max(f => f.IntervalMs):0.0} ms、50ms超 {startup.Count(f => f.IntervalMs > 50)} 個"));
            double[] iv = main.Select(f => f.IntervalMs).OrderBy(v => v).ToArray();
            sb.AppendLine("フレーム間隔(RenderingTime の差): " + Stat(iv));
            sb.AppendLine("フレーム間隔(Stopwatch の差、参考): " + Stat(main.Select(f => f.WallMs)));
            if (iv.Length > 0)
            {
                foreach (double thr in new[] { 25.0, 33.0, 50.0, 100.0 })
                {
                    int c = iv.Count(v => v > thr);
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  (参考・絶対値) > {thr:0} ms のフレーム: {c} 個 ({100.0 * c / iv.Length:0.00}%)"));
                }
                double t = Percentile(iv, 50);
                double nominal = nominalHz > 0 ? 1000.0 / nominalHz : double.NaN;
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  周期 T = このフェーズの p50 = {t:0.00} ms(モニタの公称周期 {nominal:0.00} ms、T/公称 = {t / nominal:0.00})"));
                if (t > nominal * 1.5)
                    sb.AppendLine("  注意: p50 自体が公称周期の 1.5 倍を超えている=全体が遅く、T が水増しされている。T に対する判定は甘くなる");
                foreach (double mul in new[] { 1.5, 2.0, 6.0 })
                {
                    int c = iv.Count(v => v > mul * t);
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  > {mul:0.0}T ({mul * t:0.0} ms) のフレーム: {c} 個 ({100.0 * c / iv.Length:0.00}%)"));
                }
                AppendSlowFrameAnalysis(sb, main, t);
            }
            sb.AppendLine("UI スレッドの応答遅延(heartbeat。解像度は Sleep(2) 相当で粗い): " + Stat(p.UiLagMs));
            if (p.TtfpMs.Count > 0) sb.AppendLine("画面に入ってから画像が出るまで(フレーム単位の精度): " + Stat(p.TtfpMs));
            if (p.JumpSettleMs.Count > 0) sb.AppendLine("jump 後、見えているセルが全部そろうまで: " + Stat(p.JumpSettleMs));
            if (iv.Length > 0 && kind != "rate") sb.AppendLine(Judge(p, iv, th, kind));
        }
        foreach (string note in _notes) sb.AppendLine("注記: " + note);
        sb.AppendLine();
        sb.AppendLine($"ワーキングセット: 開始 {_startWorkingSetMiB:0} MiB / 最大 {_maxWorkingSetMiB:0} MiB(増分 {_maxWorkingSetMiB - _startWorkingSetMiB:0} MiB)、プライベート最大 {_maxPrivateMiB:0} MiB");
        sb.AppendLine($"GC 回数(開始以降): gen0={GC.CollectionCount(0) - _gc0} gen1={GC.CollectionCount(1) - _gc1} gen2={GC.CollectionCount(2) - _gc2}");
        if (_maxWorkingSetMiB - _startWorkingSetMiB > th.MaxWorkingSetGrowthMiB)
            sb.AppendLine($"判定(案): ワーキングセットの増分が {th.MaxWorkingSetGrowthMiB:0} MiB を超えた → 不合格");
        else
            sb.AppendLine($"判定(案): ワーキングセットの増分 ≤ {th.MaxWorkingSetGrowthMiB:0} MiB → 合格");
        if (loader != null)
        {
            sb.AppendLine();
            sb.AppendLine($"デコード: 原本から {loader.CompletedOriginal} 件 / ディスクキャッシュから {loader.CompletedDisk} 件 / 失敗 {loader.Failed} 件 / ディスクに書いた {loader.DiskWritten} 件");
            sb.AppendLine("  原本デコード時間: " + Stat(loader.DecodeMsOriginal));
            sb.AppendLine("  ディスクキャッシュのデコード時間: " + Stat(loader.DecodeMsDisk));
            sb.AppendLine("  ディスクキャッシュの書き込み時間: " + Stat(loader.DiskWriteMs));
            foreach (string e in loader.FirstErrors) sb.AppendLine("  失敗の例: " + e);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 2T 超のフレームについて、直前のフレームのハンドラ(更新)・OnRender(描画の記録)・GC のどれが原因かを大まかに分類する。
    /// 規則(先に当てはまったもの): ハンドラ所要 ≥ 間隔の半分 → 更新 / OnRender 所要 ≥ 間隔の半分 → 描画 / gen1・gen2 の GC あり → GC / それ以外 → 不明。
    /// 「不明」は、UI スレッドの他の処理・描画スレッドや GPU の待ち・OS のスケジューリングなど、計測していないもの。
    /// </summary>
    private static void AppendSlowFrameAnalysis(StringBuilder sb, List<FrameRec> main, double t)
    {
        List<FrameRec> slow = main.Where(f => f.IntervalMs > 2 * t).OrderByDescending(f => f.IntervalMs).ToList();
        if (slow.Count == 0) return;
        int handler = 0, render = 0, gc = 0, unknown = 0, withDrain = 0, withGc0 = 0;
        foreach (FrameRec f in slow)
        {
            if (f.Drained > 0) withDrain++;
            if (f.Gc0 > 0) withGc0++;
            if (f.PrevHandlerMs >= f.IntervalMs / 2) handler++;
            else if (f.RenderMs >= f.IntervalMs / 2) render++;
            else if (f.Gc1 > 0 || f.Gc2 > 0) gc++;
            else unknown++;
        }
        sb.AppendLine($"  2T 超のフレーム {slow.Count} 個の原因の分類: 更新(ハンドラ) {handler} / 描画(OnRender) {render} / GC(gen1・gen2) {gc} / 不明 {unknown}。参考: デコード結果の取り込みがあったフレーム {withDrain} 個、gen0 GC があったフレーム {withGc0} 個");
        sb.AppendLine("  遅い順の上位(間隔 / 直前ハンドラ / OnRender / 取り込み件数 / GC gen0,1,2 / 時刻):");
        foreach (FrameRec f in slow.Take(8))
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    {f.IntervalMs:0.0} ms / {f.PrevHandlerMs:0.00} / {f.RenderMs:0.00} / {f.Drained} / {f.Gc0},{f.Gc1},{f.Gc2} / t={f.TMs / 1000:0.00}s"));
        }
    }

    private static string Judge(PhaseData p, double[] sortedIntervals, BenchThresholds th, string kind)
    {
        double t = Percentile(sortedIntervals, 50);
        double p95 = Percentile(sortedIntervals, 95);
        double over2 = 100.0 * sortedIntervals.Count(v => v > 2 * t) / sortedIntervals.Length;
        int over6 = sortedIntervals.Count(v => v > 6 * t);
        if (kind == "warm")
        {
            bool ok = p95 <= th.WarmP95T * t && over2 <= th.WarmOver2TPercent && over6 == 0;
            return string.Create(CultureInfo.InvariantCulture,
                $"判定(ウォーム、T={t:0.00}ms): p95={p95:0.00}ms(≤{th.WarmP95T}T={th.WarmP95T * t:0.00}) / 2T超={over2:0.00}%(≤{th.WarmOver2TPercent}%) / 6T超={over6}個(=0) → {(ok ? "合格" : "不合格")}");
        }
        else
        {
            bool ok = over2 <= th.ColdOver2TPercent && over6 <= th.ColdOver6TCount;
            string ttfp = "";
            if (p.TtfpMs.Count > 0 && kind == "cold")
            {
                double t95 = Percentile(p.TtfpMs.OrderBy(v => v).ToArray(), 95);
                ok = ok && t95 <= th.ColdTtfpP95Ms;
                ttfp = string.Create(CultureInfo.InvariantCulture, $" / 画像表示 p95={t95:0}ms(≤{th.ColdTtfpP95Ms:0})");
            }
            return string.Create(CultureInfo.InvariantCulture,
                $"判定(コールド/高速/jump、T={t:0.00}ms): 2T超={over2:0.00}%(≤{th.ColdOver2TPercent}%) / 6T超={over6}個(≤{th.ColdOver6TCount}){ttfp} → {(ok ? "合格" : "不合格")}");
        }
    }

    public void WriteFiles(string dir, string summary)
    {
        File.WriteAllText(Path.Combine(dir, "frames.csv"), _frameCsv.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "seconds.csv"), _secondCsv.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "summary.txt"), summary, new UTF8Encoding(false));
    }
}

/// <summary>
/// 合格線。フレーム周期 T は各フェーズの p50 の実測値。
/// WPF はモニタのリフレッシュレート(165Hz など)に追従するので、60fps 基準の絶対値でなく、T に対する倍率で線を引く。
/// </summary>
public sealed class BenchThresholds
{
    public double WarmP95T = 1.5;
    public double WarmOver2TPercent = 1;
    public double ColdOver2TPercent = 3;
    public int ColdOver6TCount = 3;
    public double ColdTtfpP95Ms = 1000;
    public double MaxWorkingSetGrowthMiB = 600;
}
