using System;
using System.Collections.Generic;
using System.Text;

namespace S3Fts;

public enum QueryKind
{
    /// <summary>FTS5 のフレーズ検索。</summary>
    Phrase,
    /// <summary>1文字のクエリ。FTS では引かず、LIKE の全件走査にする。</summary>
    SingleChar,
}

/// <param name="Tokens">Phrase のときのクエリのトークン列(索引と同じ分割規則で作る)。SingleChar では空。</param>
/// <param name="FtsMatch">Phrase のときの MATCH 式(ダブルクォートで囲んだフレーズ)。SingleChar では null。</param>
/// <param name="SingleChar">SingleChar のときの、正規化後の1文字。Phrase では null。</param>
public sealed record QueryPlan(QueryKind Kind, IReadOnlyList<Token> Tokens, string? FtsMatch, string? SingleChar);

public static class QueryPlanner
{
    public static QueryPlan Plan(string query)
    {
        CharClassifier.Validate(query, nameof(query));
        QueryTokenization q = Tokenizer.TokenizeQuery(NormalizedText.Create(query));
        if (q.SingleChar is not null)
        {
            return new QueryPlan(QueryKind.SingleChar, q.Tokens, null, q.SingleChar);
        }
        return new QueryPlan(QueryKind.Phrase, q.Tokens, "\"" + TokensToIndexText(q.Tokens) + "\"", null);
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
}
