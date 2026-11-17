using System;

namespace Ohanami.Core;

/// <summary>
/// ネイティブ DLL の環境が想定と違うとき(検索パスを設定できない、自己検査で CUDA 系の DLL がアプリのディレクトリ以外から読まれていた、
/// 必要な DLL がロードされていない、など)。メッセージに DLL の名前とパスを入れる。黙って進まない(AGENTS.md「想定外は大声で落とす」)。
/// </summary>
public sealed class NativeRuntimeException : Exception
{
    public NativeRuntimeException(string message) : base(message) { }

    public NativeRuntimeException(string message, Exception inner) : base(message, inner) { }

    public NativeRuntimeException(string message, Exception inner, NativeFailureKind kind) : base(message, inner) => Kind = kind;

    /// <summary>原因の種類(利用者に次の行動を言える失敗だけを分ける。それ以外は <see cref="NativeFailureKind.Other"/>)。</summary>
    public NativeFailureKind Kind { get; }
}

/// <summary><see cref="NativeRuntimeException"/> の原因の種類。</summary>
public enum NativeFailureKind
{
    /// <summary>分けていない失敗(メッセージのとおり)。</summary>
    Other = 0,

    /// <summary>NVIDIA の GPU(CUDA)を使えない(GPU・ドライバが無い、CUDA を初期化できない)。</summary>
    GpuUnavailable,

    /// <summary>アプリのフォルダの DLL が足りない・壊れていて、読めない(zip の展開し直しで直る)。</summary>
    FilesMissing,
}
