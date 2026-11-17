using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using S3Fts;

namespace S2Siglip2.Probe;

/// <summary>
/// 段階3: 製品の形での評価(README の「手順と段階」。判定の線は ADR 0037、まとめ方は 0033)。GPU は使わない(テキスト側は CPU、画像の埋め込みは gpu-measure の出力)。
/// <code>
/// dotnet run -c Release --project S2Siglip2.Probe -- stage3-eval --s1 &lt;S1 の run の出力ディレクトリ&gt; [--embeddings runs/gpu-2-cudnn]
///     [--known-items known-items.tsv] [--qrels qrels.tsv | none] [--out &lt;dir&gt;] [--failed-as-empty] [--allow-missing-text]
/// </code>
/// 経路1 = 全文検索(S3 の FtsIndex。BM25。語は空白で分けて AND)を2通り: 「tags なし」= ocr|description、「tags あり」= ocr|description|tag1、tag2、…。
/// tags のない run のときは「tags なし」だけ(「tags あり」の列は出さず、その旨を印字する)。
/// 経路2 = SigLIP2(テンプレートなしの素のクエリ。テキスト側は CPU)、統合 = RRF(k = 60 固定)を、全文検索の2通りそれぞれについて。
/// 判定の規則は、tags がある run では「tags あり」の統合に適用する(なければ「tags なし」)。
/// 同点の扱い(ADR 0033): 順位は「平均順位」(同点の塊が占める順位の平均。例: 4〜43位を40枚で分けるなら全員 23.5位)を主にする。
/// RRF の入力(全文検索・SigLIP2 の順位)も、known-item の順位も、統合の結果の同点も平均順位。ヒットなしの経路は寄与 0。「上位20位以内」= 平均順位 ≤ 20。
/// 楽観(厳密に良い画像の数 + 1)・悲観(同じか良い画像の数 + 1)の数は「同点の感度」として残す。
/// 上位10件(precision@10・判定用ページ)の切り出しが同点の塊をまたぐときだけは、表示の順が要るので画像の番号順で切り、そのクエリを summary に出す。
/// 列を分けない理由: (1) 列ごとの重みは調整するほど 39 クエリに過適合する。1 列なら重みが無い。(2) 既存の FtsIndex(テスト済み)を作り直さずに使える。
/// (3) 欄の区切り「|」とタグの区切り「、」は S3 の bigram では必ず区切りになるので、ocr の末尾と description の先頭、タグどうしの bigram がつながる偽陽性は出ない。
/// 弱点: どの欄で当たったかが分からない(製品で欄ごとの重みや表示が要るなら、本実装で列を分ける)。
/// </summary>
public static class Stage3Command
{
    public const int RrfK = 60;
    private static readonly char[] Spaces = [' ', '　', '\t'];

    /// <summary>全文検索の構成(tags なし / あり)と、そのクエリごとの経路。</summary>
    private sealed class Config(string key, string title, bool withTags, int n)
    {
        public string Key { get; } = key;                 // ファイル名の列名用
        public string Title { get; } = title;
        public bool WithTags { get; } = withTags;
        public Dictionary<string, RankedPath> Fts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RankedPath> Fused { get; } = new(StringComparer.Ordinal);
        public int ImageCount { get; } = n;
    }

