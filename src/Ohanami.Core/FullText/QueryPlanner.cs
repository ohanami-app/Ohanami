using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ohanami.Core.FullText;

internal enum QueryKind
{
    /// <summary>FTS5 のフレーズ検索。</summary>
    Phrase,
    /// <summary>1文字のクエリ。FTS では引かず、LIKE の全件走査にする。</summary>
    SingleChar,
    /// <summary>
    /// あいまい一致(ADR 0032)。<see cref="Phrase"/> の語のうち、日本語だけで 4 文字以上、かつ正確な一致が索引に 1 件も無かった語を、
    /// 検索のときに(<c>FullTextPath</c> が DB を見て)この種類に置き換える。編集距離 1 以内の部分一致で絞り込むだけで、順位には効かない。<see cref="QueryPlanner.Plan"/> は作らない。
    /// </summary>
    Fuzzy,
}

/// <summary>1 つの語(空白で区切った 1 つ分)の検索の計画。</summary>
/// <param name="Tokens">Phrase のときのクエリのトークン列(索引と同じ分割規則で作る)。SingleChar では空。</param>
/// <param name="FtsMatch">Phrase のときの MATCH 式(ダブルクォートで囲んだフレーズ)。SingleChar では null。</param>
/// <param name="SingleChar">SingleChar のときの、正規化後の1文字。Phrase では null。</param>
/// <param name="Word">クエリに書かれた語(空白を含まない 1 語)。あいまい一致に置き換えるかの判断と、理由の表示に使う。</param>
/// <param name="Fuzzy">Fuzzy のときの、あいまい一致の語。それ以外は null。</param>
internal sealed record QueryPlan(QueryKind Kind, IReadOnlyList<Token> Tokens, string? FtsMatch, string? SingleChar, string Word = "", FuzzyQuery? Fuzzy = null)
{
    /// <summary>
    /// この語(Phrase)を、あいまい一致の語に置き換えた計画。<see cref="Tokens"/>・<see cref="FtsMatch"/> は、正確な一致が 0 件だった式として残す
    /// (<see cref="SearchPlan.FtsMatch"/> は Phrase の語だけをつなぐので、置き換えた語は MATCH に入らない)。
    /// </summary>
    public QueryPlan ToFuzzy(FuzzyQuery fuzzy)
    {
        ArgumentNullException.ThrowIfNull(fuzzy);
        if (Kind != QueryKind.Phrase) throw new InvalidOperationException("あいまい一致にできるのは Phrase の語だけ");
        return this with { Kind = QueryKind.Fuzzy, Fuzzy = fuzzy };
    }

    /// <summary>
    /// 文書(正規化済み)がこの語に一致するか。DB に依存しない「基準の実装」: FTS5 のフレーズ検索は、文書のトークン列にクエリのトークン列が連続して
    /// 現れることと同じ。1文字は、正規化後の本文にその文字が含まれること(LIKE '%c%' と同じ)。DB 層のテストは、これと FTS5 / LIKE の結果が一致することを確かめる。
    /// </summary>
    public bool Matches(NormalizedText doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (Kind == QueryKind.SingleChar) return doc.Text.Contains(SingleChar!, StringComparison.Ordinal);
        if (Kind == QueryKind.Fuzzy) return new FuzzyMatcher.Scanner(Fuzzy!).Matches(doc.Text);
        return QueryPlanner.PhraseStarts(Tokenizer.TokenizeDocument(doc), Tokens).Count > 0;
    }
}

