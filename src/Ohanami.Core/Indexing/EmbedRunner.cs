using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;

namespace Ohanami.Core.Indexing;

/// <summary>
/// 画像の埋め込み(SigLIP2 の画像側)のジョブ。
/// <list type="number">
/// <item>待ち行列は毎回 DB から組み立てる: 現存するパス(missing_since が無い)があり、stage_status('embed') の行が無い画像を、images の rowid 順に 256 件ずつ。</item>
/// <item>CPU のワーカー 1 本が、ファイルを読み、デコード → リサイズ → 正規化する。有界のキュー(容量 8)で、GPU に渡すこの呼び出し側の流れに渡す。</item>
/// <item>この呼び出し側の流れ(GPU のワーカー)が 1 枚ずつ Embed し、**1 トランザクション**で image_vectors・stage_status(done)・images の幅高さを書く。書いたあとに onEmbedded を呼ぶ(検索用のメモリ上の表を更新する。ADR 0008)。</item>
/// <item>画像の境目(1 枚を確定したあと、次の 1 枚を取る前)で、ct と shouldYield を見る。Run は約 66ms なので、1 枚の途中では止めない(契約ではなくこの実装の選択)。</item>
/// </list>
/// 失敗の扱い:
/// <list type="bullet">
/// <item>デコード・前処理できない / エンコーダが ImageRunException(再試行しても失敗)・InvalidDataException(出力が不正)/ 非有限の値 → その画像を stage_status = failed(error 付き)にして先へ進む。</item>
/// <item>ファイルを開けない(消えた・取り外した・アクセス拒否)→ **記録せず**、この実行ではその画像を飛ばす(行を作らないので次回の Start で再試行。一時的な不在を failed にして固定しない)。</item>
/// <item>それ以外(エンコーダが作れない・自己検査の失敗・作り直しの失敗・次元の不一致・想定外の例外)→ 致命的。例外で返し、Indexer が Run = Failed にする。書き込み済みの分は残る。</item>
/// </list>
/// 資源(エンコーダ)は <see cref="ReleaseAsync"/> まで握る(Yielded でも握ったまま)。<see cref="RunRecorder.StartAsync"/> は、エンコーダの自己検査のあと・最初の書き込みの前に呼ぶ。
/// </summary>
internal sealed class EmbedRunner : JobRunner
{
    private const int ChunkSize = 256;
    private const int PipelineCapacity = 8;
    private const int TensorLength = 3 * Siglip2ImagePreprocessor.Size * Siglip2ImagePreprocessor.Size;

    private readonly OhanamiDatabase _db;
    private readonly IImageEmbedderFactory _factory;
    private readonly IImagePreprocessor _preprocessor;
    private readonly TimeProvider _time;
    private readonly ICoreLog _log;
    private readonly Action<string, float[]>? _onEmbedded;
    private readonly IReadOnlyDictionary<Stage, string> _currentModelIds;
    private readonly Dictionary<string, string> _skipped = new(StringComparer.Ordinal);   // この実行で飛ばした画像の hash → 理由(ReleaseAsync で消える)

    private IImageEmbedder? _embedder;
    private int _loggedRecreations;   // 作り直しのうち、ログに残した回数(エンコーダを作るたびに 0 に戻す)
    private bool _modelRegistered;

    public EmbedRunner(
        OhanamiDatabase db, IImageEmbedderFactory factory, IImagePreprocessor preprocessor, TimeProvider time, ICoreLog log,
        Action<string, float[]>? onEmbedded = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(preprocessor);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _factory = factory;
        _preprocessor = preprocessor;
        _time = time;
        _log = log;
        _onEmbedded = onEmbedded;
        _currentModelIds = new Dictionary<Stage, string> { [Stage.Embed] = factory.Model.ModelId };
    }

    public override JobKind Kind => JobKind.Embed;

    public override IReadOnlyList<Stage> Stages { get; } = [Stage.Embed];

    public override IReadOnlyDictionary<Stage, string> CurrentModelIds => _currentModelIds;

    // ------------------------------------------------------------------ 実行

