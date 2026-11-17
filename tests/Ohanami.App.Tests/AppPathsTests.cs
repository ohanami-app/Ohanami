using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class AppPathsTests
{
    private const string ModelName = "model-x";

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string n, string v) in vars) map[n] = v;
        return name => map.TryGetValue(name, out string? v) ? v : null;
    }

    [Fact]
    public void Default_data_directory_is_under_local_app_data_with_the_documented_layout()
    {
        using var temp = new TempDirectory();
        string local = temp.File("local");
        AppPaths p = AppPaths.Resolve(Env(), local, temp.File("start"), ModelName);

        Assert.Equal(Path.Combine(local, "Ohanami"), p.DataDirectory);
        Assert.Equal(Path.Combine(local, "Ohanami", "ohanami.db"), p.DatabasePath);
        Assert.Equal(Path.Combine(local, "Ohanami", "thumbs"), p.ThumbnailDirectory);
        Assert.Equal(Path.Combine(local, "Ohanami", "logs"), p.LogDirectory);
    }

    [Fact]
    public void Data_directory_variable_overrides_the_default_and_is_made_absolute()
    {
        using var temp = new TempDirectory();
        string data = temp.File("elsewhere");
        AppPaths p = AppPaths.Resolve(Env((AppPaths.DataDirectoryVariable, data)), temp.File("local"), temp.File("start"), ModelName);

        Assert.Equal(data, p.DataDirectory);
        Assert.Equal(Path.Combine(data, "ohanami.db"), p.DatabasePath);
    }

    [Fact]
    public void Empty_variables_are_treated_as_unset()
    {
        using var temp = new TempDirectory();
        AppPaths p = AppPaths.Resolve(Env((AppPaths.DataDirectoryVariable, ""), (AppPaths.ModelsDirectoryVariable, "")), temp.File("local"), temp.File("start"), ModelName);

        Assert.Equal(Path.Combine(temp.File("local"), "Ohanami"), p.DataDirectory);
        Assert.Equal(Path.Combine(temp.File("local"), "Ohanami", "models", ModelName), p.ModelDirectory);
    }

    [Fact]
    public void Model_directory_is_found_by_walking_up_from_the_start_directory()
    {
        using var temp = new TempDirectory();
        string expected = temp.File(Path.Combine("repo", "models", ModelName));
        Directory.CreateDirectory(expected);
        string start = temp.File(Path.Combine("repo", "src", "App", "bin", "Debug"));
        Directory.CreateDirectory(start);

        AppPaths p = AppPaths.Resolve(Env(), temp.File("local"), start, ModelName);

        Assert.Equal(expected, p.ModelDirectory);
    }

    [Fact]
    public void Nearest_models_directory_wins_when_several_exist_up_the_tree()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File(Path.Combine("a", "models", ModelName)));
        string near = temp.File(Path.Combine("a", "b", "models", ModelName));
        Directory.CreateDirectory(near);

        AppPaths p = AppPaths.Resolve(Env(), temp.File("local"), temp.File(Path.Combine("a", "b", "c")), ModelName);

        Assert.Equal(near, p.ModelDirectory);
    }

    [Fact]
    public void Models_variable_points_at_the_models_directory_and_the_model_name_is_appended()
    {
        using var temp = new TempDirectory();
        string models = temp.File("main-models");
        AppPaths p = AppPaths.Resolve(Env((AppPaths.ModelsDirectoryVariable, models)), temp.File("local"), temp.File("start"), ModelName);

        Assert.Equal(Path.Combine(models, ModelName), p.ModelDirectory);
    }

    [Fact]
    public void Without_any_models_directory_the_fallback_is_under_the_data_directory_so_startup_fails_with_a_reason()
    {
        using var temp = new TempDirectory();
        string data = temp.File("data");
        AppPaths p = AppPaths.Resolve(Env((AppPaths.DataDirectoryVariable, data)), temp.File("local"), temp.File("start"), "no-such-model-" + Guid.NewGuid().ToString("N"));

        Assert.StartsWith(Path.Combine(data, "models") + Path.DirectorySeparatorChar, p.ModelDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_arguments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(Env(), " ", "x", ModelName));
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(Env(), "x", "", ModelName));
        Assert.Throws<ArgumentException>(() => AppPaths.Resolve(Env(), "x", "x", ""));
        Assert.Throws<ArgumentNullException>(() => AppPaths.Resolve(null!, "x", "x", ModelName));
    }
}
