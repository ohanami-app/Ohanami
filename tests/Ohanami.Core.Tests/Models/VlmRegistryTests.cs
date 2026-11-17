using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>models テーブルへの VLM の登録と、行の読み戻し・再計算の検査(ADR 0006)。埋め込みモデルと同じ規則。</summary>
public sealed class VlmRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset T1 = new(2026, 11, 7, 9, 0, 0, TimeSpan.FromHours(9));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TempDirectory Dir, OhanamiDatabase Db) NewDb()
    {
        var dir = new TempDirectory();
        return (dir, OhanamiDatabase.Open(dir.File("test.db")));
    }

    private static string Text(OhanamiDatabase db, string sql)
    {
        using SqliteConnection conn = db.CreateReadConnection();
        return TestDb.Text(conn, sql);
    }

    private static Task Update(OhanamiDatabase db, string column, string value, string id) =>
        db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE models SET {column} = $v WHERE id = $id";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }, Ct);

    [Fact]
    public async Task Registering_the_current_vlm_writes_a_row_that_reads_back_as_a_typed_vlm_model()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            VlmSpec spec = VlmCatalog.Current;
            VlmModelRegistration reg = await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T0, Ct);
            Assert.Equal(RegistrationOutcome.Created, reg.Outcome);
            Assert.Empty(reg.Changes);
            Assert.Equal(spec.ModelId, reg.Model.Id);

            Assert.Equal("vlm", Text(db, "SELECT kind FROM models"));
            Assert.Equal(spec.ModelId, Text(db, "SELECT id FROM models"));
            Assert.Equal(spec.ToJson(), Text(db, "SELECT spec_json FROM models"));
            Assert.Equal("2026-11-06T00:00:00.000Z", Text(db, "SELECT created_at FROM models"));

            using SqliteConnection conn = db.CreateReadConnection();
            RegisteredVlmModel? found = ModelRegistry.FindVlm(conn, spec.ModelId);
            Assert.NotNull(found);
            Assert.True(found!.Spec.Equals(spec));
            Assert.Equal(ModelKind.Vlm, found.Kind);
            Assert.IsType<RegisteredVlmModel>(ModelRegistry.Find(conn, spec.ModelId));
            Assert.IsType<RegisteredVlmModel>(Assert.Single(ModelRegistry.ReadAll(conn)));
            Assert.Null(ModelRegistry.FindVlm(conn, "fam:000000000000"));
        }
    }

    [Fact]
    public async Task Registering_again_changes_nothing_and_a_moved_file_updates_the_record_keeping_the_creation_time()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            VlmSpec spec = VlmCatalog.Current;
            await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T0, Ct);
            VlmModelRegistration again = await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T1, Ct);
            Assert.Equal(RegistrationOutcome.Unchanged, again.Outcome);

            // 識別が同じで、パス・サイズ・名前だけが違う(配置の見直し)。model_id は変わらず、記録だけ更新する
            var moved = new VlmSpec(spec.Family, new VlmFile("sub/" + spec.Gguf.RelativePath, spec.Gguf.Sha256, spec.Gguf.Size + 1), spec.Mmproj,
                spec.PromptSha256, spec.SchemaSha256, spec.Temperature, spec.ChatTemplateKwargs, spec.ImageMinTokens, spec.ImageMaxTokens, spec.HfRepository, spec.HfRevision);
            Assert.Equal(spec.ModelId, moved.ModelId);
            VlmModelRegistration updated = await ModelRegistry.EnsureVlmModelRegisteredAsync(db, "別の名前", moved, T1, Ct);
            Assert.Equal(RegistrationOutcome.Updated, updated.Outcome);
            Assert.Contains(updated.Changes, c => c.StartsWith("name:", StringComparison.Ordinal));
            Assert.Contains(updated.Changes, c => c.StartsWith("gguf のパス:", StringComparison.Ordinal));
            Assert.Contains(updated.Changes, c => c.StartsWith("gguf のサイズ:", StringComparison.Ordinal));
            Assert.Equal("2026-11-06T00:00:00.000Z", Text(db, "SELECT created_at FROM models"));   // created_at は変えない
            Assert.Equal(moved.ToJson(), Text(db, "SELECT spec_json FROM models"));
            Assert.Equal("別の名前", Text(db, "SELECT name FROM models"));
        }
    }

    [Fact]
    public async Task A_row_with_the_same_id_but_a_different_identity_or_kind_is_a_mismatch_and_is_never_overwritten()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            VlmSpec spec = VlmCatalog.Current;
            await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T0, Ct);

            // 同じ id の行の kind が違う(spec_json は VLM の形のまま)。埋め込みとして読めず、上書きもしない
            await Update(db, "kind", "image-embedding", spec.ModelId);
            await Assert.ThrowsAsync<ModelMismatchException>(() => ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T1, Ct));
            Assert.Equal("image-embedding", Text(db, "SELECT kind FROM models"));
            Assert.Equal(spec.ToJson(), Text(db, "SELECT spec_json FROM models"));
        }
    }

    [Fact]
    public async Task A_row_whose_spec_json_no_longer_matches_its_id_is_rejected_when_read()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            VlmSpec spec = VlmCatalog.Current;
            await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T0, Ct);
            // 識別に関わる値(プロンプトのハッシュ)を書き換える: 再計算した model_id が行の id と違う
            string tampered = spec.ToJson().Replace(spec.PromptSha256, new string('0', 64), StringComparison.Ordinal);
            await Update(db, "spec_json", tampered, spec.ModelId);
            using SqliteConnection conn = db.CreateReadConnection();
            var e = Assert.Throws<ModelMismatchException>(() => ModelRegistry.FindVlm(conn, spec.ModelId));
            Assert.Contains("再計算", e.Message, StringComparison.Ordinal);
            Assert.Throws<ModelMismatchException>(() => ModelRegistry.ReadAll(conn));

            // 壊れた JSON
            await Update(db, "spec_json", "{broken", spec.ModelId);
            Assert.Throws<ModelMismatchException>(() => ModelRegistry.FindVlm(conn, spec.ModelId));
        }
    }

    [Fact]
    public async Task Embedding_and_vlm_rows_live_side_by_side_and_each_finder_refuses_the_other_kind()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            EmbeddingModelSpec emb = ModelCatalog.Current;
            VlmSpec vlm = VlmCatalog.Current;
            await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, ModelCatalog.Name, emb, T0, Ct);
            await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, vlm, T0, Ct);

            using SqliteConnection conn = db.CreateReadConnection();
            Assert.Equal(2, ModelRegistry.ReadAll(conn).Count);
            Assert.NotNull(ModelRegistry.FindEmbedding(conn, emb.ModelId));
            Assert.NotNull(ModelRegistry.FindVlm(conn, vlm.ModelId));
            Assert.Throws<ModelMismatchException>(() => ModelRegistry.FindVlm(conn, emb.ModelId));
            Assert.Throws<ModelMismatchException>(() => ModelRegistry.FindEmbedding(conn, vlm.ModelId));
        }
    }

    [Fact]
    public void Validate_reads_a_vlm_row_and_still_rejects_a_kind_it_does_not_know()
    {
        VlmSpec spec = VlmCatalog.Current;
        RegisteredModel row = ModelRegistry.Validate(spec.ModelId, "vlm", VlmCatalog.Name, spec.ToJson(), "2026-11-06T00:00:00.000Z");
        Assert.IsType<RegisteredVlmModel>(row);
        Assert.Throws<UnknownEnumValueException>(() => ModelRegistry.Validate(spec.ModelId, "ocr-engine", "n", spec.ToJson(), "t"));
    }

    [Fact]
    public async Task A_model_row_can_be_referenced_by_captions_runs_and_stage_status_of_the_new_stages()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            VlmSpec spec = VlmCatalog.Current;
            await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, T0, Ct);
            string hash = new('a', 64);
            await db.WriteAsync((c, tx) =>
            {
                TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{hash}', 1, 'png', 't')");
                TestDb.Exec(c, tx, $"INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, '{DbEnums.ToDb(JobKind.Vlm)}', 't', 'v', '{{}}')");
                foreach (Stage stage in new[] { Stage.Ocr, Stage.Caption })
                    TestDb.Exec(c, tx, $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, updated_at) VALUES ('{hash}', '{DbEnums.ToDb(stage)}', 'done', '{spec.ModelId}', 1, 't')");
                TestDb.Exec(c, tx, $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, run_id, created_at) VALUES ('{hash}', '{spec.ModelId}', '', 'd', '[]', 'stop', 1, '{{}}', 1, 't')");
            }, Ct);
            Assert.Equal("caption,ocr", Text(db, "SELECT group_concat(stage, ',') FROM (SELECT stage FROM stage_status ORDER BY stage)"));
            Assert.Equal("vlm", Text(db, "SELECT job_kind FROM runs"));
        }
    }
}
