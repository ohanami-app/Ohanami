using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Tags;

namespace Ohanami.Core.Search;

/// <summary>
/// 全文検索の経路(ADR 0029)。<see cref="SearchPlan"/>(空白区切りの AND)を DB の FTS5 と LIKE に向ける。
/// <list type="bullet">
/// <item>2 文字以上の語はフレーズ(<c>fts_docs MATCH</c>。語どうしは AND)。スコアは <c>-rank</c>(FTS5 の rank = BM25 は小さいほど良いので、符号を反転して「大きいほど良い」にそろえる)。
///   1 文字の語は <c>search_text.norm_text LIKE</c> で絞り込むだけ(順位には効かない)。</item>
/// <item>語がすべて 1 文字のときは BM25 が使えないので、語の出現回数の合計をスコアにする(多いほど良い。文書の長さで割らない)。</item>
/// <item>順位は同点を平均順位にする(融合の <c>RankedPath</c> の規則。ここでは並べない)。母集団は**疎**(ヒットした画像だけ)。
///   埋め込みがまだ無い画像も、全文で当たれば結果に出る(<see cref="SearchService"/> の母集団は「各経路の結果の和」)。</item>
/// <item>検索できる文字が無い語は除いて、残りの語で引き、除いた語を <see cref="PathOutcome.Message"/> に出す(<see cref="QueryPlanner.PlanSearchLenient"/>)。
///   1 つも残らなければ <see cref="PathState.Skipped"/>。制御文字などのクエリは例外(<see cref="SearchService"/> がこの経路の Failed にする)。</item>
/// <item>索引が <see cref="FtsIndexState.NotChecked"/> / <see cref="FtsIndexState.Rebuilding"/> の間は <see cref="PathState.NotReady"/>(見た目の経路の結果は返る)。
///   <see cref="FtsIndexState.Failed"/> は、この検索の失敗(例外)。</item>
/// <item>詳細(<see cref="TextDetail"/>)は、<see cref="PathSearchResult.PrepareDetails"/> で上位の画像の captions と手動タグを、1 つの読み取りのトランザクションの中で(同じ時点で)バッチで読み、そこから欄ごとの一致箇所を作る。</item>
/// </list>
/// </summary>
internal sealed class FullTextPath : ISearchPath
{
    public const string Id = "fulltext";

    private readonly OhanamiDatabase _db;
    private readonly FtsIndex _index;
    private readonly ICoreLog? _log;

    /// <param name="log">診断ログの出し先。索引にあるのに caption も手動タグも無い画像の警告(<see cref="SearchAsync"/> の詳細)に使う。null なら出さない(テスト用)。</param>
    public FullTextPath(OhanamiDatabase db, FtsIndex index, ICoreLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(index);
        _db = db;
        _index = index;
        _log = log;
    }

    public string PathId => Id;

    public string DisplayName => "文字";

    public string? ModelId => null;

    public async Task<PathSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        switch (_index.State)
        {
            case FtsIndexState.NotChecked:
                return PathSearchResult.NotReady("全文検索の索引の確認がまだ始まっていない");
            case FtsIndexState.Rebuilding:
                return PathSearchResult.NotReady("全文検索の索引を作り直し中");
            case FtsIndexState.Failed:
                throw new InvalidOperationException("全文検索の索引を作り直せなかった: " + _index.FailureMessage);
        }

        LenientSearchPlan lenient = QueryPlanner.PlanSearchLenient(query);   // 空・制御文字は ArgumentException(経路の Failed)
        if (lenient.Plan is not { } plan)
            return PathSearchResult.Skipped("検索できる語が無い(" + QuoteWords(lenient.Dropped) + ")");

        string? message = lenient.Dropped.Count == 0 ? null : "検索できない語を除いて検索した: " + QuoteWords(lenient.Dropped);
        // 正確な一致が索引に 1 件も無い語(日本語だけ・4 文字以上)は、あいまい一致に置き換える(ADR 0032)。置き換えた計画で、引くのも強調もする
        SearchPlan resolved = await Task.Run(() => ResolveFuzzy(plan, ct), ct).ConfigureAwait(false);
        (string[] hashes, double[] scores) = await Task.Run(() => Query(resolved, ct), ct).ConfigureAwait(false);

