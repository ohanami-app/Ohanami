using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Core.Indexing;

/// <summary><see cref="JobRunner.RunAsync"/> の終わり方。どれも例外にしない。</summary>
internal enum RunEnd
{
    /// <summary>このランナーが今できる仕事が無くなった。</summary>
    Drained,

    /// <summary>shouldYield の合図で、今の 1 枚を確定してから戻った(資源は握ったまま)。</summary>
    Yielded,

    /// <summary>ct が切れて、安全な境目で止まった。</summary>
    Cancelled,
}

/// <summary>
/// ジョブ 1 種類を動かすランナー。<c>EmbedRunner</c> と <c>VlmRunner</c> がある。
/// <list type="bullet">
/// <item>**止め方の契約**: ランナーが <c>ct</c> を見て、安全な境目で止まる。境目が長いランナー(VLM)は、中断してその 1 枚をやり直す(stage_status を書かない)。
///   埋め込みが画像 1 枚の境目で止まるのは <c>EmbedRunner</c> の実装の話。</item>
/// <item>**切り替えの合図**: <c>shouldYield</c> が true なら、今の 1 枚を終えて <see cref="RunEnd.Yielded"/> で戻る(処理中の 1 枚を捨てない)。資源は握ったまま。</item>
/// <item>画像 1 枚の結果は 1 トランザクションで確定する。待ち行列は毎回 DB から組み立てる(メモリに持たない)。</item>
/// <item>致命的な失敗(エンコーダが作れない、自己検査に失敗、想定外の例外)は例外で返す。Indexer が Run = Failed にする。</item>
/// </list>
/// </summary>
internal abstract class JobRunner
{
    public abstract JobKind Kind { get; }

    /// <summary>このランナーが埋める段階の集合(Embed → [Embed]、Vlm → [Ocr, Caption])。</summary>
    public abstract IReadOnlyList<Stage> Stages { get; }

    /// <summary>段階ごとの、現行の model_id(Outdated の判定と、再処理の対象の決定に使う)。</summary>
    public abstract IReadOnlyDictionary<Stage, string> CurrentModelIds { get; }

    /// <summary>待ちが無くなる / shouldYield が true / ct が切れる、のどれかまで処理する。</summary>
    public abstract Task<RunEnd> RunAsync(RunRecorder run, Func<bool> shouldYield, CancellationToken ct);

    /// <summary>資源(GPU のセッションなど)を空ける。一時停止・ジョブの切り替え・終了のとき。2 回呼んでもよい。</summary>
    public abstract Task ReleaseAsync();

    /// <summary>段階ごとの件数(DB から数える)。</summary>
    public abstract IReadOnlyList<StageSummary> Summarize();

    /// <summary>
    /// 待ちの画像の数(画像 1 枚を 1 と数える。<see cref="Summarize"/> の Pending を段階ごとに足すと、VLM では同じ画像を Ocr と Caption で 2 回数える)。
    /// 既定は、段階ごとの Pending の最大(段階が 1 つのランナーなら、その Pending)。
    /// </summary>
    public virtual int CountPendingImages() => Summarize().Select(s => s.Pending).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 指定した hash のうち、いまも待ちのものの数(ADR 0027。「新しく入った待ち = 待ち − 飛ばした集合のうち今も待ちのもの」の引く側)。
    /// 既定は 0(待ちの hash を引けないランナーは、飛ばした画像を持たない扱い)。
    /// </summary>
    public virtual int CountPendingAmong(IReadOnlyCollection<string> hashes) => 0;

    /// <summary>
    /// このランナーが動かせない理由になる、無いファイル(モデルのファイルなど)の名前。空なら動かせる。既定は空。
    /// <see cref="Indexer"/> は、空でないランナーを選ばず、待ちに数えない(失敗にせず、「未取得」の状態として扱う。ファイルがそろえば、次の開始・待機中の次の走査で動き出す)。
    /// 存在だけを見る(ハッシュは読まない)。呼ぶたびに調べ直す(利用者がファイルを置いたら、すぐ変わる)。
    /// </summary>
    public virtual IReadOnlyList<string> UnavailableFiles() => [];
}

/// <summary>待ちの画像(現存するパスがあり、その段階の stage_status に行が無い画像)を数える共通の問い合わせ。EmbedRunner と VlmRunner の Summarize・待ち行列と同じ条件。</summary>
internal static class PendingQueries
{
    private const int InChunk = 500;

