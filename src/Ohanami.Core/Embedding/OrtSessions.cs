using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Ohanami.Core.Models;
using Ohanami.Core.Native;

namespace Ohanami.Core.Embedding;

/// <summary>埋め込みの計算の補助(L2 正規化)。</summary>
internal static class Embeddings
{
    /// <summary>
    /// L2 正規化(ノルム 1)。モデルのグラフの中でも正規化されているが、C# 側でも必ず行う(決定。二重でも値はほぼ変わらず、出力の検査の意味もある)。
    /// ノルムが 0・NaN・無限大なら、黙って誤った値を返さず <see cref="InvalidDataException"/>。
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (float x in v) sum += (double)x * x;
        double norm = Math.Sqrt(sum);
        if (!(norm > 0) || double.IsNaN(norm) || double.IsInfinity(norm)) throw new InvalidDataException($"埋め込みのノルムが不正: {norm}");
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] / norm);
        return r;
    }
}

/// <summary>ONNX Runtime のセッションの作り方と、入出力の検査。</summary>
internal static class OrtSessions
{
    /// <summary>画像側(GPU)の ORT の内部スレッド数(入力が固定の形なので少なくてよい)。</summary>
    public const int ImageIntraOpThreads = 2;

    /// <summary>テキスト側(CPU)の既定のスレッド数(ADR 0011)。</summary>
    public const int TextIntraOpThreads = 4;

    public const int ImageSize = Siglip2ImagePreprocessor.Size;

    /// <summary>
    /// 画像側(CUDA EP)のプロバイダーのオプション。**これが実際に ORT に渡す辞書そのもの**で、runs.runtime_json にも同じものを記録する(どの設定で作ったか)。
    /// <list type="bullet">
    /// <item><c>use_tf32 = 0</c>: TF32 を使わず、全 GPU 世代で同じ fp32 の計算にする。TF32 は Ampere(RTX 30xx)以降にしか無く、既定のオンのままだと GPU の世代で数値が 2 通りになるため。
    ///   代価は Run が約 30% 遅い(64 → 82〜85ms。1 万枚で 3〜4 分の差)。CPU との 1−cos の最大は 1.8e-4(オン)→ 2e-11(オフ)。
    ///   評価の基準線は製品の経路(TF32 オフ)で取る。TF32 オンで取った埋め込みとは 1−cos の最大が 1.2e-3 違う(ADR 0011)。</item>
    /// <item><c>cudnn_conv_algo_search = HEURISTIC</c>: 既定の EXHAUSTIVE は、初回に大きな作業領域を一時的に確保し、VRAM のピークと起動時間を膨らませる(ADR 0011)。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ImageCudaProviderOptions(int deviceId = 0) => new Dictionary<string, string>
    {
        ["device_id"] = deviceId.ToString(CultureInfo.InvariantCulture),
        ["use_tf32"] = "0",
        ["cudnn_conv_algo_search"] = "HEURISTIC",
        ["arena_extend_strategy"] = "kNextPowerOfTwo",
    };

    /// <summary>画像側(GPU)のセッションの設定(プロバイダーのオプションとは別)。runs.runtime_json に記録する。</summary>
    public static IReadOnlyDictionary<string, string> ImageSessionSettings() => new Dictionary<string, string>
    {
        ["intra_op_threads"] = ImageIntraOpThreads.ToString(CultureInfo.InvariantCulture),
        ["inter_op_threads"] = "1",
        ["graph_optimization_level"] = "ORT_ENABLE_ALL",
        ["session.disable_cpu_ep_fallback"] = "1",
    };

    /// <summary>画像側を CUDA EP で開く。CPU にノードが落ちたらセッション作成を失敗させる(黙って遅くならない)。設定は <see cref="ImageCudaProviderOptions"/>。</summary>
    public static InferenceSession OpenImageCuda(string visualOnnxPath, int deviceId = 0) => TranslateNativeFailure(() => OpenImageCudaCore(visualOnnxPath, deviceId));

