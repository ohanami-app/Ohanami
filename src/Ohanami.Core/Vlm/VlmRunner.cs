using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Power;

namespace Ohanami.Core.Vlm;

/// <summary>
/// VLM のジョブの実行の指定(パス・照合する値・時間)。製品の値は <see cref="ForProduct"/>(<see cref="VlmCatalog"/> の定数)。
/// テストでは、偽の llama-server の exe・小さなモデルのファイルに替える。
/// </summary>
/// <param name="ServerExePath">llama-server.exe。</param>
/// <param name="ModelDirectory">GGUF と mmproj のあるディレクトリ(<see cref="VlmSpec"/> の相対パスの起点)。</param>
/// <param name="LogDirectory"><c>llama-server-*.log</c> を置くディレクトリ(DB のディレクトリの下の <c>logs</c>)。</param>
/// <param name="ExpectedExeSha256">llama-server.exe の SHA-256。</param>
/// <param name="ExpectedBuild"><c>--version</c> の build。</param>
/// <param name="ExpectedCommit"><c>--version</c> の commit。</param>
/// <param name="HealthTimeout">/health が 200 になるまでの上限。</param>
/// <param name="RequestTimeout">1 リクエストの上限(これを超えたら llama-server の異常として、kill → 起動し直す)。</param>
/// <param name="ServerEnvironment">子に足す環境変数(テストで偽サーバの動きを指定する)。</param>
/// <param name="VersionTimeout"><c>--version</c> の上限。null なら 60 秒。</param>
internal sealed record VlmRunnerSettings(
    string ServerExePath,
    string ModelDirectory,
    string LogDirectory,
    string ExpectedExeSha256,
    int ExpectedBuild,
    string ExpectedCommit,
    TimeSpan HealthTimeout,
    TimeSpan RequestTimeout,
    IReadOnlyDictionary<string, string>? ServerEnvironment = null,
    TimeSpan? VersionTimeout = null)
{
    /// <summary>製品の値(<see cref="VlmCatalog"/> の定数。起動待ち・リクエストのタイムアウトは、どちらも 300 秒。それぞれ ADR 0019・0025)。</summary>
    public static VlmRunnerSettings ForProduct(VlmOptions options, string logDirectory) => new(
        options.LlamaServerPath, options.ModelDirectory, logDirectory,
        VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit,
        LlamaServerLaunch.DefaultHealthTimeout, VlmClient.DefaultRequestTimeout);
}

