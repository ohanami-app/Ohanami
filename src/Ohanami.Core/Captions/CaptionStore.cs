using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;

namespace Ohanami.Core.Captions;

/// <summary>VLM の 1 枚の成功の結果(<c>finish_reason = stop</c> でパースに成功したもの)。captions に保存する。</summary>
/// <param name="Hash">画像の内容ハッシュ。</param>
/// <param name="ModelId">VLM の model_id(models に登録済み)。</param>
/// <param name="Ocr">画面の文字の書き起こし(無ければ空文字列)。加工せずに保存する。</param>
/// <param name="Description">説明文。</param>
/// <param name="Tags">tags(順序を保つ。空でもよい)。</param>
/// <param name="FinishReason">"stop" だけを成功として保存する(それ以外は <see cref="ArgumentException"/>)。</param>
/// <param name="Attempt">何回目の試行の結果か(1 = ペナルティなし、2 = dry_multiplier、3 = repeat_penalty)。</param>
/// <param name="SettingsJson">この結果を作った設定(JSON のオブジェクト)。</param>
/// <param name="PromptTokens">usage.prompt_tokens(分かれば)。</param>
internal sealed record CaptionResult(
    string Hash, string ModelId, string Ocr, string Description, IReadOnlyList<string> Tags,
    string FinishReason, int Attempt, string SettingsJson, int? PromptTokens);

/// <summary>captions の 1 行(読み出し)。<see cref="Tags"/> は <c>tags_json</c> を厳格に読んだもの。</summary>
internal sealed record CaptionRow(
    string Hash, string ModelId, string Ocr, string Description, IReadOnlyList<string> Tags,
    string FinishReason, int Attempt, string SettingsJson, int? PromptTokens, long RunId, string CreatedAt);

/// <summary><see cref="CaptionStore.SaveAsync"/> の結果。</summary>
internal enum CaptionSaveOutcome
{
    /// <summary>captions・stage_status(ocr と caption)・search_text・fts_docs を書いた。</summary>
    Saved,

    /// <summary>
    /// captions と stage_status(done)は書いたが、索引の行は作らなかった(その画像の既存の索引の行は消した)。理由は、
    /// 索引文書が作れない(まれ。文字列に対して決定的な失敗で、再試行しても直らない)か、最新の caption の tags_json が壊れている、
    /// または索引そのものが <see cref="FtsIndexState.Failed"/>(直れば作り直しで captions から入る)。警告をログに出し、<see cref="CaptionStore.SavedWithoutIndexCount"/> を数える。
    /// VLM のジョブは止めない(12 秒の結果を捨てず、索引の故障で数十時間の処理を止めない)。
    /// </summary>
    SavedWithoutIndex,
}

/// <summary>
/// captions の書き込みと読み出し。
/// <list type="bullet">
/// <item><b>1 枚の更新 = 1 トランザクション</b>: captions(upsert)、stage_status の ocr と caption(同じ内容)、search_text、fts_docs。途中で例外なら何も残らない。
///   索引は常に captions と食い違わない。</item>
/// <item>成功(<c>finish_reason = stop</c>)だけを captions に保存する。失敗は <see cref="SaveFailureAsync"/> で stage_status の failed として持つ(captions に行を作らない)。</item>
/// <item>書く前に <see cref="FtsIndex.EnsureAsync"/> を待つ(規約が DB と違えば、作り直しが終わってから書く)。
///   **索引は派生データなので、索引の都合で captions を捨てない**: 索引文書が作れない・索引が Failed のときも captions と stage_status(done)は書き、
///   索引の行だけ作らずに <see cref="CaptionSaveOutcome.SavedWithoutIndex"/> を返す(作り直しでも同じ扱い=captions は残し、索引に入れず警告と件数)。</item>
/// <item>stage_status の <c>attempts</c> は、同じ画像・段階を処理した回数(最初は 1、行が既にあれば +1)。1 回の処理の中の再試行の回数は captions.attempt と、失敗なら settings_json の一覧。</item>
/// </list>
/// 画像 1 枚の確定は ct を渡さない(始めた 1 枚は必ず確定する。<see cref="EnsureAsync"/> を待つ間だけ ct を見る)。
/// </summary>
internal sealed class CaptionStore
{
    /// <summary>ハッシュをまとめて引くときの 1 回の件数(SQL のパラメータの上限を避ける。<c>SearchService.LoadPaths</c> と同じ)。</summary>
    internal const int ReadChunk = 500;

    private readonly OhanamiDatabase _db;
    private readonly FtsIndex _index;
    private int _savedWithoutIndex;

