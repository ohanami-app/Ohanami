using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace S4Wpf;

/// <summary>表示する1件。CacheKey には項目番号が入る(同じファイルでも件ごとに別のキャッシュ項目として扱うため)。</summary>
public sealed record ThumbItem(int Index, string Path, string CacheKey);

public static class ItemSet
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>
    /// 画像ディレクトリの実ファイル(読み取りのみ。コピーも編集もしない)を論理的に count 件へ水増しする。
    /// i 番目の項目のファイルは (i × 素数) mod ファイル数。隣り合う項目が同じファイルにならないように散らす。
    /// キャッシュのキーには項目番号を含めるので、デコード・LRU・ディスクキャッシュは count 件ぶんの負荷がかかる。
    /// </summary>
    public static List<ThumbItem> Build(string imagesDir, int count)
    {
        if (!Directory.Exists(imagesDir)) throw new DirectoryNotFoundException($"画像ディレクトリがない: {imagesDir}");
        List<FileInfo> files = new DirectoryInfo(imagesDir).EnumerateFiles()
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.FullName, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) throw new InvalidOperationException($"{imagesDir} に画像がない");

        int n = files.Count;
        int stride = PickStride(n);
        var items = new List<ThumbItem>(count);
        for (int i = 0; i < count; i++)
        {
            FileInfo f = files[(int)(((long)i * stride) % n)];
            string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{i}|{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
            items.Add(new ThumbItem(i, f.FullName, key));
        }
        return items;
    }

    /// <summary>n と互いに素な、n より大きすぎない素数(散らすための歩幅)。</summary>
    private static int PickStride(int n)
    {
        foreach (int p in new[] { 7919, 7907, 7901, 7883, 7873 })
        {
            if (Gcd(p, n) == 1) return p % n == 0 ? 1 : p;
        }
        return 1;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return a;
    }
}
