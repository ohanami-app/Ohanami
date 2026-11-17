using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Native;

namespace Ohanami.Core.Vlm;

/// <summary>llama-server の起動・待ち・停止の失敗。メッセージに、直近のログ(標準出力・標準エラーの末尾)を入れる。</summary>
internal sealed class LlamaServerException : Exception
{
    public LlamaServerException(string message) : base(message) { }

    public LlamaServerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// llama-server の起動の指定。引数は <see cref="LlamaServerArguments.Build"/> が明示的に組み立てる(評価で決めた最終構成。ADR 0019: <c>-ngl 99 -c 8192 -np 1 -cram 0 -lm none --no-mmproj-offload</c>)。
/// </summary>
/// <param name="ExePath">llama-server.exe(検証は <see cref="VlmVerifier"/> が済ませてから渡す)。</param>
/// <param name="GgufPath">GGUF のフルパス。</param>
/// <param name="MmprojPath">mmproj のフルパス。</param>
/// <param name="ImageMinTokens"><c>--image-min-tokens</c>。null なら付けない(GGUF の既定)。</param>
/// <param name="ImageMaxTokens"><c>--image-max-tokens</c>。null なら付けない。</param>
/// <param name="ChatTemplateKwargs"><c>--chat-template-kwargs</c> の JSON。空・null なら付けない。</param>
/// <param name="LogDirectory"><c>llama-server-*.log</c> を置くディレクトリ(最新 3 回分を残す)。</param>
/// <param name="HealthTimeout">/health が 200 になるまでの上限(既定 300 秒)。</param>
/// <param name="MaxPortAttempts">起動に失敗した(プロセスが早く終わった)ときの、ポートを替えてのやり直しを含む試行の上限(既定 3)。</param>
/// <param name="Environment">子プロセスに足す環境変数(テストで偽サーバの動きを指定する。製品では null)。</param>
/// <param name="OutputDrainTimeout">終わったプロセスの出力を最後まで読むのを待つ上限(既定 2 秒。<see cref="LlamaServer.LogTailAfterExitAsync"/>)。テストが短くするためのもの。製品では null。</param>
internal sealed record LlamaServerLaunch(
    string ExePath,
    string GgufPath,
    string MmprojPath,
    int? ImageMinTokens,
    int? ImageMaxTokens,
    string? ChatTemplateKwargs,
    string LogDirectory,
    TimeSpan HealthTimeout,
    int MaxPortAttempts = 3,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? OutputDrainTimeout = null)
{
    public static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(300);

    public static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(2);
}

/// <summary>llama-server の引数を、明示的に組み立てる(文字列の連結の寄せ集めにしない)。</summary>
internal static class LlamaServerArguments
{
    public const int ContextSize = 8192;
    public const int GpuLayers = 99;

    public static IReadOnlyList<string> Build(LlamaServerLaunch l, int port)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "ポートの範囲外");
        foreach (string path in new[] { l.GgufPath, l.MmprojPath })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (path.Contains('"', StringComparison.Ordinal)) throw new ArgumentException("パスに二重引用符が入っている: " + path, nameof(l));
        }

        var args = new List<string>
        {
            "-m", l.GgufPath,
            "--mmproj", l.MmprojPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "-lm", "none",
            "-ngl", GpuLayers.ToString(CultureInfo.InvariantCulture),
            "-c", ContextSize.ToString(CultureInfo.InvariantCulture),
            "-np", "1",
            "-cram", "0",
            "--no-mmproj-offload",
        };
        if (l.ImageMinTokens is { } min) { args.Add("--image-min-tokens"); args.Add(min.ToString(CultureInfo.InvariantCulture)); }
        if (l.ImageMaxTokens is { } max) { args.Add("--image-max-tokens"); args.Add(max.ToString(CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrEmpty(l.ChatTemplateKwargs)) { args.Add("--chat-template-kwargs"); args.Add(l.ChatTemplateKwargs); }
        args.Add("--no-webui");
        return args;
    }
}