/// <summary>
/// VLM(Qwen3-VL。画像 1 枚の ocr・説明文・tags)のジョブ(ADR 0027、0028)。<see cref="JobRunner"/> の契約で、<see cref="Indexer"/> が動かす。
/// <list type="number">
/// <item><b>起動</b>(<see cref="RunAsync"/> の頭。Core を開くときには何も検証・起動しない): GGUF・mmproj・llama-server.exe の検証(<see cref="VlmVerifier"/>。同じプロセスの中で 2 回目以降は省略)
/// → models への登録 → llama-server の起動(<see cref="LlamaServer"/>。ジョブオブジェクトと pid ファイルに登録される)→ <c>runs</c> の行(<see cref="RunRecorder.StartAsync"/>。
/// <b>サーバが health になったあと・最初の書き込みの前</b>。runtime_json にビルド・引数・pid・ポート)。</item>
/// <item><b>待ち行列は毎回 DB から</b>: 現存するパスがあり、stage_status('caption')の行が無い画像を、<b>新しい順</b>(現存するパスの mtime の最大の降順、同じなら hash)に 64 件ずつ。
/// 処理した画像は条件から外れるのでオフセットは使わない。<b>この実行で飛ばした画像の hash の集合</b>を持ち、次の問い合わせから除く(ファイルを開けない画像は記録しないので、除かないと同じ 64 件を取り続けて止まらない)。</item>
/// <item><b>画像 1 枚ごと</b>: 境目(次の 1 枚を取る前)で <c>ct</c> と <c>shouldYield</c> を見る(1 枚の途中では shouldYield を見ない=今の 1 枚は確定させる)。<c>ct</c> が処理中に切れたら
/// HTTP を中断して Cancelled(その 1 枚は未処理のまま。stage_status を書かない)。ファイルは読んだバイト列の SHA-256 を DB のハッシュと照合して使う(違えば次のパス。使えるパスが無ければ飛ばす)。
/// 結果は 1 トランザクションで確定する(<see cref="CaptionStore"/>)。</item>
/// <item><b>サーバの異常</b>(<see cref="VlmOutcome.ServerFailure"/>): llama-server を kill → 起動し直し → <b>同じ画像をやり直す</b>。同じ画像で 2 回続けて起きたら、その画像を failed
/// (error = <c>llama-server が異常終了した</c>)にして先へ進む(その画像が原因でサーバを落とす場合に、無限に再起動しない)。再起動そのものが失敗したら致命的(例外 = Run が Failed)。
/// <b>この「サーバの異常で failed」が <see cref="MaxConsecutiveServerFailedImages"/>(3)枚続いたら</b>、原因は環境(GPU が他で使われている・ドライバ・VRAM 不足)とみて、<see cref="LlamaServerException"/> で止める
/// (全画像を failed にし続けない)。成功、またはサーバが答えた失敗(画像の失敗)で 0 に戻る。
/// スリープからの復帰(<see cref="PowerEvents"/>)で「疑わしい」の印が付いたら、次の画像の前に <c>/health</c> を確かめ、失敗なら kill → 起動し直す。画像の前にサーバが終わっていたときも同じ。</item>
/// <item><see cref="ReleaseAsync"/>: llama-server を kill する(VRAM 8GB に llama-server と ORT は同時に載らない。埋め込みへ切り替える前・一時停止・終了)。再開は新しい <c>runs</c> の行になる。</item>
/// </list>
/// 致命的: 検証の不一致(<see cref="ModelMismatchException"/>)、起動の失敗(<see cref="LlamaServerException"/>)、設定の誤り(<see cref="VlmProtocolException"/>)、想定外の例外。
/// </summary>
internal sealed class VlmRunner : JobRunner
{
    /// <summary>
    /// 待ち行列を 1 度に取る枚数。使い切るまで待ちを取り直さないので、新着(新しい順の先頭に入る)が処理に入るまで最大 ChunkSize × 約 12 秒かかる。
    /// 8 枚なら最大約 1.6 分(64 枚だと最大約 13 分)。待ちの問い合わせは軽い。ADR 0027。
    /// </summary>
    private const int ChunkSize = 8;

    /// <summary>サーバの異常で failed になった画像が、この枚数だけ続いたら、画像のせいではなく環境の異常とみて、ジョブを止める(<see cref="LlamaServerException"/>)。</summary>
    internal const int MaxConsecutiveServerFailedImages = 3;

    private readonly OhanamiDatabase _db;
    private readonly VlmSpec _spec;
    private readonly string _modelName;
    private readonly VlmRunnerSettings _settings;
    private readonly ChildProcessGuard _guard;
    private readonly VlmVerifier _verifier;
    private readonly CaptionStore _captions;
    private readonly TimeProvider _time;
    private readonly ICoreLog _log;
    private readonly IReadOnlyDictionary<Stage, string> _currentModelIds;
    private readonly Dictionary<string, string> _skipped = new(StringComparer.Ordinal);   // この実行で飛ばした画像の hash → 理由(ReleaseAsync で消える)

    private LlamaServer? _server;
    private VlmClient? _client;
    private LlamaServerVersion? _version;
    private bool _registered;
    private int _consecutiveServerFailedImages;   // サーバの異常で failed にした画像が続いた枚数。成功・サーバの答えがあった失敗・ReleaseAsync で 0 に戻る

    public VlmRunner(
        OhanamiDatabase db, VlmSpec spec, string modelName, VlmRunnerSettings settings, ChildProcessGuard guard, VlmVerifier verifier,
        CaptionStore captions, TimeProvider time, ICoreLog log, PowerEvents? power = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _spec = spec;
        _modelName = modelName;
        _settings = settings;
        _guard = guard;
        _verifier = verifier;
        _captions = captions;
        _time = time;
        _log = log;
        _currentModelIds = new Dictionary<Stage, string> { [Stage.Ocr] = spec.ModelId, [Stage.Caption] = spec.ModelId };
        if (power is not null) power.Resumed += OnResumed;
    }

