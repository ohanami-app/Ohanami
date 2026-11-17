using System;
using System.Collections.Generic;
using System.Globalization;

namespace S4Wpf;

/// <summary>
/// ベンチのシナリオ。手でスクロールせず、毎フレーム「オフセット += 速度 × 経過時間」で自動スクロールする
/// (DispatcherTimer はジッタが出るので使わない)。経過時間は実時間(Stopwatch)なので、詰まりはフレーム間隔として現れる。
/// フェーズごとに FrameMeter へ記録を分ける。
/// </summary>
public sealed class ScenarioRunner
{
    private sealed class Phase
    {
        public string Name = "";
        public bool Measure = true;
        public Action? Begin;
        public Func<double, double, bool> Tick = (_, _) => true;   // (dt 秒, 現在 ms) → 終わったら true
    }

    private readonly Options _o;
    private readonly ThumbnailGridControl _grid;
    private readonly FrameMeter _meter;
    private readonly ThumbnailLoader? _primeLoader;
    private readonly Queue<Phase> _phases = new();
    private Phase? _cur;

    /// <summary>判定に使う種類: warm / cold / fast / rate / prime</summary>
    public string Kind { get; private set; } = "cold";
    public bool Finished { get; private set; }
    public string Description { get; private set; } = "";

    public ScenarioRunner(Options o, ThumbnailGridControl grid, FrameMeter meter, ThumbnailLoader? primeLoader)
    {
        _o = o;
        _grid = grid;
        _meter = meter;
        _primeLoader = primeLoader;
        Build();
    }

    private void Build()
    {
        switch (_o.Bench)
        {
            case "rate":
                Kind = "rate";
                Description = $"描画レートの実測: デコードなし、{_o.Seconds:0} 秒、オフセットを毎フレーム動かし続ける";
                _phases.Enqueue(Rate());
                break;
            case "nodecode":
                Kind = "cold";
                Description = $"デコードなし(色付きの四角だけ)の通し。速度 {_o.Speed:0} DIP/秒。描画とスクロールだけの下限";
                _phases.Enqueue(Linear("nodecode-down", true, +1, _o.Speed, () => _grid.MaxOffset));
                _phases.Enqueue(Linear("nodecode-up", true, -1, _o.Speed, () => 0));
                break;
            case "pass":
                Kind = _o.Speed >= 8000 ? "fast" : "cold";
                Description = $"最上段→最下段の通し。速度 {_o.Speed:0} DIP/秒。ディスクキャッシュ={(_o.DiskCacheDir ?? "なし")}";
                _phases.Enqueue(Linear("pass-down", true, +1, _o.Speed, () => _grid.MaxOffset));
                break;
            case "warm":
                Kind = "warm";
                Description = $"先頭 {_o.WarmRegion} 件の範囲を往復。1往復目は読み込み(記録しない)、続く {_o.WarmPasses} 往復を記録。速度 {_o.Speed:0} DIP/秒";
                {
                    Func<double> target = () => Math.Min(_grid.MaxOffset, Math.Ceiling((double)_o.WarmRegion / _grid.Columns) * _grid.RowHeight);
                    _phases.Enqueue(Linear("warm-load-down", false, +1, _o.Speed, target));
                    _phases.Enqueue(Linear("warm-load-up", false, -1, _o.Speed, () => 0));
                    for (int k = 1; k <= _o.WarmPasses; k++)
                    {
                        _phases.Enqueue(Linear($"warm-down-{k}", true, +1, _o.Speed, target));
                        _phases.Enqueue(Linear($"warm-up-{k}", true, -1, _o.Speed, () => 0));
                    }
                }
                break;
            case "jump":
                Kind = "cold";
                Description = $"ランダムな位置へ {_o.Jumps} 回ジャンプ。各ジャンプ後、見えているセルが全部そろうまで(最大2秒)待って、次へ";
                _phases.Enqueue(Jump());
                break;
            case "prime":
                Kind = "prime";
                Description = $"全 {_grid.ItemCount} 件をデコードしてディスクキャッシュを埋める(表示・スクロールなし)。ワーカー数 {_o.Workers}";
                _phases.Enqueue(Prime());
                break;
            default:
                throw new InvalidOperationException($"未知のベンチ: {_o.Bench}");
        }
    }

