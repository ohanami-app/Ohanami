using System;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>セットアップの画面の状態の種類。</summary>
internal enum SetupKind
{
    /// <summary>特徴ベクトル検索のモデルが、そろっていない。使い始められない(画面を出す)。</summary>
    EmbeddingIncomplete,

    /// <summary>特徴ベクトル検索のモデルはそろい、テキスト検索のモデルが、そろっていない。「このまま始める」で使い始められる。</summary>
    VlmIncomplete,

    /// <summary>両方そろっている(画面は閉じて、メインの窓へ進む)。</summary>
    Ready,
}

/// <summary>セットアップの画面の結果。</summary>
internal enum SetupChoice
{
    /// <summary>アプリを終了する(既定。窓を閉じた場合も)。</summary>
    Exit,

    /// <summary>両方そろった。メインの窓へ進む。</summary>
    Ready,

    /// <summary>テキスト検索のモデルが無いまま、メインの窓へ進む。</summary>
    WithoutVlm,
}

/// <summary>
/// 起動の最初に見る、2 つのモデルの置き場の状態(<see cref="ModelFiles"/>。存在と大きさだけ。Core を開かずに作れる)。
/// </summary>
internal sealed record SetupState(ModelSetStatus Embedding, ModelSetStatus Vlm)
{
    public SetupKind Kind => !Embedding.IsComplete ? SetupKind.EmbeddingIncomplete : !Vlm.IsComplete ? SetupKind.VlmIncomplete : SetupKind.Ready;

    /// <summary>そろっていないファイル(無い・大きさが違う)の数。</summary>
    public int ProblemFileCount => Embedding.Files.Concat(Vlm.Files).Count(f => f.State != ModelFileState.Present);

    /// <summary>実際の置き場を調べる。</summary>
    public static SetupState Inspect(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new SetupState(ModelFiles.InspectEmbedding(paths.ModelDirectory), ModelFiles.InspectVlm(paths.VlmModelDirectory));
    }
}
