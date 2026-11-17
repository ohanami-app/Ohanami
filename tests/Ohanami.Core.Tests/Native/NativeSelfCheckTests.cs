using System.Collections.Generic;
using System.Linq;
using Ohanami.Core.Native;
using Xunit;

namespace Ohanami.Core.Tests.Native;

/// <summary>自己検査の判定(純粋な関数)。GPU も実際の DLL も要らない。</summary>
public sealed class NativeSelfCheckTests
{
    private const string App = @"C:\Apps\Ohanami";
    private const string System32 = @"C:\Windows\System32";

    private static LoadedModule M(string name, string dir = App, string? version = "1.0") =>
        new(name, dir.TrimEnd('\\') + "\\" + name, version);

    /// <summary>全部アプリのディレクトリから読まれている、正常な状態。</summary>
    private static List<LoadedModule> Healthy() =>
    [
        M("onnxruntime.dll"),
        M("onnxruntime_providers_shared.dll"),
        M("onnxruntime_providers_cuda.dll"),
        M("cudart64_13.dll"),
        M("cublas64_13.dll"),
        M("cublasLt64_13.dll"),
        M("cudnn64_9.dll"),
        M("cudnn_ops64_9.dll"),
        M("nvcuda.dll", System32),
        M("kernel32.dll", System32),
        M("System.Private.CoreLib.dll", @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0"),
    ];

    private static SelfCheckReport Run(List<LoadedModule> modules, string appDir = App) =>
        NativeSelfCheck.Evaluate("1.31.1", modules, appDir, System32);

    [Fact]
    public void Healthy_environment_passes_and_reports_only_checked_modules()
    {
        SelfCheckReport r = Run(Healthy());
        Assert.True(r.Passed);
        Assert.Empty(r.Violations);
        Assert.Equal("1.31.1", r.OrtVersion);
        // 報告の対象は cu* / nv* / onnxruntime* / msvcp140* / vcruntime140* だけ(kernel32 や .NET の DLL は含まない)
        Assert.All(r.Modules, m => Assert.True(NativeSelfCheck.IsReported(m.Name)));
        Assert.Contains(r.Modules, m => m.Name == "cudnn64_9.dll");
        Assert.DoesNotContain(r.Modules, m => m.Name == "kernel32.dll");
        // 名前順
        Assert.Equal(r.Modules.Select(m => m.Name).OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase), r.Modules.Select(m => m.Name));
    }

