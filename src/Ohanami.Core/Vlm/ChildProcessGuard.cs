using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Ohanami.Core.Vlm;

/// <summary>pid ファイルの中身(llama-server を起動した直後に書く)。同じ pid の別のプロセスを殺さないために、開始時刻と exe のパスを持つ。</summary>
internal sealed record PidRecord(int Pid, long StartTimeUtcTicks, string ExePath, int Port);

/// <summary>いま動いているプロセスの、同一性を確かめるための情報。<see cref="ExePath"/> が null = 読めなかった(権限など)。</summary>
internal sealed record ProcessSnapshot(long StartTimeUtcTicks, string? ExePath);

/// <summary>プロセスの調べ方と終わらせ方(テストで偽物に替える)。</summary>
internal interface IProcessInspector
{
    /// <summary>pid のプロセスを調べる。無ければ null。</summary>
    ProcessSnapshot? Find(int pid);

    /// <summary>pid のプロセスを、子孫ごと終わらせる。すでに無ければ何もしない。</summary>
    void KillTree(int pid);
}

internal sealed class SystemProcessInspector : IProcessInspector
{
    public ProcessSnapshot? Find(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            long start = p.StartTime.ToUniversalTime().Ticks;
            string? exe = null;
            try { exe = p.MainModule?.FileName; }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { /* 読めない: 同一性を確かめられない */ }
            return new ProcessSnapshot(start, exe);
        }
        catch (ArgumentException)
        {
            return null; // その pid のプロセスは無い
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null; // 調べている間に終わった、または開始時刻を読めない(終わりかけ)
        }
    }

    public void KillTree(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(10_000);
        }
        catch (ArgumentException) { /* もう無い */ }
        catch (InvalidOperationException) { /* もう終わっている */ }
    }
}

/// <summary>掃除の結果。</summary>
internal enum SweepOutcome
{
    /// <summary>pid ファイルが無かった。</summary>
    NoFile,

    /// <summary>記録と同じプロセス(pid・開始時刻・exe のパスが一致)が残っていたので、終わらせた。</summary>
    Killed,

    /// <summary>記録の pid のプロセスはもう無い。</summary>
    NotRunning,

    /// <summary>同じ pid のプロセスはあるが、開始時刻か exe のパスが違う(pid の使い回し)。殺さない。</summary>
    DifferentProcess,

    /// <summary>プロセスの exe のパスを読めず、同一性を確かめられなかった。殺さない。</summary>
    Unverifiable,

    /// <summary>pid ファイルが壊れていた(読めない・値が不正)。</summary>
    Corrupt,
}

/// <summary>
/// 子プロセス(llama-server)の孤児を防ぐ・掃除する(ADR 0026)。2 層:
/// <list type="number">
/// <item><b>ジョブオブジェクト</b>(<see cref="JobObject"/>、KILL_ON_JOB_CLOSE): 起動した子を入れる。親が強制終了されても、OS が子を終わらせる。
/// 作れない・割り当てに失敗しても、起動は止めず、警告をログに出して pid ファイルだけで続ける。</item>
/// <item><b>pid ファイル</b>: 起動の直後に pid・開始時刻・exe のパス・ポートを書く。次の起動で、ファイルがあれば、同じ pid のプロセスが生きていて
/// <b>開始時刻と exe のパスが記録と一致するときだけ</b>終わらせる(pid の使い回しで他のプロセスを殺さない)。ジョブオブジェクトが効かなかった場合の保険。</item>
/// </list>
/// 掃除は、DB を開いたあと(= 単一インスタンスを通過したあと)に行う(2 つ目のプロセスが 1 つ目の llama-server を殺さないため)。
/// pid ファイルは llama-server 1 つぶんの記録(最後に起動したもの)。<see cref="Dispose"/> はジョブを閉じる(残っている子は OS が終わらせる)。
/// </summary>
internal sealed class ChildProcessGuard : IDisposable
{
    private readonly ICoreLog _log;
    private readonly IProcessInspector _inspector;
    private readonly JobObject? _job;
    private readonly object _gate = new();

