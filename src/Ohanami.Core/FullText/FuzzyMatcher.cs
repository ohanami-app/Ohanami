using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Core.FullText;

/// <summary>
/// あいまい一致(OCR の誤読への fallback。ADR 0032)の 1 つの語。<paramref name="Items"/> は、語を<b>分割器と同じ形</b>(<see cref="FuzzyMatcher.Items"/>)に直したもの
/// (日本語の文字はコードポイント、境界は <see cref="FuzzyMatcher.Boundary"/>。両端の境界は落としてある)。<paramref name="Source"/> はクエリに書かれた語(理由の表示用)。
/// </summary>
internal sealed record FuzzyQuery(int[] Items, string Source);

/// <summary>
/// 編集距離 1(置き換え・抜け・余分)の部分一致(Sellers 法)。<c>search_text.norm_text</c>(正規化済みの全文)を総当たりで調べる。索引・規約の版は使わない・変えない。
/// <para>
/// <b>区切りの規則</b>: 素の文字列のまま比べると、区切りをまたいで当たってしまう(「銀髪ロング」が「銀髪、ロング」に余分 1 つで当たる)。
/// なので、クエリも文書も <see cref="Tokenizer"/> の区切り分けと同じ形にしてから比べる(<see cref="Items"/>): 日本語の文字は 1 項目、
/// 区切り(空白・句読点・記号など)と日本語以外の単語(英数字)は<b>境界</b> 1 項目で、連続する境界は 1 つにまとめる。日本語の文字どうしの間の空白は無かったことにする。
/// 境界は境界とだけ一致する: 境界の置き換え・余分・抜けは許さない(編集が許されるのは日本語の文字だけ)。クエリの両端の境界は落とす(正確な一致と同じ。部分一致なので、端は文書の途中でよい)。
/// </para>
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>境界を表す項目(コードポイントは負にならないので、文字とは衝突しない)。</summary>
    public const int Boundary = -1;

    /// <summary>この文字数(日本語の文字。境界は数えない)に満たない語は対象にしない。3 文字の語で 1 文字を許すと残りが 2 文字で、当たる画像が多すぎる。</summary>
    public const int MinChars = 4;

    /// <summary>許す編集距離。</summary>
    public const int MaxDistance = 1;

    private const int Inf = MaxDistance + 1;   // これ以上は「超えた」として同じに扱う(1 を超える距離は要らない)

    /// <summary>
    /// 正規化済みの文字列(NFKC + 小文字化の後)を、文字と境界の並びにする(<see cref="Tokenizer"/> の区切り分けと同じ)。
    /// <paramref name="starts"/>・<paramref name="ends"/> が渡されれば、項目ごとの <paramref name="text"/> 上の範囲(UTF-16)も入れる(境界は、まとめた区切り全体)。
    /// <paramref name="hasWord"/> は、日本語以外の単語の文字(英数字)があったか(あれば、その語はあいまい一致の対象にしない)。
    /// </summary>
    public static void Items(string text, List<int> items, List<int>? starts, List<int>? ends, out bool hasWord)
    {
        items.Clear();
        starts?.Clear();
        ends?.Clear();
        hasWord = false;
        bool lastIsChar = false;   // 直前の項目が日本語の文字(= いま日本語の連続部分の途中)
        int i = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune r, out int len) != System.Buffers.OperationStatus.Done) { r = Rune.ReplacementChar; len = 1; }
            if (CharClassifier.IsJapanese(r))
            {
                items.Add(r.Value);
                starts?.Add(i);
                ends?.Add(i + len);
                lastIsChar = true;
            }
            else
            {
                if (Rune.IsWhiteSpace(r) && lastIsChar)
                {
                    // 日本語の文字どうしの間の空白は無かったことにする(折り返し対策。Tokenizer と同じ)
                    int j = i + len;
                    while (j < text.Length && Rune.DecodeFromUtf16(text.AsSpan(j), out Rune w, out int wl) == System.Buffers.OperationStatus.Done && Rune.IsWhiteSpace(w)) j += wl;
                    if (j < text.Length && Rune.DecodeFromUtf16(text.AsSpan(j), out Rune next, out _) == System.Buffers.OperationStatus.Done && CharClassifier.IsJapanese(next))
                    {
                        i = j;
                        continue;
                    }
                }
                if (CharClassifier.IsWordChar(r)) hasWord = true;
                if (items.Count > 0 && items[^1] == Boundary)
                {
                    if (ends is not null) ends[^1] = i + len;   // 連続する区切りは 1 つにまとめる
                }
                else
                {
                    items.Add(Boundary);
                    starts?.Add(i);
                    ends?.Add(i + len);
                }
                lastIsChar = false;
            }
            i += len;
        }
    }

    /// <summary>
    /// 1 つの語(空白を含まない)をあいまい一致の対象にできるなら、その形を返す。できなければ null:
    /// 日本語の語だけ(英数字の単語の文字を含まない)・日本語の文字が <see cref="MinChars"/> 以上。両端の境界は落とす。
    /// </summary>
    public static FuzzyQuery? TryPlan(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        NormalizedText n = NormalizedText.Create(word);
        var items = new List<int>();
        Items(n.Text, items, null, null, out bool hasWord);
        if (hasWord) return null;
        int first = 0, last = items.Count - 1;
        while (first <= last && items[first] == Boundary) first++;
        while (last >= first && items[last] == Boundary) last--;
        if (first > last) return null;
        int[] trimmed = items.GetRange(first, last - first + 1).ToArray();
        int chars = 0;
        foreach (int v in trimmed) if (v != Boundary) chars++;
        return chars < MinChars ? null : new FuzzyQuery(trimmed, word);
    }

    /// <summary>
    /// 1 つの語を、文書(<c>search_text.norm_text</c>)に当てる走査器。作業の配列を使い回すので、**1 つのスレッドだけで使う**(文書ごとに作らず、検索 1 回に 1 つ作る)。
    /// </summary>
    public sealed class Scanner
    {
        private readonly int[] _q;
        private readonly int[] _delete;       // 語の項目 i を落とす(文書に無い)ときの費用
        private readonly int[] _prev, _cur, _prevStart, _curStart;
        private readonly List<int> _doc = new(1024);
        private readonly List<int> _docStart = new(1024);
        private readonly List<int> _docEnd = new(1024);

        public Scanner(FuzzyQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            _q = query.Items;
            _delete = new int[_q.Length + 1];
            for (int i = 1; i <= _q.Length; i++) _delete[i] = _q[i - 1] == Boundary ? Inf : 1;
            _prev = new int[_q.Length + 1];
            _cur = new int[_q.Length + 1];
            _prevStart = new int[_q.Length + 1];
            _curStart = new int[_q.Length + 1];
        }

        /// <summary>文書(正規化済み)に、編集距離 1 以内で一致する箇所があるか。</summary>
        public bool Matches(string normText)
        {
            Items(normText, _doc, null, null, out _);
            return Run(_doc, null, null, track: false, collect: null);
        }

        /// <summary>
        /// <see cref="Items"/> で項目にした文書に一致するか(複数の語が、同じ文書の項目を共有するため。<see cref="Matcher"/>)。
        /// </summary>
        internal bool MatchesItems(List<int> docItems) => Run(docItems, null, null, track: false, collect: null);

        /// <summary>
        /// 一致した箇所の、<paramref name="normText"/> 上の範囲(UTF-16。[開始, 終了)。昇順ではなく、重なることがある。呼び出し側がまとめる)。
        /// 一致の終わりごとに、その費用が 1 以内の最良の対応の始まりまでを範囲にする。
        /// </summary>
        public List<(int Start, int End)> Spans(string normText)
        {
            Items(normText, _doc, _docStart, _docEnd, out _);
            var spans = new List<(int, int)>();
            Run(_doc, _docStart, _docEnd, track: true, collect: spans);
            return spans;
        }

        /// <summary>_doc の項目を順に流して Sellers 法の表を 1 列ずつ更新する。<paramref name="collect"/> が null なら、最初の一致で true を返す。</summary>
        private bool Run(List<int> doc, List<int>? docStart, List<int>? docEnd, bool track, List<(int Start, int End)>? collect)
        {
            int m = _q.Length;
            int[] prev = _prev, cur = _cur, prevSt = _prevStart, curSt = _curStart;
            // 文書の項目を 1 つも読む前の列: 語の先頭 i 個を全部落とす費用(開始は文書のどこでもよいので、行 0 は常に 0)
            prev[0] = 0;
            prevSt[0] = 0;
            for (int i = 1; i <= m; i++)
            {
                prev[i] = Math.Min(Inf, prev[i - 1] + _delete[i]);
                prevSt[i] = 0;
            }

            bool found = false;
            for (int j = 1; j <= doc.Count; j++)
            {
                int d = doc[j - 1];
                bool docIsChar = d != Boundary;
                cur[0] = 0;
                curSt[0] = j;               // 列 j(項目 j まで読んだ)での行 0(空の語)の始まり = 読み終えた項目の数
                for (int i = 1; i <= m; i++)
                {
                    int qi = _q[i - 1];
                    // 一致 / 置き換え(文字どうしだけ。境界は境界とだけ一致)
                    int sub = qi == d ? 0 : (qi != Boundary && docIsChar ? 1 : Inf);
                    int best = Math.Min(Inf, prev[i - 1] + sub);
                    int from = 0;   // 0 = 斜め、1 = 語の項目を落とす、2 = 文書の項目が余分
                    // 語の項目 i を落とす(文書に無い。文字だけ)
                    int del = Math.Min(Inf, cur[i - 1] + _delete[i]);
                    if (del < best) { best = del; from = 1; }
                    // 文書の項目が余分(文字だけ)
                    int ins = docIsChar ? Math.Min(Inf, prev[i] + 1) : Inf;
                    if (ins < best) { best = ins; from = 2; }
                    cur[i] = best;
                    if (track) curSt[i] = from == 0 ? prevSt[i - 1] : from == 1 ? curSt[i - 1] : prevSt[i];
                }
                if (cur[m] <= MaxDistance)
                {
                    if (collect is null) return true;
                    found = true;
                    // 項目 [curSt[m], j) が一致した範囲(境界の項目で始まる・終わることはない: 語の両端は文字で、境界は境界とだけ一致する)
                    int s = curSt[m], e = j;   // 項目 [s, e)
                    while (s < e && doc[s] == Boundary) s++;        // 語の最後の文字を落とした対応などで、端が境界になることがある。強調は文字の範囲だけ
                    while (e > s && doc[e - 1] == Boundary) e--;
                    if (s < e) collect.Add((docStart![s], docEnd![e - 1]));
                }
                (prev, cur) = (cur, prev);
                (prevSt, curSt) = (curSt, prevSt);
            }
            return found;
        }
    }

    /// <summary>
    /// 複数の語のすべてに一致するか(AND)。文書を項目にする作業は 1 回で、語ごとの走査器が共有する。作業の配列を使い回すので、**1 つのスレッドだけで使う**。
    /// </summary>
    public sealed class Matcher
    {
        private readonly Scanner[] _scanners;
        private readonly List<int> _items = new(1024);

        public Matcher(IReadOnlyList<FuzzyQuery> queries)
        {
            ArgumentNullException.ThrowIfNull(queries);
            _scanners = [.. queries.Select(q => new Scanner(q))];
        }

        public bool MatchesAll(string normText)
        {
            Items(normText, _items, null, null, out _);
            foreach (Scanner s in _scanners)
                if (!s.MatchesItems(_items)) return false;
            return true;
        }
    }
}

