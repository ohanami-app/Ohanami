using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Ohanami.Core.Embedding;
using Ohanami.Core.Native;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Native;

/// <summary>
/// ネイティブの失敗の見分けと、利用者向けの文(<see cref="NativeFailure"/>)。見分けるのは本当にその場面だけで、似た失敗は元の例外のまま通す。
/// ORT の例外は、ORT が実際に出す形の文を作って確かめる(GPU・ドライバが無い環境では、CUDA EP がオプションを解く段階で GPU の台数を数えて失敗する)。
/// </summary>
public sealed class NativeFailureTests
{
    private const string NoGpuOrtMessage =
        "[ErrorCode:Fail] provider_options_utils.h:189 ProviderOptionsParser::Parse Failed to parse provider option \"device_id\": CUDA failure 801: (null) ; GPU=-1 ; " +
        "expr=cudaGetDeviceCount(&num_devices);  ";

    /// <summary>
    /// 指定した文の <see cref="OnnxRuntimeException"/>。ORT のコンストラクターは非公開(ErrorCode の列挙も非公開)なので、リフレクションで作る(テストだけ。製品はリフレクションに頼らない)。
    /// 1 = Fail(ORT の ErrorCode)。
    /// </summary>
    private static OnnxRuntimeException Ort(string message)
    {
        ConstructorInfo ctor = typeof(OnnxRuntimeException).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters() is [{ ParameterType.IsEnum: true }, { ParameterType: var t }] && t == typeof(string));
        object fail = Enum.ToObject(ctor.GetParameters()[0].ParameterType, 1);
        return (OnnxRuntimeException)ctor.Invoke([fail, message]);
    }

    private static DllNotFoundException DllNotFound(string name) =>
        new($"Unable to load DLL '{name}' or one of its dependencies: 指定されたモジュールが見つかりません。 (0x8007007E)");

    // ---------------------------------------------------------------- 見分け

    [Fact]
    public void Gpu_unavailable_is_recognised_only_for_the_device_count_failure_of_ort()
    {
        Assert.True(NativeFailure.IsGpuUnavailable(Ort(NoGpuOrtMessage)));

        // 似た失敗は拾わない: モデルが読めない・CUDA の別の失敗・ORT 以外の例外が同じ文字を含んでいても
        Assert.False(NativeFailure.IsGpuUnavailable(Ort("[ErrorCode:InvalidProtobuf] Load model from model.onnx failed")));
        Assert.False(NativeFailure.IsGpuUnavailable(Ort("CUDA failure 2: out of memory ; expr=cudaMalloc(&p, n);")));
        Assert.False(NativeFailure.IsGpuUnavailable(new InvalidOperationException(NoGpuOrtMessage)));
    }

    [Fact]
    public void An_unloadable_dll_is_found_inside_the_type_initializer_failure_and_named_with_its_extension()
    {
        var typeInit = new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", DllNotFound("onnxruntime"));

        Assert.Equal("onnxruntime.dll", NativeFailure.FindUnloadableDll(typeInit));
        Assert.Equal("onnxruntime.dll", NativeFailure.FindUnloadableDll(DllNotFound("onnxruntime.dll")));
        Assert.Equal("", NativeFailure.FindUnloadableDll(new DllNotFoundException("名前の無い文")));
        Assert.Null(NativeFailure.FindUnloadableDll(new InvalidOperationException("DLL が読めない")));
        Assert.Null(NativeFailure.FindUnloadableDll(new TypeInitializationException("X", new InvalidOperationException("別の失敗"))));
    }

    // ---------------------------------------------------------------- 文

    [Fact]
    public void Translate_turns_the_gpu_failure_into_a_plain_sentence_and_keeps_the_original_inside()
    {
        OnnxRuntimeException original = Ort(NoGpuOrtMessage);

        NativeRuntimeException? t = NativeFailure.Translate(original, nativeDirectory: null);

        Assert.NotNull(t);
        Assert.Equal(NativeFailureKind.GpuUnavailable, t.Kind);
        Assert.Equal(NativeFailure.GpuUnavailableMessage, t.Message);
        Assert.Same(original, t.InnerException);   // CUDA の失敗番号などの詳細は、内側(ログ)に残る
        Assert.DoesNotContain("ErrorCode", t.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cudaGetDeviceCount", t.Message, StringComparison.Ordinal);
        Assert.StartsWith("NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください", t.Message, StringComparison.Ordinal);   // すべきことが先頭にある
    }

    [Fact]
    public void Translate_names_the_unloadable_dll_and_lists_only_files_that_are_really_missing()
    {
        using var dir = new TempDirectory();
        // フォルダには、VC++ の 4 つと、GPU の実行に要る 12 個がある。vcruntime140_1.dll だけが無い
        foreach (string name in NativeFailure.BundledVcRuntimeFiles.Concat(NativeSelfCheck.RequiredFiles).Where(n => n != "vcruntime140_1.dll"))
            File.WriteAllBytes(dir.File(name), []);
        var original = new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", DllNotFound("onnxruntime"));

        NativeRuntimeException? t = NativeFailure.Translate(original, dir.Path);

        Assert.NotNull(t);
        Assert.Equal(NativeFailureKind.FilesMissing, t.Kind);
        Assert.Same(original, t.InnerException);
        Assert.Equal(
            "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした。足りないファイル: vcruntime140_1.dll)",
            t.Message);
    }

    [Fact]
    public void Translate_leaves_out_the_missing_files_part_when_the_folder_is_unknown_or_nothing_is_missing()
    {
        var original = new TypeInitializationException("X", DllNotFound("onnxruntime"));
        const string noList = "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした)";

        Assert.Equal(noList, NativeFailure.Translate(original, nativeDirectory: null)!.Message);
        Assert.Equal(noList, NativeFailure.Translate(original, Path.Combine(Path.GetTempPath(), "ohanami-no-such-dir-" + Guid.NewGuid().ToString("N")))!.Message);   // 無いフォルダ: 無いとは言い切れない

        using var dir = new TempDirectory();
        foreach (string name in NativeFailure.BundledVcRuntimeFiles.Concat(NativeSelfCheck.RequiredFiles)) File.WriteAllBytes(dir.File(name), []);
        Assert.Equal(noList, NativeFailure.Translate(original, dir.Path)!.Message);
    }

    [Fact]
    public void Translate_lists_the_five_bundled_vc_runtime_files_when_they_are_all_gone()
    {
        using var dir = new TempDirectory();
        foreach (string name in NativeSelfCheck.RequiredFiles) File.WriteAllBytes(dir.File(name), []);   // VC++ だけ無い

        NativeRuntimeException? t = NativeFailure.Translate(new TypeInitializationException("X", DllNotFound("onnxruntime")), dir.Path);

        Assert.NotNull(t);
        Assert.EndsWith("。足りないファイル: msvcp140.dll、msvcp140_1.dll、msvcp140_atomic_wait.dll、vcruntime140.dll、vcruntime140_1.dll)", t.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_returns_null_for_failures_it_cannot_tell()
    {
        Assert.Null(NativeFailure.Translate(Ort("[ErrorCode:InvalidProtobuf] Load model from model.onnx failed"), null));
        Assert.Null(NativeFailure.Translate(new InvalidOperationException("別の失敗"), null));
    }

    [Fact]
    public void Llama_server_dll_failure_message_names_the_exit_code_in_hex_and_the_remedy()
    {
        string message = NativeFailure.LlamaServerFilesMissingMessage(NativeFailure.StatusDllNotFound, ["msvcp140.dll", "vcruntime140.dll"]);

        Assert.Equal(
            "アプリのファイルが足りないか、壊れています。zip を展開し直してください (VLM の処理プログラムが必要な DLL を読めず、起動できませんでした。終了コード 0xC0000135。足りないファイル: msvcp140.dll、vcruntime140.dll)",
            message);
        Assert.DoesNotContain("-1073741515", message, StringComparison.Ordinal);
        Assert.Equal(unchecked((int)0xC0000135), NativeFailure.StatusDllNotFound);
    }

    // ---------------------------------------------------------------- ORT のセッション作成の包み

    [Fact]
    public void Session_creation_translates_what_it_can_and_passes_everything_else_through_unchanged()
    {
        NativeRuntimeException gpu = Assert.Throws<NativeRuntimeException>(() => OrtSessions.TranslateNativeFailure(() => throw Ort(NoGpuOrtMessage)));
        Assert.Equal(NativeFailureKind.GpuUnavailable, gpu.Kind);

        NativeRuntimeException dll = Assert.Throws<NativeRuntimeException>(
            () => OrtSessions.TranslateNativeFailure(() => throw new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", DllNotFound("onnxruntime"))));
        Assert.Equal(NativeFailureKind.FilesMissing, dll.Kind);

        var other = new InvalidDataException("入力の形が違う");
        Assert.Same(other, Assert.Throws<InvalidDataException>(() => OrtSessions.TranslateNativeFailure(() => throw other)));
        OnnxRuntimeException unrelated = Ort("[ErrorCode:InvalidProtobuf] Load model failed");
        Assert.Same(unrelated, Assert.Throws<OnnxRuntimeException>(() => OrtSessions.TranslateNativeFailure(() => throw unrelated)));
    }
}
