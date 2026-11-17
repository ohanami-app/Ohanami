using System;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests.Models;

/// <summary>
/// 画面に出すモデルの名前(<see cref="OhanamiCore.ModelLabels"/>。ADR 0038)が、定義(<see cref="ModelCatalog"/>・<see cref="VlmCatalog"/>)のそばの定数から来ていて、
/// 定義と食い違っていないこと。モデルを替えて定義だけ書き換えると、ここが落ちる(画面の名前が嘘になるのを防ぐ)。
/// </summary>
public sealed class ModelLabelsTests
{
    [Fact]
    public void The_public_labels_come_from_the_display_names_next_to_the_definitions()
    {
        Assert.Equal(ModelCatalog.DisplayName, OhanamiCore.ModelLabels.FeatureVector);
        Assert.Equal(VlmCatalog.DisplayName, OhanamiCore.ModelLabels.Vlm);
        Assert.Equal(("SigLIP2", "Qwen3-VL-8B-Instruct"), (OhanamiCore.ModelLabels.FeatureVector, OhanamiCore.ModelLabels.Vlm));   // ADR 0038
    }

    [Fact]
    public void The_feature_vector_display_name_matches_the_definition()
    {
        // ディレクトリ名・ファミリー名・モデルの名前に、表示名(ファミリー名)が入っている
        Assert.Contains(ModelCatalog.DisplayName, ModelCatalog.DirectoryName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ModelCatalog.DisplayName, ModelCatalog.Name, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(ModelCatalog.DisplayName, ModelCatalog.Family, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_vlm_display_name_matches_the_definition()
    {
        Assert.StartsWith(VlmCatalog.DisplayName, VlmCatalog.Name, StringComparison.Ordinal);                 // 「Qwen3-VL-8B-Instruct Q4_K_M + mmproj F16」の先頭
        Assert.Contains(VlmCatalog.DisplayName, VlmCatalog.HfRepository, StringComparison.Ordinal);            // 取得元のリポジトリ(Qwen/Qwen3-VL-8B-Instruct-GGUF)
        Assert.Contains(VlmCatalog.DisplayName, VlmCatalog.DirectoryName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_labels_are_not_empty_and_do_not_carry_a_quantization_or_a_directory_suffix()
    {
        foreach (string label in new[] { OhanamiCore.ModelLabels.FeatureVector, OhanamiCore.ModelLabels.Vlm })
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain("__", label, StringComparison.Ordinal);       // 置き場のディレクトリ名(…__ohanami)ではない
            Assert.DoesNotContain("Q4_K_M", label, StringComparison.Ordinal);   // 量子化・mmproj は添えない(ADR 0038)
            Assert.DoesNotContain("mmproj", label, StringComparison.OrdinalIgnoreCase);
        }
    }
}