/// <summary>
/// あいまい一致の総当たりを、複数のスレッドで分けて行う(1 回の検索を 200 ms 以内に収めるため。ADR 0032)。**意味は直列と同じ**: 当たる文書も、返す並びも、スレッドの数に依らない
/// (<see cref="ScanTable"/> は hash の序数順、<see cref="Filter"/> は入力の順の判定)。索引・規約・SQL の意味には触れない。
/// </summary>
internal static class FuzzyScan
{
    /// <summary>
    /// 走査に使うスレッド数の上限の既定: 論理 CPU の半分(最低 1)。検索のほかの処理(SigLIP2 のテキスト側の CPU の推論。クエリごとに約 0.2 秒)と、
    /// 同時に走る見た目の経路・全文の FTS の読み取りとぶつかりすぎないようにする。
    /// </summary>
    public static readonly int DefaultMaxThreads = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>スレッドあたりの塊の数(塊が細かいほど、文書の長さのばらつきが均される)。</summary>
    private const int ChunksPerThread = 4;

    /// <summary>
    /// <c>search_text</c> の全件を、すべての語に一致する文書の hash(序数順)にして返す。文書は id の範囲で塊に分け、スレッドごとに読み取りの接続を 1 つ持って読む
    /// (WAL なので、書き込みは待たせず、読み取りどうしも並行できる)。<paramref name="maxThreads"/> が 1 なら、呼び出したスレッドだけで走る。
    /// </summary>
    public static string[] ScanTable(OhanamiDatabase db, IReadOnlyList<FuzzyQuery> queries, int maxThreads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0) throw new ArgumentException("語が無い", nameof(queries));
        if (maxThreads < 1) throw new ArgumentOutOfRangeException(nameof(maxThreads), maxThreads, "1 以上");
        ct.ThrowIfCancellationRequested();

