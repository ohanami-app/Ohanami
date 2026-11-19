using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.Embedding;
using Ohanami.Core.FullText;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Native;
using Ohanami.Core.Scanning;
using Ohanami.Core.Search;
using Ohanami.Core.Tags;
using Ohanami.Core.Captions;
using Ohanami.Core.Power;
using Ohanami.Core.Vlm;

namespace Ohanami.Core;

/// <summary>Core を開くときの設定。パスは App が決めて渡す(Core は置き場所を持たない)。</summary>
/// <param name="DatabasePath">例: %LOCALAPPDATA%\Ohanami\ohanami.db</param>
/// <param name="ThumbnailDirectory">例: %LOCALAPPDATA%\Ohanami\thumbs</param>
/// <param name="ModelDirectory">モデルの置き場(開発中は repo の models/ViT-SO400M-16-SigLIP2-384__ohanami)</param>
/// <param name="NativeDirectory">通常は AppContext.BaseDirectory(ORT と CUDA の DLL があるところ)</param>
/// <param name="Log">診断ログの出し先(App が実装する)</param>
/// <param name="WatchFolders">true(既定)なら、登録したフォルダを監視し、定期・復帰の走査もする(<see cref="FolderWatcher"/>)。false は、走査のタイミングを自分で決めたいテスト・ツール用。</param>
/// <param name="Vlm">VLM(llama-server とモデル)の場所。null なら VLM のジョブは無い(<see cref="JobKind.Vlm"/> を指定できない)。Core を開くときには何も検証・起動しない</param>
public sealed record CoreOptions(
    string DatabasePath,
    string ThumbnailDirectory,
    string ModelDirectory,
    string NativeDirectory,
    ICoreLog Log,
    VlmOptions? Vlm = null,
    bool WatchFolders = true);

/// <summary>
/// VLM の場所。<paramref name="LlamaServerPath"/> は llama-server.exe(開発中は repo の <c>runtime/llama.cpp/b12110/llama-server.exe</c>)、
/// <paramref name="ModelDirectory"/> は GGUF と mmproj のあるディレクトリ(開発中は <c>models/Qwen3-VL-8B-Instruct-GGUF</c>)。
/// 検証と起動は、最初に VLM のジョブが必要になったとき(<c>VlmRunner</c> の実行の頭)に行う(検索だけのとき、6GB を読まない)。
/// </summary>
public sealed record VlmOptions(string LlamaServerPath, string ModelDirectory)
{
    /// <summary>models/ の下の VLM のディレクトリ名。App が開発中の置き場を探すときに使う(<c>models/&lt;この名前&gt;</c>)。</summary>
    public const string ModelDirectoryName = VlmCatalog.DirectoryName;

    /// <summary>llama-server のビルドのディレクトリ名(<c>runtime/llama.cpp/&lt;この名前&gt;/llama-server.exe</c>)。</summary>
    public const string RuntimeBuildDirectoryName = "b12110";
}

public enum CorePhase { Starting, Ready, Failed }

/// <summary>
/// Ready = 検索に要るもの(テキスト側のモデルの検証、トークナイザ、テキスト側の CPU セッション)がそろった状態。GPU も画像側のモデルも要らない
/// (ゲーム中に検索だけ使える)。ベクトルの読み込みは別の印(<see cref="VectorsLoaded"/>。読み込み中の検索は「準備中」の経路として返る)。
/// 画像側のモデルと GPU の状態は <see cref="IndexState.Engine"/> に出る。<see cref="VectorCount"/> はその時点のメモリ上のベクトルの数。
/// </summary>
public sealed record CoreStatus(CorePhase Phase, string? Reason, bool VectorsLoaded, int VectorCount);

