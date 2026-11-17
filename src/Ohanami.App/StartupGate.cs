using System;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 起動の関門(Core を開く前)。モデルのファイルがそろっているかを、存在と大きさだけで見て(<see cref="SetupState.Inspect"/>)、
/// 特徴ベクトル検索のモデルが欠けていれば、セットアップの画面(<see cref="SetupWindow"/>)を出す。テキスト検索のモデルだけが欠けているときも出し、
/// 「このまま始める」を選べる(そのときは、テキスト検索のモデルが「未取得」の状態のまま Core を開く)。両方そろっていれば、何も出さない。
/// Core は、特徴ベクトル検索のモデルが無いと、起動で失敗し、あとからファイルをそろえても戻れない。だから、Core を開く前に、ここでそろえてもらう。
/// </summary>
internal static class StartupGate
{
    /// <summary>
    /// 関門を通る。<paramref name="inspect"/> は置き場を調べる口(製品は <see cref="SetupState.Inspect"/>)、<paramref name="show"/> は画面を出して結果を返す口
    /// (省略すると、モーダルの画面 <see cref="ShowModal"/>。テストは、画面を出さずに結果を返す偽物を渡す)。結果が <see cref="SetupChoice.Exit"/> なら、呼び出し側はアプリを終了する。
    /// </summary>
    public static SetupChoice Run(Func<SetupState> inspect, IShellActions shell, IModelDownloads downloads, ICoreLog log, Func<SetupWindow, SetupChoice>? show = null)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(log);

        SetupState state = inspect();
        if (state.Kind == SetupKind.Ready) return SetupChoice.Ready;

        log.Info($"セットアップ: モデルのファイルがそろっていない ({state.Kind}、そろっていないファイル {state.ProblemFileCount} 個)。セットアップの画面を出す");
        var window = new SetupWindow(inspect, shell, downloads, state);
        SetupChoice choice = (show ?? ShowModal)(window);
        log.Info("セットアップ: " + choice);
        return choice;
    }

    /// <summary>画面を出して、閉じるまで待ち、結果を返す(製品の既定)。</summary>
    public static SetupChoice ShowModal(SetupWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowDialog();
        return window.Choice;
    }
}
