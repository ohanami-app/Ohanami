using System;
using System.Text;
using System.Threading.Tasks;

namespace S1Vlm;

public static class Program
{
    private const string Usage = @"S1Vlm - S1(VLM の日本語読み取り精度)の測定ツール。使い捨てのスパイク。

使い方(spikes/S1-vlm を作業ディレクトリにして実行):
  dotnet run -c Release -- run --server <llama-server.exe> --model <model.gguf> --mmproj <mmproj.gguf> --label <ラベル>
        [--extra-args ""<llama-server への追加引数>""] [--samples samples.tsv] [--images ../images]
        [--out runs/<ラベル>] [--only <層名[,層名...]>] [--name-contains <文字列>]
        [--prompt prompt.txt] [--schema schema.json] [--request-extra <JSON>] [--max-tokens 4096] [--timeout-sec 1800]
        [--health-timeout-sec 900] [--gpu-index 0] [--force]
  dotnet run -c Release -- draft-refs --run runs/<ラベル> [--refs refs] [--samples samples.tsv]
  dotnet run -c Release -- score --run runs/<ラベル> [--refs refs] [--samples samples.tsv] [--out <run>/score.tsv] [--skip-missing-refs]
  dotnet run -c Release -- selftest
";

    public static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
            {
                Console.Write(Usage);
                return argv.Length == 0 ? 1 : 0;
            }
            switch (argv[0])
            {
                case "run":
                    return await RunCommand.ExecuteAsync(Args.Parse(argv, 1, RunCommand.ValueOptions, RunCommand.FlagOptions));
                case "draft-refs":
                    return DraftRefsCommand.Execute(Args.Parse(argv, 1, DraftRefsCommand.ValueOptions, DraftRefsCommand.FlagOptions));
                case "score":
                    return ScoreCommand.Execute(Args.Parse(argv, 1, ScoreCommand.ValueOptions, ScoreCommand.FlagOptions));
                case "selftest":
                    Args.Parse(argv, 1, Array.Empty<string>(), Array.Empty<string>());
                    return SelfTest.Run();
                default:
                    Console.Error.WriteLine($"未知のサブコマンド: {argv[0]}");
                    Console.Error.Write(Usage);
                    return 1;
            }
        }
        catch (Exception e)
        {
            // 想定外は握りつぶさず、そのまま表示して非0で終わる。
            Console.Error.WriteLine("ERROR: " + e.Message);
            if (e is not (ArgumentException or InvalidOperationException or System.IO.InvalidDataException))
                Console.Error.WriteLine(e.ToString());
            return 1;
        }
    }
}