    public override async Task<RunEnd> RunAsync(RunRecorder run, Func<bool> shouldYield, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(shouldYield);
        if (ct.IsCancellationRequested) return RunEnd.Cancelled;

        try
        {
            if (_embedder is null)
            {
                run.ReportEngine(new EngineStatus(EnginePhase.Checking, null, null));
                try
                {
                    _embedder = await _factory.CreateAsync(ct).ConfigureAwait(false);
                    _loggedRecreations = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    run.ReportEngine(EngineStatus.NotCreated);
                    return RunEnd.Cancelled;
                }
                catch (Exception e)
                {
                    run.ReportEngine(new EngineStatus(EnginePhase.Failed, e.Message, null));
                    throw;
                }
                run.ReportEngine(new EngineStatus(EnginePhase.Ready, null, _embedder.SelfCheck));
            }

            if (!run.Started)
            {
                await EnsureModelRegisteredAsync(ct).ConfigureAwait(false);
                string provider = _embedder.SelfCheck is null ? "unknown" : "CUDAExecutionProvider";
                await run.StartAsync(RuntimeInfo.RuntimeJson(_embedder.SelfCheck, provider, _embedder.ExecutionSettings), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RunEnd.Cancelled;
        }

        long cursor = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) return RunEnd.Cancelled;
            if (shouldYield()) return RunEnd.Yielded;

            List<QueueItem> chunk = FetchQueue(cursor);
            if (chunk.Count == 0) return RunEnd.Drained;
            cursor = chunk[^1].RowId;

            RunEnd? stop = await ProcessChunkAsync(chunk, run, shouldYield, ct).ConfigureAwait(false);
            if (stop is not null) return stop.Value;
        }
    }

