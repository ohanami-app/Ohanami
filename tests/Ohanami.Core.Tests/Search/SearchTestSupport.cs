using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>検索のテストの共通部品: 小さな次元(8)の合成モデル、DB への画像・パス・ベクトルの投入、偽の経路。</summary>
internal static class SearchTestSupport
{
    public const int Dim = 8;

    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>次元 8 の合成モデル(sha256 は適当。ファイルは読まない)。</summary>
    public static EmbeddingModelSpec Spec(string family = "synthetic") => new(family, new List<ModelFile>
    {
        new(ModelRole.VisualOnnx, "visual/model.onnx", new string('1', 64), 100),
        new(ModelRole.TextualOnnx, "textual/model.onnx", new string('2', 64), 100),
        new(ModelRole.TextualExternalData, "textual/model.onnx_data", new string('3', 64), 100),
        new(ModelRole.Tokenizer, "textual/tokenizer.json", new string('4', 64), 100),
    }, "pre-v1", "clean-v1", Dim, "l2");

    /// <summary>i から決まる 64 桁の小文字 16 進のハッシュ(images.hash の制約を満たす)。</summary>
    public static string Hash(int i) => i.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0');

    /// <summary>e0 から angleDegrees だけ回した単位ベクトル(e0・e1 の平面上)。e0 とのコサインは cos(angle)。</summary>
    public static float[] Unit(double angleDegrees)
    {
        double r = angleDegrees * Math.PI / 180;
        var v = new float[Dim];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    public static (TempDirectory Dir, OhanamiDatabase Db) NewDb()
    {
        var dir = new TempDirectory();
        return (dir, OhanamiDatabase.Open(dir.File("test.db")));
    }

    /// <summary>models に行を作り(image_vectors の外部キー用)、その model_id を返す。</summary>
    public static async Task<string> RegisterModelAsync(OhanamiDatabase db, EmbeddingModelSpec? spec = null)
    {
        spec ??= Spec();
        await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db, "synthetic", spec, T0, TestContext.Current.CancellationToken);
        return spec.ModelId;
    }

    public static Task<long> AddFolderAsync(OhanamiDatabase db, string path = @"C:\shots") =>
        db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO folders (path, enabled, added_at) VALUES ($p, 1, '2026-11-05T00:00:00.000Z') RETURNING id";
            cmd.Parameters.AddWithValue("$p", path);
            return (long)cmd.ExecuteScalar()!;
        });

    /// <summary>画像 1 枚(hash)と、そのパス。missingPaths のパスには missing_since を付ける。</summary>
    public static Task AddImageAsync(OhanamiDatabase db, long folderId, string hash, string[] presentPaths, string[]? missingPaths = null) =>
        db.WriteAsync((conn, tx) =>
        {
            Exec(conn, tx, "INSERT OR IGNORE INTO images (hash, size, format, first_seen_at) VALUES ($h, 100, 'png', '2026-11-05T00:00:00.000Z')", ("$h", hash));
            foreach (string p in presentPaths)
                Exec(conn, tx, "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size) VALUES ($p, $h, $f, 1, 100)", ("$p", p), ("$h", hash), ("$f", folderId));
            foreach (string p in missingPaths ?? [])
                Exec(conn, tx, "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ($p, $h, $f, 1, 100, '2026-11-05T01:00:00.000Z')",
                    ("$p", p), ("$h", hash), ("$f", folderId));
        });

    /// <summary>image_vectors の行を足す(blob の長さを呼び出し側が選べる。壊れた行のテスト用)。</summary>
    public static Task AddVectorRowAsync(OhanamiDatabase db, string modelId, string hash, byte[] blob) =>
        db.WriteAsync((conn, tx) =>
        {
            Exec(conn, tx, "INSERT OR IGNORE INTO images (hash, size, format, first_seen_at) VALUES ($h, 100, 'png', '2026-11-05T00:00:00.000Z')", ("$h", hash));
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO image_vectors (hash, model_id, embedding) VALUES ($h, $m, $e)";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$e", blob);
            cmd.ExecuteNonQuery();
        });

    public static Task AddVectorAsync(OhanamiDatabase db, string modelId, string hash, float[] v) =>
        AddVectorRowAsync(db, modelId, hash, MemoryMarshal.AsBytes(v.AsSpan()).ToArray());

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>決まったスコアを返す偽の経路(RRF・Hits の並び・状態の報告のテスト用)。</summary>
internal sealed class FakePath : ISearchPath
{
    private readonly Func<PathSearchResult> _result;

    public FakePath(string id, string? modelId, Func<PathSearchResult> result)
    {
        PathId = id;
        DisplayName = id;
        ModelId = modelId;
        _result = result;
    }

    public string PathId { get; }
    public string DisplayName { get; }
    public string? ModelId { get; }

    public Task<PathSearchResult> SearchAsync(string query, CancellationToken ct) => Task.FromResult(_result());

    /// <summary>hashes[i] のスコアが scores[i] の結果(説明は VisualDetail(スコア))。</summary>
    public static FakePath Scored(string id, string[] hashes, double[] scores, string? modelId = "fake-model") =>
        new(id, modelId, () => PathSearchResult.Ok(hashes, scores, i => new VisualDetail(scores[i], scores[i])));
}
