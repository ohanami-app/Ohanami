using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace S1Vlm;

public static class DraftRefsCommand
{
    public static readonly string[] ValueOptions = { "run", "refs", "samples" };
    public static readonly string[] FlagOptions = Array.Empty<string>();

    /// <summary>
    /// run の出力から refs/&lt;画像名&gt;.txt(ocr の中身)を作る。
    /// 1つでも既存ファイルがあれば何も書かずにエラーで止める(手で直したものを消さないため)。
    /// </summary>
    public static int Execute(Args a)
    {
        string runDir = a.Require("run");
        string refsDir = a.GetOr("refs", "refs");
        string samplesPath = a.GetOr("samples", "samples.tsv");
        var samples = SamplesTsv.Read(samplesPath);

        var targets = new List<(string Path, string Ocr)>();
        var problems = new List<string>();
        foreach (SampleRow s in samples)
        {
            string jsonPath = RunFiles.ImageJsonPath(runDir, s);
            if (!File.Exists(jsonPath))
            {
                // --only で流していない画像は下書きを作らない(黙って空の参照を作ると誤採点のもとになる)
                continue;
            }
            RunFiles.ParsedOutput p = RunFiles.LoadParsed(jsonPath);
            if (!p.Ok)
            {
                problems.Add($"{s.Filename}: パース失敗のため下書きを作れない({p.Error})");
                continue;
            }
            targets.Add((Path.Combine(refsDir, s.Stem + ".txt"), p.Ocr!));
        }
        if (targets.Count == 0 && problems.Count == 0)
            throw new InvalidOperationException($"{runDir} に samples.tsv の画像の出力が1つもない");

        foreach (var (path, _) in targets)
        {
            if (File.Exists(path)) problems.Add($"既存の参照ファイルがある(上書きしない): {path}");
        }
        if (problems.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("draft-refs を中止した。何も書いていない:");
            foreach (string p in problems) sb.AppendLine("  " + p);
            throw new InvalidOperationException(sb.ToString().TrimEnd());
        }

        Directory.CreateDirectory(refsDir);
        foreach (var (path, ocr) in targets)
        {
            // FileMode.CreateNew: 万一ここで競合して既存ファイルがあっても IOException で落ち、上書きしない。
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            byte[] bytes = RunFiles.Utf8NoBom.GetBytes(ocr);
            fs.Write(bytes, 0, bytes.Length);
        }
        List<string> ignored = RunFiles.FindExtraOutputs(runDir, samples);
        if (ignored.Count > 0)
        {
            Console.WriteLine($"注意: samples.tsv にない画像の run 出力が {ignored.Count} 件ある。下書きは作っていない(無視): {string.Join(", ", ignored)}");
        }
        Console.WriteLine($"参照の下書きを {targets.Count} 件書いた: {Path.GetFullPath(refsDir)}");
        Console.WriteLine("画像を見ながら手で直すこと(下書きだけを読んで直さない)。");
        return 0;
    }
}
