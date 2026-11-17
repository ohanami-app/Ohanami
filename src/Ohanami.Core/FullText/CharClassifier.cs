using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ohanami.Core.FullText;

/// <summary>
/// 索引用の文字分類。入力は NFKC + 小文字化 + カタカナ → ひらがな(<see cref="KanaFold"/>)の後の文字を想定する(半角カナや全角英数は、この時点では残っていない。カタカナの ァ–ヶ ヽヾ も残らない)。
/// </summary>
internal static class CharClassifier
{
    /// <summary>
    /// 「日本語の文字」= bigram に分割する文字。範囲(コードポイント):
    /// <list type="bullet">
    /// <item>U+3041–U+3096 ひらがな(小書き・ゕゖ を含む)</item>
    /// <item>U+309D–U+309F ゝ ゞ ゟ</item>
    /// <item>U+30A1–U+30FA カタカナ(ァ–ヺ。ヶ ヵ を含む)</item>
    /// <item>U+30FC–U+30FF ー(長音符) ヽ ヾ ヿ</item>
    /// <item>U+31F0–U+31FF カタカナ拡張(小書きのㇰなど)</item>
    /// <item>U+3400–U+4DBF 漢字(CJK 拡張 A)、U+4E00–U+9FFF 漢字(基本)</item>
    /// <item>U+F900–U+FAFF CJK 互換漢字(NFKC 後も残るものがある)</item>
    /// <item>U+20000–U+2FA1F 漢字(拡張 B〜F、互換補助)、U+30000–U+323AF 漢字(拡張 G・H)</item>
    /// <item>U+3005 々、U+3006 〆、U+3007 〇</item>
    /// </list>
    /// 含めないもの: U+30FB ・(中黒。区切り文字として扱う)、U+30A0 ゠、U+3099/U+309A(結合濁点・半濁点。
    /// NFKC で直前の文字に合成されるので、単独で残るのは合成できない異常入力だけ。区切りにする)、
    /// 句読点・括弧(U+3000 台の記号)・絵文字。
    /// </summary>
    public static bool IsJapanese(Rune r)
    {
        int v = r.Value;
        return (v >= 0x3041 && v <= 0x3096)
            || (v >= 0x309D && v <= 0x309F)
            || (v >= 0x30A1 && v <= 0x30FA)
            || (v >= 0x30FC && v <= 0x30FF)
            || (v >= 0x31F0 && v <= 0x31FF)
            || (v >= 0x3400 && v <= 0x4DBF)
            || (v >= 0x4E00 && v <= 0x9FFF)
            || (v >= 0xF900 && v <= 0xFAFF)
            || (v >= 0x20000 && v <= 0x2FA1F)
            || (v >= 0x30000 && v <= 0x323AF)
            || v == 0x3005 || v == 0x3006 || v == 0x3007;
    }

    /// <summary>
    /// 英数字の「単語」を作る文字 = 日本語以外の文字(L*)と十進数字(Nd)。
    /// SQLite の unicode61 が単語文字と見なす範囲(L*, N*, Co)の部分集合になる。
    /// 私用領域(Co)は含めない(境界記号に U+E000 / U+E001 を使うため、本文の Co と衝突させない)。
    /// </summary>
    public static bool IsWordChar(Rune r) => !IsJapanese(r) && Rune.IsLetterOrDigit(r);

    /// <summary>
    /// 入力検証。null・不正なサロゲート・タブ/改行以外の制御文字は例外にする(握りつぶさない)。
    /// Rune.IsWhiteSpace が真の制御文字(\t \n \v \f \r U+0085)は空白として許す。
    /// </summary>
    public static void Validate(string? s, string paramName)
    {
        ArgumentNullException.ThrowIfNull(s, paramName);
        int i = 0;
        while (i < s.Length)
        {
            if (Rune.DecodeFromUtf16(s.AsSpan(i), out Rune r, out int consumed) != OperationStatus.Done)
            {
                throw new ArgumentException($"位置 {i} に不正なサロゲート(U+{(int)s[i]:X4})がある。", paramName);
            }
            if (Rune.GetUnicodeCategory(r) == UnicodeCategory.Control && !Rune.IsWhiteSpace(r))
            {
                throw new ArgumentException($"位置 {i} に制御文字(U+{r.Value:X4})がある。", paramName);
            }
            i += consumed;
        }
    }
}