    public CaptionStore(OhanamiDatabase db, FtsIndex index)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(index);
        _db = db;
        _index = index;
    }

    // ---------------------------------------------------------------- 書き込み

    /// <summary>
    /// 成功した 1 枚を確定する(上の 1 トランザクション)。索引の行を作れないときは、captions と stage_status は書いて
    /// <see cref="CaptionSaveOutcome.SavedWithoutIndex"/> を返す。<paramref name="ct"/> は索引の準備を待つ間だけ見る(始めた書き込みは確定する)。
    /// </summary>
    public async Task<CaptionSaveOutcome> SaveAsync(long runId, CaptionResult c, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(c);
        Validate(c);
        bool indexUsable = await _index.TryEnsureUsableAsync(ct).ConfigureAwait(false);

        string created = DbTime.Format(now);
        string tagsJson = JsonSerializer.Serialize(c.Tags);
        string? problem = indexUsable ? null : "索引が Failed の状態(作り直しに失敗している)";
        await _db.WriteAsync((conn, tx) =>
        {
            if (indexUsable) _index.RequireCurrent(conn, tx);
            UpsertCaption(conn, tx, c, tagsJson, runId, created);
            UpsertStage(conn, tx, c.Hash, Stage.Ocr, StageStatus.Done, c.ModelId, runId, null, null, created);
            UpsertStage(conn, tx, c.Hash, Stage.Caption, StageStatus.Done, c.ModelId, runId, null, null, created);
            if (indexUsable) problem = _index.TryIndexImage(conn, tx, c.Hash);
        }, CancellationToken.None).ConfigureAwait(false);

        if (problem is null) return CaptionSaveOutcome.Saved;
        int count = Interlocked.Increment(ref _savedWithoutIndex);
        _index.Log?.Warning($"captions は保存したが、全文検索の索引には入れなかった(画像 {c.Hash}、累計 {count} 枚): {problem}");
        return CaptionSaveOutcome.SavedWithoutIndex;
    }

    /// <summary>索引に入れずに保存した枚数(このインスタンスの寿命の間の累計)。</summary>
    public int SavedWithoutIndexCount => Volatile.Read(ref _savedWithoutIndex);

    /// <summary>
    /// 失敗した 1 枚を記録する: ocr と caption の stage_status を failed(error、settings_json に試した設定の一覧)にする。captions には行を作らない。
    /// 既にある captions と索引(別のモデルの結果)はそのまま。
    /// <para>
    /// <paramref name="settingsJson"/> の形(VlmClient と共通。<see cref="CheckFailureSettings"/> が検証する):
    /// <c>{"attempts":[{"attempt":1,"finish_reason":"length","max_tokens":2048,...},{"attempt":2,...}]}</c>。
    /// <c>attempts</c> は試行の順の配列(1 つ以上)で、各要素は <c>attempt</c>(1 から)・<c>finish_reason</c>(応答が無ければ省略)・
    /// 応答が無かった理由(<c>error</c>)・その試行のリクエストの設定(<c>max_tokens</c>・<c>dry_multiplier</c>・<c>repeat_penalty</c> など)を持つ。
    /// <c>source</c>(例: "s1-import")や <c>run</c> のような補足の項目を、最上位に足してよい。
    /// </para>
    /// <paramref name="ct"/> は始める前にだけ見る(始めたら確定する)。
    /// </summary>
    public Task SaveFailureAsync(long runId, string hash, string modelId, string error, string settingsJson, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        CheckFailureSettings(settingsJson, nameof(settingsJson));
        ct.ThrowIfCancellationRequested();
        string at = DbTime.Format(now);
        return _db.WriteAsync((conn, tx) =>
        {
            foreach (Stage stage in new[] { Stage.Ocr, Stage.Caption })
                UpsertStage(conn, tx, hash, stage, StageStatus.Failed, modelId, runId, settingsJson, error, at);
        }, CancellationToken.None);
    }

    /// <summary>失敗の settings_json の形を確かめる(JSON のオブジェクトで、<c>attempts</c> が 1 つ以上の「<c>attempt</c> が 1 以上の整数のオブジェクト」の配列)。違えば <see cref="ArgumentException"/>。</summary>
    internal static void CheckFailureSettings(string json, string paramName)
    {
        ArgumentNullException.ThrowIfNull(json, paramName);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("失敗の settings_json が JSON のオブジェクトでない", paramName);
            if (!root.TryGetProperty("attempts", out JsonElement attempts) || attempts.ValueKind != JsonValueKind.Array || attempts.GetArrayLength() == 0)
                throw new ArgumentException("失敗の settings_json に、1 つ以上の要素を持つ配列 attempts が無い", paramName);
            foreach (JsonElement a in attempts.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object
                    || !a.TryGetProperty("attempt", out JsonElement n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int v) || v < 1)
                    throw new ArgumentException("失敗の settings_json の attempts の要素が、1 以上の整数 attempt を持つオブジェクトでない", paramName);
            }
        }
        catch (JsonException e)
        {
            throw new ArgumentException("失敗の settings_json が JSON として読めない: " + e.Message, paramName, e);
        }
    }

    private static void Validate(CaptionResult c)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(c.Hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.ModelId);
        ArgumentNullException.ThrowIfNull(c.Ocr);
        ArgumentNullException.ThrowIfNull(c.Description);
        ArgumentNullException.ThrowIfNull(c.Tags);
        if (c.Tags.Any(t => t is null)) throw new ArgumentException("tags に null がある", nameof(c));
        if (!string.Equals(c.FinishReason, "stop", StringComparison.Ordinal))
            throw new ArgumentException($"finish_reason が stop でない結果は成功として保存しない(実際「{c.FinishReason}」)。失敗として SaveFailureAsync で記録する。", nameof(c));
        if (c.Attempt < 1) throw new ArgumentOutOfRangeException(nameof(c), c.Attempt, "Attempt は 1 以上");
        if (c.PromptTokens is < 0) throw new ArgumentOutOfRangeException(nameof(c), c.PromptTokens, "PromptTokens は 0 以上");
        CheckJsonObject(c.SettingsJson, nameof(c));
    }

    private static void CheckJsonObject(string json, string paramName)
    {
        ArgumentNullException.ThrowIfNull(json, paramName);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("settings_json が JSON のオブジェクトでない", paramName);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("settings_json が JSON として読めない: " + e.Message, paramName, e);
        }
    }

    private static void UpsertCaption(SqliteConnection conn, SqliteTransaction tx, CaptionResult c, string tagsJson, long runId, string created)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at)
            VALUES ($hash, $model, $ocr, $desc, $tags, $finish, $attempt, $settings, $tokens, $run, $created)
            ON CONFLICT(model_id, hash) DO UPDATE SET
                ocr = excluded.ocr, description = excluded.description, tags_json = excluded.tags_json, finish_reason = excluded.finish_reason,
                attempt = excluded.attempt, settings_json = excluded.settings_json, prompt_tokens = excluded.prompt_tokens,
                run_id = excluded.run_id, created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("$hash", c.Hash);
        cmd.Parameters.AddWithValue("$model", c.ModelId);
        cmd.Parameters.AddWithValue("$ocr", c.Ocr);
        cmd.Parameters.AddWithValue("$desc", c.Description);
        cmd.Parameters.AddWithValue("$tags", tagsJson);
        cmd.Parameters.AddWithValue("$finish", c.FinishReason);
        cmd.Parameters.AddWithValue("$attempt", c.Attempt);
        cmd.Parameters.AddWithValue("$settings", c.SettingsJson);
        cmd.Parameters.AddWithValue("$tokens", (object?)c.PromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$created", created);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStage(
        SqliteConnection conn, SqliteTransaction tx, string hash, Stage stage, StageStatus status, string modelId, long runId,
        string? settingsJson, string? error, string now)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at)
            VALUES ($hash, $stage, $status, $model, $run, $settings, $error, 1, $now)
            ON CONFLICT(hash, stage) DO UPDATE SET
                status = excluded.status, model_id = excluded.model_id, run_id = excluded.run_id,
                settings_json = excluded.settings_json, error = excluded.error, attempts = stage_status.attempts + 1, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(stage));
        cmd.Parameters.AddWithValue("$status", DbEnums.ToDb(status));
        cmd.Parameters.AddWithValue("$model", modelId);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$settings", (object?)settingsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 読み出し(すべて接続を受ける。読み取り用の短命の接続でよい)

    private const string SelectColumns = "hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at";

    /// <summary>(hash, model_id) の caption。無ければ null。tags_json が配列でなければ <see cref="InvalidDataException"/>。</summary>
    public static CaptionRow? Find(SqliteConnection conn, string hash, string modelId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM captions WHERE model_id = $m AND hash = $h";
        cmd.Parameters.AddWithValue("$m", modelId);
        cmd.Parameters.AddWithValue("$h", hash);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    /// <summary>画像の最新の caption(索引文書の元と同じ選び方: created_at が最大、同じなら rowid が大きいほう)。無ければ null。</summary>
    public static CaptionRow? FindLatest(SqliteConnection conn, string hash)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM captions WHERE hash = $h ORDER BY {FtsSpec.LatestCaptionOrderBy} LIMIT 1";
        cmd.Parameters.AddWithValue("$h", hash);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    /// <summary>
    /// 複数の画像の最新の caption を、<see cref="ReadChunk"/> 件ずつまとめて引く(<c>TextDetail</c> 用)。caption が無い画像は結果に入らない。
    /// 同じハッシュが重なっていても 1 回だけ引く。
    /// </summary>
    public static Dictionary<string, CaptionRow> FindLatestMany(SqliteConnection conn, IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var result = new Dictionary<string, CaptionRow>(StringComparer.Ordinal);
        foreach (string[] chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(ReadChunk))
        {
            using SqliteCommand cmd = conn.CreateCommand();
            var names = new string[chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                names[i] = "$h" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            cmd.CommandText = $"""
                SELECT {SelectColumns} FROM captions c
                WHERE c.hash IN ({string.Join(",", names)})
                  AND c.rowid = {FtsSpec.LatestCaptionRowid("c")}
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                CaptionRow row = ReadRow(r);
                result[row.Hash] = row;
            }
        }
        return result;
    }

    private static CaptionRow ReadRow(SqliteDataReader r)
    {
        string hash = r.GetString(0);
        return new CaptionRow(
            hash, r.GetString(1), r.GetString(2), r.GetString(3), FtsIndex.ParseTags(hash, r.GetString(4)),
            r.GetString(5), (int)r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : (int)r.GetInt64(8), r.GetInt64(9), r.GetString(10));
    }
}
