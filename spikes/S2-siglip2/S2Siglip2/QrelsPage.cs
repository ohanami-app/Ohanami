using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace S2Siglip2;

/// <summary>
/// 段階3の判定用ページ。各経路(全文検索・SigLIP2・統合)の上位10件を合わせた中で、qrels にまだ無い画像だけを、クエリごとに並べる。
/// 人がチェック(= 当たり)を付けると、ページの下に qrels の形(query_ja / filename / relevant / source)の TSV が出る。
/// 単体の HTML(外部のスクリプト・フォント・CDN は読まない)。画像は元のファイルを file:// で参照する(コピーしない)。
/// 書き出すのは判定済みのクエリだけ(1件以上チェック、または「確定」を押したもの)。判定済みのクエリでは、表示されてチェックがない画像は 0(外れ)。
/// </summary>
public static class QrelsPage
{
    /// <param name="File">画像のファイル名。</param>
    /// <param name="Routes">どの経路で何位だったか(表示用の文字列。例: 「全文 3位 / 画像 7位 / 統合 2位」)。</param>
    public sealed record Candidate(string File, string ImageUrl, string Routes, bool IsTarget);

    /// <param name="Query">日本語のクエリ(重複なし)。</param>
    /// <param name="Targets">このクエリを書いた目的の画像。</param>
    /// <param name="AlreadyJudged">このクエリで、上位10件のうち、すでに qrels にある画像の数(表示用)。</param>
    public sealed record QueryBlock(string Query, IReadOnlyList<string> Targets, int AlreadyJudged, IReadOnlyList<Candidate> Candidates);

    public static string Build(IReadOnlyList<QueryBlock> blocks, string title, string note, string pageId, string source)
    {
        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        sb.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(title)).Append("</title>\n<style>\n").Append(PrecisionPage.Css).Append("</style>\n</head>\n<body data-page=\"").Append(E(pageId)).Append("\" data-source=\"").Append(E(source)).Append("\">\n");
        sb.Append("<header>\n<h1>").Append(E(title)).Append("</h1>\n<p class=\"note\">").Append(E(note)).Append("</p>\n");
        sb.Append("<p class=\"how\">各クエリについて、全文検索・SigLIP2・統合のどれかの上位10件に入った画像のうち、まだ判定していないものだけを並べています。クエリに合っている(当たりの)画像にチェックを入れてください。");
        sb.Append("当たりが1件も無いクエリは「当たり 0 件で確定」を押してください(押さずにチェックもしないと、そのクエリは書き出されず、未判定のままになります)。");
        sb.Append("チェックを付けなかった画像は、判定したクエリでは外れ(0)として書き出されます。「同じ場面の別カット」など、目的の画像と別でも合っていれば当たりにして構いません。</p>\n");
        sb.Append("</header>\n<p class=\"summary\">判定済み <span id=\"judgedCount\">0</span> / ").Append(blocks.Count).Append(" クエリ。当たり <span id=\"hitCount\">0</span> 件、行 <span id=\"rowCount\">0</span></p>\n<main>\n");

        for (int q = 0; q < blocks.Count; q++)
        {
            QueryBlock b = blocks[q];
            sb.Append("<section class=\"query\" data-q=\"").Append(q).Append("\" data-query=\"").Append(E(b.Query)).Append("\">\n");
            sb.Append("<h2><span class=\"qno\">").Append(q + 1).Append(".</span> 「").Append(E(b.Query)).Append("」</h2>\n");
            if (b.Targets.Count > 0) sb.Append("<p class=\"target ok\">目的の画像(枠で表示): ").Append(string.Join("、", b.Targets.Select(E))).Append("</p>\n");
            if (b.AlreadyJudged > 0) sb.Append("<p class=\"note\">上位10件のうち ").Append(b.AlreadyJudged).Append(" 件はすでに判定済み(ここには出していません)。</p>\n");
            sb.Append("<div class=\"grid\">\n");
            foreach (Candidate c in b.Candidates)
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
        sb.Append("<p>下の欄の内容を全部コピーして、<code>spikes/S2-siglip2/qrels.tsv</code> の末尾に貼り足してください(UTF-8。1行目の列名の行を含んでいてもかまいません)。</p>\n");
        sb.Append("<p id=\"info\"></p>\n<textarea id=\"tsv\" readonly rows=\"30\" spellcheck=\"false\"></textarea>\n");
        sb.Append("<p><button type=\"button\" id=\"copy\">コピー</button> <span id=\"copyMsg\"></span></p>\n</footer>\n");
        sb.Append("<script>\n").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private const string Script = @"
(function () {
  var HEADER = 'query_ja\tfilename\trelevant\tsource';
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
";
}
