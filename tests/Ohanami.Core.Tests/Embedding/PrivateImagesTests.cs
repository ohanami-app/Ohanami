using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ohanami.Core.Embedding;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 手元の画像(私物。コミットしない)での、画像の前処理の補助確認。Trait Category=Private(通常の実行から除ける)。環境変数が無ければ skip する(コミットした正解データだけでは足りないとき用)。
///   OHANAMI_PRIVATE_IMAGES = 画像のディレクトリ(直下の *.png *.jpg *.jpeg *.webp を、ファイル名の順序で全部)
///   OHANAMI_PRIVATE_HASHES = 出力する TSV(ファイル名 TAB 384×384 RGB8 の sha256)
/// 出力を tools/siglip2-reference/compare_private.py に渡すと、Pillow の結果とのハッシュ一致を数える。
/// </summary>
public sealed class PrivateImagesTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Private")]
    public void ResizedHashes_AreDumped_WhenRequested()
    {
        string? dir = Environment.GetEnvironmentVariable("OHANAMI_PRIVATE_IMAGES");
        string? outPath = Environment.GetEnvironmentVariable("OHANAMI_PRIVATE_HASHES");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(outPath), "環境変数 OHANAMI_PRIVATE_IMAGES / OHANAMI_PRIVATE_HASHES が無い(手元の画像での補助確認。通常は skip)");

        var decoder = new SkiaImageDecoder();
        string[] files = Directory.GetFiles(dir!)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
        var sb = new StringBuilder();
        int failed = 0;
        foreach (string f in files)
        {
            try
            {
                RgbImage resized = Siglip2ImagePreprocessor.Resize(decoder.DecodeRgb(File.ReadAllBytes(f)));
                sb.Append(Path.GetFileName(f)).Append('\t').Append(Convert.ToHexStringLower(SHA256.HashData(resized.Pixels))).Append('\n');
            }
            catch (Exception e)
            {
                failed++;
                sb.Append(Path.GetFileName(f)).Append('\t').Append("ERROR ").Append(e.GetType().Name).Append('\n');
            }
        }
        File.WriteAllText(outPath!, sb.ToString(), new UTF8Encoding(false));
        output.WriteLine($"{files.Length} 枚(デコード失敗 {failed})のハッシュを書いた: {outPath}");
    }
}
