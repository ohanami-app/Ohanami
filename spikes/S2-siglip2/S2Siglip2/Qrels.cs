using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace S2Siglip2;

/// <summary>qrels の1行(クエリ × 画像 × 当たり/外れ)。</summary>
public sealed record Qrel(string Query, string File, bool Relevant, string Source);

/// <summary>
/// qrels.tsv(段階3の判定の記録)。UTF-8、タブ区切り、列は query_ja / filename / relevant(1 か 0)/ source(どのページで判定したか)。
/// ページの書き出し欄の内容を、そのまま末尾に貼り足してよい(1行目と同じ列名の行は読み飛ばす)。
/// 同じ (クエリ, 画像) が2回あれば、当たり/外れが同じなら1件にまとめ、違えば例外にする(黙ってどちらかを選ばない)。
/// 画像のファイル名は Unicode の NFC にそろえて比べる(埋め込みの名前との照合のため)。
/// </summary>
public sealed class Qrels
{
    public const string Header = "query_ja\tfilename\trelevant\tsource";

    private readonly Dictionary<(string Query, string File), Qrel> _map = new();

    public int Count => _map.Count;
    public IEnumerable<Qrel> All => _map.Values;
    public IEnumerable<string> Queries => _map.Keys.Select(k => k.Query).Distinct(StringComparer.Ordinal);

    /// <summary>判定済みならその結果(当たり = true)。未判定は null。</summary>
    public bool? TryGet(string query, string file) =>
        _map.TryGetValue((query, Norm(file)), out Qrel? q) ? q.Relevant : null;

    public IEnumerable<Qrel> ForQuery(string query) => _map.Values.Where(q => q.Query == query);

    private static string Norm(string file) => file.Normalize(NormalizationForm.FormC);

    public static Qrels Read(string path) => Parse(File.ReadAllLines(path, new UTF8Encoding(false, true)), path);