    /// <summary>その段階の待ちの画像の数。</summary>
    public static int Count(OhanamiDatabase db, Stage stage)
    {
        using SqliteConnection conn = db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM images i
            WHERE EXISTS (SELECT 1 FROM image_paths p WHERE p.hash = i.hash AND p.missing_since IS NULL)
              AND NOT EXISTS (SELECT 1 FROM stage_status s WHERE s.hash = i.hash AND s.stage = $stage)
            """;
        cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(stage));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>指定した hash のうち、その段階の待ちのものの数(hash の IN を 500 個ずつ)。</summary>
    public static int CountAmong(OhanamiDatabase db, Stage stage, IReadOnlyCollection<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (hashes.Count == 0) return 0;
        using SqliteConnection conn = db.CreateReadConnection();
        int total = 0;
        foreach (string[] chunk in hashes.Chunk(InChunk))
        {
            using SqliteCommand cmd = conn.CreateCommand();
            var names = new string[chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                names[i] = "$h" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            cmd.CommandText = $"""
                SELECT count(*) FROM images i
                WHERE i.hash IN ({string.Join(", ", names)})
                  AND EXISTS (SELECT 1 FROM image_paths p WHERE p.hash = i.hash AND p.missing_since IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM stage_status s WHERE s.hash = i.hash AND s.stage = $stage)
                """;
            cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(stage));
            total += Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return total;
    }
}

/// <summary>
/// 実行の単位(資源を作ってから空けるまで)1 回分の記録と通知の窓口。<c>runs</c> の行は、ランナーが **自己検査のあと・最初の書き込みの前** に
/// <see cref="StartAsync"/> で作る(自己検査に失敗した実行は行を残さない)。<see cref="EndAsync"/> で ended_at を入れる(強制終了なら NULL のまま)。
/// </summary>
internal sealed class RunRecorder
{
    private readonly OhanamiDatabase _db;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly Action<EngineStatus>? _onEngine;
    private readonly Action<string?>? _onCurrent;
    private readonly Action? _onCompleted;
    private readonly Action<string>? _onSkipped;
    private readonly object _skippedGate = new();
    private readonly Dictionary<string, string> _skipped = new(StringComparer.Ordinal);   // 飛ばした画像の hash → 理由
    private long _runId;
    private int _completed;
    private int _failed;

    public JobKind Kind { get; }

    public RunRecorder(
        OhanamiDatabase db, JobKind kind, string appVersion, TimeProvider time,
        Action<EngineStatus>? onEngine = null, Action<string?>? onCurrent = null, Action? onCompleted = null, Action<string>? onSkipped = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        Kind = kind;
        _appVersion = appVersion;
        _time = time;
        _onEngine = onEngine;
        _onCurrent = onCurrent;
        _onCompleted = onCompleted;
        _onSkipped = onSkipped;
    }

    public bool Started => Volatile.Read(ref _runId) != 0;

    /// <summary>runs.id。<see cref="StartAsync"/> の前に読むと例外(行が無いまま stage_status に書かせない)。</summary>
    public long RunId => Started ? Volatile.Read(ref _runId) : throw new InvalidOperationException("runs の行がまだ無い(StartAsync の前に RunId を読んだ)");

    /// <summary>この実行で完了(done / failed)した画像の数。Indexer がバッチ単位の切り替えの判断に使う。</summary>
    public int CompletedCount => Volatile.Read(ref _completed);

    /// <summary><see cref="CompletedCount"/> のうち、failed にした画像の数(ジョブの完了のログ用)。</summary>
    public int FailedCount => Volatile.Read(ref _failed);

    /// <summary>この実行で飛ばした画像の hash(記録しない。ADR 0027 の「飛ばした集合」の元。呼ぶたびに写しを返す)。</summary>
    public IReadOnlyCollection<string> SkippedHashes
    {
        get { lock (_skippedGate) return _skipped.Keys.ToArray(); }
    }

    /// <summary>この実行で飛ばした画像の hash と、飛ばした理由(画面に出す。呼ぶたびに写しを返す)。同じ画像を 2 回報告したら、あとの理由。</summary>
    public IReadOnlyDictionary<string, string> SkippedReasons
    {
        get { lock (_skippedGate) return new Dictionary<string, string>(_skipped, StringComparer.Ordinal); }
    }

    /// <summary>この実行で飛ばした画像の数(同じ画像を 2 回報告しても 1)。</summary>
    public int SkippedCount
    {
        get { lock (_skippedGate) return _skipped.Count; }
    }

    public async Task StartAsync(string runtimeJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeJson);
        if (Started) throw new InvalidOperationException("runs の行は 1 回しか作れない");
        string now = DbTime.Format(_time.GetUtcNow());
        long id = await _db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO runs(job_kind, started_at, app_version, runtime_json) VALUES ($kind, $now, $app, $runtime) RETURNING id";
            cmd.Parameters.AddWithValue("$kind", DbEnums.ToDb(Kind));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$app", _appVersion);
            cmd.Parameters.AddWithValue("$runtime", runtimeJson);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, ct).ConfigureAwait(false);
        Volatile.Write(ref _runId, id);
    }

    /// <summary>正常に止まったとき(一時停止・完了・切り替え)に ended_at を入れる。行が無ければ何もしない。</summary>
    public async Task EndAsync(CancellationToken ct = default)
    {
        if (!Started) return;
        string now = DbTime.Format(_time.GetUtcNow());
        long id = RunId;
        await _db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE runs SET ended_at = $now WHERE id = $id AND ended_at IS NULL";
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
    }

    public void ReportEngine(EngineStatus status) => _onEngine?.Invoke(status);

    public void ReportCurrent(string? path) => _onCurrent?.Invoke(path);

    /// <summary>
    /// 読めなかったので、この実行では飛ばした画像(記録しない。次回の Start で再試行)を知らせる。hash を持つのは、Indexer が「飛ばした集合」として覚え、
    /// 待ちの判定から引くため(ADR 0027)。<paramref name="reason"/> は、画面の「飛ばした画像」に出す理由。同じ画像を 2 回報告しても数は 1。
    /// </summary>
    public void ReportSkipped(string hash, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_skippedGate) _skipped[hash] = reason;
        _onSkipped?.Invoke(hash);
    }

    /// <summary>画像 1 枚を完了(done または failed)した。<paramref name="failed"/> は failed にしたとき。</summary>
    public void ReportCompleted(bool failed = false)
    {
        Interlocked.Increment(ref _completed);
        if (failed) Interlocked.Increment(ref _failed);
        _onCompleted?.Invoke();
    }
}
