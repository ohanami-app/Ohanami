using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Search;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>見た目の経路: 全ベクトルとの内積(総当たり)と、状態に応じた返し方。</summary>
public sealed class VisualPathTests
{
    private static VectorTable ReadyTable(params (int Id, double Angle)[] items)
    {
        var t = new VectorTable("model-x", Dim);
        foreach ((int id, double angle) in items) t.Add(Hash(id), Unit(angle));
        SetReady(t);
        return t;
    }

    /// <summary>DB なしで Ready にする(テスト用)。</summary>
    private static void SetReady(VectorTable t)
    {
        t.BeginLoading();
        t.EndLoading();
    }

    private static QueryEmbedder Fixed(float[] v) => (_, _) => Task.FromResult(v);

    [Fact]
    public async Task Scores_every_vector_with_its_cosine_to_the_query_in_table_order()
    {
        VectorTable table = ReadyTable((1, 0), (2, 30), (3, 60), (4, 90), (5, 180));
        var path = new VisualPath(table, Fixed(Unit(0)), null);
        PathSearchResult r = await path.SearchAsync("anything", TestContext.Current.CancellationToken);

        Assert.Equal(Ohanami.Core.PathState.Ok, r.State);
        Assert.Equal(5, r.Hashes.Count);                        // 全件(上位 N 件に切らない)
        Assert.Equal(new[] { 1.0, Math.Cos(30 * Math.PI / 180), 0.5, 0.0, -1.0 }, r.Scores.ToArray(), new ScoreComparer());
        Assert.Equal(Hash(2), r.Hashes[1]);
        var d = Assert.IsType<VisualDetail>(r.DetailOf(1));
        Assert.Equal(r.Scores[1], d.Cosine);
        Assert.Equal("visual", path.PathId);
        Assert.Equal("model-x", path.ModelId);
    }

