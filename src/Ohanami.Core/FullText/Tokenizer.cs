using System;
using System.Collections.Generic;
using System.Text;

namespace Ohanami.Core.FullText;

internal enum TokenKind
{
    /// <summary>英数字などの単語(NFKC・小文字化済み)。</summary>
    Word,
    /// <summary>
    /// 日本語の連続部分(run)の、隣り合う2文字。run の両端には境界記号(<see cref="Tokenizer.StartPad"/> /
    /// <see cref="Tokenizer.EndPad"/>)を仮想の1文字として足してから bigram を作る(「^ガ」「ガチ」「チャ」「ャ$」)。
    /// </summary>
    Bigram,
}

/// <param name="Text">索引に入れる文字列。</param>
/// <param name="NormStart">NormalizedText.Text 上の開始位置(UTF-16)。境界記号は隣の実文字の範囲を借りる。</param>
/// <param name="NormEnd">同・終了位置(排他)。</param>
internal readonly record struct Token(string Text, TokenKind Kind, int NormStart, int NormEnd);

/// <param name="Tokens">クエリのトークン列。</param>
/// <param name="SingleChar">クエリが「1文字だけ」のときのその文字(正規化後)。このとき Tokens は空。</param>
internal sealed record QueryTokenization(IReadOnlyList<Token> Tokens, string? SingleChar);

/// <summary>
/// 索引とクエリで同じ分割規則を使う。違うのは境界記号の足し方だけ:
/// <list type="bullet">
/// <item>索引(文書): 日本語の run は両端に境界記号を必ず足す。</item>
/// <item>クエリ: 前後の区切り・空白は無視し、残った部分の中で、run が他の要素(単語・区切り・別の run)と
/// 隣り合っている側にだけ境界記号を足す。クエリの端は「文書の run の途中かもしれない」ので足さない。</item>
/// </list>
/// 境界記号があるおかげで、「ガチャ10連」(連は1文字の run)が「ガチャ10連を引く」(連を〜)に当たり、
/// 句読点を挟んだ run 同士の bigram がつながって偽陽性になる(「はい。いいえ」に「はいい」が当たる)こともない。
/// </summary>
internal static class Tokenizer
{
    /// <summary>run の開始を表す境界記号。私用領域(Co)で、unicode61 は単語文字として扱う。本文側では Co は区切り扱いなので衝突しない。</summary>
    public const char StartPad = '';

    /// <summary>run の終了を表す境界記号。</summary>
    public const char EndPad = '';

    private enum SegKind { Run, Word, Sep }

    private sealed class Segment(SegKind kind)
    {
        public SegKind Kind { get; } = kind;
        public List<(Rune R, int Start, int End)> Chars { get; } = new();
        public string WordText { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
    }

    /// <summary>文書(索引する側)のトークン列。</summary>
    public static IReadOnlyList<Token> TokenizeDocument(NormalizedText n)
    {
        var tokens = new List<Token>();
        foreach (Segment seg in Segmentize(n))
        {
            if (seg.Kind == SegKind.Run) AddRunTokens(tokens, seg, padLeft: true, padRight: true);
            else if (seg.Kind == SegKind.Word) tokens.Add(new Token(seg.WordText, TokenKind.Word, seg.Start, seg.End));
        }
        return tokens;
    }

    /// <summary>クエリのトークン列。検索できる文字がなければ ArgumentException。</summary>
    public static QueryTokenization TokenizeQuery(NormalizedText n)
    {
        List<Segment> segs = Segmentize(n);
        int first = 0, last = segs.Count - 1;
        while (first <= last && segs[first].Kind == SegKind.Sep) first++;
        while (last >= first && segs[last].Kind == SegKind.Sep) last--;
        if (first > last)
        {
            throw new ArgumentException("クエリに検索できる文字(日本語・英数字)がない。", "query");
        }

        if (first == last)
        {
            Segment only = segs[first];
            if (only.Kind == SegKind.Run && only.Chars.Count == 1)
            {
                return new QueryTokenization(Array.Empty<Token>(), only.Chars[0].R.ToString());
            }
            if (only.Kind == SegKind.Word && CountRunes(only.WordText) == 1)
            {
                return new QueryTokenization(Array.Empty<Token>(), only.WordText);
            }
        }

        var tokens = new List<Token>();
        for (int i = first; i <= last; i++)
        {
            Segment seg = segs[i];
            if (seg.Kind == SegKind.Run) AddRunTokens(tokens, seg, padLeft: i > first, padRight: i < last);
            else if (seg.Kind == SegKind.Word) tokens.Add(new Token(seg.WordText, TokenKind.Word, seg.Start, seg.End));
        }
        if (tokens.Count == 0)
        {
            // 1文字の run が単独 → 上で SingleChar になっているので、ここには来ないはず
            throw new InvalidOperationException("クエリからトークンが1つも作れなかった(想定外)。");
        }
        return new QueryTokenization(tokens, null);
    }