/// <summary>
/// App が呼ぶ窓口。<see cref="OpenAsync(CoreOptions, CancellationToken)"/> が、明示的に組み立てる(DI コンテナもリフレクションも使わない):
/// <list type="number">
/// <item><c>NativeRuntime.Configure</c>(DLL の検索パス。最初に 1 度)→ DB を開く(スキーマの版の検査。ここで失敗すれば例外で止まる)。</item>
/// <item>裏で: テキスト側のモデルの SHA-256 検証(Ready の条件)→ 画像側の検証(続けて。画像エンコーダを作る時点で参照する)。
///   テキスト側の検証のあと: トークナイザの読み込みとテキスト側の CPU セッションの作成(並行)→ models への登録 → Ready。
///   ベクトルの読み込み(DB → メモリ)は DB を開いてすぐ始める。</item>
/// <item>GPU のセッションはここでは作らない(最初の埋め込みの直前に作る。<see cref="Indexing.Indexer"/>)。</item>
/// </list>
/// 検証や読み込みの失敗は <see cref="CoreStatus"/> の Failed(理由つき)で伝える(<see cref="StatusChanged"/>)。このとき検索は動かないが、フォルダの登録などは使える。
/// 画像側の検証に失敗すると、検索は使えるまま、埋め込み(<see cref="Indexer"/>)が Run = Failed になる(画像エンコーダを作る時点で例外)。
/// 破棄(<see cref="DisposeAsync"/>)は、実行中のジョブのキャンセル → 完了待ち → セッションの Dispose → DB を閉じる、の順。実行中の検索がないことは呼び出し側が保つ。
/// <para>スレッドと async の約束(Core 全体): 公開の窓口は、どのスレッドからでも呼べるように作る。時間のかかるものは <see cref="Task"/> を返し、<see cref="CancellationToken"/> を受ける。
/// <c>async void</c> は使わない。Core の内部の <c>await</c> は <c>ConfigureAwait(false)</c> で、UI のスレッドへの戻しは App が決める。
/// DB は、書き込みを 1 本の接続の直列化で、読み取りを用途ごとの短命の接続で行う(<c>OhanamiDatabase</c>)。</para>
/// </summary>
public sealed class OhanamiCore : IAsyncDisposable
{
    private readonly CoreOptions _options;
    private readonly OhanamiDatabase _db;
    private readonly TimeProvider _time;
    private readonly EmbeddingModelSpec _model;
    private readonly VectorTable _vectors;
    private readonly FtsIndex _ftsIndex;
    private readonly ChildProcessGuard? _vlmGuard;
    private readonly PowerEvents? _power;
    private readonly FolderWatcher? _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _imageVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Siglip2Tokenizer> _tokenizer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Siglip2TextEncoder> _textEncoder = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _statusLock = new();
    private CorePhase _phase = CorePhase.Starting;
    private string? _reason;
    private bool _vectorsLoaded;
    private Task _startup = Task.CompletedTask;
    private Task _vectorLoad = Task.CompletedTask;
    private Task _ftsEnsure = Task.CompletedTask;
    private int _disposed;

    public FolderCatalog Folders { get; }

    public Indexer Indexer { get; }

    public SearchService Search { get; }

    /// <summary>「監視できない」になっているフォルダ(id → 理由。定期の走査だけになる)。監視しない設定(<see cref="CoreOptions.WatchFolders"/> が false)なら空。</summary>
    public IReadOnlyDictionary<long, string> UnwatchedFolders => _watcher?.UnavailableFolders ?? new Dictionary<long, string>();

    /// <summary><see cref="UnwatchedFolders"/> が変わったとき(監視を作れた・作れなかった・外した)。呼ぶスレッドは決まっていない。</summary>
    public event Action? FolderWatchChanged;

    /// <summary>手動タグ(画像に付ける・外す、候補)。付け外しは全文検索の索引も同じトランザクションで更新する。</summary>
    public ManualTags Tags { get; }

    /// <summary>画像 1 枚の詳細と、失敗の一覧の読み出し(読むだけ)。</summary>
    public ImageCatalog Images { get; }

    /// <summary>
    /// 画像 1 枚の詳細(詳細画面の元)。<see cref="Images"/> の <see cref="ImageCatalog.GetDetailAsync"/> と同じ。
    /// 一致の情報は検索の結果(<c>SearchResult.Hits</c>)にあり、ここには入れない。画像が無ければ null。
    /// </summary>
    public Task<ImageDetail?> GetImageDetailAsync(string hash, CancellationToken ct = default) => Images.GetDetailAsync(hash, ct);

