using System;
using System.IO;
using Ohanami.Core.Native;
using Xunit;

namespace Ohanami.Core.Tests.Native;

/// <summary>
/// NativeRuntime の失敗系(プロセス全体の検索パスを変える成功系は、他のテストに影響するのでここでは呼ばない。
/// 成功系の確認は、GPU のテストと実機の起動で行う。ADR 0015)。
/// </summary>
public sealed class NativeRuntimeTests
{
    [Fact]
    public void Configure_rejects_a_missing_directory()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ohanami-no-such-dir-" + Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<NativeRuntimeException>(() => NativeRuntime.Configure(missing));
        Assert.Contains(missing, e.Message);
    }

    [Fact]
    public void Configure_rejects_blank_paths()
    {
        Assert.Throws<ArgumentException>(() => NativeRuntime.Configure(""));
        Assert.Throws<ArgumentException>(() => NativeRuntime.Configure("  "));
    }

    [Fact]
    public void Search_flags_are_application_dir_user_dirs_and_system32_only()
    {
        // LOAD_LIBRARY_SEARCH_APPLICATION_DIR(0x200) | USER_DIRS(0x400) | SYSTEM32(0x800)。DEFAULT_DIRS(0x1000)や PATH 相当は含めない
        Assert.Equal(0xE00u, NativeRuntime.SearchFlags);
    }

    [Fact]
    public void Captured_modules_include_this_test_process_core_assembly_dll()
    {
        var modules = NativeRuntime.CaptureLoadedModules();
        Assert.NotEmpty(modules);
        Assert.Contains(modules, m => string.Equals(m.Name, "Ohanami.Core.dll", StringComparison.OrdinalIgnoreCase));
        Assert.All(modules, m => Assert.True(Path.IsPathRooted(m.Path), m.Path));
    }

    [Fact]
    public void VerifyRequiredFiles_passes_when_all_files_exist_and_lists_missing_ones_otherwise()
    {
        using var dir = new Ohanami.Core.Tests.Data.TempDirectory();
        foreach (string name in NativeSelfCheck.RequiredFiles) File.WriteAllBytes(dir.File(name), [0]);
        NativeRuntime.VerifyRequiredFiles(dir.Path); // 例外にならない

        File.Delete(dir.File("cudnn_ops64_9.dll"));
        File.Delete(dir.File("cublas64_13.dll"));
        var e = Assert.Throws<NativeRuntimeException>(() => NativeRuntime.VerifyRequiredFiles(dir.Path));
        Assert.Contains("cudnn_ops64_9.dll", e.Message);
        Assert.Contains("cublas64_13.dll", e.Message);
        Assert.DoesNotContain("cudnn64_9.dll", e.Message);
    }

    [Fact]
    public void VerifyRequiredFiles_does_not_look_in_subdirectories_or_path()
    {
        // アプリのディレクトリに平らに無ければ、サブディレクトリにあっても足りない扱い(PATH も見ない)
        using var dir = new Ohanami.Core.Tests.Data.TempDirectory();
        Directory.CreateDirectory(dir.File("sub"));
        foreach (string name in NativeSelfCheck.RequiredFiles) File.WriteAllBytes(dir.File("sub/" + name), [0]);
        var e = Assert.Throws<NativeRuntimeException>(() => NativeRuntime.VerifyRequiredFiles(dir.Path));
        Assert.Contains("cudnn64_9.dll", e.Message);
    }

    [Fact]
    public void VerifyRequiredFiles_rejects_a_missing_directory()
    {
        Assert.Throws<NativeRuntimeException>(() => NativeRuntime.VerifyRequiredFiles(Path.Combine(Path.GetTempPath(), "ohanami-no-such-" + Guid.NewGuid().ToString("N"))));
    }
}
