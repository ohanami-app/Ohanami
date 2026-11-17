using System;
using System.IO;
using System.Linq;
using System.Text;

namespace S2Siglip2.Probe;

/// <summary>precision@10 の判定まわりのコマンド(GPU も埋め込みも要らない)。</summary>
public static class PrecisionCommands
{
    private static string DefaultKnown => Path.Combine(Paths.Root, "spikes", "S2-siglip2", "known-items.tsv");
    private static string DefaultJudgement => Path.Combine(Paths.Root, "spikes", "S2-siglip2", "precision-judgement.tsv");

    /// <summary>
    /// known-items.tsv の query_ja を重複なしにして、precision-judgement.tsv の雛形(relevant_count は空)を作る。
    /// すでにファイルがあれば(貼ったものを消さないよう)--force が無い限り例外にする。
    /// </summary>
    public static int Template(Args a)
    {
        string known = a.GetOr("known-items", DefaultKnown);
        string path = a.GetOr("out", DefaultJudgement);
        bool force = a.Flag("force");
        a.RequireAllUsed();
        if (File.Exists(path) && !force) throw new InvalidOperationException($"{path} はすでにある(貼った結果を消さないよう、上書きしない)。作り直すなら --force");
        var queries = PrecisionJudgement.UniqueQueries(KnownItems.Read(known));
        File.WriteAllText(path, PrecisionJudgement.Template(queries), new UTF8Encoding(false));
        Console.WriteLine($"{queries.Count} クエリの雛形を書いた: {path}");
        return 0;
    }

    /// <summary>
    /// 作成済みの precision-page.html に「qrels の書き出し」を足す(作り直さない。ページ id = localStorage の保存先はそのまま)。
    /// 元のファイルは隣に <c>precision-page.before-qrels.html</c> として残す(すでにあれば上書きしない)。
    /// </summary>
    public static int PatchPage(Args a)
    {
        string page = a.GetOr("page", Path.Combine(Paths.Root, "spikes", "S2-siglip2", "runs", "gpu-2-cudnn", "precision-page.html"));
        a.RequireAllUsed();
        string html = File.ReadAllText(page, new UTF8Encoding(false, true));
        string patched = QrelsExport.Patch(html); // すでに足してある・形が違うなら例外
        string backup = Path.Combine(Path.GetDirectoryName(page)!, "precision-page.before-qrels.html");
        if (File.Exists(backup)) throw new InvalidOperationException($"{backup} がすでにある(上書きしない)。元に戻すなら、これを precision-page.html に戻す");
        File.WriteAllText(backup, html, new UTF8Encoding(false));
        File.WriteAllText(page, patched, new UTF8Encoding(false));
        Console.WriteLine($"書き出しを足した: {page}({html.Length} → {patched.Length} 文字)。元: {backup}");
        return 0;
    }

    /// <summary>
    /// qrels.tsv を既知のクエリ・画像と照合し、precision-judgement.tsv(当たりの数)と突き合わせる。食い違いがあれば終了コード 1。
    /// </summary>
    public static int CheckQrels(Args a)
    {
        string known = a.GetOr("known-items", DefaultKnown);
        string qrelsPath = a.GetOr("qrels", Path.Combine(Paths.Root, "spikes", "S2-siglip2", "qrels.tsv"));
        string judgement = a.GetOr("judgement", DefaultJudgement);
        string embDir = a.GetOr("embeddings", Path.Combine(Paths.Root, "spikes", "S2-siglip2", "runs", "gpu-2-cudnn"));
        a.RequireAllUsed();
        var expected = PrecisionJudgement.UniqueQueries(KnownItems.Read(known));
        var names = EmbeddingStore.Read(embDir).Names;
        Qrels q = Qrels.Read(qrelsPath);
        q.Validate(expected, names);
        // precision-judgement.tsv は precision-page の判定だけ。追加の判定(stage3-judge など)の行は突き合わせの対象から外し、行数だけ報告する
        const string FirstSource = "precision-page:";
        var problems = q.CompareWithCounts(PrecisionJudgement.Read(judgement), PrecisionJudgement.TopCount, r => r.Source.StartsWith(FirstSource, StringComparison.Ordinal));
        Console.WriteLine($"qrels: {q.Count} 行、{q.Queries.Count()} クエリ、当たり {q.All.Count(r => r.Relevant)}。ソース: {string.Join("、", q.All.Select(r => r.Source).Distinct())}");
        foreach (var g in q.All.GroupBy(r => r.Source).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  ソース {g.Key}: {g.Count()} 行、当たり {g.Count(r => r.Relevant)}、クエリ {g.Select(r => r.Query).Distinct().Count()}" + (g.Key.StartsWith(FirstSource, StringComparison.Ordinal) ? "(precision-judgement.tsv と突き合わせる)" : "(追加の判定。突き合わせの対象外)"));
        if (problems.Count == 0) { Console.WriteLine("precision-judgement.tsv の当たりの数と一致(判定済みの全クエリで、precision-page 由来の行数 10・当たりの数が同じ)"); return 0; }
        foreach (string p in problems) Console.WriteLine("食い違い: " + p);
        return 1;
    }

    /// <summary>precision-judgement.tsv が埋まったら、平均の precision@10 と判定(≥ 0.7 使える、&lt; 0.4 使えない、その間は微妙)を出す。</summary>
    public static int Evaluate(Args a)
    {
        string known = a.GetOr("known-items", DefaultKnown);
        string path = a.GetOr("judgement", DefaultJudgement);
        string? outDir = a.Get("out");
        bool partial = a.Flag("allow-partial");
        a.RequireAllUsed();
        var expected = PrecisionJudgement.UniqueQueries(KnownItems.Read(known));
        var entries = PrecisionJudgement.Read(path);
        var r = PrecisionJudgement.Evaluate(entries, expected, partial);
        using var report = new Report(Path.Combine(outDir ?? Path.GetDirectoryName(path)!, "precision-summary.txt"));
        report.Line($"# precision@{PrecisionJudgement.TopCount} の判定  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Line($"判定ファイル: {path}(known-items.tsv の日本語クエリ {expected.Count} 件と照合済み)");
        report.Line($"判定済み {r.Judged} クエリ、未判定 {r.Unjudged} クエリ" + (r.Unjudged > 0 ? "(--allow-partial のため、判定済みだけの平均)" : ""));
        foreach (var (q, p) in r.PerQuery.OrderBy(x => x.Precision)) report.Line($"  {p:F1}  {q}");
        report.Line($"平均 precision@{PrecisionJudgement.TopCount} = {r.Mean:F3}");
        report.Line($"判定: {r.Label}(線: ≥ {PrecisionJudgement.UsableThreshold} 使える、< {PrecisionJudgement.UnusableThreshold} 使えない、その間は微妙。ADR 0037)");
        return 0;
    }
}