    public void Tick(double dtSec, double nowMs)
    {
        if (Finished) return;
        if (_cur == null)
        {
            if (_phases.Count == 0) { Finished = true; _meter.EndPhase(); return; }
            _cur = _phases.Dequeue();
            _cur.Begin?.Invoke();
            if (_cur.Measure) _meter.BeginPhase(_cur.Name); else _meter.EndPhase();
            return;
        }
        if (_cur.Tick(dtSec, nowMs))
        {
            _cur = null;
        }
    }

    // ------------------------------------------------------------------ フェーズの作り方

    private Phase Linear(string name, bool measure, int dir, double speed, Func<double> target)
    {
        return new Phase
        {
            Name = name,
            Measure = measure,
            Begin = () => { _grid.Offset = dir > 0 ? 0 : _grid.Offset; },
            Tick = (dt, _) =>
            {
                double t = target();
                double next = _grid.Offset + dir * speed * dt;
                bool done = dir > 0 ? next >= t : next <= t;
                _grid.Offset = done ? t : next;
                return done;
            },
        };
    }

    private Phase Rate()
    {
        double elapsed = 0;
        return new Phase
        {
            Name = "rate",
            Measure = true,
            Begin = () => _grid.Offset = 0,
            Tick = (dt, _) =>
            {
                elapsed += dt;
                double amp = Math.Min(_grid.MaxOffset, _grid.RowHeight * 4) / 2;
                _grid.Offset = amp * (1 - Math.Cos(elapsed * 2 * Math.PI / 4.0));
                return elapsed >= _o.Seconds;
            },
        };
    }

    private Phase Jump()
    {
        var rng = new Random(12345);
        int done = 0;
        int state = 0;               // 0=ジャンプする 1=そろうのを待つ 2=少し待つ
        double stateStartMs = 0;
        return new Phase
        {
            Name = "jump",
            Measure = true,
            Begin = () => _grid.Offset = 0,
            Tick = (dt, nowMs) =>
            {
                switch (state)
                {
                    case 0:
                        {
                            int row = rng.Next(0, Math.Max(1, _grid.RowCount));
                            _grid.Offset = row * _grid.RowHeight;
                            stateStartMs = nowMs;
                            state = 1;
                            break;
                        }
                    case 1:
                        if (_grid.AllVisibleLoaded() || nowMs - stateStartMs > 2000)
                        {
                            _meter.AddJumpSettle(nowMs - stateStartMs);
                            stateStartMs = nowMs;
                            state = 2;
                        }
                        break;
                    case 2:
                        if (nowMs - stateStartMs >= 100)
                        {
                            done++;
                            state = 0;
                            if (done >= _o.Jumps) return true;
                        }
                        break;
                }
                return false;
            },
        };
    }

    private Phase Prime()
    {
        if (_primeLoader == null) throw new InvalidOperationException("prime にはローダーが要る");
        double lastPrintMs = 0;
        double startMs = 0;
        return new Phase
        {
            Name = "prime",
            Measure = false,
            Begin = () =>
            {
                _primeLoader.DiscardResults = true;
                var all = new int[_grid.ItemCount];
                for (int i = 0; i < all.Length; i++) all[i] = i;
                _primeLoader.SetWanted(all);
            },
            Tick = (dt, nowMs) =>
            {
                if (startMs == 0) startMs = nowMs;
                _primeLoader.Drain();   // 結果は捨てる(キューを空にするため)
                long c = _primeLoader.CompletedTotal;
                if (nowMs - lastPrintMs >= 2000)
                {
                    lastPrintMs = nowMs;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"prime: {c}/{_grid.ItemCount} 件 ({c / Math.Max(0.001, (nowMs - startMs) / 1000):0.0} 件/秒)"));
                }
                bool finished = c >= _grid.ItemCount;
                if (finished)
                {
                    double sec = (nowMs - startMs) / 1000;
                    string note = string.Create(CultureInfo.InvariantCulture, $"prime 完了: {c} 件を {sec:0.0} 秒で処理({c / Math.Max(0.001, sec):0.0} 件/秒、ワーカー {_o.Workers})");
                    Console.WriteLine(note);
                    _meter.AddNote(note);
                }
                return finished;
            },
        };
    }
}