    /// <summary>サムネイルの画素(WPF の型を含まない)。ディスクキャッシュは <see cref="CoreOptions.ThumbnailDirectory"/>。</summary>
    public ThumbnailService Thumbnails { get; }

    /// <summary>models/ の下の、製品が使うモデルのディレクトリ名。App が開発中のモデルの置き場を探すときに使う(<c>models/&lt;この名前&gt;</c>)。</summary>
    public const string ModelDirectoryName = ModelCatalog.DirectoryName;

    /// <summary>
    /// 製品が使っているモデルの、画面に出す名前(特徴ベクトル化のモデル・VLM)。**定義(<c>ModelCatalog</c>・<c>VlmCatalog</c>)のそばの定数**から作る。App は、この値を差し込むだけにして、
    /// 名前を書き写さない(モデルを替えても嘘にならないように。ADR 0038)。
    /// </summary>
    public static ModelLabels ModelLabels { get; } = new(ModelCatalog.DisplayName, VlmCatalog.DisplayName);

    private OhanamiCore(OhanamiDatabase db, CoreOptions options, TimeProvider time)
    {
        _db = db;
        _options = options;
        _time = time;
        _model = ModelCatalog.Current;
        _vectors = new VectorTable(_model.ModelId, _model.Dim);
        _ftsIndex = new FtsIndex(db, options.Log);

        // 失敗した Task の例外は、誰かが待つとは限らない(待たれなくても未観測の例外にしない)
        Observe(_imageVerified.Task);
        Observe(_tokenizer.Task);
        Observe(_textEncoder.Task);

        Folders = new FolderCatalog(db, time, afterChange: OnFoldersChanged);
        Tags = new ManualTags(db, _ftsIndex);
        // スリープからの復帰の通知。フォルダの監視(復帰で全部の監視を作り直して走査する)と VLM(llama-server に「疑わしい」の印)の両方が使うので、VLM の設定の外で作る
        _power = new PowerEvents();
        if (!_power.Registered) options.Log.Warning("スリープからの復帰の通知を登録できない(通知なしで動く。VLM は HTTP の失敗でも復旧する。監視は定期の走査が拾う): " + _power.RegistrationError);
        // 段階ごとの「現行の model_id」(Done と Outdated の分かれ目)。ランナーと同じ値(埋め込みは ModelCatalog、OCR と説明文は VLM のカタログ)
        Images = new ImageCatalog(db, new Dictionary<Stage, string>
        {
            [Stage.Embed] = _model.ModelId,
            [Stage.Ocr] = VlmCatalog.Current.ModelId,
            [Stage.Caption] = VlmCatalog.Current.ModelId,
        });
        var scanner = new Scanner(db, time);
        var factory = new Siglip2ImageEmbedderFactory(options.ModelDirectory, _model, ModelCatalog.Name, options.NativeDirectory, _imageVerified.Task);
        var embed = new EmbedRunner(db, factory, new SkiaImagePreprocessor(), time, options.Log, OnEmbedded);
        var runners = new List<JobRunner> { embed };
        if (options.Vlm is { } vlm)
        {
            // VLM のジョブ(llama-server)。ここでは組み立てるだけ。検証・起動は、最初に VLM のジョブが必要になったとき(VlmRunner の実行の頭)
            string dbDirectory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
            _vlmGuard = new ChildProcessGuard(Path.Combine(dbDirectory, "llama-server.pid"), options.Log);
            var captions = new CaptionStore(db, _ftsIndex);
            runners.Add(new VlmRunner(db, VlmCatalog.Current, VlmCatalog.Name, VlmRunnerSettings.ForProduct(vlm, Path.Combine(dbDirectory, "logs")),
                _vlmGuard, new VlmVerifier(), captions, time, options.Log, _power));
        }
        Indexer = new Indexer(db, scanner, runners, time, RuntimeInfo.AppVersion(), options.Log,
            purger: new MissingImagePurger(db, _ftsIndex, hashes => { foreach (string hash in hashes) _vectors.Remove(hash); }));
        if (options.WatchFolders)
        {
            _watcher = new FolderWatcher(() => Folders.List(), (ids, trigger, ct) => Indexer.ScanAsync(ids, trigger, ct), time, options.Log);
            _watcher.StatusChanged += RaiseFolderWatchChanged;
            _power.Resumed += _watcher.OnResumed;
        }

        QueryEmbedder queryEmbedder = async (text, ct) =>
        {
            Siglip2TextEncoder encoder = await _textEncoder.Task.WaitAsync(ct).ConfigureAwait(false);
            return await VisualPath.TextEncoderEmbedder(encoder, _tokenizer.Task)(text, ct).ConfigureAwait(false);
        };
        Search = new SearchService(db, [new VisualPath(_vectors, queryEmbedder, HubnessCorrection.Default), new FullTextPath(db, _ftsIndex, options.Log)], _vectors);
        Thumbnails = new ThumbnailService(db, options.ThumbnailDirectory);
    }

