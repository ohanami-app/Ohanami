using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Ohanami.Core.Indexing;

/// <summary>runs.app_version(アプリのビルドの版)と runs.runtime_json(実行環境)の作り方。</summary>
internal static class RuntimeInfo
{
    /// <summary>runtime_json の書式の版。</summary>
    public const int Format = 1;

    /// <summary>
    /// このアセンブリの <c>AssemblyInformationalVersion</c>(無ければアセンブリの版)。コミットの識別子が入るかは、ビルドの設定しだいで
    /// (SDK が SourceRevisionId を付けたときだけ)。実行時に取れなければ "unknown" ではなく例外にはせず、版だけを返す。
    /// </summary>
    public static string AppVersion()
    {
        Assembly assembly = typeof(RuntimeInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;
        return assembly.GetName().Version?.ToString() ?? throw new InvalidOperationException("アプリの版が取れない");
    }

    /// <summary>
    /// runs.runtime_json: 実行環境。ORT の版、実行プロバイダ、自己検査で実際にロードされた cu* / cudnn* / onnxruntime* などのモジュール(名前・パス・版)。
    /// 自己検査の結果が無いとき(CPU の実行や偽物)は、<c>selfCheck</c> が null。<c>settings</c> は、実際に使った実行の設定(<c>cuda.use_tf32</c> など)。GPU の名前とドライバの版は未実装(キーを持たない)。
    /// </summary>
    public static string RuntimeJson(SelfCheckReport? selfCheck, string executionProvider, IReadOnlyDictionary<string, string>? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionProvider);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteString("execution_provider", executionProvider);
            if (settings is null)
            {
                w.WriteNull("settings");
            }
            else
            {
                // どの設定で作ったか(CUDA のプロバイダーのオプションそのもの、セッションの設定)。キーの順は固定(辞書順)
                w.WriteStartObject("settings");
                foreach (KeyValuePair<string, string> kv in settings.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
            if (selfCheck is null)
            {
                w.WriteNull("self_check");
            }
            else
            {
                w.WriteStartObject("self_check");
                w.WriteBoolean("passed", selfCheck.Passed);
                w.WriteString("ort", selfCheck.OrtVersion);
                w.WriteStartArray("modules");
                foreach (LoadedModule m in selfCheck.Modules)
                {
                    w.WriteStartObject();
                    w.WriteString("name", m.Name);
                    w.WriteString("path", m.Path);
                    if (m.Version is null) w.WriteNull("version"); else w.WriteString("version", m.Version);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("violations");
                foreach (string v in selfCheck.Violations) w.WriteStringValue(v);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// VLM の実行の runs.runtime_json: llama-server のビルド(<c>--version</c> の全文・build・commit)、exe のパスと sha256、実際に渡した起動の引数、プロセスの pid とポート、
    /// 使ったモデルのファイル名と画像トークンの上限。model_id には入れない設定(ビルド・-ngl・-c など)を、実行ごとにここに残す。
    /// </summary>
    public static string VlmRuntimeJson(
        string executionProvider, string versionText, int build, string commit, string exePath, string exeSha256,
        IReadOnlyList<string> arguments, int pid, int port, string ggufName, string mmprojName, int? imageMaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionProvider);
        ArgumentNullException.ThrowIfNull(arguments);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteString("execution_provider", executionProvider);
            w.WriteStartObject("llama_server");
            w.WriteString("version_text", versionText);
            w.WriteNumber("build", build);
            w.WriteString("commit", commit);
            w.WriteString("exe", exePath);
            w.WriteString("exe_sha256", exeSha256);
            w.WriteNumber("pid", pid);
            w.WriteNumber("port", port);
            w.WriteStartArray("arguments");
            foreach (string a in arguments) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("model");
            w.WriteString("gguf", ggufName);
            w.WriteString("mmproj", mmprojName);
            if (imageMaxTokens is { } max) w.WriteNumber("image_max_tokens", max); else w.WriteNull("image_max_tokens");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