    private sealed class ScoreComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x is not null && y is not null && x.Length == y.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) < 1e-6);
        public int GetHashCode(double[] obj) => obj.Length;
    }

    [Fact]
    public async Task The_scores_match_a_naive_dot_product_for_random_unit_vectors()
    {
        var rng = new Random(3);
        float[] Rand()
        {
            var v = new float[Dim];
            for (int i = 0; i < Dim; i++) v[i] = (float)(rng.NextDouble() - 0.5);
            double n = Math.Sqrt(v.Sum(x => (double)x * x));
            return v.Select(x => (float)(x / n)).ToArray();
        }
        var table = new VectorTable("m", Dim);
        var vectors = new List<float[]>();
        for (int i = 0; i < 200; i++) { float[] v = Rand(); vectors.Add(v); table.Add(Hash(i), v); }
        SetReady(table);
        float[] q = Rand();
        PathSearchResult r = await new VisualPath(table, Fixed(q), null).SearchAsync("q", TestContext.Current.CancellationToken);
        for (int i = 0; i < vectors.Count; i++)
        {
            double naive = 0;
            for (int d = 0; d < Dim; d++) naive += (double)vectors[i][d] * q[d];
            Assert.Equal(naive, r.Scores[i], 5);
        }
    }

    [Theory]
    [InlineData(13)]
    [InlineData(1152)]
    [InlineData(7)]
    public void Dot_handles_any_length_including_the_scalar_tail(int length)
    {
        var rng = new Random(length);
        float[] a = Enumerable.Range(0, length).Select(_ => (float)rng.NextDouble()).ToArray();
        float[] b = Enumerable.Range(0, length).Select(_ => (float)rng.NextDouble()).ToArray();
        double naive = 0;
        for (int i = 0; i < length; i++) naive += (double)a[i] * b[i];
        Assert.Equal(naive, VisualPath.Dot(a, b), 3);
        Assert.Throws<ArgumentException>(() => VisualPath.Dot(a, new float[length + 1]));
    }

    [Fact]
    public async Task While_the_table_is_not_ready_the_path_reports_NotReady_without_embedding_the_query()
    {
        int embedCalls = 0;
        QueryEmbedder counting = (_, _) => { embedCalls++; return Task.FromResult(Unit(0)); };
        var table = new VectorTable("m", Dim);
        table.Add(Hash(1), Unit(10));
        var path = new VisualPath(table, counting, null);

        PathSearchResult notLoaded = await path.SearchAsync("q", TestContext.Current.CancellationToken);
        Assert.Equal(Ohanami.Core.PathState.NotReady, notLoaded.State);
        Assert.Empty(notLoaded.Hashes);

        table.BeginLoading();
        PathSearchResult loading = await path.SearchAsync("q", TestContext.Current.CancellationToken);
        Assert.Equal(Ohanami.Core.PathState.NotReady, loading.State);
        Assert.Contains("読み込み中", loading.Message, StringComparison.Ordinal);
        Assert.Empty(loading.Hashes);
        Assert.Equal(0, embedCalls);                               // 準備中の間は、クエリの埋め込み(0.2 秒)を無駄にしない

        table.EndLoading();
        PathSearchResult ready = await path.SearchAsync("q", TestContext.Current.CancellationToken);
        Assert.Equal(Ohanami.Core.PathState.Ok, ready.State);
        Assert.Single(ready.Hashes);
        Assert.Equal(1, embedCalls);
    }

    [Fact]
    public async Task A_failed_table_makes_the_search_throw_so_the_service_reports_Failed()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string model = await RegisterModelAsync(db);
            await AddVectorRowAsync(db, model, Hash(1), new byte[3]);
            var table = new VectorTable(model, Dim);
            await Assert.ThrowsAsync<InvalidDataException>(() => table.LoadAsync(db, TestContext.Current.CancellationToken));
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new VisualPath(table, Fixed(Unit(0)), null).SearchAsync("q", TestContext.Current.CancellationToken));
            Assert.Contains("読み込みに失敗", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_query_vector_of_the_wrong_length_or_with_non_finite_values_is_rejected()
    {
        VectorTable table = ReadyTable((1, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VisualPath(table, Fixed(new float[Dim + 1]), null).SearchAsync("q", TestContext.Current.CancellationToken));
        float[] nan = Unit(0);
        nan[2] = float.NaN;
        await Assert.ThrowsAsync<InvalidDataException>(() => new VisualPath(table, Fixed(nan), null).SearchAsync("q", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_table_gives_an_ok_result_with_an_empty_population()
    {
        VectorTable table = ReadyTable();
        PathSearchResult r = await new VisualPath(table, Fixed(Unit(0)), null).SearchAsync("q", TestContext.Current.CancellationToken);
        Assert.Equal(Ohanami.Core.PathState.Ok, r.State);
        Assert.Empty(r.Hashes);
    }

    [Fact]
    public async Task The_search_uses_a_snapshot_so_vectors_added_after_it_started_are_not_half_included()
    {
        VectorTable table = ReadyTable((1, 0), (2, 45));
        var gate = new TaskCompletionSource<float[]>();
        var path = new VisualPath(table, (_, _) => gate.Task, null);
        Task<PathSearchResult> pending = path.SearchAsync("q", TestContext.Current.CancellationToken);
        table.Add(Hash(3), Unit(90));                              // クエリの埋め込み中に、埋め込みの進みで足された
        gate.SetResult(Unit(0));
        PathSearchResult r = await pending;
        Assert.Equal(3, r.Hashes.Count);                           // スナップショットは、クエリのベクトルができたあとに取る(足された分も含む)
        Assert.Equal(r.Hashes.Count, r.Scores.Count);
    }

    [Fact]
    public async Task An_overwritten_image_is_scored_once_with_only_its_newer_vector()
    {
        VectorTable table = ReadyTable((1, 0), (2, 90), (3, 180));
        table.Add(Hash(1), Unit(60));                              // 再埋め込みで値が変わった(古い行は無効)
        table.Add(Hash(3), Unit(120));
        var path = new VisualPath(table, Fixed(Unit(0)), null);
        PathSearchResult r = await path.SearchAsync("q", TestContext.Current.CancellationToken);

        Assert.Equal(3, r.Hashes.Count);                           // 件数は増えない
        Assert.Equal(3, r.Hashes.Distinct().Count());
        Assert.Equal(r.Hashes.Count, r.Scores.Count);
        double Score(int id) => r.Scores[r.Hashes.ToList().IndexOf(Hash(id))];
        Assert.Equal(0.5, Score(1), 5);                            // 新しいほう(cos 60°)だけ。古い(cos 0° = 1)は出ない
        Assert.Equal(0.0, Score(2), 5);
        Assert.Equal(-0.5, Score(3), 5);
        for (int i = 0; i < r.Hashes.Count; i++)                   // 詳細も、同じ位置のスコアと合っている
            Assert.Equal(r.Scores[i], Assert.IsType<VisualDetail>(r.DetailOf(i)).Cosine);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        VectorTable table = ReadyTable((1, 0));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VisualPath(table, (_, ct) => Task.FromCanceled<float[]>(ct), null).SearchAsync("q", cts.Token));
    }
}