    public ChildProcessGuard(string pidFilePath, ICoreLog log, IProcessInspector? inspector = null, bool useJobObject = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pidFilePath);
        ArgumentNullException.ThrowIfNull(log);
        PidFilePath = Path.GetFullPath(pidFilePath);
        _log = log;
        _inspector = inspector ?? new SystemProcessInspector();
        if (useJobObject)
        {
            try
            {
                _job = JobObject.CreateKillOnClose();
            }
            catch (Win32Exception e)
            {
                _log.Warning("ジョブオブジェクトを作れない。孤児の防止は pid ファイルだけになる: " + e.Message);
            }
        }
    }

    public string PidFilePath { get; }

    /// <summary>ジョブオブジェクトが使えるか(作れなかったら false)。</summary>
    public bool JobObjectAvailable => _job is not null;

    // ------------------------------------------------------------------ 掃除

    /// <summary>
    /// 前回の孤児を掃除する。pid ファイルがあれば読み、記録と同じプロセスだけを終わらせて、ファイルを消す(壊れたファイルも消す)。
    /// 殺さなかった場合(別のプロセス・確かめられない・もう無い)も、ファイルは消す(もう孤児の記録として役に立たない)。
    /// </summary>
    public SweepOutcome SweepOrphans()
    {
        lock (_gate)
        {
            if (!File.Exists(PidFilePath)) return SweepOutcome.NoFile;

            PidRecord? record;
            try
            {
                record = Parse(File.ReadAllText(PidFilePath, Encoding.UTF8));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _log.Warning($"pid ファイルを読めない。捨てる: {PidFilePath}: {e.Message}");
                TryDeleteFile();
                return SweepOutcome.Corrupt;
            }

            ProcessSnapshot? snapshot = _inspector.Find(record.Pid);
            SweepOutcome outcome;
            if (snapshot is null)
            {
                outcome = SweepOutcome.NotRunning;
            }
            else if (snapshot.ExePath is null)
            {
                _log.Warning($"前回の llama-server(pid {record.Pid})らしいプロセスが残っているが、exe のパスを読めず、同一性を確かめられない。殺さない。");
                outcome = SweepOutcome.Unverifiable;
            }
            else if (snapshot.StartTimeUtcTicks != record.StartTimeUtcTicks
                     || !string.Equals(Path.GetFullPath(snapshot.ExePath), Path.GetFullPath(record.ExePath), StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"pid {record.Pid} は、記録(開始時刻・exe のパス)と別のプロセスになっている(pid の使い回し)。殺さない。");
                outcome = SweepOutcome.DifferentProcess;
            }
            else
            {
                _log.Warning($"前回の llama-server が孤児として残っていた(pid {record.Pid}、ポート {record.Port.ToString(CultureInfo.InvariantCulture)})。終わらせる。");
                _inspector.KillTree(record.Pid);
                outcome = SweepOutcome.Killed;
            }
            TryDeleteFile();
            return outcome;
        }
    }

    // ------------------------------------------------------------------ 起動した子の登録

    /// <summary>
    /// 起動した子を、ジョブに入れ(失敗しても警告だけ)、pid ファイルに書く。起動の直後(モデルを読み始める前)に呼ぶ。
    /// ジョブに入れられたら true。
    /// </summary>
    public bool Register(Process process, string launchedExePath, int port)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchedExePath);

        bool inJob = false;
        if (_job is not null)
        {
            try
            {
                _job.Assign(process);
                inJob = true;
            }
            catch (Win32Exception e)
            {
                _log.Warning($"子プロセス(pid {process.Id})をジョブオブジェクトに入れられない。孤児の防止は pid ファイルだけになる: {e.Message}");
            }
        }

        // 記録する exe は、**起動したパス**(正規化したフルパス)。起動した直後の MainModule は、プロセスの初期化の途中で、まれに ntdll.dll など exe でない名前を返す
        // (テストの繰り返しで、約 0.3% の頻度で見つかった。ADR 0026)。それを記録すると、次の起動の掃除が「別のプロセス」と判断して、残った llama-server を殺さない
        var record = new PidRecord(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(launchedExePath), port);
        lock (_gate) WritePidFile(record);
        return inJob;
    }

    /// <summary>子を止めたとき: pid ファイルが、その pid の記録なら消す(別の pid の記録は残す)。</summary>
    public void Unregister(int pid)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(PidFilePath)) return;
                PidRecord record = Parse(File.ReadAllText(PidFilePath, Encoding.UTF8));
                if (record.Pid == pid) TryDeleteFile();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TryDeleteFile(); // 壊れていた
            }
        }
    }

    // ------------------------------------------------------------------ pid ファイル

    internal static string Serialize(PidRecord r) =>
        JsonSerializer.Serialize(new { pid = r.Pid, startTimeUtcTicks = r.StartTimeUtcTicks, exePath = r.ExePath, port = r.Port });

    /// <summary>厳格に読む: 4 つの項目がすべて在って、型と値が正しいこと。でなければ <see cref="InvalidDataException"/>(壊れた JSON は <see cref="JsonException"/>)。</summary>
    internal static PidRecord Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("pid ファイルが JSON のオブジェクトでない");
        int pid = ReadInt(root, "pid");
        long ticks = ReadLong(root, "startTimeUtcTicks");
        int port = ReadInt(root, "port");
        if (!root.TryGetProperty("exePath", out JsonElement exe) || exe.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(exe.GetString()))
            throw new InvalidDataException("pid ファイルの exePath が無い・文字列でない");
        if (pid <= 0) throw new InvalidDataException("pid ファイルの pid が正の整数でない");
        if (ticks <= 0) throw new InvalidDataException("pid ファイルの startTimeUtcTicks が正の整数でない");
        return new PidRecord(pid, ticks, exe.GetString()!, port);
    }

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v)
            ? v : throw new InvalidDataException($"pid ファイルの {name} が無い・整数でない");

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v)
            ? v : throw new InvalidDataException($"pid ファイルの {name} が無い・整数でない");

    private void WritePidFile(PidRecord record)
    {
        string? dir = Path.GetDirectoryName(PidFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = PidFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, Serialize(record), new UTF8Encoding(false));
            File.Move(tmp, PidFilePath, overwrite: true); // 読む側が途中のファイルを見ないよう、完成してから置き換える
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pid ファイルが書けなくても、ジョブオブジェクトが効いていれば孤児にはならない。起動は止めない
            _log.Warning($"pid ファイルを書けない: {PidFilePath}: {e.Message}");
            try { File.Delete(tmp); } catch (Exception) { /* 後始末 */ }
        }
    }

    private void TryDeleteFile()
    {
        try { File.Delete(PidFilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"pid ファイルを消せない: {PidFilePath}: {e.Message}");
        }
    }

    /// <summary>ジョブを閉じる。ジョブに残っている子は OS が終わらせる。</summary>
    public void Dispose() => _job?.Dispose();
}
