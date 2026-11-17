using System.Windows.Input;

namespace Ohanami.App;

/// <summary>
/// マウスの判定(純粋な関数。マウスのイベントを作らずにテストできる。キーの判定は <see cref="KeyShortcuts"/>)。
/// 手動で確かめて決めた 2 つ: 詳細画面でのマウスの戻るボタン、ダブルクリックの 2 回目からはドラッグを始めない。
/// </summary>
internal static class MouseShortcuts
{
    /// <summary>
    /// 詳細画面が開いているとき、マウスの戻るボタン(<see cref="MouseButton.XButton1"/>)は「戻る(Esc)」と同じ(一覧に戻る。選択は残る)。
    /// 進むボタン(<see cref="MouseButton.XButton2"/>)は何もしない。一覧が出ているときは、どのボタンも何もしない(今のまま)。
    /// </summary>
    public static bool ClosesDetail(MouseButton button, bool detailOpen) => detailOpen && button == MouseButton.XButton1;

    /// <summary>
    /// 左ボタンを押したとき、ドラッグを始められる状態にしてよいか。ダブルクリックの 2 回目(<paramref name="clickCount"/> が 2 以上)は、
    /// 詳細を開く操作なので始めない(指がわずかに動いただけで、ドラッグが始まってしまう)。1 回目は、始められる。
    /// </summary>
    public static bool ArmsDrag(int clickCount) => clickCount < 2;
}