    public static int Run(Args a)
    {
        string root = Path.Combine(Paths.Root, "spikes", "S2-siglip2");
        string s1Dir = a.Get("s1") ?? throw new ArgumentException("--s1(S1 の run の出力ディレクトリ)が要る");
        string embDir = a.GetOr("embeddings", Path.Combine(root, "runs", "gpu-2-cudnn"));
        string knownPath = a.GetOr("known-items", Path.Combine(root, "known-items.tsv"));
        string qrelsArg = a.GetOr("qrels", Path.Combine(root, "qrels.tsv"));
        string outDir = a.GetOr("out", Path.Combine(root, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-stage3"));
        string imagesDir = a.GetOr("images-dir", Path.Combine(Paths.Root, "spikes", "images"));
        bool allowMissing = a.Flag("allow-missing-text");   // run そのものに画像の出力が無い(1030 枚分そろっていない)ことを許す。判定は無効になる
        bool failedAsEmpty = a.Flag("failed-as-empty");     // S1 の出力はあるが失敗した(打ち切り・ループ・JSON として読めない)画像を、テキストなしとして評価に含める
        int threads = a.GetInt("threads", 4);
        a.RequireAllUsed();

        // ---- 入力(失敗した画像の一覧を summary の先頭に出すため、先に読む)
        var (names, imageEmbs) = EmbeddingStore.Read(embDir);
        int n = names.Count;
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) nameToIndex[names[i].Normalize(NormalizationForm.FormC)] = i;
        var s1 = S1Output.Load(s1Dir);
        var failedImages = s1.Failures.Where(f => nameToIndex.ContainsKey(f.File) && !s1.Docs.ContainsKey(f.File)).ToList();   // 出力はあるが失敗
        var failedSet = failedImages.Select(f => f.File).ToHashSet(StringComparer.Ordinal);
        var absentImages = names.Select(x => x.Normalize(NormalizationForm.FormC)).Where(f => !s1.Docs.ContainsKey(f) && !failedSet.Contains(f)).ToList(); // run に出力そのものが無い
        if (failedImages.Count > 0 && !failedAsEmpty)
            throw new InvalidOperationException($"S1 の出力が失敗した画像が {failedImages.Count} 枚ある(打ち切り・ループ・JSON として読めない)。テキストなしとして評価に含めるなら --failed-as-empty。例: " + string.Join(" / ", failedImages.Take(5).Select(f => f.ToString())));
        if (absentImages.Count > 0 && !allowMissing)
            throw new InvalidOperationException($"{absentImages.Count} 枚の画像に S1 の出力が無い(run にファイルが無い。失敗した {failedImages.Count} 枚は別)。仕組みの確認だけなら --allow-missing-text。1030 枚分がそろっていない実行は、判定に使えない。例: " + string.Join(", ", absentImages.Take(3)));
        if (s1.WithTags > 0 && s1.WithoutTags > 0) throw new InvalidDataException($"tags があるものと無いものが混ざっている({s1.WithTags} / {s1.WithoutTags})。1つの run で形がそろっていない");
        bool hasTags = s1.WithTags > 0;

        Directory.CreateDirectory(outDir);
        using var report = new Report(Path.Combine(outDir, "stage3-summary.txt"));
        report.Line($"# 段階3(製品の形の評価)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (failedImages.Count > 0)
        {
            report.Line($"★ S1 の出力が失敗した画像を、テキストなし(全文検索にヒットしない。SigLIP2 の経路には入る)として評価に含めた(--failed-as-empty): {failedImages.Count} 枚 / {n} 枚");
            foreach (var f in failedImages) report.Line("    - " + f);
            report.Line();
        }
        report.Line($"画像の埋め込み: {n} 枚({embDir})");

        var items = KnownItems.Read(knownPath);
        var rows = items.Where(r => r.QueryJa is not null).Select(r => (File: r.Filename.Normalize(NormalizationForm.FormC), Query: r.QueryJa!)).ToList();
        var uniqueQueries = PrecisionJudgement.UniqueQueries(items);
        var unknownTargets = rows.Select(r => r.File).Distinct().Where(f => !nameToIndex.ContainsKey(f)).ToList();
        if (unknownTargets.Count > 0) throw new InvalidOperationException("埋め込みに無い画像が known-items.tsv にある: " + string.Join(", ", unknownTargets.Take(5)));
        report.Line($"known-items: {rows.Count} 行(日本語クエリ。重複なし {uniqueQueries.Count} 件)、目的の画像 {rows.Select(r => r.File).Distinct().Count()} 枚({knownPath})");

        Qrels qrels;
        if (qrelsArg == "none")
        {
            qrels = Qrels.Parse([Qrels.Header]);
            report.Line("qrels: なし(--qrels none。すべて未判定として数える。仕組みの確認用)");
        }
        else
        {
            if (!File.Exists(qrelsArg)) throw new FileNotFoundException($"qrels が無い: {qrelsArg}(precision-page.html の書き出しを貼るのを待つ。仕組みの確認だけなら --qrels none)");
            qrels = Qrels.Read(qrelsArg);
            qrels.Validate(uniqueQueries, names);
            int judgedQueries = qrels.Queries.Count();
            report.Line($"qrels: {qrels.Count} 行、{judgedQueries} / {uniqueQueries.Count} クエリ、当たり {qrels.All.Count(r => r.Relevant)}、source: {string.Join("、", qrels.All.Select(r => r.Source).Distinct())}({qrelsArg})");
        }

        var inEmb = s1.Docs.Values.Where(d => nameToIndex.ContainsKey(d.File)).ToList();
        var notInEmb = s1.Docs.Values.Where(d => !nameToIndex.ContainsKey(d.File)).Select(d => d.File).ToList();
        report.Line($"S1 の出力: {s1Dir}");
        report.Line($"  読めた {s1.Docs.Count} 枚(tags あり {s1.WithTags}、tags なし {s1.WithoutTags})、読めなかった {s1.Failures.Count} 枚(うち評価の対象の画像 {failedImages.Count} 枚)、埋め込みに無い画像の出力 {notInEmb.Count} 枚(使わない)");
        bool complete = absentImages.Count == 0; // 失敗した画像は --failed-as-empty で「テキストなし」として含めたので、そろっているとみなす(run に出力そのものが無い画像だけが「欠け」)
        report.Line($"  全文検索の対象: {inEmb.Count} / {n} 枚(テキストなし {n - inEmb.Count} 枚 = 失敗 {failedImages.Count} 枚 + run に出力が無い {absentImages.Count} 枚。全文検索にヒットしない)");
        if (!complete) report.Line($"  ★ この実行は入力がそろっていない(run に出力が無い画像 {absentImages.Count} 枚。--allow-missing-text)。数値は仕組みの確認用で、判定には使えない");
        if (hasTags) report.Line("  tags あり: 全文検索を「tags なし(ocr|description)」と「tags あり(ocr|description|tag1、tag2、…)」の2通りで出す。判定の規則は「tags あり」に適用する");
        else report.Line("  ★ この run には tags が無い: 「tags あり」の列は出さない(全文検索は「tags なし」だけ。判定の規則はそれに適用する)");

        // ---- 経路1: 全文検索の索引(埋め込みと同じ順で入れる。id の昇順 = 画像の番号順)
        var configs = new List<Config> { new("notags", "tags なし(ocr + description)", false, n) };
        if (hasTags) configs.Add(new Config("tags", "tags あり(ocr + description + tags)", true, n));
        var indexes = new List<(Config Cfg, FtsIndex Idx, Dictionary<long, int> IdToImage)>();
        foreach (Config cfg in configs)
        {
            var idx = new FtsIndex(":memory:");
            var idToImage = new Dictionary<long, int>();
            foreach (int i in Enumerable.Range(0, n))
            {
                if (!s1.Docs.TryGetValue(names[i].Normalize(NormalizationForm.FormC), out S1Doc? d)) continue;
                try { idToImage[idx.Add(S1Output.IndexText(d, cfg.WithTags))] = i; }
                catch (ArgumentException e) { throw new InvalidDataException($"{d.File} の S1 の出力が全文検索に入れられない: {e.Message}", e); }
            }
            indexes.Add((cfg, idx, idToImage));
            report.Line($"  全文検索の索引 [{cfg.Title}]: {idToImage.Count} 文書({idx.DescribeSqlite().Split(';')[0]}、unicode61 + 文字 bigram。欄の区切り「{S1Output.FieldSeparator}」、タグの区切り「{S1Output.TagSeparator}」)");
        }

        try
        {
            // ---- 経路2: SigLIP2 のテキスト側(CPU。テンプレートなしの素のクエリ)
            string textOnnx = Paths.VerifiedModelFile("textual/model.onnx", report);
            var tok = Siglip2Tokenizer.Load(Paths.VerifiedModelFile("textual/tokenizer.json", report));
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(textOnnx)!))
            {
                string fn = Path.GetFileName(f);
                if (Paths.IsTextualExternalData(fn)) Paths.VerifiedModelFile("textual/" + fn);
            }
            using Siglip2Model textModel = Siglip2Model.OpenTextualCpu(textOnnx, threads);

            // ---- クエリごとの経路(重複なしのクエリで1回ずつ)
            var sigPath = new Dictionary<string, RankedPath>(StringComparer.Ordinal);
            foreach (string q in uniqueQueries)
            {
                string[] terms = q.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
                if (terms.Length == 0) throw new InvalidDataException($"クエリが空白だけ: 「{q}」");
                var s = RankedPath.Dense(KnownItems.Scores(textModel.EmbedText(tok.Encode(q)), imageEmbs));
                sigPath[q] = s;
                foreach (var (cfg, idx, idToImage) in indexes)
                {
                    var f = RankedPath.Sparse(n, idx.SearchAll(terms).Select(h => (idToImage[h.Id], -h.Score)));
                    cfg.Fts[q] = f;
                    cfg.Fused[q] = RankedPath.Rrf([f, s], RrfK);
                }
            }
            Evaluate(report, outDir, imagesDir, n, names, nameToIndex, rows, uniqueQueries, qrels, configs, sigPath, hasTags, complete, embDir, s1.Docs);
        }
        finally
        {
            foreach (var (_, idx, _) in indexes) idx.Dispose();
        }
        return 0;
    }

    /// <summary>順位の表示。整数なら整数、同点の平均順位は小数 1 桁(23.5)。カルチャに依らず「.」。</summary>
    private static string Fmt(double r) => r.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Rk(double? r) => r is { } v ? Fmt(v) : "-";

