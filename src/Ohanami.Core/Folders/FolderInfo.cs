using System;

namespace Ohanami.Core;

/// <summary>
/// 登録されたフォルダ 1 件。<see cref="ImageCount"/> は、このフォルダの <c>missing_since</c> の無いパスの数。
/// <see cref="LastError"/> は直近の走査で到達できなかった根・サブディレクトリの記録(件数と先頭の最大 20 件。成功したら null)。
/// </summary>
public sealed record FolderInfo(long Id, string Path, bool Enabled, int ImageCount, DateTimeOffset? LastScanAt, string? LastError);

/// <summary>フォルダの登録で拒否したとき(存在しない、登録済み、入れ子、不正なパス、id が無い)。メッセージに理由とパスを入れる。</summary>
public sealed class FolderRegistrationException : Exception
{
    public FolderRegistrationException(string message) : base(message) { }

    public FolderRegistrationException(string message, Exception inner) : base(message, inner) { }
}
