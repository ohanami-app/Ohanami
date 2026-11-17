using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace S4Wpf;

/// <summary>
/// コマンドライン引数。`--名前 値` のみ。未知の引数・値の欠落・数値でない値は例外にする(黙って無視しない)。
/// 引数なしで起動すると、手で操作する対話モードになる。
/// </summary>
public sealed class Options
{
    /// <summary>ベンチの種類: rate | nodecode | pass | warm | jump | prime。null なら対話モード。</summary>
    public string? Bench;
    public string ImagesDir = Path.Combine("..", "images");
    public int Count = 10000;
    public double Speed = 3000;                 // pass の自動スクロール速度(DIP/秒)
    public string? DiskCacheDir;                // null=ディスクキャッシュなし
    public int LruCapacity = 1500;              // メモリ上のサムネイル数の上限
    public double CellWidthDip = 256;
    public double CellHeightDip = 144;
    public double GapDip = 8;
    public int Workers = Math.Max(2, Environment.ProcessorCount / 2);
    public int WindowWidth = 1600;
    public int WindowHeight = 900;
    public string? Label;
    public string OutRoot = "runs";
    public double Seconds = 5;                  // rate の長さ
    public int Jumps = 200;
    public int WarmRegion = 1000;               // warm で往復する範囲(件数)。LRU に収まる件数にする
    public int WarmPasses = 3;
    public double PrefetchScreens = 2;          // スクロール方向への先読み(画面数)
    /// <summary>指定した画像を WIC(BitmapDecoder)で開いて結果を表示して終わる(ウィンドウは作らない)。WebP が開けるかの確認用。</summary>
    public string? CheckImage;

    private static readonly string[] Names =
    {
        "bench", "images", "count", "speed", "disk-cache", "lru", "cell-width", "cell-height", "gap", "workers",
        "window", "label", "out", "seconds", "jumps", "warm-region", "warm-passes", "prefetch", "check-image",
    };

    public static Options Parse(string[] argv)
    {
        var o = new Options();
        var seen = new HashSet<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            string k = argv[i];
            if (!k.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"想定外の引数: '{k}'");
            string name = k.Substring(2);
            if (Array.IndexOf(Names, name) < 0) throw new ArgumentException($"未知のオプション: --{name}");
            if (!seen.Add(name)) throw new ArgumentException($"オプションが重複: --{name}");
            if (i + 1 >= argv.Length) throw new ArgumentException($"--{name} に値がない");
            string v = argv[++i];
            switch (name)
            {
                case "bench":
                    if (Array.IndexOf(new[] { "rate", "nodecode", "pass", "warm", "jump", "prime" }, v) < 0)
                        throw new ArgumentException($"--bench は rate|nodecode|pass|warm|jump|prime: '{v}'");
                    o.Bench = v; break;
                case "images": o.ImagesDir = v; break;
                case "count": o.Count = Int(name, v, 1, 1_000_000); break;
                case "speed": o.Speed = Dbl(name, v, 1, 1_000_000); break;
                case "disk-cache": o.DiskCacheDir = v == "off" ? null : v; break;
                case "lru": o.LruCapacity = Int(name, v, 10, 1_000_000); break;
                case "cell-width": o.CellWidthDip = Dbl(name, v, 16, 4096); break;
                case "cell-height": o.CellHeightDip = Dbl(name, v, 16, 4096); break;
                case "gap": o.GapDip = Dbl(name, v, 0, 256); break;
                case "workers": o.Workers = Int(name, v, 1, 64); break;
                case "window":
                    {
                        string[] p = v.Split('x');
                        if (p.Length != 2) throw new ArgumentException("--window は 幅x高さ(例 1600x900)");
                        o.WindowWidth = Int(name, p[0], 200, 10000);
                        o.WindowHeight = Int(name, p[1], 200, 10000);
                        break;
                    }
                case "label": o.Label = v; break;
                case "out": o.OutRoot = v; break;
                case "seconds": o.Seconds = Dbl(name, v, 1, 3600); break;
                case "jumps": o.Jumps = Int(name, v, 1, 100000); break;
                case "warm-region": o.WarmRegion = Int(name, v, 1, 1_000_000); break;
                case "warm-passes": o.WarmPasses = Int(name, v, 1, 1000); break;
                case "prefetch": o.PrefetchScreens = Dbl(name, v, 0, 20); break;
                case "check-image": o.CheckImage = v; break;
            }
        }
        return o;
    }

    private static int Int(string name, string v, int min, int max)
    {
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new ArgumentException($"--{name} は {min}〜{max} の整数: '{v}'");
        return n;
    }

    private static double Dbl(string name, string v, double min, double max)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < min || d > max)
            throw new ArgumentException($"--{name} は {min}〜{max} の数: '{v}'");
        return d;
    }
}
