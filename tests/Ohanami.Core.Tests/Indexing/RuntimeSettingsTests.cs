using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ohanami.Core.Embedding;
using Ohanami.Core.Indexing;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>実行の設定の記録(runs.runtime_json の settings)と、画像側 GPU のプロバイダーのオプション(TF32 を切る)。</summary>
public sealed class RuntimeSettingsTests
{
    [Fact]
    public void Image_cuda_provider_options_turn_tf32_off_and_use_heuristic_conv_search()
    {
        IReadOnlyDictionary<string, string> o = OrtSessions.ImageCudaProviderOptions();
        Assert.Equal("0", o["use_tf32"]);   // 全 GPU 世代で同じ fp32 の計算にする(TF32 は Ampere 以降にしか無い)
        Assert.Equal("HEURISTIC", o["cudnn_conv_algo_search"]);
        Assert.Equal("kNextPowerOfTwo", o["arena_extend_strategy"]);
        Assert.Equal("0", o["device_id"]);
        Assert.Equal("3", OrtSessions.ImageCudaProviderOptions(3)["device_id"]);
    }

    [Fact]
    public void Real_embedder_reports_the_provider_options_it_passes_to_ort_in_a_flat_dictionary()
    {
        // 継ぎ目の実物(包み)が報告する設定は、OrtSessions が実際に ORT に渡す辞書と同じ内容
        var settings = new Dictionary<string, string>();
        foreach (var kv in OrtSessions.ImageCudaProviderOptions()) settings["cuda." + kv.Key] = kv.Value;
        Assert.Equal("0", settings["cuda.use_tf32"]);
        Assert.Equal("2", OrtSessions.ImageSessionSettings()["intra_op_threads"]);
        Assert.Equal("1", OrtSessions.ImageSessionSettings()["session.disable_cpu_ep_fallback"]);
    }

    [Fact]
    public void Runtime_json_contains_the_settings_in_sorted_key_order()
    {
        var settings = new Dictionary<string, string>
        {
            ["cuda.use_tf32"] = "0",
            ["cuda.cudnn_conv_algo_search"] = "HEURISTIC",
            ["session.intra_op_threads"] = "2",
        };

        string json = RuntimeInfo.RuntimeJson(null, "CUDAExecutionProvider", settings);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement s = doc.RootElement.GetProperty("settings");
        Assert.Equal("0", s.GetProperty("cuda.use_tf32").GetString());
        Assert.Equal("HEURISTIC", s.GetProperty("cuda.cudnn_conv_algo_search").GetString());
        Assert.Equal(["cuda.cudnn_conv_algo_search", "cuda.use_tf32", "session.intra_op_threads"], s.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("CUDAExecutionProvider", doc.RootElement.GetProperty("execution_provider").GetString());
    }

    [Fact]
    public void Runtime_json_has_null_settings_when_none_are_reported()
    {
        using JsonDocument doc = JsonDocument.Parse(RuntimeInfo.RuntimeJson(null, "unknown"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("settings").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("self_check").ValueKind);
    }
}
