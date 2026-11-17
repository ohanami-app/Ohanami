using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace S2Siglip2.Probe;

/// <summary>
/// known-item の評価(判定の線は ADR 0037)。1030 枚すべての画像の埋め込み(gpu-measure が作る embeddings.f32)と、
/// known-items.tsv のクエリ(日本語 query_ja、英語 query_en)から、「探したい1枚」の順位を出す。
/// <code>
/// dotnet run -c Release --project S2Siglip2.Probe -- rank --embeddings &lt;gpu-measure の出力ディレクトリ&gt; [--known-items &lt;tsv&gt;]
///     [--descriptions spikes/S1-vlm/runs/qwen3vl-8b-q4km] [--text-provider cpu|cuda] [--out &lt;dir&gt;]
/// </code>
/// クエリの種類: ja(query_ja)、en(query_en。列があって埋まっている行だけ)、desc(補助。S1 の候補1の description の最初の1文)。
/// 順位 = 目標より厳密にスコアが大きい画像の数 + 1(同点は別に数える)。判定の規則は <see cref="KnownItems.Evaluate"/>。
/// ランダムなら上位 20 位に入る確率は約 2%(20/1030)。件数が 33 なので、2〜3 件の差はノイズとみなす。
/// 上位に出た画像が「同じ場面の別カット」なら、人が正解扱いにしてよい(その判断のために、上位5枚のファイル名を出す)。
/// </summary>
public static class RankCommand
{
    public static int Run(Args a)
    {
        string embDir = a.Get("embeddings") ?? throw new ArgumentException("--embeddings(embeddings.f32 があるディレクトリ)が要る");
        string knownPath = a.GetOr("known-items", Path.Combine(Paths.Root, "spikes", "S2-siglip2", "known-items.tsv"));
        string? descDir = a.Get("descriptions");
        string provider = a.GetOr("text-provider", "cpu");
        string outDir = a.Get("out") ?? embDir;
        string imagesDir = a.GetOr("images-dir", Path.Combine(Paths.Root, "spikes", "images"));
        int threads = a.GetInt("threads", 4);
        bool noPage = a.Flag("no-precision-page"); // テンプレート付きクエリなど、precision@10 のページが要らない実行用
        a.RequireAllUsed();
        if (provider is not ("cpu" or "cuda")) throw new ArgumentException("--text-provider は cpu か cuda");

        using var report = new Report(Path.Combine(outDir, "known-items-summary.txt"));
        report.Line($"# known-item の評価  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var (names, imageEmbs) = EmbeddingStore.Read(embDir);
        report.Line($"画像の埋め込み: {names.Count} 枚({embDir})");
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++) nameToIndex[names[i].Normalize(NormalizationForm.FormC)] = i;

        var items = KnownItems.Read(knownPath);
        report.Line($"known-items: {items.Count} 行({knownPath})。query_ja あり {items.Count(r => r.QueryJa is not null)}、query_en あり {items.Count(r => r.QueryEn is not null)}");

        // クエリの一覧(種類、ファイル名、クエリ)
        var queries = new List<(string Kind, string File, string Query)>();
        foreach (var r in items)
        {
            if (r.QueryJa is not null) queries.Add(("ja", r.Filename, r.QueryJa));
            if (r.QueryEn is not null) queries.Add(("en", r.Filename, r.QueryEn));
        }
        if (descDir is not null)
        {
            int missing = 0;
            foreach (var r in items.DistinctBy(x => x.Filename)) // 説明文は画像ごとに1つ(同じ画像の行が複数あっても1回だけ)
            {
                string p = Path.Combine(descDir, Path.GetFileNameWithoutExtension(r.Filename) + ".json");
                if (!File.Exists(p)) { missing++; continue; }
                string? d = ReadDescription(p);
                if (d is null) { missing++; continue; }
                queries.Add(("desc", r.Filename, KnownItems.FirstSentence(d)));
            }
            report.Line($"S1 の説明文(補助): {descDir}。最初の1文をクエリにした: {queries.Count(q => q.Kind == "desc")} 件(説明文が無い・読めない {missing} 件)");
        }
        if (queries.Count == 0) throw new InvalidOperationException("クエリが1つも無い(known-items.tsv の query_ja が空のまま?)");

        // 目標の画像が埋め込みの中にあるか(無ければ黙らず例外)
        var unknown = queries.Select(q => q.File).Distinct().Where(f => !nameToIndex.ContainsKey(f.Normalize(NormalizationForm.FormC))).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException("埋め込みに無い画像がある(embedding-names.tsv と known-items.tsv の名前が違う?): " + string.Join(", ", unknown.Take(5)));

        // テキスト側
        report.Line($"テキスト側: {provider} EP");
        string textOnnx = Paths.VerifiedModelFile("textual/model.onnx", report);
        var tok = Siglip2Tokenizer.Load(Paths.VerifiedModelFile("textual/tokenizer.json", report));
        foreach (string f in Directory.GetFiles(Path.GetDirectoryName(textOnnx)!))
        {
            string n = Path.GetFileName(f);
            if (Paths.IsTextualExternalData(n)) Paths.VerifiedModelFile("textual/" + n);
        }
        using Siglip2Model textModel = provider == "cuda"
            ? Siglip2Model.OpenTextualCuda(textOnnx, new Siglip2Model.CudaSettings { IntraOpThreads = 2 })
            : Siglip2Model.OpenTextualCpu(textOnnx, threads);

        var sbRes = new StringBuilder("kind\tfilename\tquery\trank\ttied\ttarget_score\ttop1_score\ttop1_file\ttop2_file\ttop3_file\ttop4_file\ttop5_file\n");
        var ranksByKind = new Dictionary<string, List<int>>();
        var perQuery = new List<(string Kind, string File, string Query, int Rank)>();
        var textCache = new Dictionary<string, float[]>(StringComparer.Ordinal); // 同じクエリ(別の画像に同じ語を書いた場合)は1回だけ埋め込む
        foreach (var (kind, file, query) in queries)
        {
            if (!textCache.TryGetValue(query, out float[]? emb)) textCache[query] = emb = textModel.EmbedText(tok.Encode(query));
            int target = nameToIndex[file.Normalize(NormalizationForm.FormC)];
            var res = KnownItems.Rank(emb, imageEmbs, target, 5);
            if (!ranksByKind.TryGetValue(kind, out var list)) ranksByKind[kind] = list = [];
            list.Add(res.Rank);
            perQuery.Add((kind, file, query, res.Rank));
            sbRes.Append(kind).Append('\t').Append(file).Append('\t').Append(Clean(query)).Append('\t').Append(res.Rank).Append('\t').Append(res.TiedCount).Append('\t')
                 .Append(res.TargetScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                 .Append(res.Top1Score.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            foreach (int idx in res.TopIndices) sbRes.Append('\t').Append(names[idx]);
            sbRes.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "known-items-result.tsv"), sbRes.ToString(), new UTF8Encoding(false));

        report.Line();
        report.Line($"## 結果(画像 {names.Count} 枚の中での順位。ランダムなら上位20位に入る確率は約 {20.0 / names.Count * 100:F1}%)");
        report.Line("集計は2通り: 「クエリ単位」(1行 = 1クエリ。同じ画像に複数のクエリがあれば、その数だけ数える)と、「画像単位」(その画像のクエリのうち最良の順位。画像ごとに1件)。");
        foreach (string kind in new[] { "ja", "en", "desc" })
        {
            if (!ranksByKind.TryGetValue(kind, out var ranks)) continue;
            string title = kind switch { "ja" => "日本語のクエリ(query_ja)", "en" => "英語のクエリ(query_en)", _ => "補助: S1 の説明文の最初の1文" };
            var v = KnownItems.Evaluate(ranks);
            report.Line($"- {title}");
            report.Line($"    クエリ単位: {v.Count} 件。1位 {v.Top1} 件、上位5位以内 {v.Top5} 件({v.Top5Ratio * 100:F0}%)、上位20位以内 {v.Top20} 件({v.Top20Ratio * 100:F0}%)、順位の中央値 {v.MedianRank:F1} → {v.Label}" + (v.Count < 20 ? "(件数が少ない。2〜3 件の差はノイズ)" : ""));
            var bestPerImage = perQuery.Where(q => q.Kind == kind).GroupBy(q => q.File).Select(g => g.Min(q => q.Rank)).ToList();
            var vi = KnownItems.Evaluate(bestPerImage);
            report.Line($"    画像単位(最良の順位): {vi.Count} 枚。1位 {vi.Top1} 枚、上位5位以内 {vi.Top5} 枚({vi.Top5Ratio * 100:F0}%)、上位20位以内 {vi.Top20} 枚({vi.Top20Ratio * 100:F0}%)、順位の中央値 {vi.MedianRank:F1} → {vi.Label}" + (vi.Count < 20 ? "(件数が少ない。2〜3 件の差はノイズ)" : ""));
        }
        if (ranksByKind.ContainsKey("ja") && ranksByKind.ContainsKey("en"))
        {
            // 日本語と英語の差(同じ画像どうしの、最良の順位の比較)
            var ja = perQuery.Where(q => q.Kind == "ja").GroupBy(q => q.File).ToDictionary(g => g.Key, g => g.Min(q => q.Rank));
            var en = perQuery.Where(q => q.Kind == "en").GroupBy(q => q.File).ToDictionary(g => g.Key, g => g.Min(q => q.Rank));
            var both = ja.Keys.Intersect(en.Keys).ToList();
            if (both.Count > 0)
            {
                int jaBetter = both.Count(f => ja[f] < en[f]), enBetter = both.Count(f => en[f] < ja[f]);
                report.Line($"- 日本語と英語の比較(画像単位の最良の順位。{both.Count} 枚): 日本語のほうが上位 {jaBetter} 枚、英語のほうが上位 {enBetter} 枚、同順位 {both.Count - jaBetter - enBetter} 枚。順位の中央値 日本語 {Median(both.Select(f => ja[f]).ToList()):F1}、英語 {Median(both.Select(f => en[f]).ToList()):F1}");
                report.Line("    読み方: 日本語 ≈ 英語 → テキスト側の日本語は足りている(日本語 CLIP に替えても良くならない)。日本語 ≪ 英語 → 日本語側が弱い。両方悪い → 画像側かドメイン(アニメ調のゲーム画面)の問題");
            }
        }
        // ---- precision@10 用の HTML(日本語のクエリを重複なしにまとめ、クエリごとに上位10件を並べる)
        var uniqueJa = PrecisionJudgement.UniqueQueries(items);
        if (uniqueJa.Count > 0 && !noPage)
        {
            var blocks = new List<PrecisionPage.QueryBlock>();
            foreach (string q in uniqueJa)
            {
                float[] qemb = textCache[q];
                var targetFiles = items.Where(r => r.QueryJa == q).Select(r => r.Filename).Distinct(StringComparer.Ordinal).ToList();
                var targetIdx = targetFiles.ToDictionary(f => f, f => nameToIndex[f.Normalize(NormalizationForm.FormC)], StringComparer.Ordinal);
                double[] scores = KnownItems.Scores(qemb, imageEmbs);
                int[] top = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ThenBy(i => i).Take(PrecisionJudgement.TopCount).ToArray();
                var targets = targetFiles.Select(f => (File: f, Rank: KnownItems.Rank(qemb, imageEmbs, targetIdx[f]).Rank)).ToList();
                var hits = new List<PrecisionPage.Hit>();
                for (int r = 0; r < top.Length; r++)
                {
                    string imgPath = Path.GetFullPath(Path.Combine(imagesDir, names[top[r]]));
                    if (!File.Exists(imgPath)) throw new FileNotFoundException($"画像のファイルが無い(--images-dir を確認): {imgPath}");
                    hits.Add(new PrecisionPage.Hit(r + 1, names[top[r]], scores[top[r]], new Uri(imgPath).AbsoluteUri, targetIdx.Values.Contains(top[r])));
                }
                blocks.Add(new PrecisionPage.QueryBlock(q, targets, hits));
            }
            string pageId = DateTime.Now.ToString("yyyyMMddHHmmss");
            string html = PrecisionPage.Build(blocks, "S2 precision@10 の判定(日本語のクエリ)",
                $"画像 {names.Count} 枚の中から、日本語のクエリの上位10件を並べたページ。作成 {DateTime.Now:yyyy-MM-dd HH:mm}。画像は {imagesDir} の元のファイルを参照している(コピーなし。このページは外部に公開しない)。", pageId);
            string pagePath = Path.Combine(outDir, "precision-page.html");
            File.WriteAllText(pagePath, html, new UTF8Encoding(false));
            report.Line($"precision@10 のページ: {pagePath}({uniqueJa.Count} クエリ、{html.Length / 1024} KB。ブラウザで開いてチェックを入れ、ページ下の欄を spikes/S2-siglip2/precision-judgement.tsv に貼る)");
            int inTop10 = blocks.Count(b => b.Targets.Any(t => t.Rank <= PrecisionJudgement.TopCount));
            report.Line($"  目的の画像が上位10件に入っているクエリ: {inTop10} / {blocks.Count}");
        }
        report.Line();
        report.Line("上位に出た画像が「同じ場面の別カット」なら、人が正解扱いにしてよい。そのための上位5枚のファイル名は known-items-result.tsv にある。");
        report.Line($"保存: {Path.Combine(outDir, "known-items-result.tsv")}");
        return 0;
    }

    /// <summary>S1 の実行結果の JSON(1画像 = 1ファイル)から description を取り出す。response_body → choices[0].message.content(JSON 文字列)→ description。</summary>
    private static string? ReadDescription(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        var root = doc.RootElement;
        if (!root.TryGetProperty("response_body", out var rb) || rb.ValueKind != JsonValueKind.String) return null;
        using var body = JsonDocument.Parse(rb.GetString()!);
        if (!body.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        if (!choices[0].TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var inner = JsonDocument.Parse(content.GetString()!);
            return inner.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // JSON として読めない応答は、説明文なしとして数える(件数を報告に出す)
        }
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static double Median(List<int> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }
}
