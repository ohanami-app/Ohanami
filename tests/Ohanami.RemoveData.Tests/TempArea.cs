using System;
using System.IO;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// テスト 1 つ分の一時フォルダ(<see cref="ScriptEntrance.TempRoot"/> の下)。道具のコピー(<see cref="AppFolder"/>)と、データの場所(根)の置き場。
/// 片付けは、この一時フォルダの下だけ。
/// </summary>
internal sealed class TempArea : IDisposable
{
    private TempArea(string dir) => Dir = dir;

    public string Dir { get; }

    /// <summary>道具のコピーを置くフォルダ(<see cref="ScriptEntrance.RunScript"/> が、中身を作る)。</summary>
    public string AppFolder => System.IO.Path.Combine(Dir, "app");

    public static TempArea Create()
    {
        Directory.CreateDirectory(ScriptEntrance.TempRoot);
        string dir = System.IO.Path.Combine(ScriptEntrance.TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TempArea(dir);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Dir, .. parts]);

    public void Dispose()
    {
        if (!ScriptEntrance.IsUnderTempRoot(Dir)) throw new InvalidOperationException("一時フォルダの外は片付けない: " + Dir);
        try { Directory.Delete(Dir, recursive: true); }
        catch (IOException) { /* 後始末の失敗でテストを落とさない(次の実行が別の名前の一時フォルダを使う) */ }
        catch (UnauthorizedAccessException) { }
    }
}