/// <summary>
/// llama-server の子プロセス 1 つの寿命(ADR 0026): 起動 → <c>/health</c> が 200 になるまで待つ(503 = 読み込み中) → 使う → kill。
/// 状態を持たない使い捨て(穏やかな終了は要らない)。
/// <list type="bullet">
/// <item>起動の前に、前回の孤児を掃除する(<see cref="ChildProcessGuard.SweepOrphans"/>)。起動した直後に、ジョブオブジェクトと pid ファイルに登録する(モデルを読み始める前)。</item>
/// <item>ポートは OS に決めさせた空きポート(<c>--host 127.0.0.1</c>)。プロセスが /health の前に終わったら(ポートの競合など)、別のポートで最大 <see cref="LlamaServerLaunch.MaxPortAttempts"/> 回まで。</item>
/// <item>/health: 0.5 秒おき。503 は待つ。200 で完了。それ以外の HTTP の状態、プロセスの終了は即失敗(メッセージにログの末尾 40 行)。タイムアウトも失敗。</item>
/// <item>標準出力・標準エラーは <c>llama-server-*.log</c> に書き(最新 3 回分を残す)、末尾 40 行をメモリに持つ。</item>
/// <item>停止: <c>Kill(entireProcessTree: true)</c> → 10 秒まで終了を待つ。2 回呼んでもよい。<b>終わったと確かめられたときだけ、pid ファイルの記録を消す</b>(10 秒たっても終わらないなら、警告だけで記録を残す。次の起動の掃除がそのプロセスを見つけられる。消すと、VRAM を握ったままのプロセスの横に新しいサーバを立てて失敗する)。</item>
/// </list>
/// 使う側(VlmRunner)は、実行のたびにここから作る。スリープからの復帰などで「疑わしい」と思ったら <see cref="Suspect"/> を立て、次の画像の前に <see cref="CheckHealthAsync"/> する。
/// </summary>
internal sealed class LlamaServer : IAsyncDisposable
{
    private const int TailLines = 40;
    private const int KeepLogs = 3;
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthPoll = TimeSpan.FromMilliseconds(500);

    private readonly Process _process;
    private readonly TimeSpan _drainTimeout;
    private readonly int _pid;
    private readonly ChildProcessGuard _guard;
    private readonly ICoreLog _log;
    private readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly StreamWriter _logWriter;
    private readonly object _logGate = new();
    private readonly Queue<string> _tail = new();
    private Task? _stopTask;
    private readonly object _stopGate = new();
    private volatile bool _cudaInitFailed;

    private LlamaServer(Process process, ChildProcessGuard guard, ICoreLog log, int port, IReadOnlyList<string> arguments, string exePath, string logPath, StreamWriter logWriter, TimeSpan drainTimeout)
    {
        _drainTimeout = drainTimeout;
        _process = process;
        _pid = process.Id;
        _guard = guard;
        _log = log;
        Port = port;
        Arguments = arguments;
        ExePath = exePath;
        LogPath = logPath;
        _logWriter = logWriter;
        BaseUri = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
    }

    public int Port { get; }

    public int ProcessId => _pid;

    public Uri BaseUri { get; }

    /// <summary>実際に渡した引数(<c>runs.runtime_json</c> の記録用)。</summary>
    public IReadOnlyList<string> Arguments { get; }

    public string ExePath { get; }

    public string LogPath { get; }

    /// <summary>プロセスが終わっているか。</summary>
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int? ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>「疑わしい」の印(スリープからの復帰など)。次の画像の前に <see cref="CheckHealthAsync"/> で確かめ、失敗なら kill → 起動し直す。使う側が立てて、確かめたら下ろす。</summary>
    public volatile bool Suspect;

    /// <summary>直近の標準出力・標準エラー(末尾 40 行)。</summary>
    /// <remarks>
    /// 出力は非同期のイベントで溜まるので、<see cref="HasExited"/> が true になった直後は、終了の直前の行(失敗の理由)が、まだ入っていないことがある
    /// (<c>Process.HasExited</c> は、出力の読み取りの終わりを待たない)。終わったプロセスの末尾は <see cref="LogTailAfterExitAsync"/> で読む。
    /// </remarks>
    public string LogTail()
    {
        lock (_logGate) return string.Join("\n", _tail);
    }