    /// <summary>
    /// DB を開き(スキーマの版を検査する。知らない版・DB でないファイルは例外で止まる)、裏の起動(モデルの検証・テキスト側・ベクトルの読み込み)を始めて、すぐ戻る。
    /// <c>NativeRuntime.Configure</c> を最初に呼ぶ(失敗なら <see cref="NativeRuntimeException"/>)。
    /// </summary>
    public static Task<OhanamiCore> OpenAsync(CoreOptions options, CancellationToken ct = default) =>
        OpenAsync(options, configureNative: true, time: null, ct);

    /// <summary>テスト用: Configure(プロセス全体の設定)を行わない / TimeProvider を替えられる。</summary>
    internal static async Task<OhanamiCore> OpenAsync(CoreOptions options, bool configureNative, TimeProvider? time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ThumbnailDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.NativeDirectory);
        ArgumentNullException.ThrowIfNull(options.Log);

        if (configureNative) NativeRuntime.Configure(options.NativeDirectory);

        OhanamiDatabase db = await Task.Run(() => OhanamiDatabase.Open(options.DatabasePath), ct).ConfigureAwait(false);
        OhanamiCore core;
        try
        {
            core = new OhanamiCore(db, options, time ?? TimeProvider.System);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        core.SweepOrphanedLlamaServer();
        core.StartFolderWatching();
        core._vectorLoad = core.LoadVectorsAsync();
        core._ftsEnsure = core.EnsureFullTextIndexAsync();
        core._startup = core.StartupAsync();
        return core;
    }

    /// <summary>
    /// 前回の llama-server が孤児として残っていれば掃除する(ジョブオブジェクトが効かなかった場合の保険。ADR 0026)。DB を開いたあと
    /// (= 単一インスタンスを通過したあと)に行う。2 つ目のプロセスが 1 つ目の llama-server を殺さないため。VLM を使わない設定では何もしない。
    /// </summary>
    private void SweepOrphanedLlamaServer()
    {
        if (_vlmGuard is null) return;
        try
        {
            SweepOutcome outcome = _vlmGuard.SweepOrphans();
            if (outcome != SweepOutcome.NoFile) _options.Log.Info("前回の llama-server の掃除: " + outcome);
        }
        catch (Exception e)
        {
            _options.Log.Warning("前回の llama-server の掃除に失敗した(続ける): " + e.Message);
        }
    }

    /// <summary>登録済みの有効なフォルダの監視を作る(App の起動時の走査より先。走査の間のイベントは、もう一度の走査になるだけ)。作れなかったフォルダは「監視できない」になる。</summary>
    private void StartFolderWatching()
    {
        if (_watcher is null) return;
        try { _watcher.Refresh(); }
        catch (Exception e) { _options.Log.Error("フォルダの監視を始められなかった(定期の走査も動かない): " + e.Message, e); }
    }

    /// <summary>フォルダの登録・解除・有効/無効のあと(<see cref="FolderCatalog"/> から)。監視の集合を合わせる。</summary>
    private void OnFoldersChanged()
    {
        if (_watcher is null) return;
        try { _watcher.Refresh(); }
        catch (Exception e) { _options.Log.Error("フォルダの操作のあとの監視の更新に失敗した: " + e.Message, e); }
    }