/// <summary>
/// 製品の検索の文法(空白区切りの AND)の計画。クエリを空白(<see cref="char.IsWhiteSpace(char)"/>。全角空白・タブ・改行を含む)で語に分け、すべての語を含む文書だけを返す。
/// <list type="bullet">
/// <item>2文字以上の語(Phrase)は FTS5 のフレーズ(<see cref="FtsMatch"/>。語どうしは AND)。順位は FTS5 の BM25。</item>
/// <item>1文字の語(SingleChar)は FTS では引かず、LIKE で絞り込む(<see cref="LikePattern"/>)。FTS のフレーズが 1 つでもあれば、順位は BM25(1文字の語は絞り込みだけ)。</item>
/// <item>語が<b>すべて</b>1文字のときは BM25 が使えないので、語の出現回数の合計(<see cref="CountOccurrences"/>。多いほど良い。文書の長さで割らない)、
///   同じなら id 順で並べる(<see cref="RankedByOccurrences"/>)。</item>
/// </list>
/// 同じ語が 2 回あっても取り除かない(AND は同じ。出現回数の合計では 2 回分数える)。
/// 検索できる文字(日本語・英数字)が無い語、不正な文字(制御文字・不正なサロゲート)がある語は、黙って落とさず <see cref="ArgumentException"/>。
/// </summary>
internal sealed record SearchPlan(IReadOnlyList<QueryPlan> Terms)
{
    /// <summary>Phrase の語(FTS5 のフレーズで引く)。</summary>
    public IReadOnlyList<QueryPlan> Phrases => Terms.Where(t => t.Kind == QueryKind.Phrase).ToArray();

    /// <summary>SingleChar の語の文字(正規化後)。LIKE で絞り込む。</summary>
    public IReadOnlyList<string> SingleChars => Terms.Where(t => t.Kind == QueryKind.SingleChar).Select(t => t.SingleChar!).ToArray();

    /// <summary>FTS5 の MATCH 式(フレーズを AND でつなぐ)。Phrase が 1 つも無ければ null(FTS は使わない)。</summary>
    public string? FtsMatch
    {
        get
        {
            string[] phrases = Terms.Where(t => t.Kind == QueryKind.Phrase).Select(t => t.FtsMatch!).ToArray();
            return phrases.Length == 0 ? null : string.Join(" AND ", phrases);
        }
    }

    /// <summary>あいまい一致の語(絞り込みだけ。順位には効かない)。</summary>
    public IReadOnlyList<FuzzyQuery> Fuzzies => Terms.Where(t => t.Kind == QueryKind.Fuzzy).Select(t => t.Fuzzy!).ToArray();

    /// <summary>
    /// フレーズの語が 1 つも無い(BM25 が使えない)。1 文字の語の出現回数の合計で並べる(1 文字の語も無ければ、全部同点)。
    /// あいまい一致の語は、1 文字の語と同じに、絞り込みだけで順位に効かない。
    /// </summary>
    public bool RankedByOccurrences => Terms.All(t => t.Kind != QueryKind.Phrase);

    /// <summary>文書(正規化済み)がすべての語に一致するか(基準の実装。<see cref="QueryPlan.Matches"/> の AND)。</summary>
    public bool Matches(NormalizedText doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return Terms.All(t => t.Matches(doc));
    }

    /// <summary>1文字の語の出現回数の合計(<see cref="RankedByOccurrences"/> のときの順位。SQL の length(text) - length(replace(text, c, '')) の合計と同じ)。</summary>
    public int CountOccurrences(NormalizedText doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        int total = 0;
        foreach (string c in SingleChars)
        {
            int from = 0;
            while (true)
            {
                int at = doc.Text.IndexOf(c, from, StringComparison.Ordinal);
                if (at < 0) break;
                total++;
                from = at + c.Length;
            }
        }
        return total;
    }

    /// <summary>1文字の語の LIKE のパターン(<c>ESCAPE '\'</c> と対で使う)。</summary>
    public static string LikePattern(string singleChar) => "%" + QueryPlanner.EscapeLike(singleChar) + "%";
}

/// <summary>
/// 索引の規約(索引の投入と検索が使う)。
/// <list type="bullet">
/// <item><see cref="Tag"/>、<see cref="Field"/>: 索引に入れる文字列を作るときの区切り。どちらも <see cref="Tokenizer"/> が「区切り」として扱う文字で、区切りをまたいだ bigram は作られない。
///   空白や改行でつなぐと、日本語の文字の間の空白・改行は除去される(折り返し対策)ので、つないだ前後の bigram がつながってしまう。</item>
/// <item><see cref="TokenizerSpec"/>: トークンを FTS5 に入れるときの tokenize 指定。トークンの意味(仮名の濁点を区別する、英数字は単語単位)はこの指定が前提。</item>
/// </list>
/// 規約は FullTextTests で固定している。
/// </summary>
internal static class FtsSpec
{
    /// <summary>タグ(tags)をつなぐ区切り「、」。「銀髪」+「ロングヘア」が「銀髪ロング」に当たらないようにする(空白や改行でつなぐと、正規化で除かれて隣のタグの文字が並んでしまう。ADR 0029)。</summary>
    public const string Tag = "、";

