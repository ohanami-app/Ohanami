using System.Windows.Input;

namespace Ohanami.App;

/// <summary>キーボードのショートカットが起こす操作。</summary>
internal enum ShortcutAction
{
    None,

    /// <summary>選択の先頭の画像を、画像としてクリップボードへ(Ctrl+C)。</summary>
    CopyImage,

    /// <summary>選択を、ファイルとしてクリップボードへ(Ctrl+Shift+C)。</summary>
    CopyFiles,

    /// <summary>選択の先頭の画像を、エクスプローラーで表示(E)。</summary>
    RevealInExplorer,
}

/// <summary>詳細画面が開いているときのキーが起こす操作。</summary>
internal enum DetailKeyAction
{
    None,

    /// <summary>詳細を閉じて一覧に戻る(Esc)。</summary>
    Close,

    /// <summary>前の結果へ(←)。</summary>
    Previous,

    /// <summary>次の結果へ(→)。</summary>
    Next,

    /// <summary>表示中の画像を、画像としてクリップボードへ(Ctrl+C)。</summary>
    CopyImage,

    /// <summary>表示中の画像のファイルを、ファイルとしてクリップボードへ(Ctrl+Shift+C)。</summary>
    CopyFiles,

    /// <summary>表示中の画像を、エクスプローラーで表示(E)。</summary>
    RevealInExplorer,
}

/// <summary>
/// ショートカットのキーの判定(純粋な関数。WPF の要素を作らずにテストできる)。
/// <para><b>どの要素にフォーカスがあるときに何が起きるか</b>:</para>
/// <list type="table">
/// <item><term>サムネイル一覧(<see cref="ThumbnailGridControl"/>)</term>
///   <description>Ctrl+C = 画像をコピー、Ctrl+Shift+C = ファイルとしてコピー、E = エクスプローラーで表示(いずれも選択があるとき)、
///   矢印・PageUp/Down・Home/End = スクロール、Ctrl+A = 全選択、Enter = 開く。一覧は文字を受けないので、IME を無効にしてある
///   (<see cref="System.Windows.Input.InputMethod.IsInputMethodEnabled"/> = false)。日本語 IME がオンのままだと、E のような単体キーが IME に食われて
///   <see cref="Key.ImeProcessed"/> になり、ショートカットが動かなかった。</description></item>
/// <item><term>検索ボックス</term>
///   <description>文字の入力と編集(IME は有効。日本語を入力する)。E は文字として入る(ショートカットにしない)。Ctrl+C は文字列のコピー。Enter = 検索。</description></item>
/// <item><term>それ以外(ボタン・フォルダの一覧など)</term>
///   <description>ショートカットは無い(一覧にフォーカスがないので、<see cref="Resolve"/> は None)。</description></item>
/// </list>
/// 念のため、<see cref="Key.ImeProcessed"/> で来たキーは、<see cref="KeyEventArgs.ImeProcessedKey"/>(IME が食う前の本当のキー)で判定する(<see cref="EffectiveKey"/>)。
/// </summary>
internal static class KeyShortcuts
{
    /// <summary>IME が処理したキー(<see cref="Key.ImeProcessed"/>)なら、IME が食う前のキー。そうでなければ、そのキー。</summary>
    public static Key EffectiveKey(Key key, Key imeProcessedKey) => key == Key.ImeProcessed ? imeProcessedKey : key;

    /// <summary>
    /// 詳細画面が開いているときのキー。<b>Esc</b> = 閉じる(検索ボックスにフォーカスがあっても効く)。
    /// それ以外は、検索ボックスにフォーカスがないときだけ: <b>←</b> = 前、<b>→</b> = 次(検索ボックスでは、キャレットの移動に使う)、Ctrl+C = 画像をコピー、
    /// Ctrl+Shift+C = ファイルとしてコピー、E = エクスプローラーで表示(一覧と同じ。修飾キーは厳密に見る)。
    /// IME が処理したキー(<see cref="Key.ImeProcessed"/>)は、Esc・←・→ では使わない(変換の取り消しや、候補の移動が IME のもの)。
    /// </summary>
    public static DetailKeyAction ResolveDetail(Key key, Key imeProcessedKey, ModifierKeys modifiers, bool searchBoxFocused)
    {
        if (key == Key.Escape && modifiers == ModifierKeys.None) return DetailKeyAction.Close;
        if (searchBoxFocused) return DetailKeyAction.None;
        Key effective = EffectiveKey(key, imeProcessedKey);
        return (effective, modifiers) switch
        {
            (Key.Left, ModifierKeys.None) when key == Key.Left => DetailKeyAction.Previous,
            (Key.Right, ModifierKeys.None) when key == Key.Right => DetailKeyAction.Next,
            (Key.C, ModifierKeys.Control) => DetailKeyAction.CopyImage,
            (Key.C, ModifierKeys.Control | ModifierKeys.Shift) => DetailKeyAction.CopyFiles,
            (Key.E, ModifierKeys.None) => DetailKeyAction.RevealInExplorer,
            _ => DetailKeyAction.None,
        };
    }

    /// <summary>
    /// キーの押下が起こす操作。一覧にフォーカスがあり、選択があるときだけ。修飾キーは厳密に見る
    /// (Ctrl+C / Ctrl+Shift+C / 修飾なしの E。Alt との組み合わせ、Ctrl+E などは None)。
    /// </summary>
    public static ShortcutAction Resolve(Key key, Key imeProcessedKey, ModifierKeys modifiers, bool gridHasFocus, int selectedCount)
    {
        if (!gridHasFocus || selectedCount <= 0) return ShortcutAction.None;
        Key effective = EffectiveKey(key, imeProcessedKey);
        return (effective, modifiers) switch
        {
            (Key.C, ModifierKeys.Control) => ShortcutAction.CopyImage,
            (Key.C, ModifierKeys.Control | ModifierKeys.Shift) => ShortcutAction.CopyFiles,
            (Key.E, ModifierKeys.None) => ShortcutAction.RevealInExplorer,
            _ => ShortcutAction.None,
        };
    }
}
