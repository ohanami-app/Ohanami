using System;
using System.Windows;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 起動とコンポジションルート。DI コンテナも App.xaml も使わず、Core と画面のつなぎはここと <see cref="MainWindow"/> に明示的に書く。
/// 順序: 場所を決める(<see cref="AppPaths"/>)→ 単一インスタンスの確認(<see cref="SingleInstance"/>。2 つ目は既存のウィンドウを前に出して終わる)
/// → ログ(<see cref="FileCoreLog"/>)→ ウィンドウ(開いたときに <see cref="OhanamiCore.OpenAsync(CoreOptions, System.Threading.CancellationToken)"/>)。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        AppPaths paths = AppPaths.ResolveDefault(OhanamiCore.ModelDirectoryName);

        using SingleInstance instance = SingleInstance.Acquire(paths.DataDirectory);
        if (!instance.IsOwner) return 0;   // 既存のインスタンスに「前に出して」と伝え済み

        var log = new FileCoreLog(paths.LogDirectory);
        log.Info($"起動: {CoreInfo.Name} / データ {paths.DataDirectory} / モデル {paths.ModelDirectory} / ネイティブ {AppContext.BaseDirectory}");
        log.Info($"VLM: llama-server {paths.LlamaServerPath} / モデル {paths.VlmModelDirectory}(検証と起動は、VLM のジョブを始めたときに行う)");

        // 関門の画面(最初の窓)を閉じても終了しない。メインの窓を開く前に、OnMainWindowClose に替える
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Install(application);   // Fluent(ライト)の皮と、窓の背景の Mica を切る switch。テーマを入れる入口はここ 1 か所(AppTheme の説明)

        // 想定外の例外は握りつぶさない: ログに書き、利用者に知らせて、そのまま落とす
        application.DispatcherUnhandledException += (_, e) =>
        {
            log.Error("想定外の例外(画面のスレッド)", e.Exception);
            MessageBox.Show(
                "想定外のエラーで終了します。\n\n" + UiText.Plain(e.Exception) + "\n\n詳しくはログをご覧ください: " + log.CurrentFile,
                "Ohanami", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("想定外の例外(" + (e.IsTerminating ? "終了" : "継続") + ")", e.ExceptionObject as Exception);

        // 起動の関門: Core を開く前に、モデルのファイルを見る(特徴ベクトル検索のモデルが無いと、Core は起動で失敗し、あとからそろえても戻れない)
        SetupChoice setup = StartupGate.Run(() => SetupState.Inspect(paths), SystemShellActions.Instance, new SystemModelDownloads(log), log, w =>
        {
            Action bringToFront = () => w.Dispatcher.BeginInvoke(w.BringToFront);   // 2 つ目の起動は、いま出ている画面を前に出す
            instance.ActivationRequested += bringToFront;
            try { return StartupGate.ShowModal(w); }
            finally { instance.ActivationRequested -= bringToFront; }
        });
        if (setup == SetupChoice.Exit)
        {
            log.Info("終了(セットアップの画面から)");
            return 0;
        }
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;

        var window = new MainWindow(paths, log);
        instance.ActivationRequested += () => window.Dispatcher.BeginInvoke(window.BringToFront);

        int exitCode = application.Run(window);
        log.Info("終了");
        return exitCode;
    }
}