    /// <summary>欄(ocr・説明文・tags、手動タグがあれば 4 つ目)をつなぐ区切り「|」。</summary>
    public const string Field = "|";

    /// <summary>
    /// FTS5 の tokenize 指定。remove_diacritics は既定値に頼らず明示する。1 は「ラテン文字の発音区別符号だけ」を除去し、仮名の濁点・半濁点(カ と ガ、ハ と パ)は区別されたままになる
    /// (0 / 1 / 2 のどれでも区別されることを FullTextTests で実測している)。
    /// </summary>
    public const string TokenizerSpec = "unicode61 remove_diacritics 1";

    /// <summary>索引の規約文字列の 1 行目(書式の版つき。書式を変えたら数字を上げる)。</summary>
    public const string SpecHeader = "fts-spec/1";

    /// <summary>
    /// 正規化の規則(NFKC → 小文字化 → カタカナをひらがなへ。<see cref="NormalizedText"/>・<see cref="KanaFold"/>)。規則を変えたら版を上げる。
    /// 前の版は <c>nfkc-lower</c>(ひらがなとカタカナを区別していた。ADR 0031 で同一視にした)。
    /// </summary>
    public const string NormalizeVersion = "nfkc-lower-kana1";

    /// <summary>分割の規則(日本語は境界記号つきの文字 bigram、英数字は単語。<see cref="Tokenizer"/>・<see cref="CharClassifier"/>)。規則を変えたら版を上げる。</summary>
    public const string TokenizerVersion = "bigram-pad-v1";

    /// <summary>
    /// 索引文書の元の選び方(ADR 0036)。**(a) 文書にする画像**: 「最新の caption のある画像」と「手動タグ(kind = manual・source = manual)のある画像」の和。
    /// **(b) 画像ごとの caption**: created_at が最新のもの(同じなら rowid が大きいほう。モデルは問わない)。caption が無い画像は ocr・説明文・tags を空にして、model_id は NULL。
    /// **(c) 手動タグの欄**: 手動タグが 1 つでもあれば、文書の末尾に「|」と、名前順(大文字・小文字を区別しない順、同じなら文字コード順)に「、」でつないだものを足す。
    /// 無ければ足さない(手動タグを足す前と同じ文字列)。これらのどれかを変えたら版を上げる。前の版は <c>latest-caption-v1</c>(caption だけが元。手動タグ無し)。
    /// </summary>
    public const string DocSelectVersion = "latest-caption+manual-tags-v1";

    /// <summary>
    /// <see cref="DocSelectVersion"/> の並び順(captions の行を新しい順に)。「最新の caption」を選ぶ SQL はすべてこれを使う
    /// (増分の更新・作り直し・詳細の読み出しで選び方がずれると、索引と表示が食い違う)。選び方を変えたら <see cref="DocSelectVersion"/> を上げる。
    /// 列名は修飾しない(相関サブクエリの中では内側の表に解決される)。
    /// </summary>
    public const string LatestCaptionOrderBy = "created_at DESC, rowid DESC";

    /// <summary>画像ごとに最新の caption の rowid を返す相関サブクエリ。<paramref name="outerAlias"/> は外側の captions の別名。</summary>
    public static string LatestCaptionRowid(string outerAlias) =>
        $"(SELECT l.rowid FROM captions l WHERE l.hash = {outerAlias}.hash ORDER BY {LatestCaptionOrderBy} LIMIT 1)";

    /// <summary>制御文字・不正なサロゲートの置換の規則(<see cref="FtsDocumentBuilder.Sanitize"/>)。</summary>
    public const string SanitizeVersion = "sanitize-v1";
}

