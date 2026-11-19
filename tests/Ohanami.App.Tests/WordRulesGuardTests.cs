using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 語の規則の見張り(ADR 0038): **画面に出す文を作る関数の出力**に、引退させた語と、出さないコードの綴りが無いこと。
/// 文を作る関数を実際に呼んで、出てきた文字列を集めて調べる(ソースを読むのではない。ソースの文字列は、組み立てで割れていて、grep では見落とす)。
/// 集め方: 定数・プロパティは型から拾い(新しい定数も自動で入る)、引数のある関数は、状態の組み合わせを全部回して呼ぶ。
/// <list type="bullet">
/// <item>左側(左パネル・インデックスの状態の窓・通知・進捗の文・<c>UiText.Plain</c>)は、**1 つでも出たら落ちる**。</item>
/// <item>右側(検索の見出し・詳細・一覧のツールチップ・状態のバー)の残りは <see cref="KnownRightSideLeftovers"/> に、出どころと語を書く。
///   いまは空。新しい残りが出たら、まず文を直す。足したものを直したら、この一覧から消す(一覧と実際が食い違うと落ちる。消し忘れが残らない)。</item>
/// </list>
/// </summary>
public sealed class WordRulesGuardTests
{
    /// <summary>画面の語ではない言葉(引退させた語。ADR 0038)。</summary>
    private static readonly string[] RetiredWords =
        ["画像検索", "見た目が近い", "準備完了", "全文検索", "埋め込み", "索引", "ジョブ", "未処理", "要再処理", "走査"];

    /// <summary>画面に出さないコードの綴り(経路の ID・数式の名前・出どころの印)。大文字小文字は区別しない。</summary>
    private static readonly string[] CodeSpellings = ["visual", "fulltext", "cosine", "Core:"];

    /// <summary>
    /// 右側の既知の残り(出どころ|語)。全部直したので、空。新しい残りが出たら、ここに足すより先に、文を直す
    /// (一覧と実際が食い違うと落ちる作りは、足したものを直したとき、消し忘れが残らないためのもの)。
    /// </summary>
    private static readonly HashSet<string> KnownRightSideLeftovers = new(StringComparer.Ordinal);
    // ---------------------------------------------------------------- 集める

    private sealed class Corpus
    {
        public List<(string Source, string Text)> Items { get; } = [];

        public void Add(string source, string? text)
        {
            if (!string.IsNullOrEmpty(text)) Items.Add((source, text));
        }

        public void AddAll(string source, IEnumerable<string?> texts)
        {
            foreach (string? t in texts) Add(source, t);
        }
    }

