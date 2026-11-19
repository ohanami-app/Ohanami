using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Data;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>検索サービス: 見た目の経路 + 融合(RRF)+ Limit + 状態の報告 + missing_since の扱い(DB は実物、経路は合成・偽物)。</summary>
public sealed class SearchServiceTests(ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    /// <summary>画像 ids を、角度 angles(クエリ e0 とのコサイン = cos(角度))で DB とメモリの表に入れた、見た目の経路だけの検索サービス。</summary>
    private static async Task<(TempDirectory Dir, OhanamiDatabase Db, SearchService Service, VectorTable Table)> VisualEnvironment(
        (int Id, double Angle)[] items, Func<string, float[]>? embed = null)
    {
        var (dir, db) = NewDb();
        string model = await RegisterModelAsync(db);
        long folder = await AddFolderAsync(db);
        foreach ((int id, double angle) in items)
        {
            await AddImageAsync(db, folder, Hash(id), [$@"C:\shots\{id}.png"]);
            await AddVectorAsync(db, model, Hash(id), Unit(angle));
        }
        var table = new VectorTable(model, Dim);
        await table.LoadAsync(db, Ct);
        var path = new VisualPath(table, (text, _) => Task.FromResult((embed ?? (_ => Unit(0)))(text)), null);
        return (dir, db, new SearchService(db, [path], table), table);
    }

    [Fact]
    public async Task Visual_search_ranks_every_image_by_cosine_and_reports_the_hits()
    {
        var (dir, db, service, table) = await VisualEnvironment([(1, 50), (2, 10), (3, 90), (4, 30), (5, 70)]);
        using (dir) using (db)
        {
            SearchResponse r = await service.SearchAsync(new SearchQuery("anything"), Ct);

            // 角度が小さい順 = cosine が大きい順
            Assert.Equal([Hash(2), Hash(4), Hash(1), Hash(5), Hash(3)], r.Results.Select(x => x.Hash).ToArray());
            Assert.Equal([1.0, 2.0, 3.0, 4.0, 5.0], r.Results.Select(x => x.Rank).ToArray());
            Assert.Equal(5, r.IndexedImages);
            Assert.Equal(5, r.TotalImages);
            Assert.Equal("anything", r.Query);
            Assert.True(r.Elapsed >= TimeSpan.Zero);

            SearchResult first = r.Results[0];
            Assert.Equal(1.0 / 61, first.FusedScore, 12);                  // 経路 1 本の RRF = 1 / (60 + 順位)
            Assert.Equal([@"C:\shots\2.png"], first.Paths);
            PathHit hit = Assert.Single(first.Hits);
            Assert.Equal("visual", hit.PathId);                            // PathId は種類。モデルを替えても変わらない
            Assert.Equal(table.ModelId, hit.ModelId);                      // ModelId は別の欄
            Assert.NotEqual(hit.PathId, hit.ModelId);
            Assert.Equal(1.0, hit.Rank);
            Assert.Equal(Math.Cos(10 * Math.PI / 180), hit.Score, 5);
            Assert.Equal(hit.Score, Assert.IsType<VisualDetail>(hit.Detail).Cosine);

            PathOutcome outcome = Assert.Single(r.Paths);
            Assert.Equal(new PathOutcome("visual", table.ModelId, 5, PathState.Ok, null), outcome);
            Assert.Equal([new SearchPathInfo("visual", "見た目")], service.Paths);
        }
    }

    [Fact]
    public async Task The_query_text_is_embedded_by_the_query_embedder_and_decides_the_ranking()
    {
        // クエリ文字列ごとに別のベクトル(e0 と e1 方向)→ 順位が入れ替わる
        var (dir, db, service, _) = await VisualEnvironment([(1, 0), (2, 90)], text => text == "near-e1" ? Unit(90) : Unit(0));
        using (dir) using (db)
        {
            SearchResponse a = await service.SearchAsync(new SearchQuery("near-e0"), Ct);
            SearchResponse b = await service.SearchAsync(new SearchQuery("near-e1"), Ct);
            Assert.Equal(Hash(1), a.Results[0].Hash);
            Assert.Equal(Hash(2), b.Results[0].Hash);
        }
    }

    // ---------------------------------------------------------------- 融合(偽の経路 2 本)

    private static string[] Ids(int n) => Enumerable.Range(0, n).Select(Hash).ToArray();

    private static async Task<(TempDirectory Dir, OhanamiDatabase Db, VectorTable Table)> PlainDb(int images)
    {
        var (dir, db) = NewDb();
        long folder = await AddFolderAsync(db);
        for (int i = 0; i < images; i++) await AddImageAsync(db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
        return (dir, db, new VectorTable("fake-model", Dim));
    }

    [Fact]
    public async Task Fusion_uses_rrf_with_k_60_Limit_applies_after_fusion_and_hits_follow_the_path_order()
    {
        var (dir, db, table) = await PlainDb(10);
        using (dir) using (db)
        {
            string[] ids = Ids(10);
            // 経路 A: h0 > h1 > … > h9。経路 B はその逆(h9 > … > h0)。ただし h3 だけは両方で 4 位
            double[] a = Enumerable.Range(0, 10).Select(i => 100.0 - i).ToArray();
            // B の順位: h9 が 1 位、h8 が 2 位、h7 が 3 位、h3 が 4 位、h6 が 5 位、h5 が 6 位、h4 が 7 位、h2 が 8 位、h1 が 9 位、h0 が 10 位
            int[] bOrder = [9, 8, 7, 3, 6, 5, 4, 2, 1, 0];
            double[] b = new double[10];
            for (int rank = 0; rank < 10; rank++) b[bOrder[rank]] = 100.0 - rank;
            var pa = FakePath.Scored("a", ids, a);
            var pb = FakePath.Scored("b", ids, b);
            var service = new SearchService(db, [pa, pb], table);

            SearchResponse full = await service.SearchAsync(new SearchQuery("q", Limit: 10), Ct);
            // h3: A で 4 位・B で 4 位 → 2/64 = 0.03125 が最大。h0 (1 位+10 位) と h9 (10 位+1 位) は 1/61 + 1/70 で同点 → 平均順位 2.5
            Assert.Equal(Hash(3), full.Results[0].Hash);
            Assert.Equal(2.0 / 64, full.Results[0].FusedScore, 12);
            Assert.Equal(1.0, full.Results[0].Rank);
            Assert.Equal([2.5, 2.5], new[] { full.Results[1].Rank, full.Results[2].Rank });
            Assert.Equal([Hash(0), Hash(9)], new[] { full.Results[1].Hash, full.Results[2].Hash });     // 同点の並びは、表の添字の順
            Assert.Equal(1.0 / 61 + 1.0 / 70, full.Results[1].FusedScore, 12);

            // Limit は融合の後: 1 件だけなら、h3(どちらの経路でも上位 3 には入らない画像)が返る。経路ごとに上位 3 に切ってから融合していたら出ない
            SearchResponse one = await service.SearchAsync(new SearchQuery("q", Limit: 1), Ct);
            Assert.Equal([Hash(3)], one.Results.Select(x => x.Hash).ToArray());

            // Hits は経路の配列の順(a, b)。順位は経路ごとの平均順位、Score は経路のスコア
            PathHit[] hits = full.Results[0].Hits.ToArray();
            Assert.Equal(["a", "b"], hits.Select(h => h.PathId).ToArray());
            Assert.Equal([4.0, 4.0], hits.Select(h => h.Rank).ToArray());
            Assert.Equal([a[3], b[3]], hits.Select(h => h.Score).ToArray());

            Assert.Equal([PathState.Ok, PathState.Ok], full.Paths.Select(p => p.State).ToArray());
            Assert.Equal([10, 10], full.Paths.Select(p => p.Population).ToArray());
        }
    }

    [Fact]
    public async Task An_image_outside_one_paths_population_gets_hits_only_from_the_paths_that_have_it()
    {
        var (dir, db, table) = await PlainDb(4);
        using (dir) using (db)
        {
            // 経路 A は h0, h1, h2 だけ、経路 B は h2, h3 だけ(母集団が経路ごとに違う)
            var pa = FakePath.Scored("a", [Hash(0), Hash(1), Hash(2)], [3.0, 2.0, 1.0]);
            var pb = FakePath.Scored("b", [Hash(2), Hash(3)], [9.0, 8.0]);
            var service = new SearchService(db, [pa, pb], table);
            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);

            Dictionary<string, SearchResult> by = r.Results.ToDictionary(x => x.Hash);
            Assert.Equal(4, by.Count);                                                   // 母集団の和
            Assert.Equal(["a", "b"], by[Hash(2)].Hits.Select(h => h.PathId).ToArray());
            Assert.Equal(["a"], by[Hash(0)].Hits.Select(h => h.PathId).ToArray());       // B の母集団に入っていない経路は含めない
            Assert.Equal(["b"], by[Hash(3)].Hits.Select(h => h.PathId).ToArray());
            // h2: A で 3 位 + B で 1 位 = 1/63 + 1/61。h0: 1/61(A で 1 位)。h3: 1/62(B で 2 位)。h1: 1/62(A で 2 位)
            Assert.Equal(1.0 / 63 + 1.0 / 61, by[Hash(2)].FusedScore, 12);
            Assert.Equal(Hash(2), r.Results[0].Hash);
            Assert.Equal([3, 2], r.Paths.Select(p => p.Population).ToArray());
        }
    }

    // ---------------------------------------------------------------- 準備中・失敗

    [Fact]
    public async Task A_path_that_is_not_ready_is_reported_and_left_out_while_the_others_still_answer()
    {
        var (dir, db, table) = await PlainDb(3);
        using (dir) using (db)
        {
            var ready = FakePath.Scored("ready", Ids(3), [3.0, 2.0, 1.0]);
            var notReady = new FakePath("later", "m2", () => PathSearchResult.NotReady("ベクトルを読み込み中"));
            var service = new SearchService(db, [notReady, ready], table);
            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);

            Assert.Equal([Hash(0), Hash(1), Hash(2)], r.Results.Select(x => x.Hash).ToArray());
            Assert.All(r.Results, x => Assert.Equal(["ready"], x.Hits.Select(h => h.PathId).ToArray()));
            Assert.Equal(["later", "ready"], r.Paths.Select(p => p.PathId).ToArray());   // 経路の配列の順
            Assert.Equal(new PathOutcome("later", "m2", 0, PathState.NotReady, "ベクトルを読み込み中"), r.Paths[0]);
            Assert.Equal(PathState.Ok, r.Paths[1].State);
        }
    }

    [Fact]
    public async Task When_the_only_path_is_loading_the_result_is_empty_and_the_state_says_NotReady()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            long folder = await AddFolderAsync(db);
            await AddImageAsync(db, folder, Hash(1), [@"C:\a.png"]);
            var table = new VectorTable("m", Dim);
            table.BeginLoading();
            var service = new SearchService(db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null)], table);
            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);
            Assert.Empty(r.Results);
            PathOutcome o = Assert.Single(r.Paths);
            Assert.Equal(PathState.NotReady, o.State);
            Assert.Contains("読み込み中", o.Message, StringComparison.Ordinal);
            Assert.Equal(1, r.TotalImages);
            Assert.Equal(0, r.IndexedImages);

            // 読み込みが終われば答える
            table.Add(Hash(1), Unit(10));
            table.EndLoading();
            Assert.Single((await service.SearchAsync(new SearchQuery("q"), Ct)).Results);
        }
    }

    [Fact]
    public async Task A_path_that_throws_is_reported_as_Failed_with_the_reason_and_the_others_continue()
    {
        var (dir, db, table) = await PlainDb(2);
        using (dir) using (db)
        {
            var broken = new FakePath("broken", "m", () => throw new InvalidOperationException("壊れた"));
            var ok = FakePath.Scored("ok", Ids(2), [2.0, 1.0]);
            var service = new SearchService(db, [broken, ok], table);
            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);
            Assert.Equal(2, r.Results.Count);
            Assert.Equal(PathState.Failed, r.Paths[0].State);
            Assert.Contains("壊れた", r.Paths[0].Message, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", r.Paths[0].Message, StringComparison.Ordinal);
            Assert.Equal(PathState.Ok, r.Paths[1].State);
        }
    }

    [Fact]
    public async Task Cancellation_throws_instead_of_being_reported_as_a_failed_path()
    {
        var (dir, db, table) = await PlainDb(1);
        using (dir) using (db)
        {
            var slow = new SlowPath();
            var service = new SearchService(db, [slow], table);
            using var cts = new CancellationTokenSource();
            Task<SearchResponse> pending = service.SearchAsync(new SearchQuery("q"), cts.Token);
            await slow.Started.Task;
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    private sealed class SlowPath : ISearchPath
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PathId => "slow";
        public string DisplayName => "slow";
        public string? ModelId => null;

        public async Task<PathSearchResult> SearchAsync(string query, CancellationToken ct)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    // ---------------------------------------------------------------- missing_since

    [Fact]
    public async Task Images_whose_every_path_is_missing_or_that_have_no_path_never_appear_and_ranks_stay_contiguous()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string model = await RegisterModelAsync(db);
            long folder = await AddFolderAsync(db);
            await AddImageAsync(db, folder, Hash(1), [@"C:\shots\1.png"]);                                 // 普通
            await AddImageAsync(db, folder, Hash(2), [], [@"C:\shots\2-gone.png"]);                        // パスは全部 missing → 出ない
            await AddImageAsync(db, folder, Hash(3), [@"C:\shots\3-copy.png", @"C:\shots\3.png"], [@"C:\shots\3-gone.png"]);   // 一部 missing → 出る。パスは存在する分だけ
            // Hash(4) は image_paths の行が無い(フォルダの登録を解除したあとの、ベクトルだけ残った画像) → 出ない
            await AddVectorAsync(db, model, Hash(4), Unit(0));
            await AddImageAsync(db, folder, Hash(5), [@"C:\shots\5.png"]);
            // ベクトルは、cosine の大きい順に 4(0°)> 2(5°)> 3(10°)> 1(20°)> 5(40°)
            await AddVectorAsync(db, model, Hash(2), Unit(5));
            await AddVectorAsync(db, model, Hash(3), Unit(10));
            await AddVectorAsync(db, model, Hash(1), Unit(20));
            await AddVectorAsync(db, model, Hash(5), Unit(40));
            var table = new VectorTable(model, Dim);
            await table.LoadAsync(db, Ct);
            var service = new SearchService(db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null)], table);

            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);

            Assert.Equal([Hash(3), Hash(1), Hash(5)], r.Results.Select(x => x.Hash).ToArray());
            Assert.Equal([1.0, 2.0, 3.0], r.Results.Select(x => x.Rank).ToArray());                        // 抜けた画像の分の欠番は無い
            Assert.Equal([@"C:\shots\3-copy.png", @"C:\shots\3.png"], r.Results[0].Paths);                  // missing のパスは含めない。パスの昇順
            Assert.Equal(3, r.TotalImages);                                                                  // 結果に出してよい画像の数
            Assert.Equal(3, r.IndexedImages);
            Assert.Equal(3, Assert.Single(r.Paths).Population);                                              // 母集団も、見えない画像を除いた数
            // 見えない画像の分を含めた順位(4 が 1 位、2 が 2 位…)ではなく、見える画像の中での順位
            Assert.Equal(Math.Cos(10 * Math.PI / 180), Assert.Single(r.Results[0].Hits).Score, 5);
        }
    }

    [Fact]
    public async Task Images_without_a_vector_yet_count_in_TotalImages_but_not_in_IndexedImages()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string model = await RegisterModelAsync(db);
            long folder = await AddFolderAsync(db);
            for (int i = 1; i <= 4; i++) await AddImageAsync(db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
            await AddVectorAsync(db, model, Hash(1), Unit(10));
            await AddVectorAsync(db, model, Hash(2), Unit(20));
            var table = new VectorTable(model, Dim);
            await table.LoadAsync(db, Ct);
            var service = new SearchService(db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null)], table);

            SearchResponse r = await service.SearchAsync(new SearchQuery("q"), Ct);
            Assert.Equal(4, r.TotalImages);
            Assert.Equal(2, r.IndexedImages);
            Assert.Equal(2, r.Results.Count);

            // 埋め込みが進むと、すぐに検索できる(表に足す)
            table.Add(Hash(3), Unit(5));
            SearchResponse after = await service.SearchAsync(new SearchQuery("q"), Ct);
            Assert.Equal(3, after.IndexedImages);
            Assert.Equal(Hash(3), after.Results[0].Hash);
        }
    }

    [Fact]
    public async Task A_path_for_an_image_that_became_missing_is_dropped_on_the_next_search()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string model = await RegisterModelAsync(db);
            long folder = await AddFolderAsync(db);
            await AddImageAsync(db, folder, Hash(1), [@"C:\a.png", @"C:\b.png"]);
            await AddVectorAsync(db, model, Hash(1), Unit(10));
            var table = new VectorTable(model, Dim);
            await table.LoadAsync(db, Ct);
            var service = new SearchService(db, [new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), null)], table);
            Assert.Equal(2, (await service.SearchAsync(new SearchQuery("q"), Ct)).Results[0].Paths.Count);

            await db.WriteAsync((conn, tx) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE image_paths SET missing_since = '2026-11-05T02:00:00.000Z' WHERE path = 'C:\\a.png'";
                cmd.ExecuteNonQuery();
            }, Ct);
            Assert.Equal([@"C:\b.png"], (await service.SearchAsync(new SearchQuery("q"), Ct)).Results[0].Paths);
        }
    }

    // ---------------------------------------------------------------- 入力の検査・構築

    [Fact]
    public async Task Invalid_queries_and_constructor_arguments_are_rejected()
    {
        var (dir, db, table) = await PlainDb(1);
        using (dir) using (db)
        {
            var service = new SearchService(db, [FakePath.Scored("a", Ids(1), [1.0])], table);
            await Assert.ThrowsAsync<ArgumentNullException>(() => service.SearchAsync(null!, Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new SearchQuery(""), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new SearchQuery("   "), Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(new SearchQuery("q", Limit: 0), Ct));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SearchAsync(new SearchQuery("q"), new CancellationToken(canceled: true)));

            Assert.Throws<ArgumentException>(() => new SearchService(db, [], table));
            Assert.Throws<ArgumentException>(() => new SearchService(db, [FakePath.Scored("a", Ids(1), [1.0]), FakePath.Scored("a", Ids(1), [1.0])], table));  // PathId の重複
        }
    }

    // ---------------------------------------------------------------- 1 万件相当の時間(測定。厳しい基準は置かない)

    [Fact]
    public async Task A_search_over_ten_thousand_vectors_at_the_real_dimension_is_fast()
    {
        const int N = 10_000;
        const int D = 1152;
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            var rng = new Random(5);
            var table = new VectorTable("big", D);
            var v = new float[D];
            long folder = await AddFolderAsync(db);
            await db.WriteAsync((conn, tx) =>
            {
                for (int i = 0; i < N; i++)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO images (hash, size, format, first_seen_at) VALUES ($h, 1, 'png', '2026-11-05T00:00:00.000Z'); " +
                                      "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size) VALUES ($p, $h, $f, 1, 1)";
                    ins.Parameters.AddWithValue("$h", Hash(i));
                    ins.Parameters.AddWithValue("$p", $@"C:\big\{i}.png");
                    ins.Parameters.AddWithValue("$f", folder);
                    ins.ExecuteNonQuery();
                }
            }, Ct);
            for (int i = 0; i < N; i++)
            {
                double norm = 0;
                for (int d = 0; d < D; d++) { v[d] = (float)(rng.NextDouble() - 0.5); norm += (double)v[d] * v[d]; }
                norm = Math.Sqrt(norm);
                for (int d = 0; d < D; d++) v[d] = (float)(v[d] / norm);
                table.Add(Hash(i), v);
            }
            table.BeginLoading();
            table.EndLoading();
            float[] q = new float[D];
            q[0] = 1;
            var service = new SearchService(db, [new VisualPath(table, (_, _) => Task.FromResult(q), null)], table);

            await service.SearchAsync(new SearchQuery("warmup"), Ct);
            var times = new List<double>();
            for (int k = 0; k < 5; k++)
            {
                var sw = Stopwatch.StartNew();
                SearchResponse r = await service.SearchAsync(new SearchQuery("q", Limit: 300), Ct);
                times.Add(sw.Elapsed.TotalMilliseconds);
                Assert.Equal(300, r.Results.Count);
                Assert.Equal(N, r.TotalImages);
            }
            output.WriteLine($"1 万件 × 1152 次元の検索(クエリの埋め込みは除く。DB の可視判定 + 内積 + 順位付け + 融合 + パスの取得): 中央値 {times.OrderBy(x => x).ElementAt(2):F0} ms、最大 {times.Max():F0} ms");
            Assert.True(times.Max() < 2000, $"1 万件の検索が遅すぎる: {times.Max():F0} ms");
        }
    }
}