/// <summary><see cref="QueryPlanner.PlanSearchLenient"/> の結果。<paramref name="Plan"/> は検索できる語で作った計画(1 つも無ければ null)、<paramref name="Dropped"/> は検索できなくて除いた語(クエリに書かれた順)。</summary>
internal sealed record LenientSearchPlan(SearchPlan? Plan, IReadOnlyList<string> Dropped);

internal static class QueryPlanner
{
    /// <summary>1 つの語の計画(空白を含まない語を想定。空白があれば、日本語どうしの間は無かったことになり、それ以外は区切りとして 1 つの Phrase にまとまる)。</summary>
    public static QueryPlan Plan(string query)
    {
        CharClassifier.Validate(query, nameof(query));
        QueryTokenization q = Tokenizer.TokenizeQuery(NormalizedText.Create(query));
        if (q.SingleChar is not null)
        {
            return new QueryPlan(QueryKind.SingleChar, q.Tokens, null, q.SingleChar, query);
        }
        return new QueryPlan(QueryKind.Phrase, q.Tokens, "\"" + TokensToIndexText(q.Tokens) + "\"", null, query);
    }

    /// <summary>
    /// クエリ全体(空白区切りの AND)の計画。空白(全角空白・タブ・改行を含む)で語に分け、語ごとに <see cref="Plan"/> する。
    /// 語が 1 つも無い(空・空白だけ)、検索できる文字が無い語がある、不正な文字がある場合は <see cref="ArgumentException"/>。
    /// </summary>
    public static SearchPlan PlanSearch(string query)
    {
        CharClassifier.Validate(query, nameof(query));
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // null = char.IsWhiteSpace の文字すべて
        if (words.Length == 0) throw new ArgumentException("クエリに検索できる文字(日本語・英数字)がない。", nameof(query));
        return new SearchPlan(words.Select(Plan).ToArray());
    }

    /// <summary>
    /// <see cref="PlanSearch"/> の寛容版: 検索できる文字が無い語(「。」「！？」など)を除いて、残りの語で計画を作り、除いた語を返す。
    /// 語が 1 つも残らなければ <see cref="LenientSearchPlan.Plan"/> は null。語を黙って落とさず、除いた語は呼び出し側が利用者に伝える。
    /// 制御文字・不正なサロゲートがある(入力の誤り)、空・空白だけのクエリは <see cref="ArgumentException"/>(クエリ全体を除かない)。
    /// </summary>
    public static LenientSearchPlan PlanSearchLenient(string query)
    {
        CharClassifier.Validate(query, nameof(query));
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) throw new ArgumentException("クエリに検索できる文字(日本語・英数字)がない。", nameof(query));
        var plans = new List<QueryPlan>();
        var dropped = new List<string>();
        foreach (string word in words)
        {
            try
            {
                plans.Add(Plan(word));
            }
            catch (ArgumentException)
            {
                dropped.Add(word); // 検索できる文字が無い語(Validate は上で済んでいるので、ここでの ArgumentException はこれだけ)
            }
        }
        return new LenientSearchPlan(plans.Count == 0 ? null : new SearchPlan(plans.ToArray()), dropped);
    }

    /// <summary>トークンを空白区切りでつないだ、FTS5 に入れる文字列。トークンに空白・ダブルクォートは含まれない。</summary>
    public static string TokensToIndexText(IReadOnlyList<Token> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(tokens[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>LIKE の特殊文字(% _ \)のエスケープ(ESCAPE '\' と対で使う)。</summary>
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>文書のトークン列の中で、クエリのトークン列が連続して現れる位置(文書側の開始の添字)。FTS5 のフレーズ検索と同じ意味。</summary>
    public static List<int> PhraseStarts(IReadOnlyList<Token> doc, IReadOnlyList<Token> query)
    {
        var starts = new List<int>();
        for (int i = 0; i + query.Count <= doc.Count; i++)
        {
            bool all = true;
            for (int k = 0; k < query.Count; k++)
            {
                if (!string.Equals(doc[i + k].Text, query[k].Text, StringComparison.Ordinal)) { all = false; break; }
            }
            if (all) starts.Add(i);
        }
        return starts;
    }
}