    /// <summary>
    /// 同点の扱いで known-item の上位20位以内の件数が変わるかを調べる(主は平均順位。楽観・悲観は感度)。順位は楽観(厳密に良い画像の数 + 1)・平均・悲観(スコアが同じか良いほかの画像の数 + 1)で数える。
    /// 統合は3通り: 「入力は平均順位(主)」/「入力を楽観」= 入力の経路の順位も楽観にして統合 / 「入力を悲観」= 入力の経路の順位も悲観にして統合(どれも、統合結果の同点は楽観・平均・悲観のそれぞれで数える)。
    /// 全文検索の BM25 の同点の塊の大きさも出す。結果は stage3-known-items-ties.tsv にも書く。
    /// </summary>
    private static void TieSensitivity(Report report, string outDir, List<(string File, string Query)> rows, Dictionary<string, int> nameToIndex,
        List<string> uniqueQueries, List<Config> configs, Dictionary<string, RankedPath> sigPath)
    {
        // 経路の一覧: (名前, クエリ → 経路)
        var paths = new List<(string Name, Func<string, RankedPath> Get)>();
        foreach (Config c in configs) { Config cc = c; paths.Add(($"全文検索 [{cc.Title}]", q => cc.Fts[q])); }
        paths.Add(("SigLIP2", q => sigPath[q]));
        foreach (Config c in configs) { Config cc = c; paths.Add(($"統合(入力は平均順位。主) [{cc.Title}]", q => cc.Fused[q])); }
        foreach (Config c in configs) { Config cc = c; paths.Add(($"統合(入力を楽観) [{cc.Title}]", q => RankedPath.Rrf([cc.Fts[q], sigPath[q]], RrfK, TieMode.Optimistic))); }
        foreach (Config c in configs) { Config cc = c; paths.Add(($"統合(入力を悲観) [{cc.Title}]", q => RankedPath.Rrf([cc.Fts[q], sigPath[q]], RrfK, TieMode.Pessimistic))); }
        var cache = new Dictionary<(int, string), RankedPath>();
        RankedPath Get(int pi, string q)
        {
            if (!cache.TryGetValue((pi, q), out RankedPath? p)) cache[(pi, q)] = p = paths[pi].Get(q);
            return p;
        }

        report.Line();
        report.Line("## 同点の感度(known-item の上位20位以内。楽観 = 厳密に良い画像の数 + 1、平均 = 同点の塊の平均順位(主)、悲観 = スコアが同じか良いほかの画像の数 + 1)");
        var tsv = new StringBuilder("filename\tquery_ja");
        foreach (var (name, _) in paths) tsv.Append('\t').Append(name).Append("_楽観\t").Append(name).Append("_平均\t").Append(name).Append("_悲観\t").Append(name).Append("_同点の画像数");
        tsv.Append('\n');
        var flips = new List<string>();
        var counts = new (int Opt, int Mean, int Pess)[paths.Count];
        foreach (var (file, query) in rows)
        {
            int img = nameToIndex[file];
            tsv.Append(file).Append('\t').Append(query);
            for (int pi = 0; pi < paths.Count; pi++)
            {
                RankedPath p = Get(pi, query);
                if (!p.Present[img]) { tsv.Append("\t-\t-\t-\t-"); continue; }
                int opt = p.Rank[img], pess = p.PessimisticRank[img], tie = p.TieSize[img];
                double mean = p.MeanRank[img];
                tsv.Append('\t').Append(opt).Append('\t').Append(Fmt(mean)).Append('\t').Append(pess).Append('\t').Append(tie);
                if (opt <= 20) counts[pi].Opt++;
                if (mean <= 20) counts[pi].Mean++;
                if (pess <= 20) counts[pi].Pess++;
                if ((opt <= 20) != (pess <= 20)) flips.Add($"    {paths[pi].Name}: 「{query}」({file}) 楽観 {opt}位 / 平均 {Fmt(mean)}位 / 悲観 {pess}位(同点の画像 {tie} 枚)");
            }
            tsv.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "stage3-known-items-ties.tsv"), tsv.ToString(), new UTF8Encoding(false));
        report.Line($"- 上位20位以内の件数(クエリ単位 {rows.Count} 件。全文検索のヒットなしは圏外):");
        for (int pi = 0; pi < paths.Count; pi++)
            report.Line($"    {paths[pi].Name}: 楽観 {counts[pi].Opt} / 平均 {counts[pi].Mean} / 悲観 {counts[pi].Pess}" + (counts[pi].Opt == counts[pi].Pess ? "(同じ)" : $"(楽観 − 悲観 = {counts[pi].Opt - counts[pi].Pess})"));
        if (flips.Count == 0) report.Line("- 楽観と悲観で上位20位の内か外かが変わるクエリ: なし");
        else { report.Line($"- 楽観と悲観で上位20位の内か外かが変わるもの: {flips.Count} 件"); foreach (string f in flips) report.Line(f); }

