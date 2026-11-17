using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Xunit;

namespace Ohanami.Core.Tests.Captions;

/// <summary>
/// captions と索引のテスト用の土台: 一時 DB、VLM のモデル 2 つ(A と B。モデルを替えた場面を作る)、画像の行、runs の 1 行、FtsIndex と CaptionStore。
/// 実物の SQLite(FTS5)を使う。VLM も GPU も要らない。
/// </summary>
internal sealed class CaptionFixture : IDisposable
{
    public TempDirectory Dir { get; } = new();
    public OhanamiDatabase Db { get; }
    public TestLog Log { get; } = new();
    public FtsIndex Index { get; private set; }
    public CaptionStore Store { get; private set; }
    public string ModelA { get; }
    public string ModelB { get; }
    public long RunId => 1;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CaptionFixture()
    {
        Db = OhanamiDatabase.Open(Dir.File("test.db"));
        VlmSpec a = VlmCatalog.Current;
        var b = new VlmSpec(a.Family, a.Gguf, a.Mmproj, a.PromptSha256, a.SchemaSha256, "0.1", a.ChatTemplateKwargs, a.ImageMinTokens, a.ImageMaxTokens, a.HfRepository, a.HfRevision);
        ModelRegistry.EnsureVlmModelRegisteredAsync(Db, "A", a, T(0), Ct).GetAwaiter().GetResult();
        ModelRegistry.EnsureVlmModelRegisteredAsync(Db, "B", b, T(0), Ct).GetAwaiter().GetResult();
        ModelA = a.ModelId;
        ModelB = b.ModelId;
        Db.WriteAsync((c, tx) => TestDb.Exec(c, tx,
            "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'vlm', '2026-11-06T00:00:00.000Z', 'test', '{}')"), Ct).GetAwaiter().GetResult();
        Index = new FtsIndex(Db, Log);
        Store = new CaptionStore(Db, Index);
    }

    /// <summary>索引と保存口を作り直す(同じ DB を、別のインスタンス=再起動したアプリが開いた場面)。</summary>
    public void UseIndex(FtsIndex index)
    {
        Index = index;
        Store = new CaptionStore(Db, index);
    }

    /// <summary>記録された規約を、現行と違う文字列にする(古い版のアプリが作った索引が DB にある場面。次に開くと、規約が違うので作り直しになる)。</summary>
    public Task TamperSpecAsync(string value = "fts-spec/0\n") => Db.WriteAsync((c, tx) =>
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE index_meta SET value = $v WHERE key = 'fts_spec'";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }, Ct);

    public void Dispose()
    {
        Db.Dispose();
        Dir.Dispose();
    }

    // ---------------------------------------------------------------- データ

    public static string Hash(int i) => i.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0');

    /// <summary>基準の時刻から分だけ進めた時刻(created_at の新旧を作る)。</summary>
    public static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(9)).AddMinutes(minutes);

    public Task AddImagesAsync(int count, int first = 1) => Db.WriteAsync((c, tx) =>
    {
        for (int i = first; i < first + count; i++)
            TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash(i)}', 100, 'png', '2026-11-06T00:00:00.000Z')");
    }, Ct);

    public CaptionResult Result(int image, string modelId, string ocr, string description, params string[] tags) =>
        new(Hash(image), modelId, ocr, description, tags, "stop", 1, "{\"max_tokens\":2048}", 1234);

    public Task<CaptionSaveOutcome> SaveAsync(int image, string modelId, string ocr, string description, string[] tags, int minutes = 0, int attempt = 1) =>
        Store.SaveAsync(RunId, Result(image, modelId, ocr, description, tags) with { Attempt = attempt }, T(minutes), Ct);

    // ---------------------------------------------------------------- 検索(FTS5 と LIKE を、SearchPlan から直接引く。FullTextPath を通さない確認用)

    /// <summary>空白区切りの AND で引く。フレーズは fts_docs の MATCH、1 文字の語は search_text の LIKE。ハッシュの順に返す。</summary>
    public List<string> Hits(string query)
    {
        SearchPlan plan = QueryPlanner.PlanSearch(query);
        using SqliteConnection read = Db.CreateReadConnection();
        using SqliteCommand cmd = read.CreateCommand();
        var where = new List<string>();
        string[] singles = plan.SingleChars.ToArray();
        for (int i = 0; i < singles.Length; i++)
        {
            where.Add($"s.norm_text LIKE $l{i} ESCAPE '\\'");
            cmd.Parameters.AddWithValue($"$l{i}", SearchPlan.LikePattern(singles[i]));
        }
        if (plan.FtsMatch is not null)
        {
            where.Insert(0, "fts_docs MATCH $q");
            cmd.Parameters.AddWithValue("$q", plan.FtsMatch);
            cmd.CommandText = $"SELECT s.hash FROM fts_docs f JOIN search_text s ON s.id = f.rowid WHERE {string.Join(" AND ", where)} ORDER BY s.hash";
        }
        else
        {
            cmd.CommandText = $"SELECT s.hash FROM search_text s WHERE {string.Join(" AND ", where)} ORDER BY s.hash";
        }
        var list = new List<string>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public long Count(string sql)
    {
        using SqliteConnection read = Db.CreateReadConnection();
        return TestDb.Long(read, sql);
    }

    public string Text(string sql)
    {
        using SqliteConnection read = Db.CreateReadConnection();
        return TestDb.Text(read, sql);
    }

    /// <summary>索引の中身(ハッシュ → (正規化後の文書, モデル, fts の各列))。ハッシュの順。rowid の値は比べない(作り直すと振り直される)。</summary>
    public SortedDictionary<string, (string Norm, string Model, string Columns)> IndexDump()
    {
        using SqliteConnection read = Db.CreateReadConnection();
        using SqliteCommand cmd = read.CreateCommand();
        cmd.CommandText = $"SELECT s.hash, s.norm_text, s.model_id, f.tokens FROM search_text s JOIN fts_docs f ON f.rowid = s.id";
        var map = new SortedDictionary<string, (string, string, string)>(StringComparer.Ordinal);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? "(null)" : r.GetString(2), r.GetString(3));
        return map;
    }
}
