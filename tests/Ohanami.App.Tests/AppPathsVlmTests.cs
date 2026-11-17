using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>VLM(llama-server とモデル)の場所の決め方(AppPaths.Resolve の VLM の引数)。</summary>
public sealed class AppPathsVlmTests
{
    private const string ModelName = "model-x";
    private const string VlmName = "vlm-x";
    private const string Build = "b999";

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string n, string v) in vars) map[n] = v;
        return name => map.TryGetValue(name, out string? v) ? v : null;
    }

    private static AppPaths Resolve(Func<string, string?> env, TempDirectory temp, string? start = null) =>
        AppPaths.Resolve(env, temp.File("local"), start ?? temp.File("start"), ModelName, VlmName, Build);

    [Fact]
    public void Without_any_runtime_variable_llama_server_is_in_the_app_directory_and_the_models_fall_back_to_the_data_directory()
    {
        using var temp = new TempDirectory();
        string data = temp.File("data"), start = temp.File("start");
        AppPaths p = Resolve(Env((AppPaths.DataDirectoryVariable, data)), temp, start);

        Assert.Equal(Path.Combine(start, "llama-server.exe"), p.LlamaServerPath);
        Assert.Equal(Path.Combine(data, "models", VlmName), p.VlmModelDirectory);
    }

    [Fact]
    public void Runtime_and_models_variables_point_at_the_roots_and_the_names_are_appended()
    {
        using var temp = new TempDirectory();
        string runtime = temp.File("main-runtime"), models = temp.File("main-models");
        AppPaths p = Resolve(Env((AppPaths.RuntimeDirectoryVariable, runtime), (AppPaths.ModelsDirectoryVariable, models)), temp);

        Assert.Equal(Path.Combine(runtime, "llama.cpp", Build, "llama-server.exe"), p.LlamaServerPath);
        Assert.Equal(Path.Combine(models, VlmName), p.VlmModelDirectory);
        Assert.Equal(Path.Combine(models, ModelName), p.ModelDirectory);       // 埋め込みのモデルも同じ変数
    }

    [Fact]
    public void The_vlm_model_is_found_by_walking_up_but_llama_server_is_never_taken_from_a_parent_directory()
    {
        using var temp = new TempDirectory();
        // 親のどこかに、別の一式(runtime/llama.cpp/<ビルド>/llama-server.exe)がある。アプリのフォルダの隣の DLL は検証しないので、これを拾ってはいけない
        string parentExe = temp.File(Path.Combine("repo", "runtime", "llama.cpp", Build, "llama-server.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(parentExe)!);
        File.WriteAllBytes(parentExe, [1]);
        string vlm = temp.File(Path.Combine("repo", "models", VlmName));
        Directory.CreateDirectory(vlm);
        string start = temp.File(Path.Combine("repo", "src", "App", "bin", "Debug"));
        Directory.CreateDirectory(start);

        AppPaths p = Resolve(Env(), temp, start);

        Assert.Equal(Path.Combine(start, "llama-server.exe"), p.LlamaServerPath);   // 親の runtime/ ではなく、アプリのフォルダ(exe が無くても、そこを指す=VLM のジョブの検証で、足りないファイルとして出る)
        Assert.NotEqual(parentExe, p.LlamaServerPath);
        Assert.Equal(vlm, p.VlmModelDirectory);                                    // モデルは、親をたどって探す
    }

    [Fact]
    public void The_vlm_options_are_assembled_from_the_paths_without_touching_the_disk()
    {
        using var temp = new TempDirectory();
        AppPaths p = Resolve(Env((AppPaths.RuntimeDirectoryVariable, temp.File("rt")), (AppPaths.ModelsDirectoryVariable, temp.File("md"))), temp);
        VlmOptions o = p.ToVlmOptions();
        Assert.Equal(p.LlamaServerPath, o.LlamaServerPath);
        Assert.Equal(p.VlmModelDirectory, o.ModelDirectory);
        Assert.False(File.Exists(o.LlamaServerPath));     // 存在しなくても組み立てられる(検証は VLM のジョブの頭)
    }

    [Fact]
    public void The_product_names_are_the_cores_constants_and_blank_names_are_rejected()
    {
        using var temp = new TempDirectory();
        AppPaths p = AppPaths.Resolve(Env(), temp.File("local"), temp.File("start"), ModelName);
        Assert.Equal(Path.Combine(temp.File("start"), "llama-server.exe"), p.LlamaServerPath);
        Assert.EndsWith(VlmOptions.ModelDirectoryName, p.VlmModelDirectory, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(Env(), "x", "x", ModelName, " ", Build));
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(Env(), "x", "x", ModelName, VlmName, ""));
    }
}
