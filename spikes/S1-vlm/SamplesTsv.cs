using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace S1Vlm;

public sealed record SampleRow(string Filename, int Width, int Height, string Stratum, string Group)
{
    /// <summary>出力ファイル名のもとになる画像名(拡張子なし)。</summary>
    public string Stem => Path.GetFileNameWithoutExtension(Filename);
}

public static class SamplesTsv
{
    private static readonly string[] RequiredHeader = { "filename", "width", "height", "stratum", "group" };

    /// <summary>samples.tsv を読む。先頭5列のヘッダが違う・列が足りない・数値でない場合は例外。</summary>
    public static List<SampleRow> Read(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
        if (lines.Length == 0) throw new InvalidDataException($"{path}: 空ファイル");
        string[] header = lines[0].Split('\t');
        if (header.Length < RequiredHeader.Length)
            throw new InvalidDataException($"{path}: ヘッダの列が足りない: '{lines[0]}'");
        for (int i = 0; i < RequiredHeader.Length; i++)
        {
            if (header[i] != RequiredHeader[i])
                throw new InvalidDataException($"{path}: ヘッダの {i + 1} 列目が '{RequiredHeader[i]}' でない: '{header[i]}'");
        }

        var rows = new List<SampleRow>();
        var stems = new HashSet<string>(StringComparer.Ordinal);
        for (int ln = 1; ln < lines.Length; ln++)
        {
            string line = lines[ln];
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f.Length < RequiredHeader.Length)
                throw new InvalidDataException($"{path}:{ln + 1}: 列が足りない({f.Length} 列)");
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
                throw new InvalidDataException($"{path}:{ln + 1}: width/height が整数でない");
            var row = new SampleRow(f[0], w, h, f[3], f[4]);
            if (!stems.Add(row.Stem))
                throw new InvalidDataException($"{path}:{ln + 1}: 画像名(拡張子なし)が重複: '{row.Stem}'");
            rows.Add(row);
        }
        if (rows.Count == 0) throw new InvalidDataException($"{path}: データ行がない");
        return rows;
    }
}
