using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Core.Models;

/// <summary>
/// models の行を読み戻して検証した結果。kind ごとに仕様の型が違う(埋め込みモデルの行と、VLM の行 = GGUF・mmproj・llama-server のビルド・
/// プロンプト・スキーマ・サンプリングを持つ型がある。それが <see cref="ModelRegistry.ReadAll"/> を壊さないよう、読み方は kind で分ける)。
/// </summary>
internal abstract record RegisteredModel(string Id, ModelKind Kind, string Name, string CreatedAt);

/// <summary>kind = image-embedding の行。仕様は <see cref="EmbeddingModelSpec"/>。</summary>
internal sealed record RegisteredEmbeddingModel(string Id, string Name, EmbeddingModelSpec Spec, string CreatedAt)
    : RegisteredModel(Id, ModelKind.ImageEmbedding, Name, CreatedAt);

/// <summary>kind = vlm の行。仕様は <see cref="VlmSpec"/>。</summary>
internal sealed record RegisteredVlmModel(string Id, string Name, VlmSpec Spec, string CreatedAt)
    : RegisteredModel(Id, ModelKind.Vlm, Name, CreatedAt);

/// <summary>登録の結果: 新しく追加した、すでに同じだった、記録(名前・パス・サイズ)を更新した。</summary>
internal enum RegistrationOutcome { Created, Unchanged, Updated }

/// <summary>
/// <see cref="ModelRegistry.EnsureEmbeddingModelRegisteredAsync"/> の結果。<see cref="Changes"/> は更新したときの内容(ログに出すためのもの。
/// ログの出し先がまだ無いので戻り値で返す)。
/// </summary>
internal sealed record ModelRegistration(RegisteredEmbeddingModel Model, RegistrationOutcome Outcome, IReadOnlyList<string> Changes);

/// <summary><see cref="ModelRegistry.EnsureVlmModelRegisteredAsync"/> の結果(<see cref="ModelRegistration"/> の VLM 版)。</summary>
internal sealed record VlmModelRegistration(RegisteredVlmModel Model, RegistrationOutcome Outcome, IReadOnlyList<string> Changes);

/// <summary>
/// models テーブルの読み書き。行を読んだら、必ず kind ごとの読み方で検証する(kind を知っている、spec_json が読める、
/// spec_json から再計算した model_id が id と一致する。不一致・壊れた spec_json・知らない kind は例外。黙って信用しない)。
/// 製品の経路が使うのは、現行の埋め込みモデルの <see cref="FindEmbedding"/> だけ。<see cref="ReadAll"/> は診断用。
/// </summary>
internal static class ModelRegistry
{
    /// <summary>
    /// 埋め込みモデルの spec を models に登録する。
    /// <list type="bullet">
    /// <item>行が無ければ追加する(<see cref="RegistrationOutcome.Created"/>)。</item>
    /// <item>同じ id の行があれば、行を検証したうえで、<b>識別(正準文字列)と kind</b> が同じことを確かめる。違えば(= id の衝突、または kind の違い)
    ///   <see cref="ModelMismatchException"/>(上書きしない)。</item>
    /// <item>識別が同じで、name・パス・サイズだけが違うときは、記録を更新する(<see cref="RegistrationOutcome.Updated"/>、内容は <see cref="ModelRegistration.Changes"/>)。
    ///   model_id はパスとサイズを入れないので、配置の見直しで再処理にならず、起動も止めない。created_at は変えない。</item>
    /// <item>すべて同じなら何もしない(<see cref="RegistrationOutcome.Unchanged"/>)。</item>
    /// </list>
    /// </summary>
    public static Task<ModelRegistration> EnsureEmbeddingModelRegisteredAsync(
        OhanamiDatabase db, string name, EmbeddingModelSpec spec, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string id = spec.ModelId;
        string json = spec.ToJson();
        string created = DbTime.Format(now);
        return db.WriteAsync((conn, tx) =>
        {
            RegisteredModel? existing = Find(conn, tx, id);
            if (existing is null)
            {
                using SqliteCommand insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO models (id, kind, name, spec_json, created_at) VALUES ($id, $kind, $name, $spec, $created)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$kind", DbEnums.ToDb(ModelKind.ImageEmbedding));
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$spec", json);
                insert.Parameters.AddWithValue("$created", created);
                insert.ExecuteNonQuery();
                return new ModelRegistration(new RegisteredEmbeddingModel(id, name, spec, created), RegistrationOutcome.Created, []);
            }

            if (existing is not RegisteredEmbeddingModel current)
            {
                throw new ModelMismatchException(
                    $"models に同じ id「{id}」の行があるが、kind が違う(行は {DbEnums.ToDb(existing.Kind)}、登録しようとしたのは {DbEnums.ToDb(ModelKind.ImageEmbedding)})。",
                    id, DbEnums.ToDb(ModelKind.ImageEmbedding), DbEnums.ToDb(existing.Kind));
            }

            IReadOnlyList<string> changes = DescribeChanges(current, name, spec);
            if (changes.Count == 0) return new ModelRegistration(current, RegistrationOutcome.Unchanged, []);

            using SqliteCommand update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE models SET name = $name, spec_json = $spec WHERE id = $id";
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$spec", json);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            return new ModelRegistration(new RegisteredEmbeddingModel(id, name, spec, current.CreatedAt), RegistrationOutcome.Updated, changes);
        }, ct);
    }

