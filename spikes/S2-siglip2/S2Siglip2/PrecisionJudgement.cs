using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace S2Siglip2;

/// <summary>
/// precision@10 による判定(known-item に足す指標。線は ADR 0037)。
/// クエリごとに上位10件の画像を並べたローカルの HTML を人が見て、当たりの枚数を数え、
/// precision-judgement.tsv(query_ja TAB relevant_count)に貼る。ここはその TSV の入出力と判定。
/// </summary>
public static class PrecisionJudgement
{
    public const int TopCount = 10;
    public const double UsableThreshold = 0.7;
    public const double UnusableThreshold = 0.4;
    // 判定は整数で行う(上の2つを 1/10 単位にしたもの)。平均 = 合計 / (10 × 件数)
    private const int UsableTenths = 7;
    private const int UnusableTenths = 4;

    public const string Header = "query_ja\trelevant_count";

    /// <summary>query_ja を重複なしにまとめる(最初に出た順)。</summary>
    public static List<string> UniqueQueries(IEnumerable<KnownItemRow> rows) =>
        rows.Where(r => r.QueryJa is not null).Select(r => r.QueryJa!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>最初から用意しておく TSV(relevant_count は空)。</summary>
    public static string Template(IEnumerable<string> queries)
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (string q in queries)
        {
            if (q.Contains('\t') || q.Contains('\n')) throw new ArgumentException($"クエリにタブか改行が入っている: {q}");
            sb.Append(q).Append('\t').Append('\n');
        }
        return sb.ToString();
    }

    public sealed record Entry(string Query, int? RelevantCount);

    /// <summary>
    /// precision-judgement.tsv を読む(UTF-8)。1行目は列名(query_ja, relevant_count)。relevant_count は空、または 0〜10 の整数。
    /// 同じクエリが2回あれば例外。想定外の形は握りつぶさず例外にする(貼り間違えたときに、黙って誤った平均を出さない)。
    /// </summary>
    public static List<Entry> Read(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        return Parse(lines, path);
    }