    /// <summary>
    /// 終わったプロセスの直近のログ(末尾 40 行)。プロセスの出力が最後まで読まれるのを、上限(<see cref="LlamaServerLaunch.OutputDrainTimeout"/>、既定 2 秒)まで待ってから返す。
    /// 上限までに読み切れなかったら(子孫のプロセスが出力のパイプを握り続けていると、終了のあとも読み終わらない)、末尾に、欠けているかもしれないという一行を足す。
    /// 異常終了の最後の行(CUDA のメモリ不足など)は、まさに見たい行なので、ログの欠けを黙って返さない。
    /// </summary>
    public async Task<string> LogTailAfterExitAsync()
    {
        bool drained = await DrainOutputAsync().ConfigureAwait(false);
        string tail = LogTail();
        return drained ? tail : tail + "\n" + OutputIncompleteNote;
    }

    internal const string OutputIncompleteNote = "(注意: プロセスの出力を最後まで読めなかった。終了の直前の行が、この末尾に入っていないかもしれない。完全なログはログファイルにある)";

    // ------------------------------------------------------------------ 起動

    /// <summary>
    /// 起動して、/health が 200 になるまで待って返す。失敗したら(起動できない・早く終わった・タイムアウト・200 以外の応答)、プロセスを残さず <see cref="LlamaServerException"/>。
    /// <paramref name="ct"/> が切れたら、プロセスを kill して <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task<LlamaServer> StartAsync(LlamaServerLaunch launch, ChildProcessGuard guard, ICoreLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.ExePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(launch.MaxPortAttempts, 1);

        guard.SweepOrphans();   // 前回の孤児(ジョブオブジェクトが効かなかった場合の保険)

