using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ohanami.KanaYardstick;

/// <summary>合成画像 1 枚の仕様。<see cref="Lines"/> が正解の文(ひらがなの行とカタカナの行が交互に並ぶ)。</summary>
internal sealed record Spec(string Id, int W, int H, string Bg, int Height, string Font, int Seed, string[] Lines);

/// <summary>
/// 合成画像の仕様を、乱数の種から決める(同じ種なら同じ仕様)。画像の大きさは、画像トークンの上限 3584・下限は既定のとき、画像トークンが 500〜900 になる
/// 大きさ(画像トークン ≒ 横 ÷ 32 × 縦 ÷ 32): 1280×720 → 約 880、1024×576 → 約 576。
/// 文は、濁点・半濁点の付く字(24 字)と付かない字(40 字)を、意味のない並びにしたもの。単語の予測が効かない並びで、字の形だけの読み取りを測る。
/// 背景(dark / light / busy)× 字の高さ(12・16・20・24・32 px)× 6 枚 = 90 枚。1 枚に約 120 字。
/// </summary>
internal static class ImageSpecs
{
    /// <summary>濁点・半濁点の付く字(ひらがな。カタカナは +0x60 で作る)。</summary>
    internal const string Voiced = "がぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽ";

    /// <summary>付かない字(Voiced の清音をすべて含む)。</summary>
    internal const string Plain = "かきくけこさしすせそたちつてとはひふへほあいうえおなにぬねのまみむめもやゆよらりるれろわ";

    internal static readonly (int W, int H)[] Screens = [(1280, 720), (1024, 576)];
    internal static readonly string[] Backgrounds = ["dark", "light", "busy"];
    internal static readonly int[] Heights = [12, 16, 20, 24, 32];
    internal static readonly string[] Fonts = ["Yu Gothic", "Meiryo"];

    internal const int DefaultSeed = 20261119;
    internal const int TargetChars = 120;
    internal const int MaxCharsPerLine = 30;
    internal const int PerCell = 6;

    public static IReadOnlyList<Spec> Build(int seed)
    {
        var specs = new List<Spec>();
        int index = 0;
        foreach (string bg in Backgrounds)
            foreach (int height in Heights)
                for (int n = 0; n < PerCell; n++)
                {
                    (int w, int h) = Screens[n % 2];
                    string font = Fonts[(n + n / 2) % 2];
                    string id = $"{w}x{h}-{bg}-h{height.ToString("00", CultureInfo.InvariantCulture)}-{n.ToString(CultureInfo.InvariantCulture)}";
                    // 画像ごとの種。int の溢れは、そのまま(種が違えば別の文になる。同じ種で同じ仕様になる)
                    specs.Add(MakeSpec(id, w, h, bg, height, font, unchecked(seed * 1000 + index)));
                    index++;
                }
        return specs;
    }

    private static Spec MakeSpec(string id, int w, int h, string bg, int height, string font, int seed)
    {
        var rng = new Random(seed);
        int pad = Math.Max(12, height);
        int windowMaxWidth = (int)(w * 0.78);
        int charsPerLine = Math.Min(MaxCharsPerLine, (windowMaxWidth - 2 * pad) / height);
        int lines = 2 * (int)Math.Ceiling(TargetChars / (double)charsPerLine / 2);   // ひらがなの行とカタカナの行を同数にするため偶数
        int perLine = (int)Math.Ceiling(TargetChars / (double)lines);
        bool katakanaFirst = rng.Next(2) == 1;
        var text = new string[lines];
        for (int i = 0; i < lines; i++) text[i] = MakeLine(rng, perLine, katakana: (i % 2 == 0) == katakanaFirst);
        return new Spec(id, w, h, bg, height, font, seed, text);
    }

    /// <summary>1 行。濁点・半濁点の付く字が 55〜65% で、同じ字は 2 つ続けない。</summary>
    private static string MakeLine(Random rng, int n, bool katakana)
    {
        int voicedCount = (int)Math.Round(n * (0.55 + rng.NextDouble() * 0.1));
        var isVoiced = new bool[n];
        for (int i = 0; i < voicedCount; i++) isVoiced[i] = true;
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (isVoiced[i], isVoiced[j]) = (isVoiced[j], isVoiced[i]);
        }
        var sb = new StringBuilder();
        char previous = '\0';
        for (int i = 0; i < n; i++)
        {
            char c;
            do { c = isVoiced[i] ? Voiced[rng.Next(Voiced.Length)] : Plain[rng.Next(Plain.Length)]; } while (c == previous);
            previous = c;
            sb.Append(katakana ? (char)(c + 0x60) : c);
        }
        return sb.ToString();
    }
}