    private static int CountRunes(string s)
    {
        int c = 0;
        foreach (Rune _ in s.EnumerateRunes()) c++;
        return c;
    }

    private static void AddRunTokens(List<Token> tokens, Segment run, bool padLeft, bool padRight)
    {
        // 仮想の文字列: [境界] c1 c2 ... ck [境界]。隣り合う2つで bigram を作る。
        var items = new List<(string Text, bool Pad, int Start, int End)>();
        if (padLeft) items.Add((StartPad.ToString(), true, run.Chars[0].Start, run.Chars[0].End));
        foreach (var (r, s, e) in run.Chars) items.Add((r.ToString(), false, s, e));
        if (padRight) items.Add((EndPad.ToString(), true, run.Chars[^1].Start, run.Chars[^1].End));
        for (int k = 0; k + 1 < items.Count; k++)
        {
            var a = items[k];
            var b = items[k + 1];
            int start = a.Pad ? b.Start : a.Start;
            int end = b.Pad ? a.End : b.End;
            tokens.Add(new Token(a.Text + b.Text, TokenKind.Bigram, start, end));
        }
    }

    /// <summary>
    /// 正規化後のテキストを、日本語の連続部分(Run)・単語(Word)・区切り(Sep)に分ける。
    /// 日本語の文字と日本語の文字の間にある空白・改行は、Run の中で無かったことにする(折り返し対策)。
    /// </summary>
    private static List<Segment> Segmentize(NormalizedText n)
    {
        var runes = new List<(Rune R, int Start, int End)>();
        int idx = 0;
        foreach (Rune r in n.Text.EnumerateRunes())
        {
            runes.Add((r, idx, idx + r.Utf16SequenceLength));
            idx += r.Utf16SequenceLength;
        }

        var segs = new List<Segment>();
        Segment? run = null;
        Segment? word = null;
        var wordSb = new StringBuilder();

        void CloseRun()
        {
            if (run is null) return;
            run.Start = run.Chars[0].Start;
            run.End = run.Chars[^1].End;
            segs.Add(run);
            run = null;
        }

        void CloseWord()
        {
            if (word is null) return;
            word.WordText = wordSb.ToString();
            segs.Add(word);
            word = null;
            wordSb.Clear();
        }

        void AddSep()
        {
            // 連続する区切りは1つにまとめる
            if (segs.Count == 0 || segs[^1].Kind != SegKind.Sep) segs.Add(new Segment(SegKind.Sep));
        }

        for (int i = 0; i < runes.Count; i++)
        {
            var (r, start, end) = runes[i];
            if (CharClassifier.IsJapanese(r))
            {
                CloseWord();
                run ??= new Segment(SegKind.Run);
                run.Chars.Add((r, start, end));
            }
            else if (Rune.IsWhiteSpace(r))
            {
                if (run is not null)
                {
                    int j = i + 1;
                    while (j < runes.Count && Rune.IsWhiteSpace(runes[j].R)) j++;
                    if (j < runes.Count && CharClassifier.IsJapanese(runes[j].R))
                    {
                        i = j - 1; // 日本語どうしの間の空白は読み飛ばす
                        continue;
                    }
                    CloseRun();
                }
                CloseWord();
                AddSep();
            }
            else if (CharClassifier.IsWordChar(r))
            {
                CloseRun();
                if (word is null)
                {
                    word = new Segment(SegKind.Word) { Start = start };
                }
                wordSb.Append(r.ToString());
                word.End = end;
            }
            else
            {
                // 句読点・括弧・記号・絵文字・書式文字・私用領域など: 区切り
                CloseRun();
                CloseWord();
                AddSep();
            }
        }
        CloseRun();
        CloseWord();
        return segs;
    }
}