    /// <summary>
    /// 行の仕様と、これから登録する仕様の違い(name・パス・サイズ)を、人が読める文字列で返す(違いが無ければ空)。
    /// 識別(正準文字列)が違えば <see cref="ModelMismatchException"/>(同じ id なのに識別が違う = id の衝突。上書きしない)。
    /// </summary>
    internal static IReadOnlyList<string> DescribeChanges(RegisteredEmbeddingModel existing, string name, EmbeddingModelSpec incoming)
    {
        if (!string.Equals(existing.Spec.CanonicalString(), incoming.CanonicalString(), StringComparison.Ordinal))
        {
            throw new ModelMismatchException(
                $"models の行「{existing.Id}」と登録しようとした仕様で、model_id が同じなのに識別(正準文字列)が違う(id の衝突、または行が壊れている)。" +
                $"行: {existing.Spec.CanonicalString().ReplaceLineEndings(" / ")} 登録しようとした: {incoming.CanonicalString().ReplaceLineEndings(" / ")}",
                existing.Id, existing.Spec.CanonicalString(), incoming.CanonicalString());
        }

        var changes = new List<string>();
        if (!string.Equals(existing.Name, name, StringComparison.Ordinal)) changes.Add($"name: 「{existing.Name}」→「{name}」");
        foreach (ModelRole role in ModelRoles.All)
        {
            ModelFile before = existing.Spec.File(role), after = incoming.File(role);
            string label = ModelRoles.ToText(role);
            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal)) changes.Add($"{label} のパス: {before.RelativePath} → {after.RelativePath}");
            if (before.Size != after.Size)
                changes.Add($"{label} のサイズ: {before.Size.ToString(CultureInfo.InvariantCulture)} → {after.Size.ToString(CultureInfo.InvariantCulture)}");
        }
        return changes;
    }

    /// <summary>
    /// VLM の spec を models に登録する。規則は <see cref="EnsureEmbeddingModelRegisteredAsync"/> と同じ: 行が無ければ追加(Created)、同じ id の行は検証して
    /// **識別(正準文字列)と kind** が同じことを確かめ(違えば <see cref="ModelMismatchException"/>。上書きしない)、識別が同じで name・パス・サイズ・
    /// Hugging Face の情報だけが違うときは記録を更新する(Updated。created_at は変えない)。すべて同じなら何もしない(Unchanged)。
    /// </summary>
    public static Task<VlmModelRegistration> EnsureVlmModelRegisteredAsync(
        OhanamiDatabase db, string name, VlmSpec spec, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string id = spec.ModelId;
        string json = spec.ToJson();
        string created = DbTime.Format(now);
        return db.WriteAsync((conn, tx) =>
        {
            RegisteredModel? existing = Find(conn, tx, id);
            if (existing is null)
            {
                using SqliteCommand insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO models (id, kind, name, spec_json, created_at) VALUES ($id, $kind, $name, $spec, $created)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$kind", DbEnums.ToDb(ModelKind.Vlm));
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$spec", json);
                insert.Parameters.AddWithValue("$created", created);
                insert.ExecuteNonQuery();
                return new VlmModelRegistration(new RegisteredVlmModel(id, name, spec, created), RegistrationOutcome.Created, []);
            }

            if (existing is not RegisteredVlmModel current)
            {
                throw new ModelMismatchException(
                    $"models に同じ id「{id}」の行があるが、kind が違う(行は {DbEnums.ToDb(existing.Kind)}、登録しようとしたのは {DbEnums.ToDb(ModelKind.Vlm)})。",
                    id, DbEnums.ToDb(ModelKind.Vlm), DbEnums.ToDb(existing.Kind));
            }

            IReadOnlyList<string> changes = DescribeVlmChanges(current, name, spec);
            if (changes.Count == 0) return new VlmModelRegistration(current, RegistrationOutcome.Unchanged, []);

            using SqliteCommand update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE models SET name = $name, spec_json = $spec WHERE id = $id";
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$spec", json);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            return new VlmModelRegistration(new RegisteredVlmModel(id, name, spec, current.CreatedAt), RegistrationOutcome.Updated, changes);
        }, ct);
    }

    /// <summary>VLM の行の仕様と、これから登録する仕様の違い(name・パス・サイズ・Hugging Face の情報)を返す。識別(正準文字列)が違えば <see cref="ModelMismatchException"/>。</summary>
    internal static IReadOnlyList<string> DescribeVlmChanges(RegisteredVlmModel existing, string name, VlmSpec incoming)
    {
        if (!string.Equals(existing.Spec.CanonicalString(), incoming.CanonicalString(), StringComparison.Ordinal))
        {
            throw new ModelMismatchException(
                $"models の行「{existing.Id}」と登録しようとした仕様で、model_id が同じなのに識別(正準文字列)が違う(id の衝突、または行が壊れている)。" +
                $"行: {existing.Spec.CanonicalString().ReplaceLineEndings(" / ")} 登録しようとした: {incoming.CanonicalString().ReplaceLineEndings(" / ")}",
                existing.Id, existing.Spec.CanonicalString(), incoming.CanonicalString());
        }

        var changes = new List<string>();
        if (!string.Equals(existing.Name, name, StringComparison.Ordinal)) changes.Add($"name: 「{existing.Name}」→「{name}」");
        foreach ((string label, VlmFile before, VlmFile after) in new[] { ("gguf", existing.Spec.Gguf, incoming.Gguf), ("mmproj", existing.Spec.Mmproj, incoming.Mmproj) })
        {
            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal)) changes.Add($"{label} のパス: {before.RelativePath} → {after.RelativePath}");
            if (before.Size != after.Size)
                changes.Add($"{label} のサイズ: {before.Size.ToString(CultureInfo.InvariantCulture)} → {after.Size.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!string.Equals(existing.Spec.HfRepository, incoming.HfRepository, StringComparison.Ordinal)) changes.Add($"hf_repo: {existing.Spec.HfRepository} → {incoming.HfRepository}");
        if (!string.Equals(existing.Spec.HfRevision, incoming.HfRevision, StringComparison.Ordinal)) changes.Add($"hf_revision: {existing.Spec.HfRevision} → {incoming.HfRevision}");
        return changes;
    }

    /// <summary>id の行を読んで検証する。行が無ければ null。壊れていれば <see cref="ModelMismatchException"/> / <see cref="UnknownEnumValueException"/>。</summary>
    public static RegisteredModel? Find(SqliteConnection conn, string id) => Find(conn, null, id);

    /// <summary>
    /// id の行を、埋め込みモデルとして読んで検証する(製品の経路が使う入り口)。行が無ければ null。
    /// 行が埋め込みモデル以外の kind なら <see cref="ModelMismatchException"/>。
    /// </summary>
    public static RegisteredEmbeddingModel? FindEmbedding(SqliteConnection conn, string id) =>
        Find(conn, null, id) switch
        {
            null => null,
            RegisteredEmbeddingModel e => e,
            RegisteredModel other => throw new ModelMismatchException(
                $"models の行「{id}」は埋め込みモデルではない(kind = {DbEnums.ToDb(other.Kind)})。", id, DbEnums.ToDb(ModelKind.ImageEmbedding), DbEnums.ToDb(other.Kind)),
        };

    /// <summary>id の行を VLM として読んで検証する。行が無ければ null。行が VLM 以外の kind なら <see cref="ModelMismatchException"/>。</summary>
    public static RegisteredVlmModel? FindVlm(SqliteConnection conn, string id) =>
        Find(conn, null, id) switch
        {
            null => null,
            RegisteredVlmModel v => v,
            RegisteredModel other => throw new ModelMismatchException(
                $"models の行「{id}」は VLM ではない(kind = {DbEnums.ToDb(other.Kind)})。", id, DbEnums.ToDb(ModelKind.Vlm), DbEnums.ToDb(other.Kind)),
        };

    private static RegisteredModel? Find(SqliteConnection conn, SqliteTransaction? tx, string id)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, kind, name, spec_json, created_at FROM models WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? FromRow(r) : null;
    }

    /// <summary>models の全行を、kind ごとの読み方で検証して返す(id の順)。診断用。</summary>
    public static IReadOnlyList<RegisteredModel> ReadAll(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, name, spec_json, created_at FROM models ORDER BY id";
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<RegisteredModel>();
        while (r.Read()) list.Add(FromRow(r));
        return list;
    }

    private static RegisteredModel FromRow(SqliteDataReader r) =>
        Validate(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    /// <summary>
    /// 行の中身を、kind ごとの読み方で検証する。kind を知らなければ <see cref="UnknownEnumValueException"/>。
    /// image-embedding: spec_json が読めて、そこから再計算した model_id が id と一致する。
    /// vlm: 同じ検査(spec_json が読めて、再計算した model_id が id と一致する)。kind が増えたら、ここに分岐を足す(読み方の無い kind は例外。黙って読み飛ばさない)。
    /// </summary>
    public static RegisteredModel Validate(string id, string kindText, string name, string specJson, string createdAt)
    {
        ModelKind kind = DbEnums.ModelKindFromDb(kindText);
        switch (kind)
        {
            case ModelKind.ImageEmbedding:
                return ValidateEmbedding(id, name, specJson, createdAt);
            case ModelKind.Vlm:
                return ValidateVlm(id, name, specJson, createdAt);
            default:
                throw new InvalidOperationException($"models.kind「{kindText}」の行の読み方が ModelRegistry.Validate に無い(列挙に値を足したら、ここにも足す)。");
        }
    }

    private static RegisteredEmbeddingModel ValidateEmbedding(string id, string name, string specJson, string createdAt)
    {
        EmbeddingModelSpec spec;
        try
        {
            spec = EmbeddingModelSpec.FromJson(specJson);
        }
        catch (ModelMismatchException e)
        {
            throw new ModelMismatchException($"models の行「{id}」の spec_json を読めない: {e.Message}", e);
        }
        string recomputed = spec.ModelId;
        if (!string.Equals(recomputed, id, StringComparison.Ordinal))
        {
            throw new ModelMismatchException(
                $"models の行の id と、spec_json から再計算した model_id が違う: 行の id「{id}」、再計算した値「{recomputed}」。行が壊れているか、書き換えられている。",
                id, id, recomputed);
        }
        return new RegisteredEmbeddingModel(id, name, spec, createdAt);
    }

    private static RegisteredVlmModel ValidateVlm(string id, string name, string specJson, string createdAt)
    {
        VlmSpec spec;
        try
        {
            spec = VlmSpec.FromJson(specJson);
        }
        catch (ModelMismatchException e)
        {
            throw new ModelMismatchException($"models の行「{id}」の spec_json を読めない: {e.Message}", e);
        }
        string recomputed = spec.ModelId;
        if (!string.Equals(recomputed, id, StringComparison.Ordinal))
        {
            throw new ModelMismatchException(
                $"models の行の id と、spec_json から再計算した model_id が違う: 行の id「{id}」、再計算した値「{recomputed}」。行が壊れているか、書き換えられている。",
                id, id, recomputed);
        }
        return new RegisteredVlmModel(id, name, spec, createdAt);
    }
}
