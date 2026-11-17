using System;
using System.IO;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// アプリが使う場所: データは <c>%LOCALAPPDATA%\Ohanami\</c>(ohanami.db、thumbs\、logs\)。
/// 環境変数で切り替えられる(テスト・手元の確認で、本物のデータを触らないため):
/// <list type="bullet">
/// <item><c>OHANAMI_DATA_DIR</c> = データのディレクトリ(既定は %LOCALAPPDATA%\Ohanami)。</item>
/// <item><c>OHANAMI_MODELS_DIR</c> = <c>models/</c> に当たるディレクトリ(中の <c>ViT-SO400M-16-SigLIP2-384__ohanami</c> と <c>Qwen3-VL-8B-Instruct-GGUF</c> を使う)。
///   git worktree のようにモデルが無い場所から動かすときに、本体の models/ を指す(Core のテストの同名の環境変数と同じ意味)。</item>
/// <item><c>OHANAMI_RUNTIME_DIR</c> = <c>runtime/</c> に当たるディレクトリ(中の <c>llama.cpp\b12110\llama-server.exe</c> を使う。ビルドが CUDA / cuDNN の DLL を探す場所と同じ変数)。</item>
/// </list>
/// モデルの既定の場所: アプリのディレクトリから親をたどって最初に見つかる <c>models/&lt;モデルのディレクトリ名&gt;</c>。見つからなければ <c>&lt;データ&gt;\models\…</c>。
/// llama-server の既定の場所: <b>アプリのフォルダの <c>llama-server.exe</c></b>(開発のビルドも配布も、ビルドが exe の隣に置く。親をたどって探さない:
/// 実行時に SHA-256 で確かめるのは exe だけで、隣の DLL は確かめないので、親のどこかにある別の一式を拾わないようにする)。
/// 環境変数 <c>OHANAMI_RUNTIME_DIR</c> を付けたときだけ、その下の <c>llama.cpp\&lt;ビルド&gt;\llama-server.exe</c>(手元の確認・テスト用)。
/// その場所にモデルが無ければ、埋め込み側は起動で Failed になり、理由(足りないファイル名)が画面に出る。VLM 側は、VLM のジョブを始めたときの検証(<c>VlmRunner</c>)で失敗し、理由が画面に出る。
/// </summary>
internal sealed record AppPaths(
    string DataDirectory, string DatabasePath, string ThumbnailDirectory, string LogDirectory, string ModelDirectory,
    string LlamaServerPath, string VlmModelDirectory)
{
    public const string DataDirectoryVariable = "OHANAMI_DATA_DIR";
    public const string ModelsDirectoryVariable = "OHANAMI_MODELS_DIR";
    public const string RuntimeDirectoryVariable = "OHANAMI_RUNTIME_DIR";

    public const string LlamaServerExeName = "llama-server.exe";

    /// <summary>
    /// 環境変数の読み取りと、親をたどる探索の起点を渡せる(テスト用)。<paramref name="vlmModelDirectoryName"/> と <paramref name="llamaBuildDirectoryName"/> の既定は、
    /// Core の <see cref="VlmOptions"/> の定数(製品の値)。
    /// </summary>
    public static AppPaths Resolve(
        Func<string, string?> getEnvironmentVariable, string localAppData, string startDirectory, string modelDirectoryName,
        string vlmModelDirectoryName = VlmOptions.ModelDirectoryName, string llamaBuildDirectoryName = VlmOptions.RuntimeBuildDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(vlmModelDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(llamaBuildDirectoryName);

        string data = getEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } d
            ? Path.GetFullPath(d)
            : Path.Combine(localAppData, "Ohanami");

        string? modelsRoot = getEnvironmentVariable(ModelsDirectoryVariable) is { Length: > 0 } models ? Path.GetFullPath(models) : null;
        string model = ModelDirectoryIn(modelsRoot, startDirectory, data, modelDirectoryName);
        string vlmModel = ModelDirectoryIn(modelsRoot, startDirectory, data, vlmModelDirectoryName);

        string llama = getEnvironmentVariable(RuntimeDirectoryVariable) is { Length: > 0 } runtime
            ? Path.Combine(Path.GetFullPath(runtime), "llama.cpp", llamaBuildDirectoryName, LlamaServerExeName)
            : Path.Combine(Path.GetFullPath(startDirectory), LlamaServerExeName);

        return new AppPaths(data, Path.Combine(data, "ohanami.db"), Path.Combine(data, "thumbs"), Path.Combine(data, "logs"), model, llama, vlmModel);
    }

    public static AppPaths ResolveDefault(string modelDirectoryName) => Resolve(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppContext.BaseDirectory,
        modelDirectoryName);

    /// <summary>VLM の場所(Core に渡す <see cref="CoreOptions.Vlm"/>)。存在の確認はしない(検証は VLM のジョブの頭で行われ、無ければ失敗の理由が画面に出る)。</summary>
    public VlmOptions ToVlmOptions() => new(LlamaServerPath, VlmModelDirectory);

    private static string ModelDirectoryIn(string? modelsRoot, string startDirectory, string data, string name) =>
        modelsRoot is not null
            ? Path.Combine(modelsRoot, name)
            : FindModelDirectoryUpwards(startDirectory, name) ?? Path.Combine(data, "models", name);

    /// <summary>startDirectory から親へたどり、<c>models/&lt;名前&gt;</c> が在る最初のディレクトリを返す。無ければ null。</summary>
    internal static string? FindModelDirectoryUpwards(string startDirectory, string modelDirectoryName) =>
        FindUpwards(startDirectory, Path.Combine("models", modelDirectoryName), isFile: false);

    /// <summary>startDirectory から親へたどり、<paramref name="relative"/>(ファイルかディレクトリ)が在る最初の場所のフルパスを返す。無ければ null。</summary>
    internal static string? FindUpwards(string startDirectory, string relative, bool isFile)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (isFile ? File.Exists(candidate) : Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