    public override JobKind Kind => JobKind.Vlm;

    public override IReadOnlyList<Stage> Stages { get; } = [Stage.Ocr, Stage.Caption];

    public override IReadOnlyDictionary<Stage, string> CurrentModelIds => _currentModelIds;

    /// <summary>スリープから復帰した(OS のスレッドから)。サーバに「疑わしい」の印を付ける。次の画像の前に、<c>/health</c> で確かめる。</summary>
    private void OnResumed()
    {
        LlamaServer? server = _server;
        if (server is not null) server.Suspect = true;
        // 復帰の通知が届いたことを残す(通知が来たか・来ないかで、復帰後の動きの読み方が変わる。文言は tools の SleepCheckLogic が読む)
        _log.Info("スリープからの復帰の通知を受けた(PowerEvents)。" + (server is null ? "llama-server は動いていない" : "llama-server に「疑わしい」の印を付けた(次の画像の前に /health を確かめる)"));
    }

    // ------------------------------------------------------------------ 実行

    public override async Task<RunEnd> RunAsync(RunRecorder run, Func<bool> shouldYield, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(shouldYield);
        if (ct.IsCancellationRequested) return RunEnd.Cancelled;

        try
        {
            await EnsureServerAsync(run, ct).ConfigureAwait(false);
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

        while (true)
        {
            if (ct.IsCancellationRequested) return RunEnd.Cancelled;
            if (shouldYield()) return RunEnd.Yielded;

            List<QueueItem> chunk = FetchQueue();
            if (chunk.Count == 0) return RunEnd.Drained;
            foreach (QueueItem item in chunk)
            {
                // 画像の境目: 次の 1 枚を処理する前に見る(先に取った分は捨てる。次回の待ち行列でもう一度取る)
                if (ct.IsCancellationRequested) return RunEnd.Cancelled;
                if (shouldYield()) return RunEnd.Yielded;
                RunEnd? stop = await ProcessAsync(item, run, ct).ConfigureAwait(false);
                if (stop is not null) return stop.Value;
            }
        }
    }

    public override async Task ReleaseAsync()
    {
        LlamaServer? server = _server;
        VlmClient? client = _client;
        _server = null;
        _client = null;
        _skipped.Clear();
        _consecutiveServerFailedImages = 0;
        client?.Dispose();
        if (server is not null)
        {
            try { await server.StopAsync().ConfigureAwait(false); }
            catch (Exception e) { _log.Warning("llama-server を止めるのに失敗した: " + e.Message); }
        }
    }

    // ------------------------------------------------------------------ サーバの用意

    /// <summary>サーバが無い・終わっていれば、検証して起動する(<c>runs</c> の行は最初の 1 回だけ)。</summary>
    private async Task EnsureServerAsync(RunRecorder run, CancellationToken ct)
    {
        if (_server is { HasExited: false }) return;
        if (_server is not null) await DiscardServerAsync().ConfigureAwait(false);
        await StartServerAsync(run, ct).ConfigureAwait(false);
    }

    private async Task StartServerAsync(RunRecorder run, CancellationToken ct)
    {
        run.ReportEngine(new EngineStatus(EnginePhase.Checking, null, null));
        await _verifier.VerifyModelAsync(_settings.ModelDirectory, _spec, ct).ConfigureAwait(false);
        _version = await _verifier.VerifyLlamaServerAsync(
            _settings.ServerExePath, _settings.ExpectedExeSha256, _settings.ExpectedBuild, _settings.ExpectedCommit, ct, _settings.VersionTimeout).ConfigureAwait(false);
        await EnsureModelRegisteredAsync(ct).ConfigureAwait(false);

        var launch = new LlamaServerLaunch(
            _settings.ServerExePath,
            Path.Combine(_settings.ModelDirectory, _spec.Gguf.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(_settings.ModelDirectory, _spec.Mmproj.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            _spec.ImageMinTokens, _spec.ImageMaxTokens, string.IsNullOrEmpty(_spec.ChatTemplateKwargs) ? null : _spec.ChatTemplateKwargs,
            _settings.LogDirectory, _settings.HealthTimeout, Environment: _settings.ServerEnvironment);
        LlamaServer server = await LlamaServer.StartAsync(launch, _guard, _log, ct).ConfigureAwait(false);
        _server = server;
        _client = new VlmClient(server.BaseUri, _settings.RequestTimeout);
        run.ReportEngine(new EngineStatus(EnginePhase.Ready, null, null));

        if (!run.Started)
        {
            // runs の行: サーバが health になったあと・最初の書き込みの前
            string runtime = RuntimeInfo.VlmRuntimeJson(
                "llama-server", _version.Text, _version.Build, _version.Commit, server.ExePath, _settings.ExpectedExeSha256,
                server.Arguments, server.ProcessId, server.Port, _spec.Gguf.RelativePath, _spec.Mmproj.RelativePath, _spec.ImageMaxTokens);
            await run.StartAsync(runtime, ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureModelRegisteredAsync(CancellationToken ct)
    {
        if (_registered) return;
        VlmModelRegistration registration = await ModelRegistry.EnsureVlmModelRegisteredAsync(_db, _modelName, _spec, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (registration.Outcome == RegistrationOutcome.Updated)
            _log.Info($"models の記録を更新した(model_id は同じ): {string.Join(" / ", registration.Changes)}");
        _registered = true;
    }

    /// <summary>いまのサーバとクライアントを捨てる(kill)。</summary>
    private async Task DiscardServerAsync()
    {
        LlamaServer? server = _server;
        VlmClient? client = _client;
        _server = null;
        _client = null;
        client?.Dispose();
        if (server is not null) await server.StopAsync().ConfigureAwait(false);
    }

    /// <summary>kill → 起動し直す。起動に失敗したら例外(致命的)。</summary>
    private async Task RestartServerAsync(RunRecorder run, string reason, CancellationToken ct)
    {
        _log.Warning("llama-server を kill して起動し直す: " + reason);
        await DiscardServerAsync().ConfigureAwait(false);
        await StartServerAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>画像の前: サーバが終わっていたら起動し直す。「疑わしい」(スリープからの復帰)なら <c>/health</c> を確かめ、失敗なら起動し直す。</summary>
    private async Task EnsureHealthyAsync(RunRecorder run, CancellationToken ct)
    {
        LlamaServer? server = _server;
        if (server is null || server.HasExited)
        {
            await RestartServerAsync(run, "画像の前にサーバが終わっていた", ct).ConfigureAwait(false);
            return;
        }
        if (server.Suspect)
        {
            server.Suspect = false;
            if (!await server.CheckHealthAsync(ct).ConfigureAwait(false))
                await RestartServerAsync(run, "スリープからの復帰のあと、/health に答えない", ct).ConfigureAwait(false);
            else
                _log.Info("スリープからの復帰のあと、llama-server は /health に答えた。そのまま続ける");
        }
    }

    // ------------------------------------------------------------------ 待ち行列

    private sealed record QueueItem(string Hash, ImageFormat Format, string[] Paths);

    private List<QueueItem> FetchQueue()
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        var heads = new List<(string Hash, ImageFormat Format)>();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            // 新しい順 = 現存するパスの mtime の最大の降順(同じなら hash)。飛ばした画像は、後で C# 側で除く(その分だけ多めに取る)
            cmd.CommandText = """
                SELECT i.hash, i.format, MAX(p.mtime_ticks) AS newest
                FROM images i
                JOIN image_paths p ON p.hash = i.hash AND p.missing_since IS NULL
                WHERE NOT EXISTS (SELECT 1 FROM stage_status s WHERE s.hash = i.hash AND s.stage = $stage)
                GROUP BY i.hash
                ORDER BY newest DESC, i.hash
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(Stage.Caption));
            cmd.Parameters.AddWithValue("$limit", ChunkSize + _skipped.Count);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string hash = r.GetString(0);
                if (_skipped.ContainsKey(hash)) continue;
                heads.Add((hash, DbEnums.ImageFormatFromDb(r.GetString(1))));
                if (heads.Count == ChunkSize) break;
            }
        }

        var items = new List<QueueItem>(heads.Count);
        using SqliteCommand pathCmd = conn.CreateCommand();
        pathCmd.CommandText = "SELECT path FROM image_paths WHERE hash = $hash AND missing_since IS NULL ORDER BY path";
        SqliteParameter hashParam = pathCmd.Parameters.Add("$hash", SqliteType.Text);
        foreach ((string hash, ImageFormat format) in heads)
        {
            hashParam.Value = hash;
            var paths = new List<string>();
            using (SqliteDataReader r = pathCmd.ExecuteReader())
                while (r.Read()) paths.Add(r.GetString(0));
            items.Add(new QueueItem(hash, format, paths.ToArray()));
        }
        return items;
    }

    // ------------------------------------------------------------------ 1 枚

    private async Task<RunEnd?> ProcessAsync(QueueItem item, RunRecorder run, CancellationToken ct)
    {
        try
        {
            await EnsureHealthyAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RunEnd.Cancelled;
        }

        (byte[]? bytes, string? usedPath, string? problem) = ReadVerified(item);
        if (bytes is null)
        {
            // ファイルを開けない・内容が走査のときと違う: 記録せず、この実行では飛ばす(次回の Start で再試行。書き換えられていれば次の走査が新しいハッシュで拾う)
            _skipped[item.Hash] = problem ?? "読めない";
            run.ReportSkipped(item.Hash, problem ?? "読めない");
            _log.Warning($"画像を読めない、または内容が走査のときと違うので、この実行では飛ばす(記録しない): {problem}");
            return null;
        }
        run.ReportCurrent(usedPath);

        var serverFailures = new List<string>();
        while (true)
        {
            VlmResult result;
            try
            {
                result = await _client!.CaptionAsync(bytes, item.Format, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return RunEnd.Cancelled;   // その 1 枚は未処理のまま(stage_status を書かない)
            }

            try
            {
                switch (result.Outcome)
                {
                    case VlmOutcome.Ok:
                        await SaveSuccessAsync(run, item.Hash, result, ct).ConfigureAwait(false);
                        _consecutiveServerFailedImages = 0;
                        run.ReportCompleted();
                        return null;

                    case VlmOutcome.Failed:
                        _log.Warning($"{usedPath}: VLM が失敗した: {result.Error}");
                        await _captions.SaveFailureAsync(run.RunId, item.Hash, _spec.ModelId, result.Error ?? "失敗", result.SettingsJson, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                        _consecutiveServerFailedImages = 0;   // サーバは答えた(画像の失敗であって、サーバの異常ではない)
                        run.ReportCompleted(failed: true);
                        return null;

                    default:
                        serverFailures.Add(result.Error ?? "(理由なし)");
                        // kill → 起動し直し(失敗したら致命的)。同じ画像を、1 回だけやり直す
                        await RestartServerAsync(run, $"{usedPath}: {result.Error}", ct).ConfigureAwait(false);
                        if (serverFailures.Count < 2) continue;
                        _log.Warning($"{usedPath}: 同じ画像で llama-server が 2 回続けて異常になった。この画像は failed にして先へ進む");
                        await _captions.SaveFailureAsync(run.RunId, item.Hash, _spec.ModelId,
                            "llama-server が異常終了した(同じ画像で 2 回続けた): " + string.Join(" / ", serverFailures),
                            WithServerFailures(result.SettingsJson, serverFailures), _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                        run.ReportCompleted(failed: true);
                        if (++_consecutiveServerFailedImages >= MaxConsecutiveServerFailedImages) ThrowEnvironmentFailure(run);
                        return null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return RunEnd.Cancelled;   // 索引の準備を待つ間、または起動し直しの途中で切れた。その 1 枚は未処理のまま
            }
        }
    }

    /// <summary>
    /// サーバの異常で failed になった画像が <see cref="MaxConsecutiveServerFailedImages"/> 枚続いた。原因は画像でなく環境(ゲームが GPU を取った、復帰後にドライバが戻らない、
    /// VRAM 不足で起動のたびに落ちる)と見て、全画像を failed にし続けずに止める(Run = Failed。理由が画面に出る)。次の画像は stage_status を書かないまま残る。
    /// </summary>
    private void ThrowEnvironmentFailure(RunRecorder run)
    {
        _consecutiveServerFailedImages = 0;
        string message = $"llama-server が {MaxConsecutiveServerFailedImages} 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない";
        _log.Error(message);
        // 準備(検証・起動)の失敗ではなく、動かしている途中の打ち切り: Engine を Failed にしない(画面が「VLM の準備に失敗」と書いて、同じ文を「直近の失敗」でも出すのを避ける。
        // 理由は例外の文 = Indexer の LastError だけで伝える)
        throw new LlamaServerException(message);
    }

    private async Task SaveSuccessAsync(RunRecorder run, string hash, VlmResult result, CancellationToken ct)
    {
        VlmCaption caption = result.Caption ?? throw new InvalidOperationException("Ok なのに caption が無い");
        var saved = new CaptionResult(hash, _spec.ModelId, caption.Ocr, caption.Description, caption.Tags, result.FinishReason ?? "stop", result.Attempt, result.SettingsJson, result.PromptTokens);
        // 結果の確定は 1 トランザクション(始めた書き込みは確定する。ct は索引の準備を待つ間だけ)。索引に入れられなくても captions と done は書く(ジョブは止めない)
        await _captions.SaveAsync(run.RunId, saved, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>2 回続けて異常だった画像の失敗の settings_json: 2 回目の記録に、各回のサーバの異常の理由を足す。</summary>
    private static string WithServerFailures(string settingsJson, IReadOnlyList<string> failures)
    {
        JsonObject o = (JsonObject)JsonNode.Parse(settingsJson)!;
        o["server_failures"] = new JsonArray(failures.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        return o.ToJsonString();
    }

    /// <summary>
    /// ファイルを読み、<b>読んだバイト列の SHA-256 が DB のハッシュと同じ</b>ものだけを使う(違えば次のパス。走査のあとで書き換えられた内容を、古いハッシュの下に入れない)。
    /// 1 枚 12 秒かかって窓が広いので、EmbedRunner と同じ内容の照合を行う(0.8MB で約 1ms)。
    /// </summary>
    private static (byte[]? Bytes, string? Path, string? Problem) ReadVerified(QueueItem item)
    {
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
            return (read, path, null);
        }
        return (null, null, lastError ?? "現存するパスが無い");
    }

    // ------------------------------------------------------------------ 件数

    public override int CountPendingImages() => PendingQueries.Count(_db, Stage.Caption);

    public override int CountPendingAmong(IReadOnlyCollection<string> hashes) => PendingQueries.CountAmong(_db, Stage.Caption, hashes);

    /// <summary>
    /// GGUF と mmproj のうち、置き場に無いものの名前(存在だけを見る。サイズの違いは、実行の頭の検証が失敗にする)。空でなければ、<see cref="Indexer"/> はこのランナーを選ばない。
    /// 検索のモデルを先にそろえて使い始め、VLM のモデルをあとで取る使い方では、失敗ではなく「未取得」の状態になる。
    /// </summary>
    public override IReadOnlyList<string> UnavailableFiles() =>
        [.. new[] { _spec.Gguf, _spec.Mmproj }
            .Where(f => !File.Exists(Path.Combine(_settings.ModelDirectory, f.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .Select(f => f.RelativePath)];

    public override IReadOnlyList<StageSummary> Summarize()
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        return Stages.Select(stage => SummarizeStage(conn, stage)).ToList();
    }

    private StageSummary SummarizeStage(SqliteConnection conn, Stage stage)
    {
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
        cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(stage));
        cmd.Parameters.AddWithValue("$done", DbEnums.ToDb(StageStatus.Done));
        cmd.Parameters.AddWithValue("$failed", DbEnums.ToDb(StageStatus.Failed));
        cmd.Parameters.AddWithValue("$model", _spec.ModelId);
        using SqliteDataReader r = cmd.ExecuteReader();
        r.Read();
        return new StageSummary(stage, (int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3));
    }
}