        // 同点の塊の大きさ(ヒットした画像の中。全クエリ = 重複なしの日本語クエリ)
        report.Line($"- 同点の塊の大きさ(全クエリ {uniqueQueries.Count} 件。「20位をまたぐ」= 20位と21位が同点で、塊の一部が20位の外にある):");
        for (int pi = 0; pi < paths.Count; pi++)
        {
            int withTies = 0, straddle = 0, maxTie = 0, topTies = 0, tieHits = 0;
            string maxQuery = "";
            foreach (string q in uniqueQueries)
            {
                RankedPath p = Get(pi, q);
                bool any = false;
                foreach (int i in p.Order)
                {
                    if (p.TieSize[i] > 1) { any = true; tieHits++; }
                    if (p.TieSize[i] > maxTie) { maxTie = p.TieSize[i]; maxQuery = q; }
                }
                if (any) withTies++;
                if (p.Order.Length > 20 && p.Score[p.Order[19]] == p.Score[p.Order[20]]) straddle++;
                if (p.Order.Length > 0 && p.TieSize[p.Order[0]] > 1) topTies++;
            }
            report.Line($"    {paths[pi].Name}: 同点を含むクエリ {withTies} 件、1位が同点のクエリ {topTies} 件、20位をまたぐクエリ {straddle} 件、最大の塊 {maxTie} 枚(「{maxQuery}」)、同点の塊に入っている画像の延べ {tieHits}");
        }
        int targetTies = rows.Count(r => paths.Select((_, pi) => Get(pi, r.Query)).Any(p => p.Present[nameToIndex[r.File]] && p.TieSize[nameToIndex[r.File]] > 1));
        report.Line($"- known-item の対象画像が同点の塊に入っている行(どれかの経路で): {targetTies} / {rows.Count}。一覧と詳細は stage3-known-items-ties.tsv");
    }

    private static void Evaluate(Report report, string outDir, string imagesDir, int n, List<string> names, Dictionary<string, int> nameToIndex,
        List<(string File, string Query)> rows, List<string> uniqueQueries, Qrels qrels, List<Config> configs, Dictionary<string, RankedPath> sigPath,
        bool hasTags, bool complete, string embDir, IReadOnlyDictionary<string, S1Doc> docs)
    {
        Config noTags = configs[0];
        Config primary = configs[^1]; // 判定の規則を適用する構成(tags があれば「tags あり」、なければ「tags なし」)

        // 参考: rank コマンド(テンプレートなしの SigLIP2 だけの結果)と順位が一致するか
        string prevTsv = Path.Combine(embDir, "known-items-result.tsv");
        if (File.Exists(prevTsv))
        {
            int compared = 0, differ = 0;
            foreach (string line in File.ReadLines(prevTsv, new UTF8Encoding(false, true)).Skip(1))
            {
                string[] c = line.Split('\t');
                if (c.Length < 4 || c[0] != "ja") continue;
                var hit = rows.FirstOrDefault(r => r.File == c[1].Normalize(NormalizationForm.FormC) && r.Query == c[2]);
                if (hit == default) continue;
                compared++;
                if (sigPath[hit.Query].Rank[nameToIndex[hit.File]] != int.Parse(c[3])) differ++;
            }
            report.Line($"SigLIP2 経路の順位を、{prevTsv} の日本語の行と突き合わせ: {compared} 行中、食い違い {differ}" + (differ > 0 ? "  ★ 食い違いがある(embeddings か tokenizer が変わった?)" : "(一致)"));
        }

        // ---- known-item(クエリ単位 = 1行 = 1クエリ。画像単位 = その画像のクエリのうち最良の順位)
        int noHitRank = n + 1; // 全文検索で目的の画像がヒットしなかったときの順位(統計用。「ヒットなし」で件数も出す)
        var perRow = rows.Select(r =>
        {
            int idx = nameToIndex[r.File];
            return (r.File, r.Query, Sig: sigPath[r.Query].MeanRank[idx],
                    Fts: configs.Select(c => c.Fts[r.Query].MeanRankOf(idx)).ToArray(),
                    Fused: configs.Select(c => c.Fused[r.Query].MeanRank[idx]).ToArray());
        }).ToList();

        report.Line();
        report.Line($"## known-item(画像 {n} 枚の中での順位。ランダムなら上位20位に入る確率は約 {20.0 / n * 100:F1}%。順位は同点の塊の平均順位(同点なしなら「厳密に良い画像の数 + 1」)。統合(RRF)の入力も平均順位)");
        var sbRows = new StringBuilder("filename\tquery_ja");
        foreach (Config c in configs) sbRows.Append("\tfts_").Append(c.Key).Append("_mean_rank");
        sbRows.Append("\tsiglip2_mean_rank");
        foreach (Config c in configs) sbRows.Append("\tfused_").Append(c.Key).Append("_mean_rank");
        sbRows.Append('\n');
        foreach (var r in perRow)
        {
            sbRows.Append(r.File).Append('\t').Append(r.Query);
            foreach (var x in r.Fts) sbRows.Append('\t').Append(Rk(x));
            sbRows.Append('\t').Append(Fmt(r.Sig));
            foreach (double x in r.Fused) sbRows.Append('\t').Append(Fmt(x));
            sbRows.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "stage3-known-items.tsv"), sbRows.ToString(), new UTF8Encoding(false));
        for (int ci = 0; ci < configs.Count; ci++)
        {
            int k = ci;
            KnownLine(report, $"全文検索 [{configs[ci].Title}](bigram、BM25)", perRow.Select(r => (r.File, Rank: r.Fts[k] ?? noHitRank)).ToList(), $"ヒットなし {perRow.Count(r => r.Fts[k] is null)} 件は 順位 {noHitRank}(圏外)として数える");
        }
        KnownLine(report, "SigLIP2(テキスト側 CPU、素のクエリ)", perRow.Select(r => (r.File, Rank: r.Sig)).ToList(), null);
        for (int ci = 0; ci < configs.Count; ci++)
        {
            int k = ci;
            KnownLine(report, $"統合 [{configs[ci].Title}](RRF、k = {RrfK})", perRow.Select(r => (r.File, Rank: r.Fused[k])).ToList(), null);
        }
        int[] sigOnlyBy = new int[configs.Count];
        for (int ci = 0; ci < configs.Count; ci++)
        {
            int k = ci;
            int bothTop = perRow.Count(r => r.Sig <= 20 && r.Fts[k] is <= 20);
            int sigOnly = perRow.Count(r => r.Sig <= 20 && !(r.Fts[k] is <= 20));
            int ftsOnly = perRow.Count(r => !(r.Sig <= 20) && r.Fts[k] is <= 20);
            int neither = perRow.Count - bothTop - sigOnly - ftsOnly;
            sigOnlyBy[ci] = sigOnly;
            report.Line($"- 上位20位以内の内訳 [{configs[ci].Title}](クエリ単位 {perRow.Count} 件): 両方 {bothTop}、SigLIP2 だけ {sigOnly}、全文検索だけ {ftsOnly}、どちらも外れ {neither}。統合で拾う {perRow.Count(r => r.Fused[k] <= 20)}");
        }
        report.Line("  「SigLIP2 だけが拾う known-item」= SigLIP2 で上位20位以内、かつ全文検索で上位20位以内でない(ヒットなしを含む)クエリの数(クエリ単位)");
        TieSensitivity(report, outDir, rows, nameToIndex, uniqueQueries, configs, sigPath);

        // ---- tags の純寄与(known-item)
        if (hasTags)
        {
            Config tg = configs[1];
            report.Line();
            report.Line("## tags の純寄与(「tags あり」と「tags なし」の差)");
            var gained = perRow.Where(r => r.Fts[1] is <= 20 && !(r.Fts[0] is <= 20)).ToList();
            var lost = perRow.Where(r => r.Fts[0] is <= 20 && !(r.Fts[1] is <= 20)).ToList();
            report.Line($"- known-item(クエリ単位。全文検索の上位20位以内): tags ありだけで拾えた {gained.Count} 件、tags を足すと落ちた {lost.Count} 件(両方で拾えた {perRow.Count(r => r.Fts[0] is <= 20 && r.Fts[1] is <= 20)} 件)");
            foreach (var r in gained) report.Line($"    + 「{r.Query}」 {Rk(r.Fts[0])}位 → {Rk(r.Fts[1])}位({r.File})");
            foreach (var r in lost) report.Line($"    − 「{r.Query}」 {Rk(r.Fts[0])}位 → {Rk(r.Fts[1])}位({r.File})");
            var gainedF = perRow.Count(r => r.Fused[1] <= 20 && !(r.Fused[0] <= 20));
            var lostF = perRow.Count(r => r.Fused[0] <= 20 && !(r.Fused[1] <= 20));
            report.Line($"- 統合の上位20位以内: tags ありだけで拾えた {gainedF} 件、tags を足すと落ちた {lostF} 件");

            // tags を足すと新たにヒットした画像(= tags にだけ語が含まれていた画像)と、qrels での判定
            int onlyTotal = 0, onlyRel = 0, onlyMiss = 0, onlyUnj = 0, top10Total = 0, top10Rel = 0, top10Miss = 0, top10Unj = 0;
            var missList = new List<string>();
            foreach (string q in uniqueQueries)
            {
                RankedPath a0 = noTags.Fts[q], b1 = tg.Fts[q];
                var top10 = b1.Top(PrecisionJudgement.TopCount).ToHashSet();
                for (int i = 0; i < n; i++)
                {
                    if (!b1.Present[i] || a0.Present[i]) continue;
                    onlyTotal++;
                    bool? j = qrels.TryGet(q, names[i]);
                    bool inTop = top10.Contains(i);
                    if (inTop) top10Total++;
                    if (j is null) { onlyUnj++; if (inTop) top10Unj++; }
                    else if (j.Value) { onlyRel++; if (inTop) top10Rel++; }
                    else
                    {
                        onlyMiss++; if (inTop) top10Miss++;
                        if (missList.Count < 30) missList.Add($"    × 「{q}」 {names[i]}(tags ありの全文 {Fmt(b1.MeanRank[i])}位{(inTop ? "、上位10件" : "")})");
                    }
                }
            }
            report.Line($"- tags にだけ語が含まれていた画像(tags を足すと新たに全文検索にヒットした画像): 全 {onlyTotal} 組(クエリ × 画像)。qrels で 当たり {onlyRel}、外れ {onlyMiss}、未判定 {onlyUnj}");
            report.Line($"    うち「tags あり」の全文検索の上位10件に入ったもの: {top10Total} 組(当たり {top10Rel}、外れ {top10Miss}、未判定 {top10Unj})");
            report.Line($"    **tags 由来の誤ヒット**(qrels で外れと判定された、tags にだけ語が含まれていた画像)= {onlyMiss} 組(うち上位10件 {top10Miss} 組)" + (onlyUnj > 0 ? $"。未判定 {onlyUnj} 組が残っているので、これは下限" : ""));
            foreach (string l in missList) report.Line(l);
            if (onlyMiss > missList.Count) report.Line($"    …(ほか {onlyMiss - missList.Count} 組。stage3-known-items.tsv ではなく、上の件数で確認する)");
        }

        // ---- precision@10(qrels。未判定は外れとして数える。未判定の数を併記)
        report.Line();
        report.Line($"## precision@{PrecisionJudgement.TopCount}(qrels による。{uniqueQueries.Count} クエリ。未判定は外れとして数える=下限。未判定がすべて当たりなら=上限)");
        Stage3Precision.Summary Prec(Func<string, RankedPath> path) =>
            Stage3Precision.Compute(uniqueQueries.Select(q => (q, (IReadOnlyList<string>)path(q).Top(PrecisionJudgement.TopCount).Select(i => names[i]).ToList())), qrels);
        var pSig = Prec(q => sigPath[q]);
        var pFts = configs.Select(c => Prec(q => c.Fts[q])).ToArray();
        var pFused = configs.Select(c => Prec(q => c.Fused[q])).ToArray();
        var sbPrec = new StringBuilder("query_ja\tpath\trelevant\tjudged_miss\tunjudged\treturned\n");
        void Line(string title, string key, Stage3Precision.Summary p)
        {
            report.Line($"- {title}: 平均 {p.MeanLower:F3}(下限)〜 {p.MeanUpper:F3}(上限)。当たり {p.Relevant}、判定済みの外れ {p.JudgedMiss}、未判定 {p.Unjudged}、経路が返した件数 {p.Returned} / {p.Slots} 枠");
            foreach (var q in p.PerQuery) sbPrec.Append(q.Query).Append('\t').Append(key).Append('\t').Append(q.Relevant).Append('\t').Append(q.JudgedMiss).Append('\t').Append(q.Unjudged).Append('\t').Append(q.Returned).Append('\n');
        }
        for (int ci = 0; ci < configs.Count; ci++) Line($"全文検索 [{configs[ci].Title}]", "fts_" + configs[ci].Key, pFts[ci]);
        Line("SigLIP2", "siglip2", pSig);
        for (int ci = 0; ci < configs.Count; ci++) Line($"統合(RRF) [{configs[ci].Title}]", "fused_" + configs[ci].Key, pFused[ci]);
        File.WriteAllText(Path.Combine(outDir, "stage3-precision.tsv"), sbPrec.ToString(), new UTF8Encoding(false));

        // 上位10件の切り出しが同点の塊をまたぐクエリ(またぐ分は画像の番号順で切っている。意味のある順ではない)
        report.Line($"- 上位{PrecisionJudgement.TopCount}件の切り出しが同点の塊をまたぐクエリ({PrecisionJudgement.TopCount}位と{PrecisionJudgement.TopCount + 1}位が同点。またぐ分は画像の番号順で切っている。precision@10 と判定用ページに影響する):");
        void Straddle(string title, Func<string, RankedPath> path)
        {
            var qs = uniqueQueries.Where(q => path(q).TieStraddles(PrecisionJudgement.TopCount)).ToList();
            report.Line($"    {title}: {qs.Count} クエリ" + (qs.Count == 0 ? "" : "(" + string.Join("、", qs.Select(q => $"「{q}」")) + ")"));
        }
        foreach (Config c in configs) Straddle($"全文検索 [{c.Title}]", q => c.Fts[q]);
        Straddle("SigLIP2", q => sigPath[q]);
        foreach (Config c in configs) Straddle($"統合(RRF) [{c.Title}]", q => c.Fused[q]);

        // ---- カスケード(探索的な診断。判定には使わない)
        CascadeDiagnostic(report, outDir, nameToIndex, rows, uniqueQueries, configs, sigPath, Prec, pFused);

        int pi = configs.Count - 1; // 判定の規則を適用する構成の添字
        double dropLower = pFused[pi].MeanLower - pFts[pi].MeanLower;
        report.Line($"- SigLIP2 を外したときの統合の precision@10 の下がり [{primary.Title}](= 統合 − 全文検索のみ。どちらも下限): {dropLower:F3}(統合 {pFused[pi].MeanLower:F3} − 全文検索 {pFts[pi].MeanLower:F3})");
        report.Line("  (統合から SigLIP2 を外すと全文検索だけの順位になるので、「SigLIP2 を外した統合」= 全文検索のみ)");

        if (hasTags)
        {
            report.Line();
            report.Line("## 「tags あり」は「tags なし」を precision@10 で下回るか(下回ったときだけ、2回目のプロンプト修正を認める規則のため)");
            string Cmp(string title, Stage3Precision.Summary a0, Stage3Precision.Summary b1)
            {
                string Word(int x, int y) => y < x ? "下回る" : y > x ? "上回る" : "同じ";
                string lower = Word(a0.Relevant, b1.Relevant);
                string upper = Word(a0.Relevant + a0.Unjudged, b1.Relevant + b1.Unjudged);
                string s = $"- {title}: tags なし {a0.MeanLower:F3} → tags あり {b1.MeanLower:F3}(下限どうし。当たり {a0.Relevant} → {b1.Relevant} 枠)→ **{lower}**";
                if (a0.Unjudged > 0 || b1.Unjudged > 0)
                    s += $"。未判定が残っている(なし {a0.Unjudged}、あり {b1.Unjudged} 枠)。上限どうし(未判定がすべて当たりなら)は {a0.MeanUpper:F3} → {b1.MeanUpper:F3} で {upper}" + (lower != upper ? "  ★ 下限と上限で向きが違う。判定用ページで未判定を埋めるまで確定しない" : "");
                return s;
            }
            report.Line(Cmp("全文検索のみ", pFts[0], pFts[1]));
            report.Line(Cmp("統合(RRF)", pFused[0], pFused[1]));
        }

        // ---- 判定(段階3の規則。ADR 0033・0037)
        Stage3Verdict.Result Decide(int ci)
        {
            int k = ci;
            var ev = KnownItems.EvaluateMean(perRow.Select(r => r.Fused[k]).ToList());
            return Stage3Verdict.Decide(new Stage3Verdict.Input(pFused[k].Relevant, uniqueQueries.Count, pFts[k].Relevant, ev.Top20, perRow.Count, sigOnlyBy[k]));
        }
        var verdict = Decide(pi);
        report.Line();
        report.Line($"## 判定(段階3の規則。ADR 0033・0037。上の数値で機械的に出した。適用した構成: {primary.Title})");
        report.Line("  規則: 統合で precision@10 ≥ 0.7 かつ known-item の上位20位以内 ≥ 2/3 → 製品の検索は成り立つ(ADR 0011 の方針は維持)。統合で precision@10 < 0.4 → 日本語 CLIP を同じ tsv で測り、説明文のテキスト埋め込みも試す。");
        report.Line("        その間 → SigLIP2 を外しても統合の precision@10 の下がりが 0.05 未満 かつ SigLIP2 だけが拾う known-item が3件未満 → 経路2は「関連度低」として下に分け、主役は全文検索。それ以上 → 両方残して調整する。");
        report.Line($"  結果: [{verdict.Branch}] {verdict.Text}");
        report.Line($"  根拠: {verdict.Detail}");
        if (hasTags)
        {
            var v0 = Decide(0);
            report.Line($"  (参考。規則は「tags あり」に適用するのが決まり。tags なしに適用すると: [{v0.Branch}] {v0.Text})");
        }
        if (!complete) report.Line("  ★ 入力がそろっていない実行なので、この判定は無効(仕組みの確認用)");
        else if (pFused[pi].Unjudged > 0 || pFts[pi].Unjudged > 0) report.Line($"  注意: 未判定が残っている(統合 {pFused[pi].Unjudged}、全文検索 {pFts[pi].Unjudged} 枠)。判定は下限の値で出した。判定用ページで埋めると変わりうる");
        report.Line("  クエリの代表性: 今の45件は「見た目で探すなら」という条件で作ったので SigLIP2 寄りで、セリフや文脈で探すクエリがない。全文検索に不利な偏りがある(「最近実際に探したもの」の検索語は、足していない)");

        ExtraJudgementAnalysis(report, outDir, names, nameToIndex, uniqueQueries, qrels, primary, sigPath, docs);

        // ---- 判定用ページ(各経路と統合の上位10件の合併のうち、qrels にない画像だけ)
        var blocks = new List<QrelsPage.QueryBlock>();
        int candidatesTotal = 0;
        foreach (string q in uniqueQueries)
        {
            var targets = rows.Where(r => r.Query == q).Select(r => r.File).Distinct(StringComparer.Ordinal).ToList();
            var targetIdx = targets.Select(t => nameToIndex[t]).ToHashSet();
            var union = new List<int>();
            var all = new List<RankedPath> { primary.Fused[q] };
            all.AddRange(configs.SelectMany(c => new[] { c.Fts[q], c.Fused[q] }));
            all.Add(sigPath[q]);
            foreach (var path in all)
                foreach (int i in path.Top(PrecisionJudgement.TopCount)) if (!union.Contains(i)) union.Add(i);
            int already = union.Count(i => qrels.TryGet(q, names[i]) is not null);
            var cands = union.Where(i => qrels.TryGet(q, names[i]) is null).OrderBy(i => primary.Fused[q].MeanRank[i]).ThenBy(i => i).ToList();
            if (cands.Count == 0) continue;
            var list = new List<QrelsPage.Candidate>();
            foreach (int i in cands)
            {
                string imgPath = Path.GetFullPath(Path.Combine(imagesDir, names[i]));
                if (!File.Exists(imgPath)) throw new FileNotFoundException($"画像のファイルが無い(--images-dir を確認): {imgPath}");
                string routes = hasTags
                    ? $"統合 あり{Fmt(configs[1].Fused[q].MeanRank[i])}位・なし{Fmt(configs[0].Fused[q].MeanRank[i])}位 / 全文 あり{Pos(configs[1].Fts[q], i)}・なし{Pos(configs[0].Fts[q], i)} / 画像 {Fmt(sigPath[q].MeanRank[i])}位"
                    : $"統合 {Fmt(configs[0].Fused[q].MeanRank[i])}位 / 全文 {Pos(configs[0].Fts[q], i)} / 画像 {Fmt(sigPath[q].MeanRank[i])}位";
                list.Add(new QrelsPage.Candidate(names[i], new Uri(imgPath).AbsoluteUri, routes, targetIdx.Contains(i)));
            }
            candidatesTotal += list.Count;
            blocks.Add(new QrelsPage.QueryBlock(q, targets, already, list));
        }
        report.Line();
        string pagePath = Path.Combine(outDir, "qrels-judge-page.html");
        if (blocks.Count == 0)
        {
            report.Line("判定用ページ: 作らない(各経路と統合の上位10件が、すべて qrels に入っている)");
        }
        else
        {
            string pageId = DateTime.Now.ToString("yyyyMMddHHmmss");
            string html = QrelsPage.Build(blocks, "S2 段階3 の判定(qrels の追加)",
                $"画像 {n} 枚の中から、全文検索{(hasTags ? "(tags あり・なし)" : "")}・SigLIP2・統合の上位10件のうち、qrels にまだ無い画像を並べたページ。作成 {DateTime.Now:yyyy-MM-dd HH:mm}。画像は {imagesDir} の元のファイルを参照している(コピーなし。このページは外部に公開しない)。",
                pageId, "stage3-judge:" + pageId);
            File.WriteAllText(pagePath, html, new UTF8Encoding(false));
            report.Line($"判定用ページ: {pagePath}({blocks.Count} クエリ、未判定の画像 {candidatesTotal} 枚、{html.Length / 1024} KB。ブラウザで開いてチェックを入れ、ページ下の欄を spikes/S2-siglip2/qrels.tsv の末尾に貼り足す)");
        }
        report.Line($"保存: {Path.Combine(outDir, "stage3-known-items.tsv")}、{Path.Combine(outDir, "stage3-precision.tsv")}");
    }

    /// <summary>
    /// カスケード(RRF とは別の並べ方。探索的な診断で、判定には使わない)。全文検索のヒットを全文検索の順位で先に並べ、残りを SigLIP2 の順で埋める。
    /// 同じ 39 クエリ・同じ qrels で後から並べ方を選ぶので、過適合の恐れがある(本実装の調整の方向を見る材料)。
    /// </summary>
    private static void CascadeDiagnostic(Report report, string outDir, Dictionary<string, int> nameToIndex, List<(string File, string Query)> rows,
        List<string> uniqueQueries, List<Config> configs, Dictionary<string, RankedPath> sigPath,
        Func<Func<string, RankedPath>, Stage3Precision.Summary> prec, Stage3Precision.Summary[] pFused)
    {
        report.Line();
        report.Line("## カスケード(探索的な診断。判定には使わない。全文検索のヒットを全文検索の順位で先に並べ、残りを SigLIP2 の順で埋める)");
        report.Line("  ★ 同じ 39 クエリ・同じ qrels で並べ方を後から選ぶので、過適合の恐れがある。数値は RRF との比較の材料であって、製品の性能の見積もりではない");
        var tsv = new StringBuilder("query_ja");
        var cols = new List<string>();
        var cascadeRows = new Dictionary<string, (Stage3Precision.Summary Rrf, Stage3Precision.Summary Cascade)>();
        for (int ci = 0; ci < configs.Count; ci++)
        {
            Config c = configs[ci];
            var cas =uniqueQueries.ToDictionary(q => q, q => RankedPath.Cascade(c.Fts[q], sigPath[q]), StringComparer.Ordinal);
            var pc = prec(q => cas[q]);
            report.Line($"- カスケード [{c.Title}]: precision@10 = {pc.MeanLower:F3}(当たり {pc.Relevant}、判定済みの外れ {pc.JudgedMiss}、未判定 {pc.Unjudged}、返した件数 {pc.Returned}/{pc.Slots})"
                + (pc.Unjudged > 0 ? "  ★ 未判定がある(下限〜上限: " + $"{pc.MeanLower:F3}〜{pc.MeanUpper:F3})" : "(未判定 0)")
                + $"。RRF(統合)は {pFused[ci].MeanLower:F3}(当たり {pFused[ci].Relevant})");
            var straddle = uniqueQueries.Where(q => cas[q].TieStraddles(PrecisionJudgement.TopCount)).ToList();
            report.Line($"    上位{PrecisionJudgement.TopCount}件の切り出しが同点の塊をまたぐクエリ: {straddle.Count} 件" + (straddle.Count == 0 ? "" : "(" + string.Join("、", straddle.Select(q => $"「{q}」")) + ")"));
            // known-item
            var kc = rows.Select(r => (r.File, r.Query, Rank: cas[r.Query].MeanRank[nameToIndex[r.File]])).ToList();
            KnownLine(report, $"カスケード [{c.Title}](全文検索のヒット全件を先に、その後に SigLIP2 の順)", kc.Select(r => (r.File, r.Rank)).ToList(), null);
            var rrfRanks = rows.Select(r => c.Fused[r.Query].MeanRank[nameToIndex[r.File]]).ToList();
            int rrfTop = rrfRanks.Count(x => x <= 20), casTop = kc.Count(x => x.Rank <= 20);
            report.Line($"    known-item の上位20位以内: カスケード {casTop}/{kc.Count}、RRF(統合) {rrfTop}/{kc.Count}");
            for (int r = 0; r < kc.Count; r++)
                if ((kc[r].Rank <= 20) != (rrfRanks[r] <= 20))
                    report.Line($"      {(kc[r].Rank <= 20 ? "カスケードだけ内" : "RRF だけ内")}: 「{kc[r].Query}」({kc[r].File}) カスケード {Fmt(kc[r].Rank)}位 / RRF {Fmt(rrfRanks[r])}位");
            // precision@10 の上がった・下がったクエリ
            var up = new List<string>(); var down = new List<string>();
            for (int qi = 0; qi < pc.PerQuery.Count; qi++)
            {
                var a = pFused[ci].PerQuery[qi]; var b = pc.PerQuery[qi];
                if (a.Query != b.Query) throw new InvalidOperationException("クエリの並びが違う");
                if (b.Relevant > a.Relevant) up.Add($"「{b.Query}」{a.Relevant / 10.0:F1}→{b.Relevant / 10.0:F1}");
                else if (b.Relevant < a.Relevant) down.Add($"「{b.Query}」{a.Relevant / 10.0:F1}→{b.Relevant / 10.0:F1}");
            }
            report.Line($"    RRF からカスケードで precision@10 が上がったクエリ {up.Count} 件: {string.Join("、", up)}");
            report.Line($"    下がったクエリ {down.Count} 件: {string.Join("、", down)}(同じ {pc.PerQuery.Count - up.Count - down.Count} 件)");
            cols.Add(c.Key);
            tsv.Append($"\trrf_{c.Key}\tcascade_{c.Key}");
            cascadeRows[c.Key] = (pFused[ci], pc);
        }
        tsv.Append('\n');
        for (int qi = 0; qi < uniqueQueries.Count; qi++)
        {
            tsv.Append(uniqueQueries[qi]);
            foreach (string key in cols)
            {
                var (a, b) = cascadeRows[key];
                tsv.Append('\t').Append((a.PerQuery[qi].Relevant / 10.0).ToString("0.0", CultureInfo.InvariantCulture)).Append('\t').Append((b.PerQuery[qi].Relevant / 10.0).ToString("0.0", CultureInfo.InvariantCulture));
            }
            tsv.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "stage3-cascade-by-query.tsv"), tsv.ToString(), new UTF8Encoding(false));
        report.Line($"  1クエリずつの precision@10(RRF とカスケード): {Path.Combine(outDir, "stage3-cascade-by-query.tsv")}");
    }

    private const string FirstJudgeSource = "precision-page:";

    /// <summary>語の位置の判定用の正規化: S3 と同じ NFKC + 小文字化をして、空白を除く(S3 は日本語の文字の間の空白・改行を除く。ここは近似)。</summary>
    private static string Squash(string s) => new string(NormalizedText.Create(s).Text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>クエリの各語(空白区切り)が、ocr / description / tags のどこに含まれるか(語ごとの和)。「|」「、」で区切った各欄の中での部分文字列(近似)。</summary>
    private static (bool Ocr, bool Description, bool Tags) FieldsContaining(string query, S1Doc d)
    {
        bool o = false, de = false, t = false;
        string ocr = Squash(d.Ocr), desc = Squash(d.Description);
        string[] tags = (d.Tags ?? []).Select(Squash).ToArray();
        foreach (string term in query.Split(Spaces, StringSplitOptions.RemoveEmptyEntries).Select(Squash))
        {
            if (ocr.Contains(term, StringComparison.Ordinal)) o = true;
            if (desc.Contains(term, StringComparison.Ordinal)) de = true;
            if (tags.Any(x => x.Contains(term, StringComparison.Ordinal))) t = true;
        }
        return (o, de, t);
    }

    private static string FieldLabel((bool Ocr, bool Description, bool Tags) f)
    {
        var parts = new List<string>();
        if (f.Ocr) parts.Add("ocr");
        if (f.Description) parts.Add("description");
        if (f.Tags) parts.Add("tags");
        return parts.Count == 0 ? "(どこにも無い=複数語のAND等)" : string.Join("+", parts);
    }

    /// <summary>
    /// 追加の判定(precision-page 以外のソースの行 = 判定用ページで足した、上位10件の外から来た画像)の分析。
    /// (1) 追加分が、全文検索(tags あり)・統合(tags あり)・SigLIP2 のどの上位10件から来たかと、その当たりの率。
    /// (2) 全文検索(tags あり)の上位10件に入った画像で、クエリの語が ocr / description / tags のどこに含まれていたか(追加分と、全判定済みの組)。
    /// (3) SigLIP2 の上位10件で外れた画像の VLM の description と tags を stage3-siglip2-misses.txt に書く。
    /// </summary>
    private static void ExtraJudgementAnalysis(Report report, string outDir, List<string> names, Dictionary<string, int> nameToIndex, List<string> uniqueQueries,
        Qrels qrels, Config primary, Dictionary<string, RankedPath> sigPath, IReadOnlyDictionary<string, S1Doc> docs)
    {
        var extras = qrels.All.Where(r => !r.Source.StartsWith(FirstJudgeSource, StringComparison.Ordinal)).ToList();
        if (extras.Count == 0) return;
        int top = PrecisionJudgement.TopCount;
        var ftsTop = uniqueQueries.ToDictionary(q => q, q => primary.Fts[q].Top(top).ToHashSet(), StringComparer.Ordinal);
        var fusTop = uniqueQueries.ToDictionary(q => q, q => primary.Fused[q].Top(top).ToHashSet(), StringComparer.Ordinal);
        var sigTop = uniqueQueries.ToDictionary(q => q, q => sigPath[q].Top(top).ToHashSet(), StringComparer.Ordinal);
        S1Doc? Doc(int i) => docs.TryGetValue(names[i].Normalize(NormalizationForm.FormC), out S1Doc? d) ? d : null;

        report.Line();
        report.Line($"## 追加の判定(precision-page 以外のソースの行)の分析。構成は {primary.Title}");
        int hits = extras.Count(r => r.Relevant);
        var first = qrels.All.Where(r => r.Source.StartsWith(FirstJudgeSource, StringComparison.Ordinal)).ToList();
        report.Line($"- 追加分: {extras.Count} 組(ユニークな画像 {extras.Select(r => r.File).Distinct().Count()} 枚、{extras.Select(r => r.Query).Distinct().Count()} クエリ)、当たり {hits}({hits * 100.0 / extras.Count:F1}%)。最初の判定(precision-page): {first.Count} 組、当たり {first.Count(r => r.Relevant)}({first.Count(r => r.Relevant) * 100.0 / first.Count:F1}%)。最初の判定は SigLIP2 の上位10件だけ");
        int inSig = extras.Count(r => sigTop[r.Query].Contains(nameToIndex[r.File]));
        report.Line($"- 追加分のうち SigLIP2 の上位10件に入っているもの: {inSig} 組(0 のはず。最初の判定が SigLIP2 の上位10件だったため)");
        report.Line("- 追加分の由来(その画像が、全文検索 / 統合 のどちらの上位10件に入っていたか。SigLIP2 の上位10件は最初の判定済みなので含まれない):");
        var origin = extras.GroupBy(r =>
        {
            int i = nameToIndex[r.File];
            bool f = ftsTop[r.Query].Contains(i), u = fusTop[r.Query].Contains(i);
            return f && u ? "全文検索と統合の両方" : f ? "全文検索だけ" : u ? "統合だけ" : "どちらでもない(tags なしの経路だけ)";
        }).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var g in origin) report.Line($"    {g.Key}: {g.Count()} 組、当たり {g.Count(r => r.Relevant)}({g.Count(r => r.Relevant) * 100.0 / g.Count():F1}%)");

        report.Line("- 全文検索(上位10件)に入った画像で、クエリの語が含まれていた欄(語ごとの和。各欄の中の部分文字列で判定した近似):");
        void FieldTable(string title, IEnumerable<Qrel> pairs)
        {
            var groups = pairs.Where(r => ftsTop[r.Query].Contains(nameToIndex[r.File]))
                .GroupBy(r => Doc(nameToIndex[r.File]) is { } d ? FieldLabel(FieldsContaining(r.Query, d)) : "(S1 の出力なし)")
                .OrderByDescending(g => g.Count()).ToList();
            int total = groups.Sum(g => g.Count()), th = groups.Sum(g => g.Count(r => r.Relevant));
            report.Line($"    {title}: {total} 組、当たり {th}({(total == 0 ? 0 : th * 100.0 / total):F1}%)");
            foreach (var g in groups) report.Line($"        {g.Key}: {g.Count()} 組、当たり {g.Count(r => r.Relevant)}({g.Count(r => r.Relevant) * 100.0 / g.Count():F1}%)");
        }
        FieldTable("追加分", extras);
        FieldTable("判定済みの全組", qrels.All);

        // SigLIP2 の上位10件で外れた画像
        var sb = new StringBuilder();
        sb.Append("# SigLIP2 の上位10件で外れ(qrels の relevant = 0)だった画像と、VLM の出力(description と tags)\n");
        sb.Append("# 形: クエリ / SigLIP2 の precision@10 / 外れの画像(SigLIP2 の順位、全文検索 tags あり の順位)/ description / tags\n\n");
        var perQuery = new List<(string Query, int Rel, int Miss)>();
        foreach (string q in uniqueQueries)
        {
            var top10 = sigPath[q].Top(top);
            var miss = top10.Where(i => qrels.TryGet(q, names[i]) == false).ToList();
            perQuery.Add((q, top10.Count(i => qrels.TryGet(q, names[i]) == true), miss.Count));
            sb.Append($"## 「{q}」 SigLIP2 precision@10 = {top10.Count(i => qrels.TryGet(q, names[i]) == true)}/{top}、外れ {miss.Count} 枚\n");
            foreach (int i in miss)
            {
                S1Doc? d = Doc(i);
                sb.Append($"- {names[i]}(SigLIP2 {Fmt(sigPath[q].MeanRank[i])}位、全文 {Pos(primary.Fts[q], i)})\n");
                sb.Append($"    description: {(d is null ? "(S1 の出力なし)" : d.Description.Replace('\n', ' '))}\n");
                sb.Append($"    tags: {(d is null ? "(S1 の出力なし)" : d.Tags is null ? "(tags なし)" : string.Join("、", d.Tags))}\n");
            }
            sb.Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "stage3-siglip2-misses.txt"), sb.ToString(), new UTF8Encoding(false));
        report.Line($"- SigLIP2 の上位10件で外れた画像の VLM の description と tags: {Path.Combine(outDir, "stage3-siglip2-misses.txt")}(外れの多いクエリ: " +
            string.Join("、", perQuery.OrderByDescending(x => x.Miss).Take(5).Select(x => $"「{x.Query}」{x.Miss}枚")) + ")");
    }

    private static string Pos(RankedPath p, int i) => p.MeanRankOf(i) is { } r ? Fmt(r) + "位" : "ヒットなし";

    private static void KnownLine(Report report, string title, IReadOnlyList<(string File, double Rank)> rows, string? note)
    {
        var v = KnownItems.EvaluateMean(rows.Select(r => r.Rank).ToList());
        var vi = KnownItems.EvaluateMean(rows.GroupBy(r => r.File).Select(g => g.Min(r => r.Rank)).ToList());
        report.Line($"- {title}" + (note is null ? "" : $"({note})"));
        report.Line($"    クエリ単位: {v.Count} 件。1位 {v.Top1}、上位5位以内 {v.Top5}({v.Top5Ratio * 100:F0}%)、上位20位以内 {v.Top20}({v.Top20Ratio * 100:F0}%)、順位の中央値 {v.MedianRank:F1} → {v.Label}");
        report.Line($"    画像単位(最良の順位): {vi.Count} 枚。1位 {vi.Top1}、上位5位以内 {vi.Top5}({vi.Top5Ratio * 100:F0}%)、上位20位以内 {vi.Top20}({vi.Top20Ratio * 100:F0}%)、順位の中央値 {vi.MedianRank:F1} → {vi.Label}");
    }
}
