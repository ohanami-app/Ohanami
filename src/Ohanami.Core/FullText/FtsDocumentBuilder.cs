using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ohanami.Core.FullText;

/// <summary>
/// 1 枚の画像の索引文書。<see cref="NormText"/> は <c>search_text.norm_text</c>(NFKC + 小文字化した、区切りを含む文書全体。1 文字のクエリの LIKE 用)、
/// <see cref="Tokens"/> は <c>fts_docs</c> の 1 列(<see cref="FtsIndexSpec.Column"/>)に入れる、bigram 化したトークンの空白区切り。
/// </summary>
internal sealed record FtsDocument(string NormText, string Tokens);

/// <summary>
/// captions(ocr、説明文、tags)から索引文書を作る純粋な関数(ADR 0029)。
/// <list type="bullet">
/// <item>1 文書 = ocr + <see cref="FtsSpec.Field"/>(「|」)+ 説明文 + 「|」+ tags を <see cref="FtsSpec.Tag"/>(「、」)でつないだ文字列。評価で使った索引文書と同じ形
///   (評価の基準線と比べられるように)。tags が空の画像は末尾が「|」で終わる。</item>
/// <item><b>文字列の置換(<see cref="FtsSpec.SanitizeVersion"/>)</b>: VLM の出力に、<see cref="CharClassifier.Validate"/> が例外にする文字(タブ・改行以外の制御文字、不正なサロゲート)が
///   入りうる(JSON は <c>\u0000</c> や孤立した <c>\ud800</c> を運べる)。captions には元の文字列を保存し、索引文書と強調表示だけ、該当する 1 文字(UTF-16 の 1 コードユニット)を
///   U+FFFD に置き換える(<see cref="Sanitize"/>)。1 対 1 の置換なので、元のテキスト上の位置は変わらない(強調の範囲がそのまま使える)。U+FFFD は記号なので、索引では区切りになる。</item>
/// </list>
/// 索引文書が作れない(<see cref="NormalizedText.Create"/> が、書記素クラスタごとの NFKC と全体の NFKC の不一致で <see cref="InvalidOperationException"/> を投げる)ときは、この関数も例外を投げる
/// (呼び出し側が、その画像を failed として記録する)。置換の規則を変えたら <see cref="FtsSpec.SanitizeVersion"/> を上げる。
/// </summary>
internal static class FtsDocumentBuilder
{
    /// <summary>置換に使う文字(U+FFFD REPLACEMENT CHARACTER。記号なので区切りになる)。</summary>
    public const char Replacement = '�';

    /// <summary>
    /// 索引と強調の前に文字列を整える(<c>sanitize-v1</c>): タブ・改行以外の制御文字と、不正なサロゲート(対になっていない上位・下位)の 1 コードユニットを U+FFFD にする。
    /// 長さは変わらない。<paramref name="s"/> が null なら例外。
    /// </summary>
    public static string Sanitize(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        StringBuilder? sb = null;
        int i = 0;
        while (i < s.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(s.AsSpan(i), out Rune r, out int consumed);
            if (consumed <= 0) consumed = 1; // 進まないと止まらない(想定外の戻り値への備え)
            bool bad = status != OperationStatus.Done
                || (Rune.GetUnicodeCategory(r) == UnicodeCategory.Control && !Rune.IsWhiteSpace(r));
            if (bad)
            {
                sb ??= new StringBuilder(s);
                // 不正なサロゲートは 1 コードユニット(consumed = 1)。制御文字は BMP の 1 コードユニット
                for (int k = 0; k < consumed; k++) sb[i + k] = Replacement;
            }
            i += consumed;
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>ocr・説明文・tags を、区切りでつないだ 1 つの文字列にする(置換の前の元の文字列から。<see cref="Sanitize"/> は各欄にかける)。手動タグが無い画像の文書。</summary>
    public static string Join(string ocr, string description, IReadOnlyList<string> tags) =>
        Join(ocr, description, tags, []);

    /// <summary>
    /// ocr・説明文・VLM の tags・**手動タグ**を、区切りでつないだ 1 つの文字列にする(ADR 0036)。
    /// **手動タグが 1 つも無ければ、4 つ目の欄を足さず、<see cref="Join(string, string, IReadOnlyList{string})"/> と同じ文字列**にする
    /// (手動タグを使わない画像の文書が、手動タグを足す前と変わらないことを保つため。区切りの「|」の数も変えない)。
    /// 手動タグがあれば、末尾に「|」と、手動タグを「、」でつないだものを足す(名前順に並べて渡す。並びは呼び出し側の責任)。
    /// caption が無い画像は、ocr・説明文・tags を空にして渡す(「|||手動タグ」の形になる)。
    /// </summary>
    public static string Join(string ocr, string description, IReadOnlyList<string> tags, IReadOnlyList<string> manualTags)
    {
        ArgumentNullException.ThrowIfNull(manualTags);
        string document = Sanitize(ocr) + FtsSpec.Field + Sanitize(description) + FtsSpec.Field + JoinTags(tags);
        return manualTags.Count == 0 ? document : document + FtsSpec.Field + JoinTags(manualTags);
    }

    /// <summary>tags を「、」でつないだ文字列(各 tag に <see cref="Sanitize"/>)。</summary>
    public static string JoinTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return string.Join(FtsSpec.Tag, tags.Select(t => Sanitize(t ?? throw new ArgumentException("tags に null がある", nameof(tags)))));
    }

    /// <summary>
    /// 索引文書を作る(連結した文書全体を 1 列に。列をまたぐ区切りは <see cref="FtsSpec.Field"/>・<see cref="FtsSpec.Tag"/> がトークンを作らないことで保つ)。
    /// 例外: 索引文書が作れない入力(<see cref="InvalidOperationException"/>)。
    /// </summary>
    public static FtsDocument Build(string ocr, string description, IReadOnlyList<string> tags) => Build(ocr, description, tags, []);

    /// <summary>手動タグつきの索引文書(<see cref="Join(string, string, IReadOnlyList{string}, IReadOnlyList{string})"/>)。例外は <see cref="Build(string, string, IReadOnlyList{string})"/> と同じ。</summary>
    public static FtsDocument Build(string ocr, string description, IReadOnlyList<string> tags, IReadOnlyList<string> manualTags)
    {
        NormalizedText whole = NormalizedText.Create(Join(ocr, description, tags, manualTags));
        return new FtsDocument(whole.Text, TokensOf(whole));
    }

    private static string TokensOf(NormalizedText n) => QueryPlanner.TokensToIndexText(Tokenizer.TokenizeDocument(n));
}
