using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ohanami.Core.Tags;

/// <summary>
/// 手動タグの名前の決まり(ADR 0036)。保存する名前は <see cref="Normalize"/> の結果だけ。
/// <list type="bullet">
/// <item>前後の空白を除き、NFKC にする(全角英数を半角に。半角カナは全角カナに)。**大文字・小文字は打ったまま**保存し、表示もそのまま。
///   同じ名前かどうかの判定は、tags 表の <c>UNIQUE (name COLLATE NOCASE, kind)</c> に任せる(ASCII だけ。非 ASCII の大文字・小文字の重複は許す)。表示用の列は足さない。</item>
/// <item>拒む名前(<see cref="InvalidTagNameException"/>。理由つき): 空(空白だけも)、制御文字(タブを含む)・改行(U+2028・U+2029 も)を含む、「|」「、」を含む、
///   <see cref="MaxLength"/> 文字を超える、対になっていないサロゲートを含む。「|」「、」は索引の上では区切りで語を作らない(Tokenizer)ので害は無いが、
///   索引文書と画面の表示の区切りと衝突するため拒む。</item>
/// </list>
/// 拒む判定は**正規化のあと**に行う(全角の「｜」は NFKC で「|」になる)。長さは Unicode のコードポイント(文字)の数で数える(サロゲートペアは 1 文字)。
/// </summary>
public static class ManualTagName
{
    /// <summary>名前の長さの上限(文字。NFKC のあと)。</summary>
    public const int MaxLength = 64;

    /// <summary>名前を整える。拒む名前は <see cref="InvalidTagNameException"/>。同じ入力には同じ結果を返し、結果をもう一度通しても変わらない。</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // 対になっていないサロゲートは、NFKC が例外にする(または黙って置き換える)ので、先に見つけて理由を出す
        int i = 0;
        while (i < name.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(name.AsSpan(i), out _, out int consumed);
            if (status != OperationStatus.Done) throw new InvalidTagNameException(name, "不正な文字(対になっていないサロゲート)を含む");
            i += consumed;
        }

        string normalized = name.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0) throw new InvalidTagNameException(name, "空(空白だけ)");

        int runes = 0;
        foreach (Rune r in normalized.EnumerateRunes())
        {
            runes++;
            UnicodeCategory category = Rune.GetUnicodeCategory(r);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new InvalidTagNameException(name, "制御文字・改行を含む");
            if (r.Value == '|' || r.Value == '、')
                throw new InvalidTagNameException(name, $"「{(char)r.Value}」を含む(索引と表示の区切りに使っている)");
        }
        if (runes > MaxLength) throw new InvalidTagNameException(name, $"{MaxLength} 文字を超える({runes} 文字)");
        return normalized;
    }
}