    private void RaiseFolderWatchChanged()
    {
        Action? handler = FolderWatchChanged;
        if (handler is null) return;
        try { handler(); }
        catch (Exception e) { _options.Log.Error("FolderWatchChanged の購読者が例外を投げた: " + e.Message, e); }
    }

    /// <summary>テスト用: DB への窓口。</summary>
    internal OhanamiDatabase DatabaseForTests => _db;

    /// <summary>テスト用: フォルダの監視(<see cref="CoreOptions.WatchFolders"/> が false なら null)。</summary>
    internal FolderWatcher? FolderWatcherForTests => _watcher;

    /// <summary>テスト用: スリープからの復帰の通知(VLM の設定の有無によらず作る)。</summary>
    internal PowerEvents? PowerEventsForTests => _power;

    public CoreStatus Status
    {
        get
        {
            lock (_statusLock) return new CoreStatus(_phase, _reason, _vectorsLoaded, _vectors.Count);
        }
    }

    public event Action<CoreStatus>? StatusChanged;

    /// <summary>起動の流れ(モデルの検証・テキスト側の用意)が終わる(Ready か Failed になる)のを待つ。App の起動処理とテスト用。</summary>
    public async Task<CoreStatus> WaitUntilStartedAsync(CancellationToken ct = default)
    {
        await _startup.WaitAsync(ct).ConfigureAwait(false);
        return Status;
    }

    // ------------------------------------------------------------------ 起動の流れ(裏)