        long min, max;
        using (SqliteConnection conn = db.CreateReadConnection())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT min(id), max(id) FROM search_text";
            using SqliteDataReader r = cmd.ExecuteReader();
            r.Read();
            if (r.IsDBNull(0)) return [];
            min = r.GetInt64(0);
            max = r.GetInt64(1);
        }

        int chunks = maxThreads == 1 ? 1 : maxThreads * ChunksPerThread;
        long span = Math.Max(1, (max - min + 1 + chunks - 1) / chunks);
        var ranges = new List<(long From, long To)>();   // [From, To)
        for (long a = min; a <= max; a += span) ranges.Add((a, Math.Min(max + 1, a + span)));
        var perChunk = new List<string>[ranges.Count];

        void ReadChunk(int k, SqliteConnection conn, FuzzyMatcher.Matcher matcher)
        {
            var hits = new List<string>();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hash, norm_text FROM search_text WHERE id >= $a AND id < $b";
            cmd.Parameters.AddWithValue("$a", ranges[k].From);
            cmd.Parameters.AddWithValue("$b", ranges[k].To);
            using SqliteDataReader r = cmd.ExecuteReader();
            int n = 0;
            while (r.Read())
            {
                if ((++n & 255) == 0) ct.ThrowIfCancellationRequested();
                if (matcher.MatchesAll(r.GetString(1))) hits.Add(r.GetString(0));
            }
            perChunk[k] = hits;
        }

        if (maxThreads == 1)
        {
            using SqliteConnection conn = db.CreateReadConnection();
            ReadChunk(0, conn, new FuzzyMatcher.Matcher(queries));
        }
        else
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = ct };
            Parallel.For(0, ranges.Count, options,
                () => (Conn: db.CreateReadConnection(), Matcher: new FuzzyMatcher.Matcher(queries)),
                (k, _, local) => { ReadChunk(k, local.Conn, local.Matcher); return local; },
                local => local.Conn.Dispose());
        }

        string[] all = [.. perChunk.SelectMany(c => c)];
        Array.Sort(all, StringComparer.Ordinal);
        return all;
    }

    /// <summary>
    /// 文書(正規化済み)の並びのそれぞれが、すべての語に一致するか。結果は入力と同じ順(スレッドの数に依らない)。
    /// FTS・LIKE で絞った候補(順位つき)に、あいまい一致の語を重ねるときに使う。
    /// </summary>
    public static bool[] Filter(IReadOnlyList<string> texts, IReadOnlyList<FuzzyQuery> queries, int maxThreads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(queries);
        if (maxThreads < 1) throw new ArgumentOutOfRangeException(nameof(maxThreads), maxThreads, "1 以上");
        var result = new bool[texts.Count];
        if (texts.Count == 0) return result;
        ct.ThrowIfCancellationRequested();
        if (maxThreads == 1 || texts.Count < 64)
        {
            var matcher = new FuzzyMatcher.Matcher(queries);
            for (int i = 0; i < texts.Count; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                result[i] = matcher.MatchesAll(texts[i]);
            }
            return result;
        }
        int rangeSize = Math.Max(16, texts.Count / (maxThreads * ChunksPerThread));
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = ct };
        Parallel.ForEach(Partitioner.Create(0, texts.Count, rangeSize), options,
            () => new FuzzyMatcher.Matcher(queries),
            (range, _, matcher) =>
            {
                for (int i = range.Item1; i < range.Item2; i++) result[i] = matcher.MatchesAll(texts[i]);
                return matcher;
            },
            _ => { });
        return result;
    }
}