        Dictionary<string, CaptionRow>? captions = null;
        Dictionary<string, IReadOnlyList<string>>? manualTags = null;
        return PathSearchResult.Ok(
            hashes, scores,
            detail: i =>
            {
                if (captions is null || manualTags is null) throw new InvalidOperationException("PrepareDetails を呼ぶ前に DetailOf を呼んだ(SearchService は上位を決めたあと、DetailOf の前に呼ぶ)。");
                // 索引に行がある画像は、caption か手動タグのどちらかがある(caption だけ、タグだけ、両方)。caption が無いのは、手動タグだけで索引に入った画像。
                // 検索のあとで最後の手動タグが外される(「見つからない画像の記録を消す」操作で caption が消える)と、どちらも無くなりうる(正当な競合)。
                // そのときは全部の欄が空の詳細を返し(例外にしない)、黙って通さないために警告を 1 行残す(続くなら索引の食い違い)
                captions.TryGetValue(hashes[i], out CaptionRow? row);
                IReadOnlyList<string> tags = manualTags.TryGetValue(hashes[i], out IReadOnlyList<string>? t) ? t : [];
                if (row is null && tags.Count == 0)
                    _log?.Warning($"全文検索: 索引にある画像 {hashes[i][..Math.Min(12, hashes[i].Length)]}… に caption も手動タグも無い(検索のあとで外された・消された可能性。続くなら索引の食い違い)");
                return BuildDetail(row, tags, resolved);
            },
            message: message,
            prepare: indices =>
            {
                using SqliteConnection conn = _db.CreateReadConnection();
                (captions, manualTags) = ReadDetailSources(conn, [.. indices.Select(i => hashes[i])]);
            });
    }

    /// <summary>
    /// 詳細の元(上位の画像の最新の caption と手動タグ)を、**1 つの読み取りのトランザクションの中**で読む。2 つの文が別の時点を見ると、
    /// 片方だけ外れた状態(caption は消えたがタグは残っている、など)を読んでしまうので、WAL の同じスナップショットで読む
    /// (最初の SELECT で読み取りの時点が決まり、COMMIT まで変わらない。書き込みは待たせない)。
    /// <paramref name="betweenReads"/> は 2 つの読み取りの間に呼ばれる(テスト用。ここで書き込んでも、このトランザクションの読みには見えない)。
    /// </summary>
    internal static (Dictionary<string, CaptionRow> Captions, Dictionary<string, IReadOnlyList<string>> ManualTags) ReadDetailSources(
        SqliteConnection conn, string[] hashes, Action? betweenReads = null)
    {
        return OhanamiDatabase.InSnapshot(conn, () =>
        {
            Dictionary<string, CaptionRow> captions = CaptionStore.FindLatestMany(conn, hashes);
            betweenReads?.Invoke();
            Dictionary<string, IReadOnlyList<string>> manualTags = ManualTags.ForImages(conn, hashes);
            return (captions, manualTags);
        });
    }

    private static string QuoteWords(IReadOnlyList<string> words) => string.Join("、", words.Select(w => "「" + w + "」"));

    // ---------------------------------------------------------------- あいまい一致(ADR 0032)

    /// <summary>
    /// 語ごとの fallback: <see cref="QueryKind.Phrase"/> の語のうち、あいまい一致にできる(<see cref="FuzzyMatcher.TryPlan"/>: 日本語だけ・4 文字以上)もので、
    /// 正確な一致(その語だけのフレーズ)が索引に 1 件も無いものを、<see cref="QueryKind.Fuzzy"/> に置き換える。正確な一致がある語・1 文字の語・英数字を含む語は、そのまま。
    /// 「正確な一致が 0 件か」は索引全体で決まる(今日あいまい一致で見つかる画像は、同じ語を正確に含む画像が入った翌日から出なくなる。既知の性質。ADR 0032)。
    /// </summary>
    private SearchPlan ResolveFuzzy(SearchPlan plan, CancellationToken ct)
    {
        List<(int Index, FuzzyQuery Fuzzy)> candidates = [];
        for (int i = 0; i < plan.Terms.Count; i++)
        {
            QueryPlan t = plan.Terms[i];
            if (t.Kind != QueryKind.Phrase) continue;
            if (FuzzyMatcher.TryPlan(t.Word) is { } fuzzy) candidates.Add((i, fuzzy));
        }
        if (candidates.Count == 0) return plan;

        using SqliteConnection conn = _db.CreateReadConnection();
        var terms = plan.Terms.ToArray();
        foreach ((int index, FuzzyQuery fuzzy) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM {FtsIndexSpec.TableName} WHERE {FtsIndexSpec.TableName} MATCH $q LIMIT 1";
            cmd.Parameters.AddWithValue("$q", terms[index].FtsMatch);
            if (cmd.ExecuteScalar() is null) terms[index] = terms[index].ToFuzzy(fuzzy);
        }
        return new SearchPlan(terms);
    }

    // ---------------------------------------------------------------- 検索の SQL

    private (string[] Hashes, double[] Scores) Query(SearchPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<FuzzyQuery> fuzzies = plan.Fuzzies;
        // あいまい一致の語だけ(FTS のフレーズも 1 文字の語も無い): 全件を総当たり。順位に効く語が無いので全部同点(hash の序数順に並べて返す。同点は平均順位)
        if (fuzzies.Count > 0 && plan.FtsMatch is null && plan.SingleChars.Count == 0)
        {
            string[] found = FuzzyScan.ScanTable(_db, fuzzies, FuzzyScan.DefaultMaxThreads, ct);
            return (found, new double[found.Length]);
        }
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        string[] singles = plan.SingleChars.ToArray();
        var where = new List<string>();
        for (int i = 0; i < singles.Length; i++)
        {
            where.Add($"s.norm_text LIKE $l{i.ToString(CultureInfo.InvariantCulture)} ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$l" + i.ToString(CultureInfo.InvariantCulture), SearchPlan.LikePattern(singles[i]));
        }

        if (plan.FtsMatch is { } match)
        {
            // フレーズがある: 順位は BM25(-rank)。1 文字の語は LIKE の絞り込みだけ
            where.Insert(0, $"{FtsIndexSpec.TableName} MATCH $q");
            cmd.Parameters.AddWithValue("$q", match);
            cmd.CommandText =
                $"SELECT s.hash, -f.rank{(fuzzies.Count > 0 ? ", s.norm_text" : "")} FROM {FtsIndexSpec.TableName} f JOIN search_text s ON s.id = f.rowid WHERE {string.Join(" AND ", where)} ORDER BY f.rank, s.hash";
        }
        else
        {
            // すべて 1 文字: 出現回数の合計(length(文書) - length(その文字を除いた文書) の合計。SearchPlan.CountOccurrences と同じ値)
            var counts = new List<string>();
            for (int i = 0; i < singles.Length; i++)
            {
                string p = "$c" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(p, singles[i]);
                counts.Add($"(length(s.norm_text) - length(replace(s.norm_text, {p}, '')))");
            }
            cmd.CommandText = $"SELECT s.hash, CAST(({string.Join(" + ", counts)}) AS REAL) AS score{(fuzzies.Count > 0 ? ", s.norm_text" : "")} FROM search_text s WHERE {string.Join(" AND ", where)} ORDER BY score DESC, s.hash";
        }

        var hashes = new List<string>();
        var scores = new List<double>();
        var texts = fuzzies.Count > 0 ? new List<string>() : null;   // あいまい一致の語があるときだけ、候補の本文も読む
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            if ((hashes.Count & 1023) == 0) ct.ThrowIfCancellationRequested();
            hashes.Add(r.GetString(0));
            scores.Add(r.GetDouble(1));
            texts?.Add(r.GetString(2));
        }
        if (texts is null) return (hashes.ToArray(), scores.ToArray());

        // FTS・LIKE で絞った候補(順位の順)に、あいまい一致の語を重ねる。順位は変えない(絞り込みだけ)
        bool[] keep = FuzzyScan.Filter(texts, fuzzies, FuzzyScan.DefaultMaxThreads, ct);
        var keptHashes = new List<string>();
        var keptScores = new List<double>();
        for (int i = 0; i < keep.Length; i++)
        {
            if (!keep[i]) continue;
            keptHashes.Add(hashes[i]);
            keptScores.Add(scores[i]);
        }
        return (keptHashes.ToArray(), keptScores.ToArray());
    }

    // ---------------------------------------------------------------- 詳細(欄ごとの一致箇所)

    /// <summary>
    /// caption と手動タグから欄ごとの一致箇所を作る。強調は、索引と同じ置換(<see cref="FtsDocumentBuilder.Sanitize"/>。長さは変わらない)をかけた欄の文字列に対して行うので、
    /// 返す <see cref="FieldMatch.Text"/> 上の位置がそのまま使える。ocr・説明文・tags・手動タグの 4 欄を、この順で常に返す(一致が無い欄は Spans が空)。
    /// <paramref name="row"/> が null(caption が無い。手動タグだけの画像)なら、最初の 3 欄は空の文字列。
    /// </summary>
    internal static TextDetail BuildDetail(CaptionRow? row, IReadOnlyList<string> manualTags, SearchPlan plan)
    {
        return new TextDetail(
        [
            Field(TextField.Ocr, FtsDocumentBuilder.Sanitize(row?.Ocr ?? ""), plan),
            Field(TextField.Description, FtsDocumentBuilder.Sanitize(row?.Description ?? ""), plan),
            Field(TextField.Tags, FtsDocumentBuilder.JoinTags(row?.Tags ?? []), plan),
            Field(TextField.ManualTags, FtsDocumentBuilder.JoinTags(manualTags), plan),
        ])
        {
            // この画像は、すべての語に一致している。あいまい一致の語は、その語で当たった(正確な一致が索引に無かった)
            FuzzyTerms = plan.Terms.Where(t => t.Kind == QueryKind.Fuzzy).Select(t => t.Fuzzy!.Source).ToArray(),
        };
    }

    private static FieldMatch Field(TextField field, string text, SearchPlan plan)
    {
        IReadOnlyList<TextRange> ranges;
        try
        {
            ranges = Highlighter.Find(text, plan);
        }
        catch (InvalidOperationException)
        {
            ranges = []; // 書記素クラスタごとの NFKC が合わない、まれな文字列。強調だけを諦める(検索の結果は返す)
        }
        return new FieldMatch(field, text, ranges.Select(r => new TextSpan(r.Start, r.Length)).ToArray());
    }
}