    /// <summary>型の中の、公開(内部を含む)の文字列の定数と、引数の無い静的プロパティの値を集める(新しい文を足したとき、見張りも自動で追いつくように)。</summary>
    private static IEnumerable<(string Name, string Value)> StringMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (FieldInfo f in type.GetFields(flags))
            if (!f.IsPrivate && !f.Name.EndsWith("PathId", StringComparison.Ordinal) && f.IsLiteral && f.FieldType == typeof(string) && f.GetRawConstantValue() is string s)
                yield return (f.Name, s);
        foreach (PropertyInfo p in type.GetProperties(flags))
            if (p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPrivate: false } && p.GetValue(null) is string s)
                yield return (p.Name, s);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 8, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly string Hash = new('a', 64);

    private static IndexState State(IndexRunState run, JobKind? job, EngineStatus engine, string? error, int skipped, bool counts)
    {
        var stages = new List<StageSummary>
        {
            new(Stage.Embed, counts ? 4 : 0, 5, counts ? 3 : 0, counts ? 2 : 0),
            new(Stage.Ocr, counts ? 4 : 0, 5, counts ? 3 : 0, counts ? 2 : 0),
            new(Stage.Caption, counts ? 4 : 0, 5, counts ? 3 : 0, counts ? 2 : 0),
        };
        return new IndexState(run, job, stages, engine, null, @"C:\x\a.png", 0.5, TimeSpan.FromHours(2), error, skipped);
    }

    /// <summary>左側: 左パネル・窓・通知・進捗の文。</summary>
    private static void CollectLeftSide(Corpus c)
    {
        // 型の定数・プロパティ(窓の列の名前・ボタン・ヒント・状態の文など)
        foreach (Type t in new[] { typeof(ProcessingWords), typeof(NoticeWords), typeof(ProcessingText), typeof(IndexStatusText), typeof(SetupWords) })
            foreach ((string name, string value) in StringMembers(t)) c.Add($"{t.Name}.{name}", value);

        // 進捗の文(実行の状態 × 段階 × 準備の状態 × エラーの有無)
        var engines = new List<EngineStatus> { EngineStatus.NotCreated };
        foreach (EnginePhase phase in Enum.GetValues<EnginePhase>()) engines.Add(new EngineStatus(phase, "準備の理由", null));
        foreach (IndexRunState run in Enum.GetValues<IndexRunState>())
            foreach (JobKind? job in new JobKind?[] { null, JobKind.Embed, JobKind.Vlm })
                foreach (EngineStatus engine in engines)
                    foreach (string? error in new[] { null, "別の理由" })
                        foreach (bool counts in new[] { false, true })
                        {
                            IndexState state = State(run, job, engine, error, counts ? 2 : 0, counts);
                            c.Add("IndexStatusText.Build", IndexStatusText.Build(state));
                            // 「インデックス」欄の固定の行
                            IndexRows rows = IndexStatusText.BuildRows(state);
                            foreach (string text in new[] { rows.Stage1Count, rows.Stage1Extra, rows.Stage2Count, rows.Stage2Extra, rows.State, rows.Progress, rows.Note })
                                c.Add("IndexStatusText.BuildRows", text);
                            foreach (string line in rows.NoteFull.Split('\n')) c.Add("IndexStatusText.BuildRows.NoteFull", line);
                            // モデルのファイルが無くて動かせない段階(失敗ではなく「未取得」)
                            IndexState notFetched = state with { UnavailableJobs = [new UnavailableJob(JobKind.Vlm, ["Qwen3-VL-8B-Instruct-Q4_K_M.gguf", "mmproj-F16.gguf"])] };
                            c.Add("IndexStatusText.Build", IndexStatusText.Build(notFetched));
                            IndexRows notFetchedRows = IndexStatusText.BuildRows(notFetched);
                            foreach (string text in new[] { notFetchedRows.Stage2Extra, notFetchedRows.Note })
                                c.Add("IndexStatusText.BuildRows", text);
                            foreach (string line in notFetchedRows.NoteFull.Split('\n')) c.Add("IndexStatusText.BuildRows.NoteFull", line);
                        }
        foreach (Stage stage in Enum.GetValues<Stage>())
            c.Add("IndexStatusText.StageLine", IndexStatusText.StageLine("ラベル", new StageSummary(stage, 4, 5, 3, 2)));
        foreach (JobKind? job in new JobKind?[] { null, JobKind.Embed, JobKind.Vlm })
        {
            c.Add("IndexStatusText.JobLabel", IndexStatusText.JobLabel(job));
            c.Add("ProcessingWords.StageNameLong", ProcessingWords.StageNameLong(job));
            c.Add("ProcessingWords.RunningLine", ProcessingWords.RunningLine(job));
            c.Add("ProcessingWords.PreparingLine", ProcessingWords.PreparingLine(job));
            c.Add("ProcessingWords.PrepareFailedPrefix", ProcessingWords.PrepareFailedPrefix(job));
        }
        c.Add("ProcessingWords.SkippedImagesLine", ProcessingWords.SkippedImagesLine(3));

        // モデルのセットアップの画面(文を作る関数。ファイル名・場所は作った値)
        foreach (JobKind job in new[] { JobKind.Embed, JobKind.Vlm })
        {
            c.Add("SetupWords.SectionTitle", SetupWords.SectionTitle(job));
            c.Add("SetupWords.SectionPurpose", SetupWords.SectionPurpose(job));
        }
        foreach (ModelFileState fileState in Enum.GetValues<ModelFileState>())
            c.Add("SetupWords.FileState", SetupWords.FileState(new ModelFileStatus("a/b.bin", 100, fileState, fileState == ModelFileState.Missing ? null : 50)));
        foreach (SetupState setupState in new[] { SetupWindowTests.BothMissing(), SetupWindowTests.VlmOnlyMissing(), SetupWindowTests.WithMismatch(), SetupWindowTests.AllReady() })
            foreach (ModelSetStatus set in new[] { setupState.Embedding, setupState.Vlm })
            {
                c.Add("SetupWords.SetState", SetupWords.SetState(set));
                c.Add("SetupWords.SetSummary", SetupWords.SetSummary(set));
            }
        foreach (long size in new long[] { 0, 999, 560_482, 34_362_885, 5_027_784_800 })
        {
            c.Add("SetupWords.FormatSize", SetupWords.FormatSize(size));
            c.Add("SetupWords.ExactSize", SetupWords.ExactSize(size));
        }
        var downloadPlan = new ModelDownloadPlan(
            [new ModelDownloadFile(ModelSets.FeatureVector, "a/b.bin", 1_000_000_000, ModelFileState.Missing, 0), new ModelDownloadFile(ModelSets.FeatureVector, "c.bin", 500_000_000, ModelFileState.SizeMismatch, 200_000_000)], "C:\e", "C:\v");
        foreach (ModelDownloadPlan plan in new[] { downloadPlan, new ModelDownloadPlan([downloadPlan.Files[0]], "C:\e", "C:\v") })
        {
            c.Add("SetupWords.DownloadButton", SetupWords.DownloadButton(plan));
            foreach (long? free in new long?[] { null, 100_000_000_000 }) c.Add("SetupWords.PlanLine", SetupWords.PlanLine(plan, free, "C:\\"));
            c.Add("SetupWords.NoSpaceReason", SetupWords.NoSpaceReason(plan, 1_000_000_000, "C:\\"));
            c.Add("SetupWords.DownloadBothButton", SetupWords.DownloadBothButton(plan));
            c.Add("SetupWords.PlanLine", SetupWords.PlanLine(plan, [("C:\\", 100_000_000_000), ("Z:\\", 50_000_000_000)]));
            c.Add("SetupWords.NoSpaceReason", SetupWords.NoSpaceReason(plan.RequiredFreeBytes, 1_000_000_000, "Z:\\"));
            c.Add("SetupWords.BothOrder", SetupWords.BothOrder);
            c.Add("SetupWords.DownloadRemainingButton", SetupWords.DownloadRemainingButton(plan));
            foreach (JobKind ready in new[] { JobKind.Embed, JobKind.Vlm })
            {
                c.Add("SetupWords.BulkReadyNote", SetupWords.BulkReadyNote(ready));
                c.Add("SetupWords.RemainingPlanLine", SetupWords.RemainingPlanLine(ready, plan, [("C:\\", 100_000_000_000)]));
                c.Add("SetupWords.RemainingPlanLine", SetupWords.RemainingPlanLine(ready, plan, [("C:\\", null)]));
            }
        }
        foreach (ModelDownloadPhase phase in Enum.GetValues<ModelDownloadPhase>())
        {
            var progress = new ModelDownloadProgress(phase, "a/b.bin", 1, 4, 1_000_000_000, 4_000_000_000);
            c.Add("SetupWords.ProgressLine", SetupWords.ProgressLine(progress));
            c.Add("SetupWords.AmountLine", SetupWords.AmountLine(progress, null));
            c.Add("SetupWords.AmountLine", SetupWords.AmountLine(progress, 50_000_000));
        }
        foreach (ModelDownloadFailure kind in Enum.GetValues<ModelDownloadFailure>())
            foreach (string? file in new[] { null, "a/b.bin" })
                foreach (int? status in new int?[] { null, 404, 429, 503 })
                {
                    var failure = new ModelDownloadException(kind, "理由(テスト)", file, status, "cdn.example.test", 4_000_000_000, 1_000_000_000);
                    c.Add("SetupWords.FailureSummary", SetupWords.FailureSummary(failure));
                    c.Add("SetupWords.FailureDetail", SetupWords.FailureDetail(failure));
                }
        c.Add("SetupWords.StillIncomplete", SetupWords.StillIncomplete(3));
        c.Add("SetupWords.OpenFolderFailed", SetupWords.OpenFolderFailed("理由"));
        foreach (JobKind job in new[] { JobKind.Embed, JobKind.Vlm })
        {
            c.Add("ProcessingWords.ModelNotFetchedNote", ProcessingWords.ModelNotFetchedNote(job));
            c.Add("ProcessingWords.ModelNotFetchedLine", ProcessingWords.ModelNotFetchedLine(job));
            c.Add("ProcessingWords.ModelNotFetchedHint", ProcessingWords.ModelNotFetchedHint(job, ["a.gguf", "b.gguf"]));
            c.Add("ProcessingWords.ModelNotFetchedWindowLine", ProcessingWords.ModelNotFetchedWindowLine(job));
        }

        foreach (JobKind job in new[] { JobKind.Embed, JobKind.Vlm })
        {
            c.Add("ProcessingWords.StageName", ProcessingWords.StageName(job));
            c.Add("ProcessingWords.StageSubject", ProcessingWords.StageSubject(job));
            c.Add("ProcessingWords.StageHint", ProcessingWords.StageHint(job));
            c.Add("ProcessingWords.ModelName", ProcessingWords.ModelName(job));
            c.Add("ProcessingWords.StageNameWithModel", ProcessingWords.StageNameWithModel(job));
            c.Add("ProcessingWords.OutdatedHint", ProcessingWords.OutdatedHint(job));
            c.Add("ProcessingWords.OutdatedLine", ProcessingWords.OutdatedLine(job, 300));
            c.Add("ProcessingText.JobLabel", ProcessingText.JobLabel(job));
            c.Add("ProcessingText.Subject", ProcessingText.Subject(job));
            c.Add("ProcessingText.PerImageBasis", ProcessingText.PerImageBasis(job));
            c.Add("ProcessingText.RequeueConfirmTitle", ProcessingText.RequeueConfirmTitle(job));
            foreach (int count in new[] { 1, 5, 300 }) c.Add("ProcessingText.RequeueConfirmText", ProcessingText.RequeueConfirmText(job, count));
            c.Add("NoticeWords.RequeueReset", NoticeWords.RequeueReset(job, 7));
            c.Add("NoticeWords.RequeueStartFailed", NoticeWords.RequeueStartFailed(job, "理由"));
            c.Add("NoticeWords.RequeueWhat", NoticeWords.RequeueWhat(job));
            c.Add("ProcessingWords.FailureHeading", ProcessingWords.FailureHeading(job, 12, 10));
        }
        c.Add("ProcessingWords.OutdatedHeading", ProcessingWords.OutdatedHeading(3, 300));
        c.Add("ProcessingWords.SkippedHeading", ProcessingWords.SkippedHeading(3));
        c.Add("ProcessingWords.MissingHeading", ProcessingWords.MissingHeading(12, 3, 10));
        c.Add("ProcessingWords.MissingHeading", ProcessingWords.MissingHeading(12, 0, 12));
        c.Add("ProcessingWords.RetryActionLabel", ProcessingWords.RetryActionLabel(4));
        c.Add("ProcessingWords.ManualTagCountText", ProcessingWords.ManualTagCountText(2));
        c.Add("ProcessingWords.StartTooltip", ProcessingWords.StartTooltip());
        c.Add("ProcessingText.PurgeConfirmText", ProcessingText.PurgeConfirmText(12, 3));
        c.Add("ProcessingText.PurgeConfirmText", ProcessingText.PurgeConfirmText(12, 0));

        // 処理状態の窓の、種類・操作・押せない理由(実行の状態 × 開いている × 操作中 × 数 × フォルダに無い画像の数)
        foreach (ProblemKind kind in Enum.GetValues<ProblemKind>()) c.Add("ProcessingWords.KindLabel", ProcessingWords.KindLabel(kind));
        foreach (IndexRunState run in Enum.GetValues<IndexRunState>())
            foreach (bool open in new[] { false, true })
                foreach (bool busy in new[] { false, true })
                    foreach (int n in new[] { 0, 3 })
                        foreach (int? missing in new int?[] { null, 0, 5 })
                        {
                            var s = new ProcessingSnapshot(run, open, busy, n, n, n, n, n, missing);
                            c.Add("ProcessingWords.LeftSummary", ProcessingWords.LeftSummary(s));
                            foreach (ProblemKind kind in Enum.GetValues<ProblemKind>())
                            {
                                c.Add("ProcessingWords.KindRadioText", ProcessingWords.KindRadioText(kind, s));
                                foreach (ProcessingAction action in ProcessingWords.ActionsOf(kind))
                                {
                                    c.Add("ProcessingWords.ActionLabel", ProcessingWords.ActionLabel(action, s));
                                    c.Add("ProcessingWords.ActionLead", ProcessingWords.ActionLead(action, s));
                                    c.Add("ProcessingSnapshot.Action.Reason", s.Action(action).Reason);
                                    c.Add("ProcessingWords.NothingToDo", ProcessingWords.NothingToDo(action));
                                }
                            }
                            if (run is IndexRunState.Running or IndexRunState.Pausing or IndexRunState.Paused)   // ほかの状態では「押せない理由」が無い(例外になる)
                                foreach (bool purge in new[] { false, true }) c.Add("ProcessingWords.RunBlockedReason", ProcessingWords.RunBlockedReason(run, purge));
                        }

        // 窓の一覧(見出し・列・行)
        var failures = new FailureList(Stage.Embed, [new FailureItem(Hash, @"C:\shots\a.png", "画像をデコード・前処理できない: x", 2, T0, "m"), new FailureItem(Hash, null, null, 1, T0, "m")], 2);
        foreach (JobKind job in new[] { JobKind.Embed, JobKind.Vlm }) AddList(c, "ProcessingText.FailureRows", ProcessingText.FailureRows(job, failures, Utc));
        AddList(c, "ProcessingText.SkippedRows", ProcessingText.SkippedRows([new SkippedItem(JobKind.Vlm, Hash, @"C:\shots\a.png", "読めない"), new SkippedItem(JobKind.Embed, Hash, null, "読めない")]));
        AddList(c, "ProcessingText.MissingRows", ProcessingText.MissingRows(new MissingImageList([new MissingImageItem(Hash, @"C:\shots\a.png", T0, 2, true), new MissingImageItem(Hash, null, null, 0, false)], 2, 1), Utc));
        AddList(c, "ProcessingText.OutdatedList", ProcessingText.OutdatedList(new RequeuePlan(3, 300)));

        // 通知・ダイアログ(NoticeWords の関数を全部)
        var summary = new ScanSummary(1, 2, 3, 4, 5, 6, 7, TimeSpan.FromSeconds(1.5));
        c.Add("NoticeWords.DescribeScan", NoticeWords.DescribeScan(summary));
        c.Add("NoticeWords.ScanFinished", NoticeWords.ScanFinished(summary));
        foreach (ScanTrigger trigger in Enum.GetValues<ScanTrigger>())
        {
            c.Add("NoticeWords.TriggerLabel", NoticeWords.TriggerLabel(trigger));
            c.Add("NoticeWords.AutoScanFinished", NoticeWords.AutoScanFinished(trigger, summary));
        }
        c.Add("NoticeWords.ScanFailed", NoticeWords.ScanFailed("理由"));
        c.Add("NoticeWords.RemoveFolderFailed", NoticeWords.RemoveFolderFailed("理由"));
        foreach (bool stopping in new[] { false, true }) c.Add("NoticeWords.ToggleImportFailed", NoticeWords.ToggleImportFailed(stopping, "理由"));
        foreach (Func<string, string> f in new Func<string, string>[]
        {
            NoticeWords.StartFailed, NoticeWords.ResumeFailed, NoticeWords.PauseFailed, NoticeWords.RetryResetFailed, NoticeWords.RetryFailedStart, NoticeWords.RetrySkippedFailed,
            NoticeWords.RequeueResetFailed, NoticeWords.MissingCountFailed, NoticeWords.PurgeFailed,
        })
            c.Add("NoticeWords.(失敗の通知)", f("理由"));
        c.Add("NoticeWords.RetryRestarted", NoticeWords.RetryRestarted(2, 3));
        c.Add("NoticeWords.Purged", NoticeWords.Purged(4));
        c.Add("NoticeWords.RemoveFolderConfirm", NoticeWords.RemoveFolderConfirm(@"C:\shots"));

        // フォルダの行のツールチップ
        foreach (string? lastError in new[] { null, "読めなかった" })
            foreach (string? unwatched in new[] { null, "ネットワークの場所なので監視できない" })
                c.Add("IndexPanel.FolderTooltip", IndexPanel.FolderTooltip(@"C:\shots", lastError, unwatched));

        // Core の文の言い換え(UiText.Plain)。実在の Core の文のうち、画面に出うるもの
        foreach (string core in CoreSentences) c.Add("UiText.Plain", UiText.Plain(core));
    }

    /// <summary>Core が出す文のうち、画面に出うるもの(例外のメッセージ・準備の理由・止まった原因)。言い換え(UiText.Plain)を通したあとに、引退させた語が残らないこと。</summary>
    private static readonly string[] CoreSentences =
    [
        "ジョブは実行中(状態 Running)。止めてから始める。",
        "ジョブ Vlm のランナーが無い(この版では動かせない)",
        "全文検索の索引を作り直している",
        "索引が壊れている",
        "モデルのファイルの sha256 が違う: visual/model.onnx(場所: C:\\m\\visual\\model.onnx)。",
        "llama-server.exe が無い: C:\\rt\\llama-server.exe。",
        "llama-server.exe の中身が違う(場所: C:\\rt\\llama-server.exe)",
        "llama-server が 3 枚続けて異常になった。",
        "GPU の自己検査に失敗した: x",
        "不正な文字(対になっていないサロゲート)を含む",
        "SHParseDisplayName が失敗(HRESULT 0x80070002): C:\\a.png",
        "見つからない画像の記録を消せなかった",
        "内容が走査のときと違う(走査のあとで書き換えられた)",
        "到達できなかった場所 2 件(配下は今回の走査の対象外):",
        // ネイティブの失敗(利用者に次の行動を言える文。Core が平らな日本語で作る)
        "NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください (CUDA を初期化できませんでした)",
        "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした。足りないファイル: msvcp140.dll、vcruntime140.dll)",
        "アプリのファイルが足りないか、壊れています。zip を展開し直してください (VLM の処理プログラムが必要な DLL を読めず、起動できませんでした。終了コード 0xC0000135)",
    ];

    private static void AddList(Corpus c, string source, ProblemList list)
    {
        c.Add(source + ".Heading", list.Heading);
        foreach (ProblemColumn col in list.Columns) c.Add(source + ".Column", col.Header);
        foreach (ProblemRow row in list.Rows)
        {
            c.Add(source + ".PathText", row.PathText);
            c.AddAll(source + ".Cells", row.Cells);
            c.Add(source + ".Tooltip", row.Tooltip);
        }
    }

    private static PathOutcome Outcome(string id, PathState state, string? message = null, int population = 10) => new(id, null, population, state, message);

    private static SearchResult Result(PathDetail? fulltext, params string[] hitIds)
    {
        var hits = new List<PathHit>();
        if (hitIds.Contains("visual")) hits.Add(new PathHit("visual", "m", 3, 0.2, new VisualDetail(0.21, 0.21)));
        if (hitIds.Contains("fulltext") && fulltext is not null) hits.Add(new PathHit("fulltext", null, 1, 5.5, fulltext));
        return new SearchResult(Hash, [@"C:\shots\a.png"], 2, 0.03, hits);
    }

    private static TextDetail TextOf(params string[] fuzzy) =>
        new([
            new FieldMatch(TextField.Ocr, "文字", [new TextSpan(0, 1)]),
            new FieldMatch(TextField.Description, "説明", []),
            new FieldMatch(TextField.Tags, "", []),
            new FieldMatch(TextField.ManualTags, "", []),
        ]) { FuzzyTerms = fuzzy };

    /// <summary>右側: 検索の見出し・ツールチップ・詳細・状態のバー。</summary>
    private static async Task CollectRightSideAsync(Corpus c)
    {
        foreach (Type t in new[] { typeof(DetailPresenter), typeof(ReasonText), typeof(ResultsPanel), typeof(DetailView) })
            foreach ((string name, string value) in StringMembers(t)) c.Add($"right:{t.Name}.{name}", value);

        // 検索の経路の状態の組み合わせ(特徴ベクトル検索 × 全文検索 × 準備中・失敗・飛ばした・メッセージ)
        var states = new List<PathOutcome?> { null };
        foreach (PathState st in Enum.GetValues<PathState>())
            foreach (string? message in new[] { null, "メッセージ", "全文検索のインデックスを作り直している" }) states.Add(Outcome("visual", st, message));
        var ftStates = new List<PathOutcome?> { null };
        foreach (PathState st in Enum.GetValues<PathState>())
            foreach (string? message in new[] { null, "検索できる語を除いた: 、", "全文検索のインデックスを作り直している" }) ftStates.Add(Outcome("fulltext", st, message, population: 7));

        foreach (PathOutcome? v in states)
            foreach (PathOutcome? f in ftStates)
            {
                PathOutcome[] paths = new[] { v, f }.Where(o => o is not null).Select(o => o!).ToArray();
                foreach (int results in new[] { 0, 3 })
                    foreach ((int indexed, int total) in new[] { (10, 10), (4, 10) })
                    {
                        var response = new SearchResponse("猫 ガチャ", Enumerable.Range(0, results).Select(i => new SearchResult(new string((char)('a' + i), 64), [$"C:\\{i}.png"], i + 1, 0.5, [])).ToList(), paths, indexed, total, TimeSpan.FromMilliseconds(12));
                        c.Add("right:ResultsPanel.BuildHeader", ResultsPanel.BuildHeader(response));
                    }
                foreach (SearchResult r in new[] { Result(null, "visual"), Result(TextOf(), "visual", "fulltext"), Result(TextOf("ガチャ"), "fulltext") })
                    foreach (bool noTextIndex in new[] { false, true })
                    {
                        c.Add("right:ResultsPanel.BuildTooltip", ResultsPanel.BuildTooltip(r, paths, noTextIndex));
                        c.AddAll("right:ReasonText.Lines", ReasonText.Lines(r, noTextIndex));
                        foreach ((string line, string? tip) in DetailPresenter.RankEntries(r, paths)) { c.Add("right:DetailPresenter.RankEntries", line); c.Add("right:DetailPresenter.RankEntries.Tip", tip); }
                    }
            }

        foreach (int n in new[] { 0, 5 })
        {
            c.Add("right:ResultsPanel.BuildRecentHeader", ResultsPanel.BuildRecentHeader(new RecentResponse(Enumerable.Range(0, n).Select(i => new RecentImage(Hash, [@"C:\a.png"], T0)).ToList(), n, TimeSpan.Zero)));
        }
        foreach (string[] paths in new[] { new[] { @"C:\a.png" }, new[] { @"C:\a.png", @"C:\b.png" } })
            c.Add("right:ResultsPanel.BuildRecentTooltip", ResultsPanel.BuildRecentTooltip(new SearchResult(Hash, paths, 1, 0, []), T0, Utc));

        // 詳細(状態の行・寸法・記録が無い・読み込み中・失敗)
        foreach (Stage stage in Enum.GetValues<Stage>())
            foreach (StageState state in Enum.GetValues<StageState>())
                c.Add("right:DetailPresenter.StageLine", DetailPresenter.StageLine(new StageDetail(stage, state, "m", "エラー", 2, T0), Utc));
        var stages = Enum.GetValues<Stage>().SelectMany(s => Enum.GetValues<StageState>().Select(st => new StageDetail(s, st, "m", "エラー", 1, T0))).ToList();
        var detail = new ImageDetail(Hash, 1234567, "png", 1920, 1080, T0, [new ImagePathDetail(@"C:\shots\a.png", false, null, T0, 1234567), new ImagePathDetail(@"C:\old\a.png", true, T0, T0, 1234567)], stages, new CaptionDetail("m", "文字", "説明", ["タグ"], T0), ["手動"]);
        var detailNoSize = detail with { Width = null, Height = null, Caption = null, ManualTags = [] };
        PathOutcome[] both = [Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Ok)];
        var loads = new List<DetailLoad> { DetailLoad.Loaded(detail), DetailLoad.Loaded(detailNoSize), DetailLoad.Loading, DetailLoad.RecordMissing, DetailLoad.Failed("llama-server が失敗した") };
        foreach (DetailLoad load in loads)
            foreach ((bool fromSearch, bool fromRecent) in new[] { (true, false), (false, false), (false, true) })
                foreach (SearchResult r in new[] { Result(TextOf(), "visual", "fulltext"), Result(null, "visual") })
                {
                    DetailModel m = DetailPresenter.Build(r, both, load, Utc, fromSearch, fromRecent, noTextIndexHint: true);
                    c.Add("right:DetailPresenter.Build.Title", m.Title);
                    c.AddAll("right:DetailPresenter.Build.RankLines", m.RankLines);
                    c.AddAll("right:DetailPresenter.Build.RankTips", m.RankTips);
                    c.Add("right:DetailPresenter.Build.FieldsNote", m.FieldsNote);
                    c.Add("right:DetailPresenter.Build.FieldsHeader", m.FieldsHeader);
                    c.AddAll("right:DetailPresenter.Build.StageLines", m.StageLines);
                    c.AddAll("right:DetailPresenter.Build.InfoLines", m.InfoLines);
                    c.AddAll("right:DetailPresenter.Build.ReasonLines", m.ReasonLines);
                    c.Add("right:DetailPresenter.Build.RecordNotice", m.RecordNotice);
                }

        // 状態のバー
        await StaDispatcher.RunAsync(() =>
        {
            var bar = new StatusBarView();
            foreach (CorePhase phase in Enum.GetValues<CorePhase>())
                foreach (bool loaded in new[] { false, true })
                {
                    bar.ShowCoreStatus(new CoreStatus(phase, "llama-server が失敗した", loaded, 12));
                    c.Add("right:StatusBarView.ShowCoreStatus", bar.CoreText);
                }
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 調べる

    /// <summary>1 つの文字列に含まれる、出してはいけない語。</summary>
    internal static IEnumerable<string> Violations(string text)
    {
        foreach (string w in RetiredWords)
        {
            // 「準備完了」は、「検索準備完了」(状態のバー。検索の両方の経路が答えられるとき)だけ許す(ADR 0038)
            bool found = w == "準備完了" ? Regex.IsMatch(text, "(?<!検索)準備完了") : text.Contains(w, StringComparison.Ordinal);
            if (found) yield return w;
        }
        // コードの綴りは、単語として出たときだけ(ファイルやフォルダの名前の一部 visual/model.onnx は、画面に出てよい。実在のパスだから)
        foreach (string w in CodeSpellings)
            if (Regex.IsMatch(text, @"(?<![\\/\w.])" + Regex.Escape(w) + @"(?![\\/\w.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) yield return w;
    }

    /// <summary>出どころ|語 → 出た文の例(診断用。文の頭 80 字まで)。</summary>
    private static Dictionary<string, string> Found(Corpus c, Func<string, bool> sourceFilter, bool withStyle)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string source, string text) in c.Items.Where(i => sourceFilter(i.Source)))
        {
            // 「準備完了」は、セットアップの画面の区画の状態(モデル 1 組のファイルがそろった)にだけ使う(ADR 0038)。検索全体の「検索準備完了」(状態のバー)とは別の意味
            if (text == SetupWords.SetComplete && source.StartsWith("SetupWords.", StringComparison.Ordinal)) continue;
            IEnumerable<string> words = withStyle ? Violations(text).Concat(StyleViolations(text)) : Violations(text);
            foreach (string w in words) found.TryAdd($"{source}|{w}", text.Length <= 80 ? text : text[..80] + "…");
        }
        return found;
    }

    /// <summary>日本語の文字(ひらがな・カタカナ・漢字・長音)の直後の「(」。前に半角の空白が要る(ADR 0038)。</summary>
    private static readonly Regex JapaneseThenParen = new(@"[\u3040-\u30FF\u3400-\u9FFF\uFF66-\uFF9F]\(", RegexOptions.CultureInvariant);

    /// <summary>「だ・である」調の語尾(説明の文は「です・ます」調。ADR 0038)。文の終わり(文末・「。」・改行・「)」・「: 」・「(」の前)にあるものだけ。</summary>
    private static readonly Regex PlainStyleEnding = new(
        @"(できなかった|できない|が無い|がない|ではない|に失敗した|に失敗している|ていない|られない|ている|終わった|終了した)(?=$|[。\n)]|: | ?\()", RegexOptions.CultureInvariant);

    /// <summary>
    /// 書き方の規則(<c>括弧の前に空白</c>・<c>です・ます調</c>)に外れた箇所。パスは見ない(空白で区切った塊が「\」か「/」を含むときは、パスとみなす)。
    /// 左側の文の関数にも、右側の文にも掛ける。
    /// </summary>
    internal static IEnumerable<string> StyleViolations(string text)
    {
        string withoutPaths = Regex.Replace(text, @"\S+", m => m.Value.Contains('\\') || m.Value.Contains('/') ? "" : m.Value);
        if (JapaneseThenParen.IsMatch(withoutPaths)) yield return "括弧の前に空白が無い";
        Match end = PlainStyleEnding.Match(withoutPaths);
        if (end.Success) yield return "です・ます調でない語尾: " + end.Groups[1].Value;
    }

    [Fact]
    public void The_left_side_sentences_have_no_retired_words_and_no_code_spellings()
    {
        var c = new Corpus();
        CollectLeftSide(c);

        Assert.True(c.Items.Count > 1000, $"集めた文が少なすぎる({c.Items.Count} 件)。集め方が壊れていないか");
        Dictionary<string, string> found = Found(c, s => !s.StartsWith("right:", StringComparison.Ordinal), withStyle: true);
        Assert.True(found.Count == 0, "左側の文に、画面の語ではない言葉が出ている(出どころ|語 = 文の例): " + string.Join(" / ", found.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " = " + kv.Value)));
    }

    [Fact]
    public async Task The_right_side_sentences_have_only_the_known_leftovers()
    {
        var c = new Corpus();
        await CollectRightSideAsync(c);

        Assert.True(c.Items.Count > 500, $"集めた文が少なすぎる({c.Items.Count} 件)。集め方が壊れていないか");
        Dictionary<string, string> found = Found(c, s => s.StartsWith("right:", StringComparison.Ordinal), withStyle: true);
        string[] fresh = [.. found.Keys.Except(KnownRightSideLeftovers).Order(StringComparer.Ordinal)];
        string[] fixedAlready = [.. KnownRightSideLeftovers.Except(found.Keys).Order(StringComparer.Ordinal)];
        Assert.True(fresh.Length == 0, "右側の文に、新しく画面の語ではない言葉が出ている(出どころ|語 = 文の例): " + string.Join(" / ", fresh.Select(k => k + " = " + found[k])));
        Assert.True(fixedAlready.Length == 0, "既知の残りが直っている。KnownRightSideLeftovers から消す: " + string.Join(", ", fixedAlready));
    }

    // ---------------------------------------------------------------- 見張り自身の確認

    [Theory]
    [InlineData("画像検索が使える", "画像検索")]
    [InlineData("見た目が近い順", "見た目が近い")]
    [InlineData("準備完了", "準備完了")]
    [InlineData("全文検索で一致", "全文検索")]
    [InlineData("走査が終わりました", "走査")]
    [InlineData("今すぐフォルダを再走査", "走査")]
    [InlineData("全文検索の対象 3 枚", "全文検索")]
    [InlineData("埋め込み 3 枚", "埋め込み")]
    [InlineData("索引を作り直す", "索引")]
    [InlineData("ジョブは実行中", "ジョブ")]
    [InlineData("未処理 3", "未処理")]
    [InlineData("要再処理 3", "要再処理")]
    [InlineData("経路 visual", "visual")]
    [InlineData("Fulltext の結果", "fulltext")]
    [InlineData("cosine 0.2", "cosine")]
    [InlineData("Core: 起動できなかった", "Core:")]
    public void The_watch_catches_each_retired_word_and_code_spelling(string text, string expected)
    {
        Assert.Contains(expected, Violations(text));
    }

    [Theory]
    [InlineData("特徴ベクトル検索: 12 / 30 枚 (未作成 18、失敗 1、要再作成 2)")]
    [InlineData("インデックスの状態…")]
    [InlineData("検索準備完了 (特徴ベクトル検索の対象 10 枚)")]
    [InlineData("今すぐフォルダを再スキャン")]
    [InlineData("スキャンが終わりました (0.2 秒): 新規 1、変更 0、消えた 0")]
    [InlineData("VLM の処理プログラム (llama-server) がありません")]
    [InlineData(@"モデルのファイルの中身が想定と違う: visual/model.onnx(場所: C:\misual\model.onnx)。")]   // 実在のパスの一部は、画面に出てよい
    public void The_watch_lets_the_current_words_through(string text)
    {
        Assert.Empty(Violations(text));
    }

    [Theory]
    [InlineData("検索準備完了(特徴ベクトル検索の対象 10 枚)", "括弧の前に空白が無い")]
    [InlineData("取り込みを止められなかった", null)]                       // 文の終わりでない(「: 」「。」が続かない)ので、語尾の規則は掛けない
    [InlineData("取り込みを止められない: 理由", "です・ます調でない語尾: られない")]
    [InlineData("起動できなかった。", "です・ます調でない語尾: できなかった")]
    [InlineData("フォルダに無い画像の記録はありません", null)]
    [InlineData("フォルダの記録が無い (原因は不明です)", "です・ます調でない語尾: が無い")]   // 「が無い」の後ろに「(」が来るのは、文の終わりの前(空白あり)なので、語尾の規則に掛かる
    [InlineData(@"C:\shots\名前(1).png: 読み込めませんでした", null)]         // パスの中の「(」は変えない・見ない
    public void The_style_watch_catches_a_missing_space_before_a_paren_and_plain_style_endings_but_not_in_paths(string text, string? expected)
    {
        string[] found = [.. StyleViolations(text)];
        if (expected is null) Assert.Empty(found);
        else Assert.Contains(expected, found);
    }

    [Fact]
    public void The_collecting_reaches_the_sentence_functions_by_type_so_a_new_constant_is_watched_too()
    {
        // 型から拾えていること(拾えていなければ、見張りが空振りする)
        Assert.Contains(StringMembers(typeof(ProcessingWords)), m => m.Name == "WordOutdated" && m.Value == "要再作成");
        Assert.Contains(StringMembers(typeof(NoticeWords)), m => m.Name == "NoOutdated");
        Assert.Contains(StringMembers(typeof(ProcessingWords)), m => m.Name == "ProcessingStateTooltip");   // プロパティも
    }
}