    public override Task ReleaseAsync()
    {
        _embedder?.Dispose();
        _embedder = null;
        _loggedRecreations = 0;
        _skipped.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// エンコーダが、Run の失敗でセッションを作り直していたら、ログに残す(スリープの復帰のあとの最初の Run が失敗して、作り直して成功した場合は、
    /// 結果の上では見えない)。文言は tools の SleepCheckLogic が読む。
    /// </summary>
    private void LogRecreations()
    {
        IImageEmbedder? embedder = _embedder;
        if (embedder is null || embedder.RecreateCount <= _loggedRecreations) return;
        _loggedRecreations = embedder.RecreateCount;
        _log.Info($"画像のセッションを作り直した(Run の失敗から。累計 {_loggedRecreations.ToString(CultureInfo.InvariantCulture)} 回。理由: {embedder.LastRecreateReason ?? "(不明)"})");
    }

    private async Task EnsureModelRegisteredAsync(CancellationToken ct)
    {
        if (_modelRegistered) return;
        ModelRegistration registration = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(_db, _factory.ModelName, _factory.Model, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (registration.Outcome == RegistrationOutcome.Updated)
            _log.Info($"models の記録を更新した(model_id は同じ): {string.Join(" / ", registration.Changes)}");
        _modelRegistered = true;
    }

    // ------------------------------------------------------------------ 待ち行列

    private sealed record QueueItem(long RowId, string Hash, string[] Paths);

    private List<QueueItem> FetchQueue(long afterRowId)
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        var heads = new List<(long RowId, string Hash)>();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT i.rowid, i.hash
                FROM images i
                WHERE i.rowid > $after
                  AND NOT EXISTS (SELECT 1 FROM stage_status s WHERE s.hash = i.hash AND s.stage = $stage)
                  AND EXISTS (SELECT 1 FROM image_paths p WHERE p.hash = i.hash AND p.missing_since IS NULL)
                ORDER BY i.rowid
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$after", afterRowId);
            cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(Stage.Embed));
            cmd.Parameters.AddWithValue("$limit", ChunkSize);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) heads.Add((r.GetInt64(0), r.GetString(1)));
        }

        var items = new List<QueueItem>(heads.Count);
        using SqliteCommand pathCmd = conn.CreateCommand();
        pathCmd.CommandText = "SELECT path FROM image_paths WHERE hash = $hash AND missing_since IS NULL ORDER BY path";
        SqliteParameter hashParam = pathCmd.Parameters.Add("$hash", SqliteType.Text);
        foreach ((long rowId, string hash) in heads)
        {
            hashParam.Value = hash;
            var paths = new List<string>();
            using (SqliteDataReader r = pathCmd.ExecuteReader())
                while (r.Read()) paths.Add(r.GetString(0));
            items.Add(new QueueItem(rowId, hash, paths.ToArray()));
        }
        return items;
    }

    // ------------------------------------------------------------------ パイプライン(CPU のワーカー → GPU の流れ)

    private enum PreparedKind { Ready, Failed, Skipped }

    private sealed record Prepared(PreparedKind Kind, QueueItem Item, string? Path, PreprocessedImage? Image, string? Message);

    private async Task<RunEnd?> ProcessChunkAsync(List<QueueItem> chunk, RunRecorder run, Func<bool> shouldYield, CancellationToken ct)
    {
        List<QueueItem> items = chunk.Where(i => !_skipped.ContainsKey(i.Hash)).ToList();
        if (items.Count == 0) return null;

        var channel = Channel.CreateBounded<Prepared>(new BoundedChannelOptions(PipelineCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task producer = Task.Run(async () =>
        {
            try
            {
                foreach (QueueItem item in items)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(Prepare(item), stop.Token).ConfigureAwait(false);
                }
                channel.Writer.Complete();
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e); // 想定外(前処理の契約違反など)。読む側で例外になる=致命的
            }
        }, CancellationToken.None);

        RunEnd? result = null;
        try
        {
            await foreach (Prepared p in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // 画像の境目: 次の 1 枚を処理する前に見る。先読みした分は捨てる(次回の待ち行列でもう一度取る)
                if (ct.IsCancellationRequested) { result = RunEnd.Cancelled; break; }
                if (shouldYield()) { result = RunEnd.Yielded; break; }
                await HandleAsync(p, run).ConfigureAwait(false);
            }
        }
        finally
        {
            stop.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (Exception) { /* 生産側の例外は上の ReadAllAsync が返す。ここでは握りつぶすだけ */ }
        }
        return result;
    }

    /// <summary>
    /// CPU 側: ファイルを読んで前処理する。ファイルを開けない、または**読んだバイト列の SHA-256 が DB のハッシュと違う**(走査のあとで書き換えられた。
    /// 古いハッシュの下に新しい内容の結果を入れない)パスは使わず、次のパスを試す。使えるパスが無ければ Skipped(記録しない。次の走査が新しい内容を
    /// 新しいハッシュとして拾う)。デコード・前処理できなければ Failed。
    /// 内容の照合は更新日時とサイズの照合より強い。0.8MB で約 1ms(805MB/秒)で、1 枚 90ms 前後の処理に比べて小さい。
    /// </summary>
    private Prepared Prepare(QueueItem item)
    {
        byte[]? bytes = null;
        string? usedPath = null;
        string? lastError = null;
        foreach (string path in item.Paths)
        {
            byte[] read;
            try
            {
                read = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lastError = $"{path}: {e.Message}";
                continue;
            }
            string actual = Convert.ToHexStringLower(SHA256.HashData(read));
            if (!string.Equals(actual, item.Hash, StringComparison.Ordinal))
            {
                lastError = $"{path}: 内容が走査のときと違う(ハッシュ {actual[..12]}… ≠ {item.Hash[..12]}…。走査のあとで書き換えられた)";
                continue;
            }
            bytes = read;
            usedPath = path;
            break;
        }
        if (bytes is null) return new Prepared(PreparedKind.Skipped, item, item.Paths.FirstOrDefault(), null, lastError ?? "現存するパスが無い");

        PreprocessedImage image;
        try
        {
            image = _preprocessor.Preprocess(bytes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Prepared(PreparedKind.Failed, item, usedPath, null, "画像をデコード・前処理できない: " + e.Message);
        }
        if (image.Tensor.Length != TensorLength)
            throw new InvalidOperationException($"前処理のテンソルの長さが {TensorLength} でない(実際 {image.Tensor.Length})。前処理の契約違反。");
        return new Prepared(PreparedKind.Ready, item, usedPath, image, null);
    }

    /// <summary>GPU 側: 1 枚を Embed して 1 トランザクションで確定する。</summary>
    private async Task HandleAsync(Prepared p, RunRecorder run)
    {
        switch (p.Kind)
        {
            case PreparedKind.Skipped:
                _skipped[p.Item.Hash] = p.Message ?? "読めない";
                run.ReportSkipped(p.Item.Hash, p.Message ?? "読めない");
                _log.Warning($"画像を読めない、または内容が走査のときと違うので、この実行では飛ばす(記録しない。次回の Start で再試行。書き換えられていれば次の走査が新しいハッシュで拾う): {p.Message}");
                return;

            case PreparedKind.Failed:
                run.ReportCurrent(p.Path);
                await WriteFailedAsync(run, p.Item.Hash, null, p.Message!).ConfigureAwait(false);
                _log.Warning($"{p.Path}: {p.Message}");
                run.ReportCompleted(failed: true);
                return;
        }

        PreprocessedImage image = p.Image!;
        run.ReportCurrent(p.Path);
        float[] vector;
        try
        {
            vector = _embedder!.Embed(image.Tensor);
        }
        catch (Exception e) when (e is ImageRunException or InvalidDataException)
        {
            LogRecreations();
            await WriteFailedAsync(run, p.Item.Hash, image, "埋め込みに失敗した: " + e.Message).ConfigureAwait(false);
            _log.Warning($"{p.Path}: 埋め込みに失敗した: {e.Message}");
            run.ReportCompleted(failed: true);
            return;
        }
        LogRecreations();

        int dim = _factory.Model.Dim;
        if (vector.Length != dim)
            throw new InvalidOperationException($"エンコーダの出力の次元が {dim} でない(実際 {vector.Length})。モデルと前処理の取り違え。");
        if (!AllFinite(vector))
        {
            await WriteFailedAsync(run, p.Item.Hash, image, "埋め込みに NaN / 無限大が含まれる").ConfigureAwait(false);
            _log.Warning($"{p.Path}: 埋め込みに NaN / 無限大が含まれる");
            run.ReportCompleted(failed: true);
            return;
        }

        await WriteDoneAsync(run, p.Item.Hash, image, vector).ConfigureAwait(false);
        _onEmbedded?.Invoke(p.Item.Hash, vector);
        run.ReportCompleted();
    }

    private static bool AllFinite(float[] v)
    {
        foreach (float x in v) if (!float.IsFinite(x)) return false;
        return true;
    }

    // ------------------------------------------------------------------ 書き込み(1 枚 = 1 トランザクション。ct は渡さない=始めた 1 枚は必ず確定する)

    private Task WriteDoneAsync(RunRecorder run, string hash, PreprocessedImage image, float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        string modelId = _factory.Model.ModelId;
        long runId = run.RunId;
        string now = DbTime.Format(_time.GetUtcNow());
        return _db.WriteAsync((conn, tx) =>
        {
            SetDimensions(conn, tx, hash, image);
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO image_vectors(hash, model_id, embedding) VALUES ($hash, $model, $embedding)";
                cmd.Parameters.AddWithValue("$hash", hash);
                cmd.Parameters.AddWithValue("$model", modelId);
                cmd.Parameters.AddWithValue("$embedding", bytes);
                cmd.ExecuteNonQuery();
            }
            UpsertStatus(conn, tx, hash, StageStatus.Done, modelId, runId, null, now);
        }, CancellationToken.None);
    }

    private Task WriteFailedAsync(RunRecorder run, string hash, PreprocessedImage? image, string error)
    {
        string modelId = _factory.Model.ModelId;
        long runId = run.RunId;
        string now = DbTime.Format(_time.GetUtcNow());
        return _db.WriteAsync((conn, tx) =>
        {
            if (image is not null) SetDimensions(conn, tx, hash, image);
            UpsertStatus(conn, tx, hash, StageStatus.Failed, modelId, runId, error, now);
        }, CancellationToken.None);
    }

    private static void SetDimensions(SqliteConnection conn, SqliteTransaction tx, string hash, PreprocessedImage image)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE images SET width = $w, height = $h WHERE hash = $hash";
        cmd.Parameters.AddWithValue("$w", image.Width);
        cmd.Parameters.AddWithValue("$h", image.Height);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStatus(SqliteConnection conn, SqliteTransaction tx, string hash, StageStatus status, string modelId, long runId, string? error, string now)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at)
            VALUES ($hash, $stage, $status, $model, $run, NULL, $error, 1, $now)
            ON CONFLICT(hash, stage) DO UPDATE SET
                status = excluded.status, model_id = excluded.model_id, run_id = excluded.run_id,
                settings_json = NULL, error = excluded.error, attempts = stage_status.attempts + 1, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(Stage.Embed));
        cmd.Parameters.AddWithValue("$status", DbEnums.ToDb(status));
        cmd.Parameters.AddWithValue("$model", modelId);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ 件数

    public override int CountPendingImages() => PendingQueries.Count(_db, Stage.Embed);

    public override int CountPendingAmong(IReadOnlyCollection<string> hashes) => PendingQueries.CountAmong(_db, Stage.Embed, hashes);

    public override IReadOnlyList<StageSummary> Summarize()
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT count(*) FROM images i
                 WHERE EXISTS (SELECT 1 FROM image_paths p WHERE p.hash = i.hash AND p.missing_since IS NULL)
                   AND NOT EXISTS (SELECT 1 FROM stage_status s WHERE s.hash = i.hash AND s.stage = $stage)),
              (SELECT count(*) FROM stage_status WHERE stage = $stage AND status = $done AND model_id = $model),
              (SELECT count(*) FROM stage_status WHERE stage = $stage AND status = $failed),
              (SELECT count(*) FROM stage_status WHERE stage = $stage AND status = $done AND model_id <> $model)
            """;
        cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(Stage.Embed));
        cmd.Parameters.AddWithValue("$done", DbEnums.ToDb(StageStatus.Done));
        cmd.Parameters.AddWithValue("$failed", DbEnums.ToDb(StageStatus.Failed));
        cmd.Parameters.AddWithValue("$model", _factory.Model.ModelId);
        using SqliteDataReader r = cmd.ExecuteReader();
        r.Read();
        return [new StageSummary(Stage.Embed, (int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3))];
    }
}