        string exe = Path.GetFullPath(launch.ExePath);
        LlamaServerException? last = null;
        for (int attempt = 1; attempt <= launch.MaxPortAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            int port = FindFreePort();
            LlamaServer server = Launch(launch, exe, port, guard, log);
            try
            {
                await server.WaitHealthyAsync(launch.HealthTimeout, ct).ConfigureAwait(false);
                log.Info($"llama-server を起動した(pid {server.ProcessId}、ポート {port.ToString(CultureInfo.InvariantCulture)}、ログ {server.LogPath})");
                return server;
            }
            catch (EarlyExitException e)
            {
                // /health の前にプロセスが終わった: ポートの競合の可能性があるので、別のポートでやり直す
                await server.StopAsync().ConfigureAwait(false);
                last = new LlamaServerException(e.Message);
                log.Warning($"llama-server が起動中に終わった(試行 {attempt.ToString(CultureInfo.InvariantCulture)} / {launch.MaxPortAttempts.ToString(CultureInfo.InvariantCulture)}、ポート {port.ToString(CultureInfo.InvariantCulture)}): {e.Message}");
            }
            catch (Exception)
            {
                await server.StopAsync().ConfigureAwait(false);   // 待ちの失敗・キャンセル: プロセスを残さない
                throw;
            }
        }
        throw last ?? new LlamaServerException("llama-server を起動できなかった");
    }

    private static LlamaServer Launch(LlamaServerLaunch launch, string exe, int port, ChildProcessGuard guard, ICoreLog log)
    {
        IReadOnlyList<string> args = LlamaServerArguments.Build(launch, port);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (launch.Environment is not null)
        {
            foreach (KeyValuePair<string, string> kv in launch.Environment) psi.Environment[kv.Key] = kv.Value;
        }

        Directory.CreateDirectory(launch.LogDirectory);
        string logPath = Path.Combine(launch.LogDirectory, "llama-server-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".log");
        var writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        PruneLogs(launch.LogDirectory, logPath, log);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new LlamaServerException("llama-server を起動できなかった(Process.Start が null)");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            writer.Dispose();
            throw new LlamaServerException($"llama-server を起動できなかった: {exe}: {e.Message}", e);
        }

        var server = new LlamaServer(process, guard, log, port, args, exe, logPath, writer, launch.OutputDrainTimeout ?? LlamaServerLaunch.DefaultOutputDrainTimeout);
        process.OutputDataReceived += (_, e) => server.AppendLog("out", e.Data);
        process.ErrorDataReceived += (_, e) => server.AppendLog("err", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        guard.Register(process, exe, port);   // 起動の直後(モデルを読み始める前)に、ジョブオブジェクトと pid ファイルへ
        return server;
    }

    private static void PruneLogs(string directory, string current, ICoreLog log)
    {
        try
        {
            FileInfo[] old = new DirectoryInfo(directory).GetFiles("llama-server-*.log")
                .Where(f => !string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)   // 名前が日時の順(新しい順)
                .Skip(KeepLogs - 1)                                       // 今回のぶんを入れて KeepLogs 個
                .ToArray();
            foreach (FileInfo f in old) f.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning("古い llama-server のログを消せない: " + e.Message);
        }
    }

    private void AppendLog(string stream, string? line)
    {
        if (line is null) return;
        lock (_logGate)
        {
            try { _logWriter.WriteLine($"[{stream}] {line}"); }
            catch (ObjectDisposedException) { return; }
            catch (IOException) { /* ログに書けなくても、末尾は持つ */ }
            _tail.Enqueue(line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }
        // CUDA を初期化できないと、llama-server は CPU に落ちて動き続ける(8B のモデルを CPU で回すと、実用にならない遅さ)。気づけるよう、出力の行を見張る
        if (line.Contains(NativeFailure.LlamaCudaInitFailedMarker, StringComparison.Ordinal)) _cudaInitFailed = true;
    }

    private sealed class EarlyExitException : Exception
    {
        public EarlyExitException(string message) : base(message) { }
    }

    /// <summary>
    /// CUDA を初期化できなかった起動を、失敗にする(呼び出し側が kill する)。CPU に落ちて起動を続けても、画像側(GPU の CPU 落ちを失敗にしている)と同じく、黙って遅くならない。
    /// </summary>
    private void ThrowIfCudaInitFailed()
    {
        if (!_cudaInitFailed) return;
        _log.Warning($"llama-server が CUDA を初期化できなかった(CPU では動かさず、失敗にする)。ログ: {LogPath}");
        throw new LlamaServerException(NativeFailure.GpuUnavailableMessage);
    }

    private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string last = "(まだ応答なし)";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfCudaInitFailed();
            if (HasExited)
            {
                if (ExitCode == NativeFailure.StatusDllNotFound)
                {
                    // DLL を読めずに終わる(何も出力しない)。ポートの競合ではないので、やり直さない(EarlyExitException にしない)
                    _log.Warning($"llama-server が DLL を読めずに終了した(終了コード {NativeFailure.ExitCodeText(NativeFailure.StatusDllNotFound)})。{ExePath}");
                    string? exeDirectory = Path.GetDirectoryName(ExePath);
                    throw new LlamaServerException(NativeFailure.LlamaServerFilesMissingMessage(NativeFailure.StatusDllNotFound, NativeFailure.MissingFiles(exeDirectory)));
                }
                string tail = await LogTailAfterExitAsync().ConfigureAwait(false);   // 終了の直前の出力(失敗の理由)をログの末尾に入れてから、メッセージを作る(読み切れなければ、その旨の一行つき)
                throw new EarlyExitException($"llama-server が起動中に終了した(終了コード {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})。直近のログ:\n{tail}");
            }
            try
            {
                using HttpResponseMessage r = await _health.GetAsync(new Uri(BaseUri, "/health"), ct).ConfigureAwait(false);
                int code = (int)r.StatusCode;
                if (code == 200)
                {
                    ThrowIfCudaInitFailed();   // 読み込みの途中で見つけていなければ、ここで(CPU で動いてしまったものを、使わせない)
                    return;
                }
                if (code != 503)
                {
                    throw new LlamaServerException($"/health が想定外の HTTP {code.ToString(CultureInfo.InvariantCulture)} を返した(200 = 準備完了、503 = 読み込み中 以外)。直近のログ:\n{LogTail()}");
                }
                last = "HTTP 503(読み込み中)";
            }
            catch (HttpRequestException e) { last = "接続できない: " + e.Message; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = "/health が 5 秒でタイムアウト"; }

            if (sw.Elapsed > timeout)
            {
                throw new LlamaServerException($"/health が {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒以内に 200 にならなかった。最後の状態: {last}。直近のログ:\n{LogTail()}");
            }
            await Task.Delay(HealthPoll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 終わったプロセスの出力が最後まで読まれるのを、上限まで待つ。読み切れたら true。
    /// <c>Process.WaitForExitAsync</c> は、プロセスの終了のあと、非同期で読んでいる標準出力・標準エラーの終わり(EOF。最後の行のイベントの処理が済んだあと)まで待つ
    /// (dotnet/runtime の Process.cs の WaitForExitAsync の WaitUntilOutputEOF、AsyncStreamReader.EOF)。
    /// </summary>
    private async Task<bool> DrainOutputAsync()
    {
        using var cts = new CancellationTokenSource(_drainTimeout);
        try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }   // 子孫が出力のハンドルを持ち続けている(または、まだ終わっていない)。今ある分で続ける
        catch (InvalidOperationException) { return true; }     // もう破棄されている(止めたあと。ログは止める側が読み切っている)
    }

    // ------------------------------------------------------------------ 使っている間

    /// <summary><c>GET /health</c> を 1 回叩く。200 なら true。接続できない・タイムアウト・200 以外・プロセスの終了は false(例外にしない。使う側が kill → 起動し直しを決める)。</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        if (HasExited) return false;
        try
        {
            using HttpResponseMessage r = await _health.GetAsync(new Uri(BaseUri, "/health"), ct).ConfigureAwait(false);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }   // 5 秒のタイムアウト
    }

    // ------------------------------------------------------------------ 停止

    /// <summary>kill(子孫ごと)→ 10 秒まで終了を待つ。ログを閉じ、pid ファイルの記録を消す。2 回呼んでもよい(2 回目は 1 回目の終わりを待つ)。</summary>
    public Task StopAsync()
    {
        lock (_stopGate)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        int pid = -1;
        // 終わったことを確かめられたときだけ pid ファイルの記録を消す。終わらない(VRAM を握ったゾンビ)なら記録を残し、
        // 次の起動の掃除(SweepOrphanedLlamaServer)がそのプロセスを見つけられるようにする(消すと、ゾンビの横に新しいサーバを立てて失敗する)
        bool exited = false;
        try
        {
            pid = _pid;
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* もう終わっている */ }
                catch (System.ComponentModel.Win32Exception e) { _log.Warning($"llama-server(pid {pid.ToString(CultureInfo.InvariantCulture)})を kill できなかった: {e.Message}"); }
            }
            using var cts = new CancellationTokenSource(StopWait);
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                // 終了の待ちが切れても、プロセス自体が終わっていれば(出力の読み切りだけが遅れた場合)、記録は消してよい
                exited = _process.HasExited;
                if (!exited) _log.Warning($"llama-server(pid {pid.ToString(CultureInfo.InvariantCulture)})が kill のあと 10 秒たっても終わらない。pid ファイルの記録は残す(次の起動の掃除に任せる)");
            }
        }
        finally
        {
            if (pid > 0 && exited)
            {
                _guard.Unregister(pid);
                // 正常な停止の行(理由は呼び出し側の Indexer が「ジョブ: VLM を止める(理由: …)」で出す)。終わらないとき・kill できないときは、上の Warning
                _log.Info($"llama-server を止めた(pid {pid.ToString(CultureInfo.InvariantCulture)})");
            }
            lock (_logGate)
            {
                try { _logWriter.Dispose(); } catch (IOException) { /* ログを閉じられなくても続ける */ }
            }
            _health.Dispose();
            _process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
