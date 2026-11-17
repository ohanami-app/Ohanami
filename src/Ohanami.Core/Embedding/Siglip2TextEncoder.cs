using System;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Ohanami.Core.Models;

namespace Ohanami.Core.Embedding;

/// <summary>
/// SigLIP2 のテキスト側(CPU)。検索のクエリを埋め込みにする。常駐する(セッション作成 1.8〜3.0 秒、常駐 約 1.76GiB。ADR 0011)。
/// 入力は 64 個の id(<see cref="Siglip2Tokenizer.Encode"/> の結果、int32)、出力は L2 正規化した 1152 次元。
/// ORT のセッションは複数のスレッドから同時に Run してよい(このクラスは状態を持たない)。
/// モデルのハッシュの検証は呼び出し側が済ませておく(<see cref="ModelVerifier.VerifyTextSideAsync"/>。起動時に 1 度。ここでは存在とサイズだけ確かめる)。
/// </summary>
internal sealed class Siglip2TextEncoder : IDisposable
{
    private readonly InferenceSession _session;
    private bool _disposed;

    private Siglip2TextEncoder(InferenceSession session) => _session = session;

    /// <summary>
    /// <paramref name="modelDirectory"/> の中のテキスト側のモデル(<paramref name="spec"/> の textual/model.onnx と外部データ)を CPU で開く。
    /// 入出力の名前・型・形が想定と違えば <see cref="InvalidDataException"/>。
    /// </summary>
    public static Siglip2TextEncoder Create(string modelDirectory, EmbeddingModelSpec spec, int threads = OrtSessions.TextIntraOpThreads)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ModelVerifier.CheckPresence(modelDirectory, [spec.File(ModelRole.TextualOnnx), spec.File(ModelRole.TextualExternalData)]);
        return new Siglip2TextEncoder(OrtSessions.OpenTextCpu(ModelVerifier.PathOf(modelDirectory, spec.File(ModelRole.TextualOnnx)), threads));
    }

    /// <summary>64 個の id から、L2 正規化した 1152 次元の埋め込みを返す。</summary>
    public float[] Embed(int[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ids.Length != Siglip2Tokenizer.ContextLength) throw new ArgumentException($"id 列の長さが {Siglip2Tokenizer.ContextLength} でない(実際 {ids.Length})", nameof(ids));
        return Embeddings.L2Normalize(OrtSessions.RunText(_session, ids));
    }

    /// <summary>クエリの文字列から埋め込みを返す(テキストの整理 → トークナイズ → Run)。</summary>
    public float[] EmbedQuery(string text, Siglip2Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokenizer);
        return Embed(tokenizer.Encode(text));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
