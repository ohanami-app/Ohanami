using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>models テーブルへの登録と、行の読み戻し・再計算の検査(GPU もモデルのファイルも要らない)。</summary>
public sealed class ModelRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 34, 56, 789, TimeSpan.FromHours(9));

    private static EmbeddingModelSpec Spec(string pre = "pre-v1") => new("fam", new List<ModelFile>
    {
        new(ModelRole.VisualOnnx, "visual/model.onnx", new string('1', 64), 100),
        new(ModelRole.TextualOnnx, "textual/model.onnx", new string('2', 64), 100),
        new(ModelRole.TextualExternalData, "textual/model.onnx_data", new string('3', 64), 100),
        new(ModelRole.Tokenizer, "textual/tokenizer.json", new string('4', 64), 100),
    }, pre, "clean-v1", 1152, "l2");

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

    [Fact]
    public async Task Registering_the_current_model_writes_a_row_that_can_be_read_back_and_validated()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            EmbeddingModelSpec spec = ModelCatalog.Current;
            ModelRegistration reg = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, ModelCatalog.Name, spec, T0, TestContext.Current.CancellationToken);
            Assert.Equal(RegistrationOutcome.Created, reg.Outcome);
            Assert.Empty(reg.Changes);
            Assert.Equal(spec.ModelId, reg.Model.Id);

            Assert.Equal(spec.ModelId, Text(db, "SELECT id FROM models"));
            Assert.Equal("image-embedding", Text(db, "SELECT kind FROM models"));
            Assert.Equal(ModelCatalog.Name, Text(db, "SELECT name FROM models"));
            Assert.Equal(spec.ToJson(), Text(db, "SELECT spec_json FROM models"));
            Assert.Equal("2026-11-05T03:34:56.789Z", Text(db, "SELECT created_at FROM models")); // UTC の ISO 8601

            using SqliteConnection conn = db.CreateReadConnection();
            RegisteredEmbeddingModel? found = ModelRegistry.FindEmbedding(conn, spec.ModelId);
            Assert.NotNull(found);
            Assert.True(found!.Spec.Equals(spec));
            Assert.Equal(ModelKind.ImageEmbedding, found.Kind);
            Assert.IsType<RegisteredEmbeddingModel>(ModelRegistry.Find(conn, spec.ModelId)); // 汎用の入り口も、kind に応じた型を返す
            Assert.Single(ModelRegistry.ReadAll(conn));
        }
    }

    [Fact]
    public async Task Registering_the_same_model_again_changes_nothing()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", Spec(), T0, TestContext.Current.CancellationToken);
            ModelRegistration again = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", Spec(), T0.AddDays(1), TestContext.Current.CancellationToken);
            Assert.Equal("1", Text(db, "SELECT count(*) FROM models"));
            Assert.Equal("2026-11-05T03:34:56.789Z", Text(db, "SELECT created_at FROM models")); // 最初の登録の時刻のまま
            Assert.Equal("2026-11-05T03:34:56.789Z", again.Model.CreatedAt);
            Assert.Equal(RegistrationOutcome.Unchanged, again.Outcome);
            Assert.Empty(again.Changes);
        }
    }

    [Fact]
    public async Task A_different_model_gets_its_own_row_and_the_old_one_stays()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", Spec("pre-v1"), T0, TestContext.Current.CancellationToken);
            await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", Spec("pre-v2"), T0, TestContext.Current.CancellationToken);
            using SqliteConnection conn = db.CreateReadConnection();
            Assert.Equal(2, ModelRegistry.ReadAll(conn).Count);
            Assert.NotEqual(Spec("pre-v1").ModelId, Spec("pre-v2").ModelId);
        }
    }

    private static EmbeddingModelSpec SpecWith(string visualPath = "visual/model.onnx", long visualSize = 100) => new("fam", new List<ModelFile>
    {
        new(ModelRole.VisualOnnx, visualPath, new string('1', 64), visualSize),
        new(ModelRole.TextualOnnx, "textual/model.onnx", new string('2', 64), 100),
        new(ModelRole.TextualExternalData, "textual/model.onnx_data", new string('3', 64), 100),
        new(ModelRole.Tokenizer, "textual/tokenizer.json", new string('4', 64), 100),
    }, "pre-v1", "clean-v1", 1152, "l2");

    [Fact]
    public async Task A_changed_path_or_size_or_name_updates_the_record_and_keeps_the_model_id_and_created_at()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            EmbeddingModelSpec original = SpecWith();
            await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", original, T0, TestContext.Current.CancellationToken);

            // パスだけ違う(配置の見直し)。内容(sha256)が同じなので model_id も同じ=再処理にならない。起動も止めない
            EmbeddingModelSpec moved = SpecWith(visualPath: "models/visual.onnx");
            Assert.Equal(original.ModelId, moved.ModelId);
            ModelRegistration r1 = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", moved, T0.AddDays(1), TestContext.Current.CancellationToken);
            Assert.Equal(RegistrationOutcome.Updated, r1.Outcome);
            Assert.Contains(r1.Changes, c => c.Contains("visual-onnx", StringComparison.Ordinal) && c.Contains("パス", StringComparison.Ordinal)
                                             && c.Contains("visual/model.onnx", StringComparison.Ordinal) && c.Contains("models/visual.onnx", StringComparison.Ordinal));
            Assert.Single(r1.Changes);
            Assert.Equal(moved.ToJson(), Text(db, "SELECT spec_json FROM models"));            // 記録が更新された
            Assert.Equal("1", Text(db, "SELECT count(*) FROM models"));
            Assert.Equal("2026-11-05T03:34:56.789Z", Text(db, "SELECT created_at FROM models")); // created_at は最初のまま
            using (SqliteConnection conn = db.CreateReadConnection())
            {
                RegisteredEmbeddingModel? found = ModelRegistry.FindEmbedding(conn, original.ModelId);   // 読み戻しの検証(model_id の再計算)も通る
                Assert.Equal("models/visual.onnx", found!.Spec.File(ModelRole.VisualOnnx).RelativePath);
            }

            // サイズだけ違う
            EmbeddingModelSpec resized = SpecWith(visualPath: "models/visual.onnx", visualSize: 999);
            ModelRegistration r2 = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", resized, T0, TestContext.Current.CancellationToken);
            Assert.Equal(RegistrationOutcome.Updated, r2.Outcome);
            Assert.Single(r2.Changes);
            Assert.Contains("サイズ", r2.Changes[0], StringComparison.Ordinal);

            // 名前だけ違う
            ModelRegistration r3 = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "other name", resized, T0, TestContext.Current.CancellationToken);
            Assert.Equal(RegistrationOutcome.Updated, r3.Outcome);
            Assert.Contains(r3.Changes, c => c.Contains("name", StringComparison.Ordinal) && c.Contains("other name", StringComparison.Ordinal));
            Assert.Equal("other name", Text(db, "SELECT name FROM models"));

            // 同じものを続けて登録しても何も起きない
            ModelRegistration r4 = await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "other name", resized, T0, TestContext.Current.CancellationToken);
            Assert.Equal(RegistrationOutcome.Unchanged, r4.Outcome);
        }
    }

    [Fact]
    public void Describing_changes_refuses_an_identity_mismatch_even_when_the_id_is_the_same()
    {
        // 同じ id なのに識別(正準文字列)が違う = id の衝突。通常は起きない(12 桁の接頭辞の衝突)ので、判定の関数に別の仕様を直接渡して確かめる
        EmbeddingModelSpec a = Spec("pre-v1");
        EmbeddingModelSpec b = Spec("pre-v2");
        Assert.NotEqual(a.CanonicalString(), b.CanonicalString());
        var existing = new RegisteredEmbeddingModel(a.ModelId, "name", a, "2026-11-05T00:00:00.000Z");
        var e = Assert.Throws<ModelMismatchException>(() => ModelRegistry.DescribeChanges(existing, "name", b));
        Assert.Contains(a.ModelId, e.Message, StringComparison.Ordinal);
        Assert.Contains("衝突", e.Message, StringComparison.Ordinal);
        // 識別が同じなら、違いが無いことも返せる
        Assert.Empty(ModelRegistry.DescribeChanges(existing, "name", Spec("pre-v1")));
    }

    // ---------------------------------------------------------------- 行が壊れている・書き換えられている

    private static async Task<(TempDirectory Dir, OhanamiDatabase Db, EmbeddingModelSpec Spec)> RegisteredDb()
    {
        var (dir, db) = NewDb();
        EmbeddingModelSpec spec = Spec();
        await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", spec, T0, TestContext.Current.CancellationToken);
        return (dir, db, spec);
    }

    private static void Update(OhanamiDatabase db, string column, string value, string id)
    {
        using SqliteConnection conn = TestDb.OpenRaw(db.Path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE models SET {column} = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task A_spec_json_that_was_edited_so_the_recomputed_model_id_differs_throws()
    {
        var (dir, db, spec) = await RegisteredDb();
        using (dir) using (db)
        {
            // sha256 を 1 桁変える(JSON としては正しい)→ 再計算した model_id が行の id と違う
            string tampered = spec.ToJson().Replace(new string('1', 64), new string('1', 63) + "2", StringComparison.Ordinal);
            Assert.NotEqual(spec.ToJson(), tampered);
            Update(db, "spec_json", tampered, spec.ModelId);

            using SqliteConnection conn = db.CreateReadConnection();
            var e = Assert.Throws<ModelMismatchException>(() => ModelRegistry.Find(conn, spec.ModelId));
            Assert.Contains(spec.ModelId, e.Message, StringComparison.Ordinal);
            Assert.Contains(EmbeddingModelSpec.FromJson(tampered).ModelId, e.Message, StringComparison.Ordinal); // 再計算した値も出る
            Assert.Throws<ModelMismatchException>(() => ModelRegistry.ReadAll(conn));
            await Assert.ThrowsAsync<ModelMismatchException>(() => ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "name", spec, T0, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"format\":2}")]
    public async Task A_broken_spec_json_throws_ModelMismatchException_naming_the_row(string brokenJson)
    {
        var (dir, db, spec) = await RegisteredDb();
        using (dir) using (db)
        {
            Update(db, "spec_json", brokenJson, spec.ModelId);
            using SqliteConnection conn = db.CreateReadConnection();
            var e = Assert.Throws<ModelMismatchException>(() => ModelRegistry.Find(conn, spec.ModelId));
            Assert.Contains(spec.ModelId, e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_row_whose_id_was_changed_throws()
    {
        var (dir, db, spec) = await RegisteredDb();
        using (dir) using (db)
        {
            const string NewId = "fam:000000000000";
            Update(db, "id", NewId, spec.ModelId);
            using SqliteConnection conn = db.CreateReadConnection();
            var e = Assert.Throws<ModelMismatchException>(() => ModelRegistry.Find(conn, NewId));
            Assert.Contains(NewId, e.Message, StringComparison.Ordinal);
            Assert.Contains(spec.ModelId, e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_unknown_kind_throws_UnknownEnumValueException()
    {
        var (dir, db, spec) = await RegisteredDb();
        using (dir) using (db)
        {
            Update(db, "kind", "vlm-from-the-future", spec.ModelId);
            using SqliteConnection conn = db.CreateReadConnection();
            var e = Assert.Throws<UnknownEnumValueException>(() => ModelRegistry.Find(conn, spec.ModelId));
            Assert.Equal("vlm-from-the-future", e.Value);
        }
    }

    [Fact]
    public async Task Find_returns_null_when_there_is_no_row()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            using SqliteConnection conn = db.CreateReadConnection();
            Assert.Null(ModelRegistry.Find(conn, "fam:000000000000"));
            Assert.Empty(ModelRegistry.ReadAll(conn));
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Rows_are_read_by_kind_embedding_rows_become_typed_embedding_models_and_unknown_kinds_throw()
    {
        var (dir, db, spec) = await RegisteredDb();
        using (dir) using (db)
        {
            using (SqliteConnection conn = db.CreateReadConnection())
            {
                RegisteredModel row = Assert.Single(ModelRegistry.ReadAll(conn));
                RegisteredEmbeddingModel typed = Assert.IsType<RegisteredEmbeddingModel>(row);
                Assert.Equal(ModelKind.ImageEmbedding, typed.Kind);
                Assert.Equal(spec.ModelId, typed.Spec.ModelId);
                Assert.Null(ModelRegistry.FindEmbedding(conn, "fam:000000000000"));
            }

            // 知っているのは image-embedding と vlm だけ。知らない kind は、読み方を足すまで例外のまま(黙って読み飛ばさない)
            Update(db, "kind", "audio-embedding", spec.ModelId);
            using SqliteConnection conn2 = db.CreateReadConnection();
            Assert.Throws<UnknownEnumValueException>(() => ModelRegistry.ReadAll(conn2));
            Assert.Throws<UnknownEnumValueException>(() => ModelRegistry.FindEmbedding(conn2, spec.ModelId));
            Assert.Throws<UnknownEnumValueException>(() => ModelRegistry.Validate(spec.ModelId, "audio-embedding", "n", spec.ToJson(), "2026-11-05T00:00:00.000Z"));
        }
    }
}
