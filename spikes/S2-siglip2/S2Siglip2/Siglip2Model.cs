using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace S2Siglip2;

/// <summary>
/// Immich の SigLIP2 ONNX(画像側 visual/model.onnx と テキスト側 textual/model.onnx)を ONNX Runtime で動かす。
/// 入出力はグラフから実測したもの: 画像 `image` float32 [1,3,384,384] → `embedding` float32 [1,1152]、
/// テキスト `text` int32 [1,64] → `embedding` float32 [1,1152]。
/// 製品は ONNX の出所に依存しない形にするため、出力は必ず C# 側で L2 正規化する(正規化済みなら何も変わらない)。
/// この段階では CPU の実行プロバイダだけを使う(GPU の測定は S1 の GPU の測定が終わってから)。
/// </summary>
public sealed class Siglip2Model : IDisposable
{
    public const int EmbeddingDim = 1152;

    private readonly InferenceSession? _visual;
    private readonly InferenceSession? _textual;

    private Siglip2Model(InferenceSession? visual, InferenceSession? textual)
    {
        _visual = visual;
        _textual = textual;
    }

    /// <summary>画像側だけを CPU で開く。</summary>
    public static Siglip2Model OpenVisualCpu(string visualOnnxPath, int intraOpThreads)
    {
        var s = Open(visualOnnxPath, intraOpThreads);
        Verify(s, "image", typeof(float), [1, 3, Siglip2ImagePreprocessor.Size, Siglip2ImagePreprocessor.Size]);
        return new Siglip2Model(s, null);
    }

    /// <summary>テキスト側だけを CPU で開く。</summary>
    public static Siglip2Model OpenTextualCpu(string textualOnnxPath, int intraOpThreads)
    {
        var s = Open(textualOnnxPath, intraOpThreads);
        Verify(s, "text", typeof(int), [1, Siglip2Tokenizer.ContextLength]);
        return new Siglip2Model(null, s);
    }

    /// <summary>画像側を CUDA の実行プロバイダで開く(cuDNN なしの構成でも作れるかは、呼び出し側が確かめる)。</summary>
    public static Siglip2Model OpenVisualCuda(string visualOnnxPath, CudaSettings cuda)
    {
        var s = Open(visualOnnxPath, cuda.IntraOpThreads, cuda);
        Verify(s, "image", typeof(float), [1, 3, Siglip2ImagePreprocessor.Size, Siglip2ImagePreprocessor.Size]);
        return new Siglip2Model(s, null);
    }

    /// <summary>テキスト側を CUDA の実行プロバイダで開く。</summary>
    public static Siglip2Model OpenTextualCuda(string textualOnnxPath, CudaSettings cuda)
    {
        var s = Open(textualOnnxPath, cuda.IntraOpThreads, cuda);
        Verify(s, "text", typeof(int), [1, Siglip2Tokenizer.ContextLength]);
        return new Siglip2Model(null, s);
    }

    /// <summary>CUDA EP の設定。cudnn_conv_algo_search は HEURISTIC(既定の EXHAUSTIVE は、初回に大きな作業領域を一時的に確保し、VRAM のピークと起動時間を膨らませる)。</summary>
    public sealed record CudaSettings
    {
        public int DeviceId { get; init; }
        public string ConvAlgoSearch { get; init; } = "HEURISTIC";
        /// <summary>"kNextPowerOfTwo"(ORT の既定)か "kSameAsRequested"。</summary>
        public string ArenaExtendStrategy { get; init; } = "kNextPowerOfTwo";
        /// <summary>0 = 指定しない。</summary>
        public long GpuMemLimitBytes { get; init; }
        public int IntraOpThreads { get; init; } = 2;
        /// <summary>true なら、CPU の実行プロバイダにノードが落ちるとセッション作成が失敗する(session.disable_cpu_ep_fallback)。</summary>
        public bool DisableCpuFallback { get; init; }
        /// <summary>true なら VERBOSE ログを出す(ノードの配置を取るため)。ログの受け口は OrtEnv.CreateInstanceWithOptions で先に設定する。</summary>
        public bool VerboseLog { get; init; }
    }