    [Theory]
    [InlineData(@"C:\Apps\Ohanami")]            // 同梱した物がアプリのフォルダから読まれた(通常)
    [InlineData(@"C:\Windows\System32")]        // 別のモジュールが先に System32 の物をロード済み(同名のロード済みの物が使われる)。正常に動く環境を落とさない
    public void Vc_runtime_modules_are_reported_with_their_path_and_version_but_never_a_violation(string dir)
    {
        string[] names = ["msvcp140.dll", "msvcp140_1.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
        var modules = Healthy();
        foreach (string n in names) modules.Add(M(n, dir, "14.44.35211.0"));
        modules.Add(M("msvcp_win.dll", System32));           // OS の部品。名前は似ているが、報告しない
        modules.Add(M("vcruntime140_cor3.dll"));              // .NET 自身の写し(vcruntime140 で始まるので報告には載る。違反にはしない)

        SelfCheckReport r = Run(modules);

        Assert.True(r.Passed, string.Join(" / ", r.Violations));
        foreach (string n in names)
        {
            LoadedModule m = Assert.Single(r.Modules, x => x.Name == n);
            Assert.Equal(dir.TrimEnd('\\') + "\\" + n, m.Path);
            Assert.Equal("14.44.35211.0", m.Version);
        }
        Assert.DoesNotContain(r.Modules, m => m.Name == "msvcp_win.dll");
        Assert.All(names, n => Assert.False(NativeSelfCheck.IsEnforced(n)));
    }

    [Fact]
    public void Driver_module_in_system32_is_allowed()
    {
        SelfCheckReport r = Run(Healthy());
        Assert.Contains(r.Modules, m => m.Name == "nvcuda.dll");
        Assert.True(r.Passed);
    }

    [Theory]
    [InlineData("nvspcap64.dll", @"C:\Windows\System32")]                              // この機の実測: ウィンドウを持つ App に、ドライバのキャプチャ用の部品が差し込まれる
    [InlineData("nvspcap64.dll", @"C:\Program Files\NVIDIA Corporation\NvContainer")]  // NVIDIA App / GeForce Experience の版によっては、Program Files の下から来る
    [InlineData("nvldumdx.dll", @"C:\Windows\System32")]
    [InlineData("nvd3dumx.dll", @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_x")]
    [InlineData("nvcudart_hybrid64.dll", @"C:\Windows\System32\DriverStore\FileRepository\nvmdi.inf_amd64_x")] // ドライバ側。Toolkit の cudart ではない
    [InlineData("nvapi64.dll", @"D:\anywhere")]
    public void Nvidia_driver_and_overlay_components_are_reported_but_never_a_violation_wherever_they_are_loaded_from(string name, string dir)
    {
        var modules = Healthy();
        modules.Add(M(name, dir));
        SelfCheckReport r = Run(modules);
        Assert.True(r.Passed, string.Join(" / ", r.Violations));
        Assert.Contains(r.Modules, m => m.Name == name); // 報告の一覧には載る
    }

    [Theory]
    [InlineData("nvrtc64_130_0.dll", @"C:\Windows\System32")]                    // Toolkit の系統は、System32 でも違反
    [InlineData("nvJitLink_130_0.dll", @"C:\Windows\System32")]
    [InlineData("nvToolsExt64_1.dll", @"C:\Windows\System32")]
    [InlineData("nvrtc64_130_0.dll", @"D:\cuda\bin")]
    [InlineData("nvfatbin_130_0.dll", @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\x64")]
    [InlineData("nvblas64_13.dll", @"D:\cuda")]
    [InlineData("nvinfer_10.dll", @"D:\cuda")]
    [InlineData("nvonnxparser.dll", @"D:\cuda")]
    [InlineData("nvvm64_40_0.dll", @"D:\cuda")]
    [InlineData("nvptxcompiler_static.dll", @"D:\cuda")]
    [InlineData("cudnn64_9.dll", @"C:\Windows\System32")]       // 古い手順で cuDNN を System32 に入れた環境の漏れ
    [InlineData("cudart64_13.dll", @"C:\Windows\System32")]
    [InlineData("onnxruntime.dll", @"C:\Windows\System32")]
    public void Cuda_toolkit_libraries_are_a_violation_unless_loaded_from_the_app_directory_even_in_the_system_directory(string name, string dir)
    {
        var modules = Healthy().Where(m => m.Name != name).ToList();
        modules.Add(M(name, dir));
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains(name, System.StringComparison.Ordinal) && v.Contains("外から読まれている", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Enforced_nv_libraries_loaded_from_the_app_directory_pass()
    {
        var modules = Healthy();
        modules.Add(M("nvrtc64_130_0.dll"));
        Assert.True(Run(modules).Passed);
    }

    [Fact]
    public void Cudart_is_not_required_because_ort_does_not_load_it()
    {
        // 確かめた事実(ADR 0015): ORT 1.31.1 の CUDA EP は、全部そろった正常な状態でも cudart64_*.dll をロードしない
        var modules = Healthy().Where(m => m.Name != "cudart64_13.dll").ToList();
        Assert.True(Run(modules).Passed);
    }

    [Fact]
    public void Gpu_driver_components_under_driverstore_are_allowed()
    {
        // この機の実測: GPU ドライバが nvcuda.dll の下で、System32 の下の DriverStore の FileRepository から部品をロードする
        string store = System32 + @"\DriverStore\FileRepository\nvmdi.inf_amd64_01095d3b50bff033";
        var modules = Healthy();
        foreach (string n in new[] { "nvcuda64.dll", "nvapi64_impl.dll", "nvcudart_hybrid64.dll", "nvdxgdmal64.dll", "nvobjectloader64.dll" })
            modules.Add(M(n, store));
        modules.Add(M("nvapi64.dll", System32));
        SelfCheckReport r = Run(modules);
        Assert.True(r.Passed, string.Join(" / ", r.Violations));
        Assert.Contains(r.Modules, m => m.Name == "nvcuda64.dll");
    }

    [Fact]
    public void A_cuda_runtime_under_driverstore_is_still_not_exempt_when_it_is_outside_the_system_directory()
    {
        // DriverStore の名前だけのディレクトリ(システムディレクトリの下でない)は免除しない
        var modules = Healthy().Where(m => m.Name != "cublas64_13.dll").ToList();
        modules.Add(M("cublas64_13.dll", @"D:\somewhere\DriverStore"));
        Assert.False(Run(modules).Passed);
    }

    [Fact]
    public void Cuda_runtime_loaded_from_path_is_a_violation_that_names_the_dll_and_path()
    {
        // 開発機のように PATH の CUDA Toolkit から cuBLAS が読まれた(同梱の漏れが隠れた)状態
        var modules = Healthy().Where(m => m.Name != "cublas64_13.dll").ToList();
        modules.Add(M("cublas64_13.dll", @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\x64"));
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        string v = Assert.Single(r.Violations);
        Assert.Contains("cublas64_13.dll", v);
        Assert.Contains(@"CUDA\v13.3\bin\x64", v);
    }

    [Fact]
    public void Cudnn_in_system32_is_a_violation_but_the_driver_is_not()
    {
        var modules = Healthy().Where(m => m.Name != "cudnn64_9.dll").ToList();
        modules.Add(M("cudnn64_9.dll", System32));
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        Assert.Contains("cudnn64_9.dll", Assert.Single(r.Violations));
    }

    [Fact]
    public void Module_in_a_subdirectory_of_the_app_directory_is_fine()
    {
        // 例: RuntimeIdentifier を付けずに runtimes\win-x64\native\ の下に ORT が入る場合
        var modules = Healthy().Where(m => !m.Name.StartsWith("onnxruntime", System.StringComparison.OrdinalIgnoreCase)).ToList();
        modules.Add(M("onnxruntime.dll", App + @"\runtimes\win-x64\native"));
        modules.Add(M("onnxruntime_providers_cuda.dll", App + @"\runtimes\win-x64\native"));
        Assert.True(Run(modules).Passed);
    }

    [Fact]
    public void Directory_with_the_same_prefix_is_not_under_the_app_directory()
    {
        // C:\Apps\Ohanami2 は C:\Apps\Ohanami の配下ではない(前方一致の取り違えをしない)
        var modules = Healthy().Where(m => m.Name != "cudart64_13.dll").ToList();
        modules.Add(M("cudart64_13.dll", @"C:\Apps\Ohanami2"));
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        Assert.Contains("cudart64_13.dll", Assert.Single(r.Violations));
    }

    [Fact]
    public void Comparison_ignores_case_and_trailing_separators()
    {
        var modules = Healthy().Select(m => m.Name.StartsWith("cu", System.StringComparison.OrdinalIgnoreCase)
            ? new LoadedModule(m.Name, @"c:\apps\OHANAMI\" + m.Name, m.Version) : m).ToList();
        Assert.True(NativeSelfCheck.Evaluate("1.31.1", modules, @"C:\Apps\Ohanami\", System32).Passed);
    }

    [Theory]
    [InlineData("onnxruntime.dll")]
    [InlineData("onnxruntime_providers_cuda.dll")]
    [InlineData("cublas64_13.dll")]
    [InlineData("cudnn64_9.dll")]
    public void A_required_module_that_was_never_loaded_is_a_violation(string missing)
    {
        // cuDNN を同梱し忘れて、しかも PATH にも無い、などで「動いたように見えて実は」を検出する
        var modules = Healthy().Where(m => !string.Equals(m.Name, missing, System.StringComparison.OrdinalIgnoreCase)).ToList();
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains("ロードされていない"));
    }

    [Fact]
    public void Several_violations_are_all_listed()
    {
        var modules = new List<LoadedModule> { M("cudart64_13.dll", @"D:\cuda"), M("onnxruntime.dll") };
        SelfCheckReport r = Run(modules);
        Assert.False(r.Passed);
        // cudart は外から、onnxruntime_providers_cuda / cublas / cudnn は未ロード
        Assert.True(r.Violations.Count >= 4);
        Assert.Contains(r.Violations, v => v.Contains("cudart64_13.dll") && v.Contains(@"D:\cuda"));
    }

    [Theory]
    [InlineData("cudnn64_9.dll", "cudnn64_*.dll", true)]
    [InlineData("CUDNN64_9.DLL", "cudnn64_*.dll", true)]
    [InlineData("cudnn_ops64_9.dll", "cudnn64_*.dll", false)]
    [InlineData("cudart64_13.dll", "cudart64_*.dll", true)]
    [InlineData("onnxruntime.dll", "onnxruntime.dll", true)]
    [InlineData("onnxruntime_providers_cuda.dll", "onnxruntime.dll", false)]
    [InlineData("a.dll", "a*.dll", true)]
    [InlineData("a.dl", "a*.dll", false)]
    public void Pattern_matching(string name, string pattern, bool expected) =>
        Assert.Equal(expected, NativeSelfCheck.MatchesPattern(name, pattern));

    [Fact]
    public void Pattern_with_two_stars_is_rejected()
    {
        Assert.Throws<System.ArgumentException>(() => NativeSelfCheck.MatchesPattern("a", "a*b*c"));
    }

    [Fact]
    public void Null_or_blank_arguments_are_rejected()
    {
        Assert.Throws<System.ArgumentException>(() => NativeSelfCheck.Evaluate("", Healthy(), App, System32));
        Assert.Throws<System.ArgumentException>(() => NativeSelfCheck.Evaluate("1", Healthy(), "", System32));
        Assert.Throws<System.ArgumentNullException>(() => NativeSelfCheck.Evaluate("1", null!, App, System32));
    }

    [Fact]
    public void FindMissingFiles_is_empty_when_every_required_file_is_present_regardless_of_case()
    {
        var names = NativeSelfCheck.RequiredFiles.Select(n => n.ToUpperInvariant()).Concat(["Ohanami.Core.dll", "other.dll"]);
        Assert.Empty(NativeSelfCheck.FindMissingFiles(names));
    }

    [Fact]
    public void FindMissingFiles_names_each_missing_file()
    {
        var names = NativeSelfCheck.RequiredFiles.Where(n => n != "cudnn64_9.dll" && n != "cublasLt64_13.dll");
        var missing = NativeSelfCheck.FindMissingFiles(names);
        Assert.Equal(["cublasLt64_13.dll", "cudnn64_9.dll"], missing.OrderBy(n => n, System.StringComparer.Ordinal));
    }

    [Fact]
    public void Required_files_are_the_ort_cuda_cublas_and_the_seven_cudnn_dlls_without_duplicates()
    {
        string[] files = NativeSelfCheck.RequiredFiles;
        Assert.Equal(files.Length, files.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(7, files.Count(f => f.StartsWith("cudnn", System.StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("cudnn_adv64_9.dll", files);
        Assert.DoesNotContain("cudnn_cnn64_9.dll", files);
        Assert.DoesNotContain("cudnn_ext64_9.dll", files);
        Assert.Contains("onnxruntime_providers_cuda.dll", files);
        Assert.Contains("cublas64_13.dll", files);
        Assert.Contains("cublasLt64_13.dll", files);
    }
}
