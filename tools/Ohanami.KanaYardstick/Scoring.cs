using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ohanami.KanaYardstick;

/// <summary>字の数と誤りの数(1 つの集計の単位)。</summary>
internal sealed class Tally
{
    /// <summary>正解の字数。</summary>
    public int Ref;

    /// <summary>正解のうち、濁点・半濁点の付く字(<see cref="Zu"/> を含む)。</summary>
    public int Voiced;

    /// <summary>正解のうち、ぢ・づ・ヂ・ヅ。字の形の差が小さく、ほかの字より取り違えやすいので、別に数える。</summary>
    public int Zu;

    /// <summary>濁点・半濁点だけの誤り(<see cref="DakZu"/> を含む)と、その内訳(落 = 付く字に付かなかった / 付加 = 付かない字に付いた / 取違 = 濁点と半濁点の取り違え)。</summary>
    public int Dak, DakLost, DakAdded, DakSwap, DakZu;

    /// <summary>濁点・半濁点以外の誤り(別の字への置換 + 読み落とし)を、正解の字が濁点の付く字か付かない字(清音)かで分けたもの。</summary>
    public int OtherSubVoiced, OtherSubPlain, DeletedVoiced, DeletedPlain;

    /// <summary>挿入(出力にだけある余計な字)と、そのうち記号・約物。</summary>
    public int Inserted, InsertedPunctuation;

    public int Plain => Ref - Voiced;

    public int VoicedWithoutZu => Voiced - Zu;

    public int DakWithoutZu => Dak - DakZu;

    public int Errors => Dak + OtherSubVoiced + OtherSubPlain + DeletedVoiced + DeletedPlain + Inserted;

    public void Add(Tally o)
    {
        Ref += o.Ref; Voiced += o.Voiced; Zu += o.Zu;
        Dak += o.Dak; DakLost += o.DakLost; DakAdded += o.DakAdded; DakSwap += o.DakSwap; DakZu += o.DakZu;
        OtherSubVoiced += o.OtherSubVoiced; OtherSubPlain += o.OtherSubPlain; DeletedVoiced += o.DeletedVoiced; DeletedPlain += o.DeletedPlain;
        Inserted += o.Inserted; InsertedPunctuation += o.InsertedPunctuation;
    }
}

/// <summary>
/// 採点。正規化は ADR 0021 と同じ(NFKC・小文字化・空白除去)。正解と出力を文字(コードポイント)で並べ、編集距離の対応づけ(斜めを優先)から数える。
/// 濁点・半濁点だけの誤り = 置換のうち、濁点・半濁点を外した字が同じもの。それ以外の置換・読み落とし(削除)は、正解の字が濁点の付く字か付かない字かで分ける。
/// 挿入 = 余計な字(でっち上げ)。
/// </summary>
internal static class Scoring
{
    /// <summary>ひらがな(0)・カタカナ(1)・それ以外(2)。</summary>
    internal const int Hiragana = 0, Katakana = 1, OtherScript = 2;

    public static string Normalize(string s)
    {
        string n = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (char c in n)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>濁点(U+3099)なら 1、半濁点(U+309A)なら 2、どちらも付かなければ 0。付く字は、付かない字(Base)と合わせて返す。</summary>
    public static (int Base, int Mark) Decompose(int codePoint)
    {
        string d = char.ConvertFromUtf32(codePoint).Normalize(NormalizationForm.FormD);
        int b = char.ConvertToUtf32(d, 0);
        int mark = d.Contains('゙') ? 1 : d.Contains('゚') ? 2 : 0;
        return (b, mark);
    }

    public static bool IsZu(int codePoint) => codePoint is 'ぢ' or 'づ' or 'ヂ' or 'ヅ';

    public static int ScriptOf(int codePoint) => codePoint is >= 0x3041 and <= 0x309F ? Hiragana : codePoint is >= 0x30A0 and <= 0x30FF ? Katakana : OtherScript;

    private static int[] CodePoints(string s) => s.EnumerateRunes().Select(r => r.Value).ToArray();

    /// <summary>正規化済みの正解と出力を数える。戻りは、文字種(<see cref="Hiragana"/>・<see cref="Katakana"/>・<see cref="OtherScript"/>)ごとの集計と、誤りの並び(人が読む用)。</summary>
    public static (Tally[] ByScript, string Alignment) Count(string reference, string hypothesis)
    {
        Tally[] t = [new(), new(), new()];
        int[] r = CodePoints(reference), h = CodePoints(hypothesis);
        foreach (int c in r)
        {
            Tally tally = t[ScriptOf(c)];
            tally.Ref++;
            if (Decompose(c).Mark != 0) tally.Voiced++;
            if (IsZu(c)) tally.Zu++;
        }
        int n = r.Length, m = h.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                dp[i, j] = Math.Min(dp[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1), Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1));

        var ops = new List<string>();
        int a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && dp[a, b] == dp[a - 1, b - 1] + (r[a - 1] == h[b - 1] ? 0 : 1))
            {
                if (r[a - 1] != h[b - 1])
                {
                    Tally tally = t[ScriptOf(r[a - 1])];
                    (int baseRef, int markRef) = Decompose(r[a - 1]);
                    (int baseHyp, int markHyp) = Decompose(h[b - 1]);
                    string rs = char.ConvertFromUtf32(r[a - 1]), hs = char.ConvertFromUtf32(h[b - 1]);
                    if (baseRef == baseHyp && markRef != markHyp)
                    {
                        tally.Dak++;
                        if (IsZu(r[a - 1])) tally.DakZu++;
                        if (markRef != 0 && markHyp == 0) tally.DakLost++;
                        else if (markRef == 0 && markHyp != 0) tally.DakAdded++;
                        else tally.DakSwap++;
                        ops.Add($"{rs}→{hs}[濁]");
                    }
                    else
                    {
                        if (markRef != 0) tally.OtherSubVoiced++; else tally.OtherSubPlain++;
                        ops.Add($"{rs}→{hs}");
                    }
                }
                a--; b--;
            }
            else if (a > 0 && dp[a, b] == dp[a - 1, b] + 1)
            {
                Tally tally = t[ScriptOf(r[a - 1])];
                if (Decompose(r[a - 1]).Mark != 0) tally.DeletedVoiced++; else tally.DeletedPlain++;
                ops.Add($"{char.ConvertFromUtf32(r[a - 1])}→∅");
                a--;
            }
            else
            {
                Tally tally = t[ScriptOf(h[b - 1])];
                tally.Inserted++;
                if (IsPunctuationOrSymbol(h[b - 1])) tally.InsertedPunctuation++;
                ops.Add($"∅→{char.ConvertFromUtf32(h[b - 1])}");
                b--;
            }
        }
        ops.Reverse();
        return (t, string.Join(" ", ops));
    }

    private static bool IsPunctuationOrSymbol(int codePoint)
    {
        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return cat is UnicodeCategory.OtherPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.MathSymbol
            or UnicodeCategory.OtherSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol;
    }

    /// <summary>Wilson の 95% 区間(%の表記の文字列。母数 0 なら空)。</summary>
    public static string WilsonInterval(int k, int n)
    {
        if (n == 0) return "";
        const double z = 1.96;
        double p = k / (double)n, d = 1 + z * z / n;
        double c = (p + z * z / (2 * n)) / d, h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return string.Create(CultureInfo.InvariantCulture, $"[{(c - h) * 100:0.0}-{(c + h) * 100:0.0}]");
    }
}
