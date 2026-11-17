using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ohanami.Bench;

/// <summary>判定ページの 1 枚の候補。<paramref name="Routes"/> は表示用(例: 「融合 2位 / 画像 7位 / 全文 3位」。各経路の上位 10 件の中の順位)。</summary>
internal sealed record JudgeCandidate(string File, string ImageUrl, string Routes, bool IsTarget);

/// <summary>判定ページの 1 クエリ。<paramref name="AlreadyJudged"/> は、上位 10 件に入ったうち、すでに qrels にある画像の数(表示用)。</summary>
internal sealed record JudgeBlock(string Query, IReadOnlyList<string> Targets, int AlreadyJudged, IReadOnlyList<JudgeCandidate> Candidates);

/// <summary>
/// 判定ページの中身を決める(判定ページの入力に当たる部分): run の precision.tsv の上位 10 件(経路ごと・融合)のうち、qrels に無い画像だけを、クエリごとに集める。
/// 純粋な計算(ファイルも DB も触らない)。
/// </summary>
internal static class JudgePlan
{
    /// <summary>経路名の表示。知らない経路は PathId のまま。</summary>
    public static string Label(string pathId) => pathId switch
    {
        Metrics.Fused => "融合",
        "visual" => "画像",
        "fulltext" => "全文",
        ThirdPath.PathId => "3 本目",
        ThirdPath.Fused3 => "融合 + 3 本目",
        _ => pathId,
    };

    /// <param name="precision">run の経路 × クエリの表(Top = その経路の上位 10 件のファイル名。順位順)。</param>
    /// <param name="qrels">今の qrels(判定済みの組は出さない)。</param>
    /// <param name="targets">クエリごとの「目的の画像」(known-item。枠で表示する)。</param>
    /// <param name="imageUrl">ファイル名 → 画像の URL(file:///)。画像が無ければ例外にしてよい。</param>
    /// <returns>未判定の画像があるクエリだけ(precision の最初に出た順)。</returns>
    public static List<JudgeBlock> Build(
        IReadOnlyList<PrecisionRow> precision, Qrels qrels, IReadOnlyDictionary<string, IReadOnlyList<string>> targets, Func<string, string> imageUrl)
    {
        ArgumentNullException.ThrowIfNull(precision);
        ArgumentNullException.ThrowIfNull(qrels);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(imageUrl);
        var blocks = new List<JudgeBlock>();
        foreach (IGrouping<string, PrecisionRow> byQuery in precision.GroupBy(r => r.Query, StringComparer.Ordinal))
        {
            string query = byQuery.Key;
            IReadOnlyList<string> target = targets.TryGetValue(query, out IReadOnlyList<string>? t) ? t : [];
            // 画像(ファイル名)ごとに、各経路での順位(上位 10 件の中の位置)を集める
            var routes = new Dictionary<string, List<(string Path, int Rank)>>(StringComparer.Ordinal);
            foreach (PrecisionRow row in byQuery)
            {
                for (int i = 0; i < row.Top.Count; i++)
                {
                    string file = BenchInputs.Nfc(row.Top[i]);
                    if (!routes.TryGetValue(file, out List<(string, int)>? list)) routes[file] = list = [];
                    list.Add((row.Path, i + 1));
                }
            }
            int judged = routes.Keys.Count(f => qrels.TryGet(query, f) is not null);
            var candidates = routes
                .Where(kv => qrels.TryGet(query, kv.Key) is null)
                .OrderBy(kv => kv.Value.Min(x => x.Rank)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new JudgeCandidate(
                    kv.Key, imageUrl(kv.Key),
                    string.Join(" / ", kv.Value.Select(x => $"{Label(x.Path)} {x.Rank}位")),
                    target.Contains(kv.Key, StringComparer.Ordinal)))
                .ToList();
            if (candidates.Count > 0) blocks.Add(new JudgeBlock(query, target, judged, candidates));
        }
        return blocks;
    }

    /// <summary>ページの識別子(保存したチェックの保存先の名前)。候補の中身から決まる(同じ内容のページを作り直しても、チェックが残る)。</summary>
    public static string PageId(IReadOnlyList<JudgeBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (JudgeBlock b in blocks)
            foreach (JudgeCandidate c in b.Candidates)
                sb.Append(b.Query).Append('\t').Append(c.File).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
    }
}