    public static List<Entry> Parse(IEnumerable<string> lines, string source = "(入力)")
    {
        var list = lines.Select(l => l.TrimEnd('\r')).ToList();
        if (list.Count == 0) throw new InvalidDataException($"{source} が空");
        string header = list[0].TrimStart('﻿');
        if (header != Header) throw new InvalidDataException($"{source} の1行目は「{Header}」(タブ区切り)であること: {header}");
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Length == 0) continue;
            string[] c = list[i].Split('\t');
            if (c.Length != 2) throw new InvalidDataException($"{source} の {i + 1} 行目: 列が2つでない(タブ区切りで query_ja と relevant_count): {list[i]}");
            string query = c[0];
            if (query.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: query_ja が空");
            if (!seen.Add(query)) throw new InvalidDataException($"{source} の {i + 1} 行目: クエリ「{query}」が重複している");
            string v = c[1].Trim();
            if (v.Length == 0) { entries.Add(new Entry(query, null)); continue; }
            if (!int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) || n < 0 || n > TopCount)
                throw new InvalidDataException($"{source} の {i + 1} 行目: relevant_count は 0〜{TopCount} の整数(または空): 「{c[1]}」");
            entries.Add(new Entry(query, n));
        }
        return entries;
    }

    public sealed record Result(int Judged, int Unjudged, double Mean, string Label, IReadOnlyList<(string Query, double Precision)> PerQuery);

    /// <summary>
    /// 平均の precision@10 と判定: ≥ 0.7 →「見た目の経路として使える」、&lt; 0.4 →「使えない」、その間 →「微妙」。
    /// 未判定(relevant_count が空)のクエリがあれば、allowPartial でない限り例外にする(未判定を 0 として数えない。黙って平均を歪めない)。
    /// expectedQueries を渡すと、judgement に無いクエリ・余計なクエリも例外にする。
    /// </summary>
    public static Result Evaluate(IReadOnlyList<Entry> entries, IReadOnlyCollection<string>? expectedQueries = null, bool allowPartial = false)
    {
        if (expectedQueries is not null)
        {
            var have = entries.Select(e => e.Query).ToHashSet(StringComparer.Ordinal);
            var missing = expectedQueries.Where(q => !have.Contains(q)).ToList();
            var extra = have.Where(q => !expectedQueries.Contains(q)).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                throw new InvalidDataException(
                    "判定のクエリが known-items.tsv と合わない。" +
                    (missing.Count > 0 ? $" 足りない: {string.Join("、", missing.Take(10))}。" : "") +
                    (extra.Count > 0 ? $" 余計: {string.Join("、", extra.Take(10))}。" : ""));
            }
        }
        var unjudged = entries.Where(e => e.RelevantCount is null).ToList();
        if (unjudged.Count > 0 && !allowPartial)
        {
            throw new InvalidDataException($"relevant_count が空のクエリが {unjudged.Count} 件ある(未判定を 0 として数えない): {string.Join("、", unjudged.Take(10).Select(e => e.Query))}。全部埋めるか、--allow-partial で判定済みだけの平均を出す");
        }
        var judged = entries.Where(e => e.RelevantCount is not null).ToList();
        if (judged.Count == 0) throw new InvalidDataException("判定済みのクエリが1つも無い");
        var per = judged.Select(e => (e.Query, Precision: e.RelevantCount!.Value / (double)TopCount)).ToList();
        double mean = per.Average(p => p.Precision);
        // 判定の境界は整数で比べる(0.7 ちょうどが浮動小数点の誤差で 0.6999… になって「微妙」にならないように)。
        // 平均 = 合計 / (10 × 件数) なので、平均 ≥ 0.7 ⇔ 合計 ≥ 7 × 件数、平均 < 0.4 ⇔ 合計 < 4 × 件数。
        int total = judged.Sum(e => e.RelevantCount!.Value);
        string label = total >= UsableTenths * judged.Count ? "見た目の経路として使える(平均 precision@10 ≥ 0.7)"
                     : total < UnusableTenths * judged.Count ? "使えない(平均 precision@10 < 0.4)"
                     : "微妙(平均 precision@10 が 0.4 以上 0.7 未満)";
        return new Result(judged.Count, unjudged.Count, mean, label, per);
    }
}

/// <summary>
/// クエリごとに上位10件の画像を並べた、単体で動くローカルの HTML を作る(外部の CDN・フォント・スクリプトは読まない。画像は元のファイルを file:// で参照し、コピーしない)。
/// 各画像にチェックボックスがあり、クエリごとの当たりの数と全体の平均がその場で表示される。ページの下に、
/// 全クエリの結果を「query_ja TAB relevant_count」にしたテキスト欄がある(precision-judgement.tsv に貼る)。
/// 未判定(まだ何もチェックしておらず、「当たり 0 件で確定」も押していない)のクエリは relevant_count を空で出す。
/// </summary>
public static class PrecisionPage
{
    public sealed record Hit(int Rank, string File, double Score, string ImageUrl, bool IsTarget);

    /// <param name="Query">日本語のクエリ(重複なし)。</param>
    /// <param name="Targets">このクエリを書いた目的の画像(複数ありうる)と、それぞれの順位。</param>
    /// <param name="Top">上位10件(順位の昇順)。</param>
    public sealed record QueryBlock(string Query, IReadOnlyList<(string File, int Rank)> Targets, IReadOnlyList<Hit> Top);

    public static string Build(IReadOnlyList<QueryBlock> blocks, string title, string note, string pageId)
    {
        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body data-page=\"").Append(E(pageId)).Append("\">\n");
        sb.Append("<header>\n<h1>").Append(E(title)).Append("</h1>\n<p class=\"note\">").Append(E(note)).Append("</p>\n");
        sb.Append("<p class=\"how\">各クエリの上位10件のうち、クエリに合っている(当たりの)画像にチェックを入れてください。チェックの数がそのクエリの当たりの数になります。");
        sb.Append("当たりが1件も無いクエリは「当たり 0 件で確定」を押してください(押さずにチェックもしないと、未判定のままになります)。");
        sb.Append("「同じ場面の別カット」など、目的の画像と別でも合っていれば当たりにして構いません。</p>\n");
        sb.Append("</header>\n<p class=\"summary\">判定済み <span id=\"judgedCount\">0</span> / ").Append(blocks.Count).Append(" クエリ。平均 precision@10 = <span id=\"mean\">-</span></p>\n<main>\n");

