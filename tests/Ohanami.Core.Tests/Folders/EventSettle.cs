using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.Core.Tests.Folders;

/// <summary>
/// 実物の OS の監視を、止まっている(手で進める)時計で動かすテストの待ち。1 つの操作(ファイルを書いて更新時刻を直す、一時ファイルの名前の変更、など)は、
/// OS のイベントを複数、少しずつ遅れて届ける。**最初のイベントで時計を進めると、あとから届いたイベントが「進めたあとの時刻」で最後のイベントの時刻を更新し、
/// 静かな時間(最後のイベントから 3 秒)の起算がやり直しになって、走査が起きない**(時計がもう進まないため)。実物のアプリでは時計が進むので、3 秒のうちに届き終わる。
/// そのため、時計を進める前に、数えたイベントの数が落ち着く(一定の時間、変わらない)のを待つ。
/// </summary>
internal static class EventSettle
{
    /// <summary>まとめの待ちに入るのを待ち(OS のイベントが 1 つも届かなければ、失敗)、数えたイベントの数が 0.4 秒変わらなくなるまで待つ。</summary>
    public static async Task WaitUntilQuietAsync(FolderWatcher watcher, long folderId, int timeoutMs = 15_000)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var sw = Stopwatch.StartNew();
        while (!watcher.HasPendingEvents(folderId))
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("OS のイベントが届かなかった");
            await Task.Delay(20, ct);
        }
        int last = -1;
        var stable = Stopwatch.StartNew();
        while (stable.ElapsedMilliseconds < 400)
        {
            await Task.Delay(50, ct);
            int now = watcher.NotedEventCount;
            if (now != last)
            {
                last = now;
                stable.Restart();
            }
        }
    }
}