/// <summary>
/// 判定用のページ。未判定の画像をクエリごとに並べ、判定する人がチェック(= 当たり)を付けると、ページの下に qrels の形
/// (<see cref="Qrels.Header"/>)の TSV が出る。**単体の HTML**: 外部のスクリプト・スタイル・フォント・CDN を読まない。画像は元のファイルを file:/// で参照する(コピーしない)。
/// 書き出すのは判定済みのクエリだけ(1 件以上チェック、または「当たり 0 件で確定」を押したもの)。判定済みのクエリでは、表示されてチェックが無い画像は 0(外れ)。
/// チェックの状態はブラウザの localStorage に保存する(使えなくても動く)。
/// </summary>
internal static class JudgePage
{
    public static string Build(IReadOnlyList<JudgeBlock> blocks, string title, string note, string pageId, string source)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        // TSV にそのまま出すので、タブ・改行が入っていると列がずれる(黙って壊れた qrels を作らない)
        foreach (JudgeBlock b in blocks)
        {
            CheckCell(b.Query);
            foreach (JudgeCandidate c in b.Candidates) CheckCell(c.File);
        }
        CheckCell(source);

        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body data-page=\"").Append(E(pageId)).Append("\" data-source=\"").Append(E(source)).Append("\">\n");
        sb.Append("<header>\n<h1>").Append(E(title)).Append("</h1>\n<p class=\"note\">").Append(E(note)).Append("</p>\n");
        sb.Append("<p class=\"how\">各クエリについて、融合・画像・全文のどれかの上位 10 件に入った画像のうち、まだ判定していないものだけを並べています。クエリに合っている(当たりの)画像にチェックを入れてください。");
        sb.Append("当たりが 1 件も無いクエリは「当たり 0 件で確定」を押してください(押さずにチェックもしないと、そのクエリは書き出されず、未判定のままになります)。");
        sb.Append("チェックを付けなかった画像は、判定したクエリでは外れ(0)として書き出されます。「同じ場面の別カット」など、目的の画像と別でも合っていれば当たりにして構いません。</p>\n");
        sb.Append("</header>\n<p class=\"summary\">判定済み <span id=\"judgedCount\">0</span> / ").Append(blocks.Count).Append(" クエリ。当たり <span id=\"hitCount\">0</span> 件、行 <span id=\"rowCount\">0</span></p>\n<main>\n");

        for (int q = 0; q < blocks.Count; q++)
        {
            JudgeBlock b = blocks[q];
            sb.Append("<section class=\"query\" data-q=\"").Append(q).Append("\" data-query=\"").Append(E(b.Query)).Append("\">\n");
            sb.Append("<h2><span class=\"qno\">").Append(q + 1).Append(".</span> 「").Append(E(b.Query)).Append("」</h2>\n");
            if (b.Targets.Count > 0) sb.Append("<p class=\"target ok\">目的の画像(枠で表示): ").Append(string.Join("、", b.Targets.Select(E))).Append("</p>\n");
            if (b.AlreadyJudged > 0) sb.Append("<p class=\"note\">上位 10 件に入った画像のうち ").Append(b.AlreadyJudged).Append(" 枚はすでに判定済み(ここには出していません)。</p>\n");
            sb.Append("<div class=\"grid\">\n");
            foreach (JudgeCandidate c in b.Candidates)
            {
                sb.Append("<label class=\"card").Append(c.IsTarget ? " isTarget" : "").Append("\">");
                sb.Append("<span class=\"rankline\"><span class=\"score\">").Append(E(c.Routes)).Append("</span>");
                if (c.IsTarget) sb.Append(" <span class=\"badge\">目的の画像</span>");
                sb.Append("</span>");
                sb.Append("<img loading=\"lazy\" decoding=\"async\" src=\"").Append(E(c.ImageUrl)).Append("\" alt=\"").Append(E(c.File)).Append("\">");
                sb.Append("<span class=\"check\"><input type=\"checkbox\" class=\"hit\" data-q=\"").Append(q).Append("\" data-file=\"").Append(E(c.File)).Append("\"> 当たり</span>");
                sb.Append("<span class=\"fname\">").Append(E(c.File)).Append("</span></label>\n");
            }
            sb.Append("</div>\n<p class=\"count\">当たり <span class=\"n\">0</span> / ").Append(b.Candidates.Count)
              .Append(" <span class=\"status\">(未判定)</span> <button type=\"button\" class=\"zero\">当たり 0 件で確定</button></p>\n</section>\n");
        }

        sb.Append("</main>\n<footer>\n<h2>結果(qrels の形)</h2>\n");
        sb.Append("<p>下の欄の内容を全部コピーして、<code>spikes/S2-siglip2/qrels.tsv</code> の末尾に貼り足してください(UTF-8。1 行目の列名の行を含んでいてもかまいません)。</p>\n");
        sb.Append("<p id=\"info\"></p>\n<textarea id=\"tsv\" readonly rows=\"30\" spellcheck=\"false\"></textarea>\n");
        sb.Append("<p><button type=\"button\" id=\"copy\">コピー</button> <span id=\"copyMsg\"></span></p>\n</footer>\n");
        sb.Append("<script>\n").Append(Script.Replace("/*HEADER*/", Qrels.Header.Replace("\t", "\\t", StringComparison.Ordinal), StringComparison.Ordinal)).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void CheckCell(string s)
    {
        if (s.Contains('\t') || s.Contains('\n') || s.Contains('\r')) throw new ArgumentException($"タブか改行が入っている(TSV にできない): {s}");
    }

    internal const string Css = """
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
        .rankline { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; } .score { opacity: .75; font-variant-numeric: tabular-nums; }
        .badge { background: var(--target); color: #fff; border-radius: 4px; padding: 0 6px; font-size: .75rem; }
        .fname { font-size: .7rem; opacity: .6; word-break: break-all; }
        .target { margin: 2px 0; font-size: .9rem; } .target.ok { color: var(--ok); }
        .count { font-weight: 600; } .count .status { font-weight: 400; opacity: .7; } .judged .count .status { color: var(--ok); opacity: 1; }
        textarea { width: 100%; font-family: ui-monospace, Consolas, monospace; font-size: .9rem; }
        button { padding: 4px 10px; }
        """;

    private const string Script = """
        (function () {
          var HEADER = '/*HEADER*/';
          var sections = Array.prototype.slice.call(document.querySelectorAll('section.query'));
          var source = document.body.getAttribute('data-source');
          var storeKey = 'qrels-judge:' + document.body.getAttribute('data-page');
          var state = {};
          try { state = JSON.parse(localStorage.getItem(storeKey) || '{}'); } catch (e) { state = {}; }
          function save() { try { localStorage.setItem(storeKey, JSON.stringify(state)); } catch (e) { /* 保存できなくても動く */ } }
          function key(cb) { return cb.getAttribute('data-q') + '|' + cb.getAttribute('data-file'); }
          function update() {
            var judgedCount = 0, hits = 0, rows = 0, lines = [HEADER], skipped = [];
            sections.forEach(function (s, q) {
              var boxes = s.querySelectorAll('input.hit');
              var n = 0;
              Array.prototype.forEach.call(boxes, function (cb) {
                if (cb.checked) { n++; cb.closest('label').classList.add('checked'); } else cb.closest('label').classList.remove('checked');
              });
              var judged = n > 0 || !!state['j' + q];
              s.classList.toggle('judged', judged);
              s.querySelector('.count .n').textContent = n;
              s.querySelector('.count .status').textContent = judged ? '(判定済み)' : '(未判定)';
              if (!judged) { skipped.push(s.getAttribute('data-query')); return; }
              judgedCount++;
              Array.prototype.forEach.call(boxes, function (cb) {
                rows++; if (cb.checked) hits++;
                lines.push(s.getAttribute('data-query') + '\t' + cb.getAttribute('data-file') + '\t' + (cb.checked ? 1 : 0) + '\t' + source);
              });
            });
            document.getElementById('judgedCount').textContent = judgedCount;
            document.getElementById('hitCount').textContent = hits;
            document.getElementById('rowCount').textContent = rows;
            document.getElementById('info').textContent = '判定済み ' + judgedCount + ' クエリ、' + rows + ' 行(当たり ' + hits + ')。未判定 ' + skipped.length + ' クエリは含まれません。';
            document.getElementById('tsv').value = lines.join('\n') + '\n';
          }
          Array.prototype.forEach.call(document.querySelectorAll('input.hit'), function (cb) {
            cb.checked = !!state[key(cb)];
            cb.addEventListener('change', function () { state[key(cb)] = cb.checked; save(); update(); });
          });
          sections.forEach(function (s, q) {
            s.querySelector('button.zero').addEventListener('click', function () {
              var any = Array.prototype.some.call(s.querySelectorAll('input.hit'), function (cb) { return cb.checked; });
              if (any) return;
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
        """;
}
