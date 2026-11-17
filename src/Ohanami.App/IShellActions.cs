using System.Threading.Tasks;
using System.Windows;

namespace Ohanami.App;

/// <summary>
/// 画面が「利用者の環境」(クリップボード・エクスプローラー・他のアプリへのドラッグ)に届く操作の継ぎ目。製品は <see cref="SystemShellActions"/>(<see cref="ShellActions"/> に渡す)。
/// テストは偽物に替える: 本物のクリップボードを書き換えたり、エクスプローラーを開いたり、OLE のドラッグを始めたりしないため。
/// <see cref="ResultsPanel"/> に明示的に渡す(DI のコンテナは使わない)。
/// </summary>
internal interface IShellActions
{
    /// <summary>エクスプローラーでフォルダを開いてファイルを選ぶ。失敗は <see cref="System.IO.IOException"/>・COMException。</summary>
    void RevealInExplorer(string path);

    /// <summary>エクスプローラーでフォルダを開く(何も選ばない)。失敗は <see cref="System.IO.IOException"/>・COMException。</summary>
    void OpenFolder(string path);

    /// <summary>画像を、画像としてクリップボードへ(UI スレッドで呼ぶ)。</summary>
    Task CopyImageAsync(string path);

    /// <summary>ファイルを、ファイルとしてクリップボードへ。</summary>
    void CopyFiles(string[] paths);

    /// <summary>ファイルを、OS のドラッグ&ドロップで他のアプリへ渡す(Copy だけ許す。ドロップされるか取り消されるまで戻らない)。結果の操作を返す。</summary>
    DragDropEffects DragFiles(DependencyObject source, string[] paths);
}

/// <summary>製品の実装: 本物のシェル・クリップボード・OLE のドラッグ(<see cref="ShellActions"/>)。</summary>
internal sealed class SystemShellActions : IShellActions
{
    public static SystemShellActions Instance { get; } = new();

    private SystemShellActions() { }

    public void RevealInExplorer(string path) => ShellActions.RevealInExplorer(path);

    public void OpenFolder(string path) => ShellActions.OpenFolder(path);

    public Task CopyImageAsync(string path) => ShellActions.CopyImageAsync(path);

    public void CopyFiles(string[] paths) => ShellActions.CopyFiles(paths);

    public DragDropEffects DragFiles(DependencyObject source, string[] paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
    }
}