    private static InferenceSession OpenImageCudaCore(string visualOnnxPath, int deviceId)
    {
        using var so = NewOptions(ImageIntraOpThreads);
        // CUDA EP で扱えないノードが CPU に黙って落ちて遅くなるのを、例外にして止める。cuDNN の欠落を守るものではない(それは自己検査)。
        so.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>(ImageCudaProviderOptions(deviceId)));
        so.AppendExecutionProvider_CUDA(cuda);
        InferenceSession session = new(visualOnnxPath, so);
        try
        {
            VerifyImageSession(session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>画像側を CPU で開く(GPU が要らないテスト用。製品は GPU 固定)。</summary>
    public static InferenceSession OpenImageCpu(string visualOnnxPath, int threads) => TranslateNativeFailure(() => OpenImageCpuCore(visualOnnxPath, threads));

    private static InferenceSession OpenImageCpuCore(string visualOnnxPath, int threads)
    {
        using var so = NewOptions(threads);
        InferenceSession session = new(visualOnnxPath, so);
        try
        {
            VerifyImageSession(session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>テキスト側を CPU で開く。</summary>
    public static InferenceSession OpenTextCpu(string textualOnnxPath, int threads) => TranslateNativeFailure(() => OpenTextCpuCore(textualOnnxPath, threads));

    private static InferenceSession OpenTextCpuCore(string textualOnnxPath, int threads)
    {
        using var so = NewOptions(threads);
        InferenceSession session = new(textualOnnxPath, so);
        try
        {
            Verify(session, "text", typeof(int), [1, Siglip2Tokenizer.ContextLength]);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// セッションを作る処理の ORT の失敗を、見分けられるもの(GPU を使えない・アプリのフォルダの DLL が読めない)だけ、平らな文の <see cref="NativeRuntimeException"/> にする
    /// (<see cref="NativeFailure"/>)。見分けられない失敗は、元の例外のまま通す。元の例外は内側に残る(ログに出る)。
    /// </summary>
    internal static InferenceSession TranslateNativeFailure(Func<InferenceSession> open)
    {
        try
        {
            return open();
        }
        catch (Exception e)
        {
            if (NativeFailure.Translate(e, NativeRuntime.ConfiguredDirectory) is { } translated) throw translated;
            throw;
        }
    }

    private static SessionOptions NewOptions(int threads) => new()
    {
        IntraOpNumThreads = threads,
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
    };

    public static void VerifyImageSession(InferenceSession s) => Verify(s, "image", typeof(float), [1, 3, ImageSize, ImageSize]);

    /// <summary>入出力の名前・型・形が想定どおりでなければ、黙って誤読せず例外にする。</summary>
    private static void Verify(InferenceSession s, string inputName, Type inputType, int[] inputShape)
    {
        if (s.InputMetadata.Count != 1) throw new InvalidDataException($"入力が 1 つでない: {string.Join(",", s.InputMetadata.Keys)}");
        if (!s.InputMetadata.TryGetValue(inputName, out NodeMetadata? im)) throw new InvalidDataException($"入力 '{inputName}' が無い: {string.Join(",", s.InputMetadata.Keys)}");
        if (im.ElementType != inputType) throw new InvalidDataException($"入力 '{inputName}' の型が {im.ElementType}(想定 {inputType})");
        if (!im.Dimensions.AsSpan().SequenceEqual(inputShape)) throw new InvalidDataException($"入力 '{inputName}' の形が [{string.Join(",", im.Dimensions)}](想定 [{string.Join(",", inputShape)}])");
        if (s.OutputMetadata.Count != 1 || !s.OutputMetadata.TryGetValue("embedding", out NodeMetadata? om)) throw new InvalidDataException("出力が 'embedding' の 1 つだけでない");
        if (om.ElementType != typeof(float) || !om.Dimensions.AsSpan().SequenceEqual([1, ModelCatalog.EmbeddingDim]))
            throw new InvalidDataException($"出力 'embedding' の型・形が想定と違う: {om.ElementType} [{string.Join(",", om.Dimensions)}]");
    }

    /// <summary>形 [3,384,384] の float32(CHW)を入れて、モデルの出力(正規化前)を返す。</summary>
    public static float[] RunImage(InferenceSession s, float[] chwTensor)
    {
        var t = new DenseTensor<float>(chwTensor, [1, 3, ImageSize, ImageSize]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = s.Run([NamedOnnxValue.CreateFromTensor("image", t)]);
        return ReadEmbedding(results);
    }

    /// <summary>64 個の id を入れて、モデルの出力(正規化前)を返す。</summary>
    public static float[] RunText(InferenceSession s, int[] ids)
    {
        var t = new DenseTensor<int>(ids, [1, Siglip2Tokenizer.ContextLength]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = s.Run([NamedOnnxValue.CreateFromTensor("text", t)]);
        return ReadEmbedding(results);
    }

    private static float[] ReadEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        float[] v = results[0].AsEnumerable<float>().ToArray();
        if (v.Length != ModelCatalog.EmbeddingDim) throw new InvalidDataException($"埋め込みの長さが {v.Length}(想定 {ModelCatalog.EmbeddingDim})");
        return v;
    }
}
