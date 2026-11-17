using System;

namespace S2Siglip2.Probe;

/// <summary>
/// CUDA の DLL の置き場の点検だけを行う(セッションは作らない。GPU は使わない)。
/// onnxruntime_providers_cuda.dll のロードを試し、静的 import と動的ロードされる DLL の有無を表示する。
/// gpu-measure のセッション作成・Run が失敗したときに出す説明と同じもの。
/// </summary>
public static class Diagnose
{
    public static int Run(Args a)
    {
        bool keepPath = a.Flag("keep-path");
        int? pid = a.Get("gpu-proc-mem-pid") is { } sp ? int.Parse(sp) : null;
        a.RequireAllUsed();
        if (!keepPath)
        {
            var removed = CudaEnvironment.SanitizePath();
            Console.WriteLine(removed.Count == 0 ? "PATH: CUDA / cuDNN の DLL があるディレクトリは無かった" : "PATH から外した: " + string.Join("; ", removed));
        }
        if (pid is not null)
        {
            var (mib, why) = GpuProcessMemory.Read(pid.Value);
            Console.WriteLine(mib.HasValue ? $"pid {pid}: 専用 VRAM {mib.Value:F1} MiB" : $"pid {pid}: 取得不可: {why}");
            return 0;
        }
        string native = CudaEnvironment.FindOrtNativeDir();
        Console.WriteLine($"ORT のネイティブ DLL のディレクトリ: {native}");
        Console.WriteLine(CudaEnvironment.ExplainFailure(new InvalidOperationException("(診断のみ。実際の失敗ではない)"), native));
        return 0;
    }
}
