using System;
using System.Text;

namespace S2Siglip2.Probe;

/// <summary>
/// S2 の測定用コマンド。使い方:
/// <code>
/// dotnet run -c Release --project S2Siglip2.Probe -- gpu-measure [--provider cuda|cpu] [--images &lt;dir&gt;] [--out &lt;dir&gt;] [--limit N] ...
/// dotnet run -c Release --project S2Siglip2.Probe -- rank --embeddings &lt;gpu-measure の出力&gt; [--descriptions &lt;S1 の runs/...&gt;]
/// dotnet run -c Release --project S2Siglip2.Probe -- ram-text [--threads 4] [--runs 40]
/// </code>
/// 結果は --out(既定は spikes/S2-siglip2/runs/&lt;日時&gt;-&lt;種類&gt;/)の report.txt など(UTF-8)に書く。
/// </summary>
public static class Program
{
    public static int Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("コマンド: gpu-measure | rank | ram-text | diagnose-dlls | precision-template | precision-eval。詳しくは README.md と、各クラスの説明(GpuMeasure / RankCommand / RamText)");
            return argv.Length == 0 ? 1 : 0;
        }
        Args a = new(argv);
        return a.Command switch
        {
            "gpu-measure" => GpuMeasure.Run(a),
            "rank" => RankCommand.Run(a),
            "ram-text" => RamText.Run(a),
            "diagnose-dlls" => Diagnose.Run(a),
            "precision-template" => PrecisionCommands.Template(a),
            "precision-eval" => PrecisionCommands.Evaluate(a),
            "qrels-patch-page" => PrecisionCommands.PatchPage(a),
            "qrels-check" => PrecisionCommands.CheckQrels(a),
            "stage3-eval" => Stage3Command.Run(a),
            _ => throw new ArgumentException($"未知のコマンド: {a.Command}(gpu-measure | rank | ram-text | diagnose-dlls | precision-template | precision-eval)"),
        };
    }
}
