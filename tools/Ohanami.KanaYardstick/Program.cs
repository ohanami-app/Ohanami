using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ohanami.KanaYardstick;

/// <summary>
/// 濁点・半濁点の読み取りの尺の入り口。使い方は <see cref="Usage"/>。終了コード: 0 = 正常、1 = 使い方の誤り・入力の誤り・実行の失敗。
/// 画像を描く WPF の呼び出しのため、Main は STA(async にできないので、流す処理は同期で待つ)。
/// </summary>
internal static class Program
{
    private const string Usage = """
        Ohanami.KanaYardstick: 濁点・半濁点の読み取りの尺(合成画像。私物の画像は使わない)

          gen   --work <dir> [--seed <数>]
                合成画像 90 枚(<work>/images/*.png)と、正解の文を持つ <work>/manifest.json を作る。同じ種なら同じ仕様。既定の種 = 20261119。
          run   --work <dir> --llama <llama-server.exe> --model <gguf> --mmproj <gguf> --cond <名前> [--min-tokens <数>] [--max-tokens <数>] [--extra "<引数>"] [--only <文字列>]
                製品と同じ引数・要求で llama-server を起動し、画像を 1 枚ずつ流して <work>/results.jsonl に足す。1 回で 1 つの条件。
                --min-tokens = --image-min-tokens(既定は付けない)、--max-tokens = --image-max-tokens(既定 製品の値)、--extra = 製品の引数のあとに足す引数(空白区切り)、
                --only = id にこの文字列を含む画像だけ(試し用)。済みの (条件, id) は流さない。
          score --work <dir> [--text-tokens <数>] [--base <条件>] [--variant <条件>]
                results.jsonl を採点して、<work>/report.txt・per-image.tsv・alignments.txt を書き、report を表示する。
                --text-tokens = プロンプトの文章のトークン数(既定 536 = Qwen3-VL-8B + 製品のプロンプト。別のモデルでは、小さい画像の prompt_tokens から測り直す)、
                --base・--variant = 「進む線」の表で比べる 2 つの条件(既定 c1・c2)。

        --work はリポジトリの外(生成物と結果をコミットしないため)。
        """;

    private static readonly string[] KnownOptions = ["--work", "--seed", "--llama", "--model", "--mmproj", "--cond", "--min-tokens", "--max-tokens", "--extra", "--only", "--text-tokens", "--base", "--variant"];

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
            Dictionary<string, string> flags = ParseFlags(args);
            return args[0] switch
            {
                "gen" => Generate(flags),
                "run" => Run(flags),
                "score" => Score(flags),
                _ => Fail("知らないコマンド: " + args[0]),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or ArgumentException or TimeoutException or JsonException)
        {
            Console.Error.WriteLine("失敗: " + e.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("中断した。");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            if (!KnownOptions.Contains(args[i])) throw new ArgumentException($"知らないオプション: {args[i]}");
            if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} の値が無い");
            flags[args[i]] = args[++i];
        }
        return flags;
    }

    private static string Required(Dictionary<string, string> flags, string flag) =>
        flags.TryGetValue(flag, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"{flag} が要る");

    private static int? OptionalInt(Dictionary<string, string> flags, string flag) =>
        flags.TryGetValue(flag, out string? v)
            ? (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : throw new ArgumentException($"{flag} は 0 以上の整数: {v}"))
            : null;

    private static string WorkDirectory(Dictionary<string, string> flags)
    {
        string work = Path.GetFullPath(Required(flags, "--work"));
        RefuseInsideRepository(work);
        return work;
    }

    private static int Generate(Dictionary<string, string> flags)
    {
        string work = WorkDirectory(flags);
        int seed = OptionalInt(flags, "--seed") ?? ImageSpecs.DefaultSeed;
        ImageRenderer.CheckFonts();
        Directory.CreateDirectory(Path.Combine(work, "images"));
        IReadOnlyList<Spec> specs = ImageSpecs.Build(seed);
        foreach (Spec s in specs) ImageRenderer.SavePng(ImageRenderer.Render(s), Path.Combine(work, "images", s.Id + ".png"));
        File.WriteAllText(Path.Combine(work, "manifest.json"),
            JsonSerializer.Serialize(specs, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        Console.WriteLine($"images={specs.Count} chars={specs.Sum(s => s.Lines.Sum(l => l.Length))} seed={seed}");
        return 0;
    }

    private static int Run(Dictionary<string, string> flags)
    {
        string work = WorkDirectory(flags);
        var o = new RunOptions(work, Path.GetFullPath(Required(flags, "--llama")), Path.GetFullPath(Required(flags, "--model")), Path.GetFullPath(Required(flags, "--mmproj")),
            Required(flags, "--cond"), OptionalInt(flags, "--min-tokens"), OptionalInt(flags, "--max-tokens") ?? Ohanami.Core.Models.VlmCatalog.ImageMaxTokens,
            flags.GetValueOrDefault("--extra") ?? "", flags.GetValueOrDefault("--only") ?? "");
        if (o.Condition.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new ArgumentException("--cond は英数字と - _ だけ");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // llama-server を残さず止めてから終わる
        return Runner.RunAsync(o, cts.Token).GetAwaiter().GetResult();
    }

    private static int Score(Dictionary<string, string> flags) =>
        ScoreReport.Run(new ScoreOptions(WorkDirectory(flags), OptionalInt(flags, "--text-tokens") ?? 536, flags.GetValueOrDefault("--base") ?? "c1", flags.GetValueOrDefault("--variant") ?? "c2"));

    /// <summary>--work がリポジトリの中なら拒否する(生成物と結果をコミットに入れない)。</summary>
    private static void RefuseInsideRepository(string path)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!(Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))) continue;
            string repo = d.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(repo, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"--work {path} はリポジトリ {repo} の中。生成物(画像)と結果は、リポジトリの外に置く。");
            return;
        }
    }
}
