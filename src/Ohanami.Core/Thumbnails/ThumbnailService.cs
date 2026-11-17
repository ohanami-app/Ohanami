using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Embedding;
using Ohanami.Core.Thumbnails;
using SkiaSharp;

namespace Ohanami.Core;

/// <summary>
/// サムネイルの画素を作る(Core。WPF の型は含まない。表示用の <c>BitmapSource</c> にするのは App)。ADR 0041。
/// <list type="bullet">
/// <item>ディスクキャッシュにあれば、その JPEG を読んで返す。無ければ原本(DB の <c>missing_since</c> の無いパスのうち、最初に使えるもの)をデコードし、
///   箱に収まるよう縮小(アスペクト比を維持、拡大しない)して、キャッシュに書いて返す。</item>
/// <item>キャッシュの鍵は<b>内容ハッシュ × 箱の大きさ × 書式の版</b>。場所は <c>&lt;根&gt;/v1/&lt;ハッシュの先頭 2 桁&gt;/&lt;ハッシュ&gt;_&lt;幅&gt;x&lt;高さ&gt;.jpg</c>
///   (JPEG 品質 85)。検索結果の並びが変わっても、パスが変わっても(移動・リネーム)、同じ画像のサムネイルは再利用される。
///   キャッシュの書式(品質・縮小の方法など)を変えたら、版(<see cref="CacheFormatVersion"/>)を上げる(古い版のディレクトリは使わない)。</item>
/// <item>キャッシュが壊れていたら(空、JPEG でない、途中で切れた、箱より大きい)、捨てて作り直す。キャッシュの書き込みの失敗(ディスクが一杯など)は、
///   サムネイルの表示を止めず、<see cref="CacheWriteFailures"/> に数える。</item>
/// <item>原本が使えない・デコードできないのは、例外でなく <see cref="ThumbnailResult"/> の失敗で返す。原本は、走査のときの (更新日時, サイズ) と同じものだけを使う
///   (走査のあとで書き換えられたファイルの画素を、古いハッシュのキャッシュに入れない)。</item>
/// <item>同じ鍵の要求が同時に来ても、作るのは 1 回(進行中の仕事を共有する)。呼び出し側のキャンセルは待つのをやめるだけで、作りかけの仕事は終わらせてキャッシュに残す。</item>
/// </list>
/// 公開 API はすべて任意のスレッドから同時に呼んでよい。
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>キャッシュの書式の版(ディレクトリ名 v1)。</summary>
    public const int CacheFormatVersion = 1;

    private readonly OhanamiDatabase _db;
    private readonly string _root;
    private readonly ConcurrentDictionary<Key, Lazy<Task<ThumbnailResult>>> _inFlight = new();
    private long _cacheWriteFailures;
    private volatile bool _stopped;

    private readonly record struct Key(string Hash, int Width, int Height);

    /// <param name="db">DB(原本のパスを引く)。</param>
    /// <param name="thumbnailDirectory">キャッシュの根(<c>CoreOptions.ThumbnailDirectory</c> = <c>%LOCALAPPDATA%\Ohanami\thumbs</c>)。無ければ最初の書き込みで作る。</param>
    internal ThumbnailService(OhanamiDatabase db, string thumbnailDirectory)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbnailDirectory);
        _db = db;
        _root = Path.GetFullPath(thumbnailDirectory);
    }

    /// <summary>キャッシュに書けなかった回数(診断用。書けなくても結果は返している)。</summary>
    public long CacheWriteFailures => Interlocked.Read(ref _cacheWriteFailures);

    /// <summary>
    /// ハッシュの画像のサムネイルを、箱(幅 × 高さ、ピクセル)に収まる大きさで返す。<paramref name="hash"/> は 64 桁の小文字 16 進、箱は 1〜8192。
    /// 原本が使えない・デコードできないときは、失敗の <see cref="ThumbnailResult"/>(例外ではない)。キャンセルは <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<ThumbnailResult> GetAsync(string hash, int boxWidthPx, int boxHeightPx, CancellationToken ct = default)
    {
        CheckHash(hash);
        CheckBox(boxWidthPx, boxHeightPx);
        ct.ThrowIfCancellationRequested();
        if (_stopped) return StoppedResult;
        var key = new Key(hash, boxWidthPx, boxHeightPx);
        Lazy<Task<ThumbnailResult>> work = _inFlight.GetOrAdd(key, k => new Lazy<Task<ThumbnailResult>>(() => RunAsync(k)));
        return await work.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ThumbnailResult> RunAsync(Key key)
    {
        try
        {
            return await Task.Run(() => Produce(key)).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// デコード済みの画素(RGB8)からサムネイルを作ってキャッシュに書き、画素を返す(将来、埋め込みの段階からデコードを 1 回で済ませるための入口。今は呼ばない)。
    /// 箱の大きさ・ハッシュの検査は <see cref="GetAsync"/> と同じ。
    /// </summary>
    internal ThumbnailPixels CreateFrom(string hash, RgbImage decoded, int boxWidthPx, int boxHeightPx)
    {
        CheckHash(hash);
        CheckBox(boxWidthPx, boxHeightPx);
        ThumbnailPixels pixels = ThumbnailRenderer.FromRgb(decoded, boxWidthPx, boxHeightPx);
        WriteCache(hash, boxWidthPx, boxHeightPx, pixels);
        return pixels;
    }

    // ---------------------------------------------------------------- キャッシュ

    /// <summary>キャッシュのファイルのパス。</summary>
    internal string CachePath(string hash, int boxWidthPx, int boxHeightPx) =>
        Path.Combine(_root, "v" + CacheFormatVersion.ToString(CultureInfo.InvariantCulture), hash[..2],
            string.Create(CultureInfo.InvariantCulture, $"{hash}_{boxWidthPx}x{boxHeightPx}.jpg"));

    private ThumbnailPixels? TryReadCache(Key key)
    {
        string path = CachePath(key.Hash, key.Width, key.Height);
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // 読めないキャッシュは無いものとして作り直す
        }

        try
        {
            ThumbnailPixels pixels = ThumbnailRenderer.DecodeJpeg(bytes);
            if (pixels.Width > key.Width || pixels.Height > key.Height) throw new InvalidDataException($"キャッシュの画像 {pixels.Width}x{pixels.Height} が箱 {key.Width}x{key.Height} より大きい");
            return pixels;
        }
        catch (InvalidDataException)
        {
            TryDelete(path); // 壊れたキャッシュは捨てる
            return null;
        }
    }

    private void WriteCache(string hash, int boxWidthPx, int boxHeightPx, ThumbnailPixels pixels)
    {
        string path = CachePath(hash, boxWidthPx, boxHeightPx);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] jpeg = ThumbnailRenderer.EncodeJpeg(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, jpeg);
            File.Move(tmp, path, overwrite: true); // 読む側が途中のファイルを見ないよう、完成してから置き換える
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _cacheWriteFailures);
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 消せなくても、次に作り直すときに上書きする */ }
    }

    // ---------------------------------------------------------------- 作る

    private static readonly ThumbnailResult StoppedResult = ThumbnailResult.Failure(ThumbnailStatus.SourceUnavailable, "Core を閉じているので、作れない");

    /// <summary>
    /// 破棄の前に(<c>OhanamiCore.DisposeAsync</c> から): 新しい要求には <see cref="ThumbnailStatus.SourceUnavailable"/> を返すようにし、進行中の仕事が終わるのを待つ
    /// (DB を閉じたあとに、進行中の仕事が DB を読んで例外にならないように)。
    /// </summary>
    internal async Task StopAsync()
    {
        _stopped = true;
        Task[] running = _inFlight.Values.Select(l => l.Value).ToArray();
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { /* 仕事の失敗は、要求した側に伝わっている */ }
    }

    private ThumbnailResult Produce(Key key)
    {
        if (_stopped) return StoppedResult;
        ThumbnailPixels? cached = TryReadCache(key);
        if (cached is not null) return ThumbnailResult.Ok(cached, fromCache: true);

        var reasons = new List<string>();
        foreach (SourceRow row in LoadSources(key.Hash))
        {
            SKData? data;
            try
            {
                var info = new FileInfo(row.Path);
                if (!info.Exists) { reasons.Add($"{row.Path}: ファイルが無い"); continue; }
                if (info.Length != row.Size || info.LastWriteTimeUtc.Ticks != row.MtimeTicks)
                {
                    reasons.Add($"{row.Path}: 走査のあとで書き換えられている(次の走査で更新される)");
                    continue;
                }
                if (info.Length == 0) return ThumbnailResult.Failure(ThumbnailStatus.SourceUndecodable, $"{row.Path}: 空のファイル");
                data = SKData.Create(row.Path); // ネイティブのメモリに読む(マネージドの配列にしない。ラージオブジェクトヒープを使わず、gen2 の GC を増やさない)
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                reasons.Add($"{row.Path}: 開けない({e.Message})");
                continue;
            }
            if (data is null) { reasons.Add($"{row.Path}: 開けない"); continue; }

            ThumbnailPixels pixels;
            using (data)
            {
                try
                {
                    pixels = ThumbnailRenderer.Render(data, key.Width, key.Height);
                }
                catch (ThumbnailDecodeException e)
                {
                    // 走査のときと同じ内容のファイルなので、他のパスも同じ結果になる。次のパスは試さない
                    return ThumbnailResult.Failure(ThumbnailStatus.SourceUndecodable, $"{row.Path}: {e.Message}");
                }
            }
            WriteCache(key.Hash, key.Width, key.Height, pixels);
            return ThumbnailResult.Ok(pixels, fromCache: false);
        }

        return ThumbnailResult.Failure(ThumbnailStatus.SourceUnavailable,
            reasons.Count == 0 ? "この画像には、存在するパスの記録が無い" : "使える原本が無い: " + string.Join(" / ", reasons));
    }

    private sealed record SourceRow(string Path, long MtimeTicks, long Size);

    private List<SourceRow> LoadSources(string hash)
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, mtime_ticks, size FROM image_paths WHERE hash = $h AND missing_since IS NULL ORDER BY path";
        cmd.Parameters.AddWithValue("$h", hash);
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<SourceRow>();
        while (r.Read()) list.Add(new SourceRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    // ---------------------------------------------------------------- 引数の検査

    private static void CheckHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 64) throw new ArgumentException($"ハッシュは 64 桁の小文字 16 進(実際 {hash.Length} 文字)", nameof(hash));
        foreach (char c in hash)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) throw new ArgumentException("ハッシュは 64 桁の小文字 16 進", nameof(hash));
        }
    }

    private static void CheckBox(int width, int height)
    {
        if (width < 1 || width > ThumbnailRenderer.MaxBox) throw new ArgumentOutOfRangeException(nameof(width), width, $"箱の幅は 1〜{ThumbnailRenderer.MaxBox}");
        if (height < 1 || height > ThumbnailRenderer.MaxBox) throw new ArgumentOutOfRangeException(nameof(height), height, $"箱の高さは 1〜{ThumbnailRenderer.MaxBox}");
    }
}
