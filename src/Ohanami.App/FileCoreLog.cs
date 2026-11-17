using System;
using System.Globalization;
using System.IO;
using System.Text;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 診断ログ(<see cref="ICoreLog"/> の実装)。<c>logs\ohanami-yyyyMMdd.log</c> に、1 行 1 イベントのテキストを追記する。
/// 行の形: <c>2030-01-02T03:04:05.678Z [info] メッセージ</c>(時刻は UTC。レベルは info / warn / error)。
/// 改行を含むメッセージ・例外は 1 行にする(改行は <c>⏎</c> に置き換える)。書けなかった(ディスクが一杯など)ときはログを諦めるだけで、アプリを止めない
/// (診断のために本体を落とさない)。スレッドセーフ。
/// </summary>
internal sealed class FileCoreLog : ICoreLog
{
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public FileCoreLog(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>今日のログのファイル。</summary>
    public string CurrentFile => FileFor(_time.GetUtcNow());

    public void Info(string message) => Write("info", message);

    public void Warning(string message) => Write("warn", message);

    public void Error(string message, Exception? exception = null) =>
        Write("error", exception is null ? message : message + " | " + exception);   // ToString = 型・メッセージ・スタック(1 行にする)

    internal static string FormatLine(DateTimeOffset now, string level, string message) =>
        now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " [" + level + "] " + OneLine(message);

    internal static string OneLine(string text) =>
        text.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace("\n", " ⏎ ", StringComparison.Ordinal).Replace("\r", " ⏎ ", StringComparison.Ordinal);

    private string FileFor(DateTimeOffset now) =>
        Path.Combine(_directory, "ohanami-" + now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    private void Write(string level, string message)
    {
        DateTimeOffset now = _time.GetUtcNow();
        string line = FormatLine(now, level, message) + "\n";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(FileFor(now), line, new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // ログを書けなくても、本体は続ける
        }
    }
}