        for (int q = 0; q < blocks.Count; q++)
        {
            QueryBlock b = blocks[q];
            sb.Append("<section class=\"query\" data-q=\"").Append(q).Append("\" data-query=\"").Append(E(b.Query)).Append("\">\n");
            sb.Append("<h2><span class=\"qno\">").Append(q + 1).Append(".</span> 「").Append(E(b.Query)).Append("」</h2>\n");
            var inTop = b.Targets.Where(t => t.Rank <= b.Top.Count).ToList();
            if (inTop.Count > 0)
                sb.Append("<p class=\"target ok\">目的の画像が上位").Append(b.Top.Count).Append("件に入っています(枠で表示): ")
                  .Append(string.Join("、", inTop.Select(t => E(t.File) + "(" + t.Rank + "位)"))).Append("</p>\n");
            if (b.Targets.Count > inTop.Count)
                sb.Append("<p class=\"target ng\">目的の画像が上位").Append(b.Top.Count).Append("件に無い: ")
                  .Append(string.Join("、", b.Targets.Where(t => t.Rank > b.Top.Count).Select(t => E(t.File) + "(" + t.Rank + "位)"))).Append("</p>\n");
            sb.Append("<div class=\"grid\">\n");
            foreach (Hit h in b.Top)
            {
                sb.Append("<label class=\"card").Append(h.IsTarget ? " isTarget" : "").Append("\">");
                sb.Append("<span class=\"rankline\"><span class=\"rank\">").Append(h.Rank).Append("位</span> <span class=\"score\">").Append(h.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append("</span>");
                if (h.IsTarget) sb.Append(" <span class=\"badge\">目的の画像</span>");
                sb.Append("</span>");
                sb.Append("<img loading=\"lazy\" decoding=\"async\" src=\"").Append(E(h.ImageUrl)).Append("\" alt=\"").Append(E(h.File)).Append("\">");
                sb.Append("<span class=\"check\"><input type=\"checkbox\" class=\"hit\" data-q=\"").Append(q).Append("\" data-file=\"").Append(E(h.File)).Append("\"> 当たり</span>");
                sb.Append("<span class=\"fname\">").Append(E(h.File)).Append("</span></label>\n");
            }
            sb.Append("</div>\n<p class=\"count\">当たり <span class=\"n\">0</span> / ").Append(b.Top.Count)
              .Append(" <span class=\"status\">(未判定)</span> <button type=\"button\" class=\"zero\">当たり 0 件で確定</button></p>\n</section>\n");
        }

        sb.Append("</main>\n<footer>\n<h2>結果(precision-judgement.tsv に貼る)</h2>\n");
        sb.Append("<p>下の欄の内容を全部コピーして、<code>spikes/S2-siglip2/precision-judgement.tsv</code> に貼り付けてください(UTF-8。1行目は列名)。未判定のクエリは relevant_count が空になります。</p>\n");
        sb.Append("<textarea id=\"tsv\" readonly rows=\"").Append(Math.Min(60, blocks.Count + 3)).Append("\" spellcheck=\"false\"></textarea>\n");
        sb.Append("<p><button type=\"button\" id=\"copy\">コピー</button> <span id=\"copyMsg\"></span></p>\n</footer>\n");
        sb.Append("<script>\n").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    internal const string Css = @"
:root { color-scheme: light dark; --bd:#8884; --target:#e08a00; --ok:#1a7f37; }
body { font-family: system-ui, 'Yu Gothic UI', 'Meiryo', sans-serif; margin: 0 auto; max-width: 1500px; padding: 0 16px 48px; line-height: 1.5; }
header { border-bottom: 1px solid var(--bd); padding: 8px 0; }
h1 { font-size: 1.3rem; margin: 4px 0; } h2 { font-size: 1.1rem; margin: 20px 0 6px; }
.note, .how { margin: 2px 0; font-size: .9rem; opacity: .85; } .summary { position: sticky; top: 0; background: Canvas; margin: 0; padding: 6px 0; border-bottom: 1px solid var(--bd); font-weight: 600; z-index: 5; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); gap: 10px; }
.card { display: flex; flex-direction: column; gap: 4px; border: 2px solid var(--bd); border-radius: 8px; padding: 6px; cursor: pointer; }
.card.isTarget { border-color: var(--target); box-shadow: 0 0 0 2px var(--target) inset; }
.card.checked { background: #1a7f3718; }
.card img { width: 100%; aspect-ratio: 16 / 9; object-fit: contain; background: #8882; border-radius: 4px; }
.rankline { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; } .rank { font-weight: 700; } .score { opacity: .75; font-variant-numeric: tabular-nums; }
.badge { background: var(--target); color: #fff; border-radius: 4px; padding: 0 6px; font-size: .75rem; }
.fname { font-size: .7rem; opacity: .6; word-break: break-all; }
.target { margin: 2px 0; font-size: .9rem; } .target.ok { color: var(--ok); } .target.ng { color: #c0392b; font-weight: 600; }
.count { font-weight: 600; } .count .status { font-weight: 400; opacity: .7; } .judged .count .status { color: var(--ok); opacity: 1; }
textarea { width: 100%; font-family: ui-monospace, Consolas, monospace; font-size: .9rem; }
button { padding: 4px 10px; }
";

    private const string Script = @"
(function () {
  var sections = Array.prototype.slice.call(document.querySelectorAll('section.query'));
  var storeKey = 'precision10:' + document.body.getAttribute('data-page'); // ページを作り直したら別の保存先になる
  var state = {};
  try { state = JSON.parse(localStorage.getItem(storeKey) || '{}'); } catch (e) { state = {}; }
  function save() { try { localStorage.setItem(storeKey, JSON.stringify(state)); } catch (e) { /* 保存できなくても動く */ } }
  function key(cb) { return cb.getAttribute('data-q') + '|' + cb.getAttribute('data-file'); }
  function judgedOf(q) { return !!state['j' + q]; }
  function update() {
    var judgedCount = 0, sum = 0, lines = ['query_ja\trelevant_count'];
    sections.forEach(function (s, q) {
      var boxes = s.querySelectorAll('input.hit');
      var n = 0;
      Array.prototype.forEach.call(boxes, function (cb) {
        if (cb.checked) { n++; cb.closest('label').classList.add('checked'); } else cb.closest('label').classList.remove('checked');
      });
      var judged = n > 0 || judgedOf(q);
      s.classList.toggle('judged', judged);
      s.querySelector('.count .n').textContent = n;
      s.querySelector('.count .status').textContent = judged ? '(判定済み)' : '(未判定)';
      if (judged) { judgedCount++; sum += n / boxes.length; }
      lines.push(s.getAttribute('data-query') + '\t' + (judged ? n : ''));
    });
    document.getElementById('judgedCount').textContent = judgedCount;
    document.getElementById('mean').textContent = judgedCount ? (sum / judgedCount).toFixed(3) + '(判定済み ' + judgedCount + ' クエリの平均)' : '-';
    document.getElementById('tsv').value = lines.join('\n') + '\n';
  }
  Array.prototype.forEach.call(document.querySelectorAll('input.hit'), function (cb) {
    cb.checked = !!state[key(cb)];
    cb.addEventListener('change', function () { state[key(cb)] = cb.checked; save(); update(); });
  });
  sections.forEach(function (s, q) {
    s.querySelector('button.zero').addEventListener('click', function () {
      var any = Array.prototype.some.call(s.querySelectorAll('input.hit'), function (cb) { return cb.checked; });
      if (any) return; // チェックがあるなら、すでに判定済み
      state['j' + q] = !state['j' + q]; save(); update();
    });
  });
  document.getElementById('copy').addEventListener('click', function () {
    var ta = document.getElementById('tsv'), msg = document.getElementById('copyMsg');
    ta.focus(); ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    msg.textContent = ok ? 'コピーしました' : 'コピーできなかったので、欄を選択して Ctrl+C してください';
  });
  update();
})();
";
}
