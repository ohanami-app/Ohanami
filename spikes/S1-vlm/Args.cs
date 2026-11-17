using System;
using System.Collections.Generic;

namespace S1Vlm;

/// <summary>
/// 素朴な引数パーサ。`--名前 値` と `--名前`(値なしフラグ)だけを受け付ける。
/// 各コマンドが許可する名前を宣言し、未知の引数・値の欠落・重複は例外にする。
/// </summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _values = new();
    private readonly HashSet<string> _flags = new();

    public static Args Parse(string[] argv, int start, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var a = new Args();
        var valueSet = new HashSet<string>(valueOptions);
        var flagSet = new HashSet<string>(flagOptions);
        for (int i = start; i < argv.Length; i++)
        {
            string k = argv[i];
            if (!k.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"想定外の引数: '{k}'(オプションは --名前 の形)");
            string name = k.Substring(2);
            if (flagSet.Contains(name))
            {
                if (!a._flags.Add(name)) throw new ArgumentException($"オプションが重複: --{name}");
            }
            else if (valueSet.Contains(name))
            {
                if (i + 1 >= argv.Length) throw new ArgumentException($"--{name} に値がない");
                if (a._values.ContainsKey(name)) throw new ArgumentException($"オプションが重複: --{name}");
                a._values[name] = argv[++i];
            }
            else
            {
                throw new ArgumentException($"未知のオプション: --{name}");
            }
        }
        return a;
    }

    public string Require(string name)
    {
        if (_values.TryGetValue(name, out var v)) return v;
        throw new ArgumentException($"--{name} は必須");
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string GetOr(string name, string defaultValue) => _values.TryGetValue(name, out var v) ? v : defaultValue;

    public bool Flag(string name) => _flags.Contains(name);

    public int GetInt(string name, int defaultValue)
    {
        if (!_values.TryGetValue(name, out var v)) return defaultValue;
        if (!int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
            throw new ArgumentException($"--{name} は整数: '{v}'");
        return n;
    }
}