    public static Qrels Parse(IEnumerable<string> lines, string source = "(入力)")
    {
        var list = lines.Select(l => l.TrimEnd('\r')).ToList();
        if (list.Count == 0) throw new InvalidDataException($"{source} が空");
        if (list[0].TrimStart('﻿') != Header) throw new InvalidDataException($"{source} の1行目は「{Header}」(タブ区切り)であること: {list[0]}");
        var result = new Qrels();
        for (int i = 1; i < list.Count; i++)
        {
            string line = list[i];
            if (line.Length == 0 || line == Header) continue;
            string[] c = line.Split('\t');
            if (c.Length != 4) throw new InvalidDataException($"{source} の {i + 1} 行目: 列が4つでない(query_ja / filename / relevant / source): {line}");
            string query = c[0], file = c[1].Trim(), rel = c[2].Trim(), src = c[3].Trim();
            if (query.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: query_ja が空");
            if (file.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: filename が空");
            if (src.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: source が空(どのページで判定したかを残す)");
            bool relevant = rel switch
            {
                "1" => true,
                "0" => false,
                _ => throw new InvalidDataException($"{source} の {i + 1} 行目: relevant は 1 か 0: 「{c[2]}」"),
            };
            var key = (query, Norm(file));
            if (result._map.TryGetValue(key, out Qrel? old))
            {
                if (old.Relevant != relevant)
                    throw new InvalidDataException($"{source} の {i + 1} 行目: 「{query}」×「{file}」の判定が食い違う(先: {(old.Relevant ? 1 : 0)} [{old.Source}]、後: {(relevant ? 1 : 0)} [{src}])");
                continue;
            }
            result._map[key] = new Qrel(query, Norm(file), relevant, src);
        }
        return result;
    }

    /// <summary>
    /// 既知のクエリ・画像と照合する。qrels に知らないクエリや画像があれば例外(名前の綴り違い・別の run のものを黙って無視しない)。
    /// </summary>
    public void Validate(IReadOnlyCollection<string> knownQueries, IReadOnlyCollection<string> knownImages)
    {
        var qs = knownQueries.ToHashSet(StringComparer.Ordinal);
        var imgs = knownImages.Select(Norm).ToHashSet(StringComparer.Ordinal);
        var badQ = _map.Values.Where(q => !qs.Contains(q.Query)).Select(q => q.Query).Distinct().ToList();
        var badF = _map.Values.Where(q => !imgs.Contains(q.File)).Select(q => q.File).Distinct().ToList();
        if (badQ.Count > 0 || badF.Count > 0)
        {
            throw new InvalidDataException("qrels が known-items.tsv か埋め込みと合わない。" +
                (badQ.Count > 0 ? $" 知らないクエリ {badQ.Count} 件: {string.Join("、", badQ.Take(5))}。" : "") +
                (badF.Count > 0 ? $" 知らない画像 {badF.Count} 件: {string.Join("、", badF.Take(5))}。" : ""));
        }
    }

    /// <summary>qrels の行を TSV にする(ヘッダつき)。</summary>
    public static string ToTsv(IEnumerable<Qrel> rows)
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (Qrel r in rows)
        {
            if (r.Query.Contains('\t') || r.File.Contains('\t') || r.Source.Contains('\t')) throw new ArgumentException($"タブが入っている: {r}");
            sb.Append(r.Query).Append('\t').Append(r.File).Append('\t').Append(r.Relevant ? '1' : '0').Append('\t').Append(r.Source).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// precision-judgement.tsv(クエリごとの当たりの数)と qrels の突き合わせ。
    /// 判定済みのクエリは、qrels の当たりの数が relevant_count と一致し、行数が topCount であること。食い違いはすべて文字列で返す(空なら一致)。
    /// precision-judgement.tsv は precision-page の判定(クエリごとの上位10件)だけを記録しているので、追加の判定(別のソースの行)が qrels に
    /// 加わった後は、rowFilter で precision-page 由来の行だけを突き合わせる(null なら全行)。
    /// </summary>
    public List<string> CompareWithCounts(IEnumerable<PrecisionJudgement.Entry> entries, int topCount, Func<Qrel, bool>? rowFilter = null)
    {
        var problems = new List<string>();
        foreach (var e in entries)
        {
            var rows = ForQuery(e.Query).Where(r => rowFilter is null || rowFilter(r)).ToList();
            if (e.RelevantCount is null)
            {
                if (rows.Count > 0) problems.Add($"「{e.Query}」: precision-judgement.tsv では未判定なのに、qrels に {rows.Count} 行ある");
                continue;
            }
            int rel = rows.Count(r => r.Relevant);
            if (rows.Count != topCount) problems.Add($"「{e.Query}」: qrels の行数 {rows.Count} ≠ {topCount}");
            if (rel != e.RelevantCount) problems.Add($"「{e.Query}」: 当たりの数 qrels {rel} ≠ precision-judgement.tsv {e.RelevantCount}");
        }
        return problems;
    }
}

/// <summary>
/// 既存の precision-page.html(PrecisionPage.Build が作ったもの)を、作り直さずに「qrels の書き出し」つきにする。
/// ページの保存先(localStorage の鍵)は <c>'precision10:' + body の data-page</c> で、ページの中身のハッシュには依存しない。
/// そこで元のスクリプトには触れず、ページの末尾に「書き出し」の区画と別のスクリプトを足すだけにする(チェックの状態は DOM から読む)。
/// 判定済みのクエリ(当たりが1件以上、または「当たり 0 件で確定」を押したもの)だけを書き出す。
/// 表示されていてチェックがない画像は 0(外れ)。決めた手順(当たりにだけチェックを付ける)で、precision-judgement.tsv の
/// 当たりの数もこの前提で数えられているため、同じ前提で qrels にする。
/// </summary>
public static class QrelsExport
{
    public const string Marker = "id=\"qrelsExport\"";

    public const string Section = @"
<section id=""qrelsExport"">
<h2>qrels の書き出し(クエリ × 画像 × 当たり/外れ)</h2>
<p>判定済みのクエリについて、上に表示された画像ごとに「チェックあり = 1(当たり)、なし = 0(外れ)」の行を作ります。未判定のクエリは含みません。下の欄の内容を全部コピーして、<code>spikes/S2-siglip2/qrels.tsv</code> に貼り付けてください(UTF-8。1行目は列名)。</p>
<p id=""qrelsInfo""></p>
<textarea id=""qrelsTsv"" readonly rows=""30"" spellcheck=""false"" style=""width:100%""></textarea>
<p><button type=""button"" id=""qrelsRefresh"">書き出しを更新</button> <button type=""button"" id=""qrelsCopy"">コピー</button> <span id=""qrelsMsg""></span></p>
</section>
";

    /// <summary>書き出しのスクリプト。<c>source</c> 列は 'precision-page:' + data-page。</summary>
    public const string Script = @"
(function () {
  var HEADER = 'query_ja\tfilename\trelevant\tsource';
  var source = 'precision-page:' + document.body.getAttribute('data-page');
  function build() {
    var lines = [HEADER], judged = 0, skipped = [], rows = 0, hits = 0;
    Array.prototype.forEach.call(document.querySelectorAll('section.query'), function (s) {
      var boxes = s.querySelectorAll('input.hit');
      var any = Array.prototype.some.call(boxes, function (cb) { return cb.checked; });
      if (!any && !s.classList.contains('judged')) { skipped.push(s.getAttribute('data-query')); return; }
      judged++;
      Array.prototype.forEach.call(boxes, function (cb) {
        rows++; if (cb.checked) hits++;
        lines.push(s.getAttribute('data-query') + '\t' + cb.getAttribute('data-file') + '\t' + (cb.checked ? 1 : 0) + '\t' + source);
      });
    });
    document.getElementById('qrelsTsv').value = lines.join('\n') + '\n';
    document.getElementById('qrelsInfo').textContent = '判定済み ' + judged + ' クエリ、' + rows + ' 行(当たり ' + hits + ')。未判定 ' + skipped.length + ' クエリ' + (skipped.length ? '(' + skipped.join('、') + ')は含まれません' : '') + '。';
  }
  document.getElementById('qrelsRefresh').addEventListener('click', build);
  document.getElementById('qrelsCopy').addEventListener('click', function () {
    build();
    var ta = document.getElementById('qrelsTsv'), msg = document.getElementById('qrelsMsg');
    ta.focus(); ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    msg.textContent = ok ? 'コピーしました' : 'コピーできなかったので、欄を選択して Ctrl+C してください';
  });
  build();
})();
";

    /// <summary>
    /// ページの HTML に書き出しの区画を足す。すでに足してあれば例外(二重に足さない)。
    /// 元のスクリプトとページ id(data-page)は変えない。想定した形でないページも例外にする。
    /// </summary>
    public static string Patch(string html)
    {
        if (html.Contains(Marker, StringComparison.Ordinal)) throw new InvalidOperationException("すでに qrels の書き出しが入っているページ");
        if (!html.Contains("<body data-page=\"", StringComparison.Ordinal) || !html.Contains("class=\"query\"", StringComparison.Ordinal) || !html.Contains("'precision10:' + document.body.getAttribute('data-page')", StringComparison.Ordinal))
            throw new InvalidDataException("PrecisionPage が作ったページの形ではない(body の data-page、section.query、保存の鍵 'precision10:' + data-page が見つからない)");
        const string tail = "</script>\n</body>\n</html>";
        int at = html.LastIndexOf(tail, StringComparison.Ordinal);
        if (at < 0 || html.IndexOf(tail, StringComparison.Ordinal) != at) throw new InvalidDataException("ページの末尾が想定と違う(</script></body></html> が1か所だけのはず)");
        // 元のスクリプトの閉じタグの直後に、区画と新しいスクリプトを足す
        string insert = "</script>\n" + Section.Trim('\n') + "\n<script>\n" + Script.Trim('\n') + "\n</script>\n</body>\n</html>";
        return html[..at] + insert + html[(at + tail.Length)..];
    }
}
