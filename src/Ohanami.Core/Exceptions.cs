using System;

namespace Ohanami.Core;

/// <summary>
/// DB のファイルが Ohanami のものでない、スキーマの版を知らない(新しすぎる・0 なのに表がある)、
/// または表が想定と違うとき。黙って読み進めず、開く時点で止める(AGENTS.md「想定外は大声で落とす」)。
/// </summary>
public sealed class UnknownSchemaVersionException : Exception
{
    public UnknownSchemaVersionException(string message) : base(message) { }

    public UnknownSchemaVersionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// DB から読んだ列挙の値を、コードが知らないとき。列挙の列には CHECK 制約を付けない(ADR 0005)ので、
/// 「知らない値」を検出するのは C# 側の明示的な変換の役目。黙って既定値にしない。
/// </summary>
public sealed class UnknownEnumValueException : Exception
{
    /// <summary>どの列挙・列か(例: "images.format")。</summary>
    public string Where { get; }

    /// <summary>知らなかった値。</summary>
    public string Value { get; }

    public UnknownEnumValueException(string where, string value)
        : base($"{where} の値「{value}」を知らない(この版のコードが扱えない値)。")
    {
        Where = where;
        Value = value;
    }
}

/// <summary>
/// 手動タグの名前が決まりに合わないとき(空、制御文字・改行・「|」「、」を含む、長すぎる、など。<see cref="Tags.ManualTagName"/>)。
/// <see cref="Reason"/> は利用者に見せてよい理由(日本語)。保存はしない(何も書かれない)。
/// </summary>
public sealed class InvalidTagNameException : ArgumentException
{
    /// <summary>入力された名前(正規化の前)。</summary>
    public string Name { get; }

    /// <summary>拒んだ理由。</summary>
    public string Reason { get; }

    public InvalidTagNameException(string name, string reason)
        : base($"タグの名前として使えない: {reason}")
    {
        Name = name;
        Reason = reason;
    }
}

/// <summary>
/// モデルのファイルが無い・サイズが違う・SHA-256 が MANIFEST/カタログと違う、または models の行(spec_json)が壊れている・
/// 再計算した model_id が行の id と一致しないとき。メッセージに、ファイル名(または models の id)と、期待値・実際の値を入れる
/// App は表示して止める。
/// </summary>
public sealed class ModelMismatchException : Exception
{
    /// <summary>問題のあったファイル(モデルのディレクトリからの相対パス)または models の id。分からなければ null。</summary>
    public string? Subject { get; }

    /// <summary>期待した値(SHA-256、サイズ、model_id など)。無ければ null。</summary>
    public string? Expected { get; }

    /// <summary>実際の値。無ければ null。</summary>
    public string? Actual { get; }

    public ModelMismatchException(string message, string? subject = null, string? expected = null, string? actual = null)
        : base(message)
    {
        Subject = subject;
        Expected = expected;
        Actual = actual;
    }

    public ModelMismatchException(string message, Exception inner) : base(message, inner) { }
}
