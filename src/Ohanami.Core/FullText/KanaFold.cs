namespace Ohanami.Core.FullText;

/// <summary>
/// ひらがなとカタカナの同一視(ADR 0031)。NFKC → 小文字化のあとの文字を、カタカナはひらがなへ 1 文字ずつ写す。
/// <list type="bullet">
/// <item>U+30A1–U+30F6(ァ–ヶ。ヴ ヵ ヶ を含む)→ U+3041–U+3096(ぁ–ゖ。差は一定の 0x60)</item>
/// <item>U+30FD ヽ → U+309D ゝ、U+30FE ヾ → U+309E ゞ(同じ差 0x60)</item>
/// <item>写さない文字(ヿ U+30FF は NFKC が コト に分けるので、写像を通らずに「こと」になる): 長音符 ー(U+30FC)、ヷ–ヺ(U+30F7–U+30FA。対応するひらがなが無い)、中黒 ・(U+30FB)、カタカナ拡張(U+31F0–U+31FF)</item>
/// </list>
/// 1 文字 → 1 文字(UTF-16 でも 1 単位 → 1 単位)なので、正規化後の位置と元のテキストの位置の対応(<see cref="NormalizedText"/>)は変わらない。
/// 写す前と後で、どちらも <see cref="CharClassifier.IsJapanese"/> は真(分割の種類は変わらない)。
/// 半角カナは NFKC で全角のカタカナになってから写る。濁点・半濁点の有無は区別したまま(か と が は別の文字)。
/// 索引・クエリ・強調はどれも <see cref="NormalizedText.Create"/> を通るので、写像はここ 1 か所に入れる。
/// 手動タグの名前の正規化(<c>ManualTagName.Normalize</c>)と、見た目の経路(SigLIP2 のテキスト側)には使わない。
/// </summary>
internal static class KanaFold
{
    private const int Offset = 0x60;

    /// <summary>カタカナなら、対応するひらがな。それ以外はそのまま。</summary>
    public static char Fold(char c)
    {
        if ((c >= 'ァ' && c <= 'ヶ') || c == 'ヽ' || c == 'ヾ')
        {
            return (char)(c - Offset);
        }
        return c;
    }

    /// <summary>文字列の各文字を <see cref="Fold(char)"/> で写す(長さは変わらない)。</summary>
    public static string Fold(string s)
    {
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = Fold(src[i]);
        });
    }
}