    private static InferenceSession Open(string path, int threads, CudaSettings? cuda = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"ONNX が無い: {path}");
        using var so = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (cuda is not null)
        {
            if (cuda.VerboseLog)
            {
                so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
                so.LogVerbosityLevel = 0;
            }
            if (cuda.DisableCpuFallback) so.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
            using var opts = new OrtCUDAProviderOptions();
            var d = new Dictionary<string, string>
            {
                ["device_id"] = cuda.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cudnn_conv_algo_search"] = cuda.ConvAlgoSearch,
                ["arena_extend_strategy"] = cuda.ArenaExtendStrategy,
            };
            if (cuda.GpuMemLimitBytes > 0) d["gpu_mem_limit"] = cuda.GpuMemLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            opts.UpdateOptions(d);
            so.AppendExecutionProvider_CUDA(opts);
        }
        return new InferenceSession(path, so);
    }

    /// <summary>入出力の名前・型・形が想定どおりでなければ、黙って誤読せず例外にする。</summary>
    private static void Verify(InferenceSession s, string inputName, Type inputType, int[] inputShape)
    {
        if (s.InputMetadata.Count != 1) throw new InvalidDataException($"入力が1つでない: {string.Join(",", s.InputMetadata.Keys)}");
        if (!s.InputMetadata.TryGetValue(inputName, out var im)) throw new InvalidDataException($"入力 '{inputName}' が無い: {string.Join(",", s.InputMetadata.Keys)}");
        if (im.ElementType != inputType) throw new InvalidDataException($"入力 '{inputName}' の型が {im.ElementType}(想定 {inputType})");
        if (!im.Dimensions.AsSpan().SequenceEqual(inputShape)) throw new InvalidDataException($"入力 '{inputName}' の形が [{string.Join(",", im.Dimensions)}](想定 [{string.Join(",", inputShape)}])");
        if (s.OutputMetadata.Count != 1 || !s.OutputMetadata.TryGetValue("embedding", out var om)) throw new InvalidDataException("出力が 'embedding' の1つだけでない");
        if (om.ElementType != typeof(float) || !om.Dimensions.AsSpan().SequenceEqual([1, EmbeddingDim])) throw new InvalidDataException($"出力 'embedding' の型・形が想定と違う: {om.ElementType} [{string.Join(",", om.Dimensions)}]");
    }

    /// <summary>形 [3,384,384] の float32(CHW)から、L2 正規化した 1152 次元の埋め込みを返す。</summary>
    public float[] EmbedImage(float[] chwTensor)
    {
        if (_visual is null) throw new InvalidOperationException("画像側を開いていない");
        if (chwTensor.Length != 3 * Siglip2ImagePreprocessor.Size * Siglip2ImagePreprocessor.Size) throw new ArgumentException("テンソルの長さが 3×384×384 でない", nameof(chwTensor));
        var t = new DenseTensor<float>(chwTensor, [1, 3, Siglip2ImagePreprocessor.Size, Siglip2ImagePreprocessor.Size]);
        using var results = _visual.Run([NamedOnnxValue.CreateFromTensor("image", t)]);
        return L2Normalize(results[0].AsEnumerable<float>().ToArray());
    }

    /// <summary>64 個の id(Siglip2Tokenizer.Encode の結果)から、L2 正規化した 1152 次元の埋め込みを返す。</summary>
    public float[] EmbedText(int[] ids)
    {
        if (_textual is null) throw new InvalidOperationException("テキスト側を開いていない");
        if (ids.Length != Siglip2Tokenizer.ContextLength) throw new ArgumentException("id 列の長さが 64 でない", nameof(ids));
        var t = new DenseTensor<int>(ids, [1, Siglip2Tokenizer.ContextLength]);
        using var results = _textual.Run([NamedOnnxValue.CreateFromTensor("text", t)]);
        return L2Normalize(results[0].AsEnumerable<float>().ToArray());
    }

    public static float[] L2Normalize(float[] v)
    {
        double sum = 0;
        foreach (float x in v) sum += (double)x * x;
        double norm = Math.Sqrt(sum);
        if (!(norm > 0) || double.IsNaN(norm) || double.IsInfinity(norm)) throw new InvalidDataException($"埋め込みのノルムが不正: {norm}");
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = (float)(v[i] / norm);
        return r;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("長さが違う");
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public void Dispose()
    {
        _visual?.Dispose();
        _textual?.Dispose();
    }
}