    private async Task StartupAsync()
    {
        CancellationToken ct = _lifetime.Token;
        ICoreLog log = _options.Log;
        try
        {
            // 1. テキスト側の SHA-256 の検証(Ready の条件)。不一致・無いファイルは ModelMismatchException
            await ModelVerifier.VerifyTextSideAsync(_options.ModelDirectory, _model, ct).ConfigureAwait(false);

            // 2. 画像側の検証は続けて裏で行い、結果は画像エンコーダを作る時点で参照する(Ready は待たせない)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ModelVerifier.VerifyImageSideAsync(_options.ModelDirectory, _model, ct).ConfigureAwait(false);
                    _imageVerified.TrySetResult();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _imageVerified.TrySetCanceled(ct); }
                catch (Exception e)
                {
                    log.Error("画像側のモデルの検証に失敗した(検索は使える。埋め込みは Failed になる): " + e.Message, e);
                    _imageVerified.TrySetException(e);
                }
            }, CancellationToken.None);

            // 3. トークナイザの読み込みとテキスト側の CPU セッションの作成(並行)
            string tokenizerPath = ModelVerifier.PathOf(_options.ModelDirectory, _model.File(ModelRole.Tokenizer));
            Task<Siglip2Tokenizer> tokenizerTask = Task.Run(() => Siglip2Tokenizer.Load(tokenizerPath), ct);
            Task<Siglip2TextEncoder> encoderTask = Task.Run(() => Siglip2TextEncoder.Create(_options.ModelDirectory, _model), ct);
            Siglip2Tokenizer tokenizer = await tokenizerTask.ConfigureAwait(false);
            _tokenizer.TrySetResult(tokenizer);
            Siglip2TextEncoder encoder = await encoderTask.ConfigureAwait(false);
            if (!_textEncoder.TrySetResult(encoder)) encoder.Dispose(); // 破棄と競合した

            // 4. models への登録(識別と kind が違う行は ModelMismatchException)。名前・パス・サイズだけ違えば更新して記録する
            ModelRegistration registration = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(
                _db, ModelCatalog.Name, _model, _time.GetUtcNow(), ct).ConfigureAwait(false);
            if (registration.Outcome == RegistrationOutcome.Updated)
                _options.Log.Info("models の記録を更新した(model_id は同じ): " + string.Join(" / ", registration.Changes));

            SetPhase(CorePhase.Ready, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _tokenizer.TrySetCanceled(ct);
            _textEncoder.TrySetCanceled(ct);
            _imageVerified.TrySetCanceled(ct);
        }
        catch (Exception e)
        {
            _options.Log.Error("起動に失敗した: " + e.Message, e);
            _tokenizer.TrySetException(e);
            _textEncoder.TrySetException(e);
            _imageVerified.TrySetException(e);
            SetPhase(CorePhase.Failed, e.Message);
        }
    }

    private async Task LoadVectorsAsync()
    {
        try
        {
            await _vectors.LoadAsync(_db, _lifetime.Token).ConfigureAwait(false);
            lock (_statusLock) _vectorsLoaded = true;
            RaiseStatusChanged();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 破棄された
        }
        catch (Exception e)
        {
            _options.Log.Error("ベクトルの読み込みに失敗した: " + e.Message, e);
            SetPhase(CorePhase.Failed, "ベクトルの読み込みに失敗した: " + e.Message);
        }
    }

    /// <summary>
    /// 全文検索の索引の規約を、裏で照合する(違えば captions から作り直す。ADR 0030)。<see cref="OpenAsync(CoreOptions, CancellationToken)"/> を待たせない。
    /// 作り直している間、全文の経路は NotReady(見た目の経路は使える)。失敗しても Core は止めず、全文の経路がこの検索の失敗を返す(理由はログと <see cref="FtsIndex.FailureMessage"/>)。
    /// </summary>
    private async Task EnsureFullTextIndexAsync()
    {
        try
        {
            // 破棄でも途中で止めず、作り直しが終わるのを待たせる(書き込みの途中で DB を閉じない。作り直しは 1 万件で数秒)
            await _ftsIndex.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // FtsIndex がログに書き、State = Failed にしてある(全文の経路が失敗として伝える)
        }
    }

    /// <summary>テストと、VLM の結果の保存口が使う: 全文検索の索引(DB 層)。</summary>
    internal FtsIndex FullTextIndex => _ftsIndex;

    /// <summary>埋め込みが 1 枚進むたびに、メモリ上のベクトルの表に足す(検索がすぐ使う)。表の書式検査に通らなければ例外=ジョブが Failed。</summary>
    private void OnEmbedded(string hash, float[] vector) => _vectors.Add(hash, vector);

    // ------------------------------------------------------------------ 状態

    private void SetPhase(CorePhase phase, string? reason)
    {
        lock (_statusLock)
        {
            // Failed は上書きしない(最初の失敗の理由を残す)
            if (_phase == CorePhase.Failed) return;
            _phase = phase;
            _reason = reason;
        }
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        Action<CoreStatus>? handler = StatusChanged;
        if (handler is null) return;
        try { handler(Status); }
        catch (Exception e) { _options.Log.Error("StatusChanged の購読者が例外を投げた: " + e.Message, e); }
    }

    private static void Observe(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // ------------------------------------------------------------------ 破棄

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _lifetime.Cancel();
        if (_watcher is not null) await _watcher.StopAsync().ConfigureAwait(false); // 監視と、監視が要求した走査を先に止める(走査が止まってから Indexer を止める)
        await Indexer.StopAsync().ConfigureAwait(false); // 実行中のジョブと走査をキャンセルし、完了を待ち、GPU のセッションを Dispose する
        await Thumbnails.StopAsync().ConfigureAwait(false); // 進行中のサムネイルの仕事を待つ(DB を閉じたあとに触らせない)
        try { await _startup.ConfigureAwait(false); } catch (Exception) { /* 起動の失敗は Status で伝えてある */ }
        try { await _vectorLoad.ConfigureAwait(false); } catch (Exception) { }
        try { await _ftsEnsure.ConfigureAwait(false); } catch (Exception) { } // 作り直しの書き込みが済んでから DB を閉じる
        if (_textEncoder.Task.IsCompletedSuccessfully) _textEncoder.Task.Result.Dispose();
        _power?.Dispose();
        _vlmGuard?.Dispose();   // ジョブを閉じる(残っている llama-server があれば OS が終わらせる。通常は、上の Indexer.StopAsync で kill 済み)
        _db.Dispose();
        _lifetime.Dispose();
    }
}
