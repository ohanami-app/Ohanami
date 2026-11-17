using System;
using System.Collections.Generic;
using System.Linq;

namespace Ohanami.Core;

/// <summary>取るモデルの組(足し合わせて指定する)。</summary>
[Flags]
public enum ModelSets
{
    None = 0,

    /// <summary>特徴ベクトル検索のモデル(SigLIP2。自前変換の 4 ファイル)。</summary>
    FeatureVector = 1,

    /// <summary>VLM(Qwen3-VL の GGUF と mmproj)。</summary>
    Vlm = 2,
}

/// <summary>ダウンロードの途中の段階。</summary>
public enum ModelDownloadPhase
{
    /// <summary>サーバーに接続して、応答の頭(ヘッダ)を待っている。</summary>
    Connecting,

    /// <summary>本文を受け取って、.part に書いている。</summary>
    Downloading,

    /// <summary>取り終わって、中身(SHA-256)を確かめている(数 GB のファイルでは数秒かかる)。</summary>
    Verifying,

    /// <summary>
    /// このファイルを、確かめて、置き場に置き終えた(通知の時点で、ファイルは置き場にあり、.part は無い。次のファイルの <see cref="Connecting"/> の前に 1 回だけ届く。
    /// 確かめで落ちたファイルには届かない)。
    /// </summary>
    Completed,
}

/// <summary>
/// ダウンロードの進み具合(<see cref="IProgress{T}"/> で受け取る。本文の読み取りの途中は 100 ミリ秒以上の間隔、段階の切り替えは即時)。
/// <paramref name="FileName"/> は、モデルのフォルダからの相対パス(<see cref="ModelFileStatus.RelativePath"/> と同じ)。<paramref name="FileIndex"/> は、今回取るファイルの中での 0 始まりの番号。
/// <paramref name="BytesDone"/> / <paramref name="BytesTotal"/> は、今回取るファイル全部を通した済みのバイト数と総バイト数(前回の続きの .part の分を含む)。
/// </summary>
public sealed record ModelDownloadProgress(ModelDownloadPhase Phase, string FileName, int FileIndex, int FileCount, long BytesDone, long BytesTotal);

/// <summary>ダウンロードが済んだ結果。取ったファイルの数と、今回ネットワークから受け取ったバイト数(続きの .part の分は含まない)。</summary>
public sealed record ModelDownloadSummary(int FilesDownloaded, long BytesReceived);

/// <summary>取る必要のあるファイル 1 つ(無い・サイズが違うもの)。<paramref name="PartialBytes"/> は、前回の続きの .part のサイズ(無ければ 0)。</summary>
public sealed record ModelDownloadFile(ModelSets Set, string RelativePath, long Size, ModelFileState State, long PartialBytes);

/// <summary>
/// 取る前の見積もり(ネットワークには出ない)。<paramref name="Files"/> は取る必要のあるファイルだけ(在って、サイズも合うものは含めない)。
/// </summary>
public sealed record ModelDownloadPlan(IReadOnlyList<ModelDownloadFile> Files, string EmbeddingDirectory, string VlmDirectory)
{
    /// <summary>置き場の空きに足す余裕(バイト)。</summary>
    public const long FreeSpaceMargin = 64L * 1024 * 1024;

    /// <summary>取るファイルの大きさの合計(バイト)。</summary>
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>これから受け取る量(続きの .part の分を引いた残り)。</summary>
    public long RemainingBytes => Files.Sum(f => f.Size - Math.Min(f.PartialBytes, f.Size));

    /// <summary>置き場に要る空き(残り + 余裕)。取るものが無ければ 0。</summary>
    public long RequiredFreeBytes => RemainingBytes > 0 ? RemainingBytes + FreeSpaceMargin : 0;

    /// <summary>取るものが無い(要るファイルが全部、在って、サイズも合う)。</summary>
    public bool IsEmpty => Files.Count == 0;
}

/// <summary>ダウンロードが失敗した理由の種類。</summary>
public enum ModelDownloadFailure
{
    /// <summary>サーバーが 200・206 以外の状態を返した(404・403・429・5xx など)。<see cref="ModelDownloadException.StatusCode"/> に状態コード。</summary>
    HttpStatus,

    /// <summary>応答が想定と違う(Range を頼んでいないのに 206、Content-Range が頼んだ範囲と合わない、など)。</summary>
    UnexpectedResponse,

    /// <summary>サイズが定義と違う(応答の全体のサイズ、受け取った量、取り終わったファイルのサイズ)。</summary>
    SizeMismatch,

    /// <summary>取り終わったファイルの SHA-256 が定義と違う。取ったものは消した。自動では繰り返さない。</summary>
    HashMismatch,

    /// <summary>本文が一定の時間まったく来なくなり、続きから再開しても進まなかった。</summary>
    Stalled,

    /// <summary>接続できない・途中で切れる、が続いて進まなかった(名前の解決・TLS・接続の失敗を含む)。</summary>
    Network,

    /// <summary>置き場の空きが足りない(取る前に止めた)。<see cref="ModelDownloadException.NeededBytes"/>・<see cref="ModelDownloadException.AvailableBytes"/>。</summary>
    InsufficientSpace,

    /// <summary>ファイルを書く・動かすのに失敗した(権限・ディスクなど)。</summary>
    Io,
}

/// <summary>
/// モデルのダウンロードの失敗。画面は、メッセージと、<see cref="Host"/>(実体は huggingface.co 以外の配信元から来るので、ファイアウォールの許可に要る名前)・
/// <see cref="StatusCode"/> を出し、手で置く方法(<see cref="ModelFiles"/> の置き場とファイル名)を案内する。
/// </summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadFailure Kind { get; }

    /// <summary>問題のあったファイル(モデルのディレクトリからの相対パス)。分からなければ null。</summary>
    public string? FileName { get; }

    /// <summary>HTTP の状態コード(<see cref="ModelDownloadFailure.HttpStatus"/> のとき)。</summary>
    public int? StatusCode { get; }

    /// <summary>最後に接続したホスト名(リダイレクトの先を含む)。署名つきの URL など、パスや問い合わせは持たない。</summary>
    public string? Host { get; }

    /// <summary>要る空き(バイト。<see cref="ModelDownloadFailure.InsufficientSpace"/> のとき)。</summary>
    public long? NeededBytes { get; }

    /// <summary>実際の空き(バイト)。</summary>
    public long? AvailableBytes { get; }

    public ModelDownloadException(
        ModelDownloadFailure kind, string message, string? fileName = null, int? statusCode = null, string? host = null,
        long? neededBytes = null, long? availableBytes = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        FileName = fileName;
        StatusCode = statusCode;
        Host = host;
        NeededBytes = neededBytes;
        AvailableBytes = availableBytes;
    }
}
