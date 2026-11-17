using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.Search;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>メモリ上のベクトルの表: 追加、書式の検査、スナップショット、DB からの読み込み。</summary>
public sealed class VectorTableTests
{
    /// <summary>ハッシュの<b>有効な</b>行の位置(上書きされた古い行にも同じハッシュが残っているので、Array.IndexOf では選べない)。</summary>
    private static int LiveRow(VectorSnapshot s, string hash)
    {
        for (int i = 0; i < s.Count; i++)
            if (s.Valid[i] && s.Hashes[i] == hash) return i;
        throw new InvalidOperationException("有効な行が無い: " + hash);
    }

    [Fact]
    public void Add_stores_vectors_and_the_snapshot_reads_them_back()
    {
        var t = new VectorTable("m", Dim);
        Assert.True(t.Add(Hash(1), Unit(10)));
        Assert.True(t.Add(Hash(2), Unit(20)));
        Assert.Equal(2, t.Count);
        Assert.True(t.Contains(Hash(1)));
        Assert.False(t.Contains(Hash(3)));
        VectorSnapshot s = t.Snapshot();
        Assert.Equal(2, s.Count);
        Assert.Equal(Hash(2), s.Hashes[1]);
        Assert.Equal(Unit(20), s.Vector(1).ToArray());
    }

    [Fact]
    public void Adding_the_same_hash_with_the_same_vector_is_ignored()
    {
        var t = new VectorTable("m", Dim);
        t.Add(Hash(1), Unit(10));
        Assert.False(t.Add(Hash(1), Unit(10)));       // 読み込みと埋め込みの進みが重なった場合
        Assert.Equal(1, t.Count);
        Assert.Equal(1, t.Snapshot().Count);          // 行も増えない
    }

    // ---------------------------------------------------------------- 上書き(モデルを A → B → A と戻した再埋め込みなど。GPU の再計算は同じ値とは限らない)

    [Fact]
    public void Adding_the_same_hash_with_a_different_vector_overwrites_it_and_the_count_does_not_grow()
    {
        var t = new VectorTable("m", Dim);
        t.Add(Hash(1), Unit(10));
        t.Add(Hash(2), Unit(20));
        Assert.True(t.Add(Hash(1), Unit(11)));         // 例外にならない。後から来たものが勝つ
        Assert.Equal(2, t.Count);
        Assert.True(t.Contains(Hash(1)));

        VectorSnapshot s = t.Snapshot();
        Assert.Equal(3, s.Count);                      // 行は 1 つ増える(古い行は無効の印)
        Assert.Equal(2, s.LiveCount);
        Assert.False(s.AllValid);
        Assert.False(s.Valid[0]);                      // 古い Hash(1)
        Assert.True(s.Valid[1]);
        Assert.True(s.Valid[2]);
        Assert.Equal(Hash(1), s.Hashes[2]);
        Assert.Equal(Unit(11), s.Vector(2).ToArray());
        Assert.Equal(Unit(10), s.Vector(0).ToArray()); // 古い行の中身は書き換えない
    }

    [Fact]
    public void Overwriting_repeatedly_keeps_only_the_latest_vector_live()
    {
        var t = new VectorTable("m", Dim);
        for (int k = 0; k < 5; k++) t.Add(Hash(1), Unit(10 + k));
        Assert.Equal(1, t.Count);
        VectorSnapshot s = t.Snapshot();
        Assert.Equal(1, s.LiveCount);
        Assert.Equal(1, s.Valid.Count(v => v));
        Assert.True(s.Valid[4]);
        Assert.Equal(Unit(14), s.Vector(4).ToArray());
    }

    [Fact]
    public void An_overwrite_still_checks_the_format_and_leaves_the_old_vector_alone_when_it_fails()
    {
        var t = new VectorTable("m", Dim);
        t.Add(Hash(1), Unit(10));
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), new float[Dim - 1]));
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), Unit(10).Select(x => x * 2).ToArray()));
        VectorSnapshot s = t.Snapshot();
        Assert.Equal(1, s.Count);
        Assert.True(s.AllValid);
        Assert.Equal(Unit(10), s.Vector(0).ToArray());
    }

    [Fact]
    public void A_snapshot_taken_before_an_overwrite_is_not_changed_by_it()
    {
        var t = new VectorTable("m", Dim);
        t.Add(Hash(1), Unit(10));
        t.Add(Hash(2), Unit(20));
        VectorSnapshot before = t.Snapshot();
        t.Add(Hash(1), Unit(11));
        for (int i = 3; i < 3000; i++) t.Add(Hash(i), Unit(i * 0.1));   // 配列の作り直しをまたぐ
        Assert.Equal(2, before.Count);
        Assert.True(before.AllValid);                                    // 古い行は、取った時点では有効のまま
        Assert.True(before.Valid[0]);
        Assert.Equal(Unit(10), before.Vector(0).ToArray());
        Assert.Equal(Hash(1), before.Hashes[0]);
    }

    [Fact]
    public async Task Concurrent_overwrites_and_snapshot_reads_always_see_each_image_exactly_once_with_a_whole_vector()
    {
        var t = new VectorTable("m", Dim);
        const int images = 100;
        for (int i = 0; i < images; i++) t.Add(Hash(i), Unit(i));
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            int reads = 0;
            do
            {
                VectorSnapshot s = t.Snapshot();
                var seen = new HashSet<string>();
                for (int i = 0; i < s.Count; i++)
                {
                    if (!s.Valid[i]) continue;
                    Assert.True(seen.Add(s.Hashes[i]));            // どの画像も、有効な行は 1 つだけ
                    float[] v = s.Vector(i).ToArray();
                    Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 3);   // 壊れた(途中まで書かれた)ベクトルではない
                    // その行の角度は id + 0.5 度の整数倍(上書きのたびに 0.5 度ずつ足す)。別の画像のベクトルが混ざっていない
                    int id = Convert.ToInt32(s.Hashes[i].TrimStart('0').PadLeft(1, '0'), 16);
                    double angle = Math.Atan2(v[1], v[0]) * 180 / Math.PI - id;
                    Assert.True(angle > -0.01 && angle < 10.01 && Math.Abs(angle / 0.5 - Math.Round(angle / 0.5)) < 0.01, $"{s.Hashes[i]}: {angle}");
                }
                Assert.Equal(images, seen.Count);
                Assert.Equal(images, s.LiveCount);
                if (reads++ == 0) started.SetResult();
            } while (!stop.IsCancellationRequested);
            return reads;
        }, TestContext.Current.CancellationToken);
        await started.Task;
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w =>
        {
            for (int round = 1; round <= 20; round++)
                for (int i = w; i < images; i += 4) t.Add(Hash(i), Unit(i + round * 0.5));
        });
        await stop.CancelAsync();
        Assert.True(await reader > 0);
        Assert.Equal(images, t.Count);
        VectorSnapshot last = t.Snapshot();
        for (int i = 0; i < last.Count; i++)
            if (last.Valid[i])
            {
                int id = Convert.ToInt32(last.Hashes[i].TrimStart('0').PadLeft(1, '0'), 16);
                Assert.Equal(Unit(id + 20 * 0.5), last.Vector(i).ToArray());   // 最後の上書きが勝つ
            }
    }

    [Fact]
    public async Task LoadAsync_compacts_the_invalid_rows_made_by_overwrites()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(11));
            await AddVectorAsync(db, current, Hash(2), Unit(20));
            var t = new VectorTable(current, Dim);
            t.Add(Hash(1), Unit(10));
            t.Add(Hash(2), Unit(20));
            t.Add(Hash(1), Unit(11));                                       // 無効な行が 1 つできる
            VectorSnapshot before = t.Snapshot();
            Assert.Equal(3, before.Count);

            await t.LoadAsync(db, TestContext.Current.CancellationToken);

            VectorSnapshot after = t.Snapshot();
            Assert.Equal(2, after.Count);                                   // 詰まった
            Assert.True(after.AllValid);
            Assert.Equal(2, t.Count);
            Assert.Equal(Unit(11), after.Vector(LiveRow(after, Hash(1))).ToArray());
            Assert.Equal(3, before.Count);                                  // 前の Snapshot はそのまま読める
            Assert.Equal(Unit(10), before.Vector(0).ToArray());
        }
    }

    [Fact]
    public async Task A_row_read_by_LoadAsync_replaces_what_the_table_already_had()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(11));          // DB が正(読み込みの前に Add されていたのは古い値)
            var t = new VectorTable(current, Dim);
            t.Add(Hash(1), Unit(10));
            await t.LoadAsync(db, TestContext.Current.CancellationToken);
            Assert.Equal(1, t.Count);
            VectorSnapshot s = t.Snapshot();
            Assert.Equal(Unit(11), s.Vector(LiveRow(s, Hash(1))).ToArray());
        }
    }

    [Fact]
    public void An_add_during_a_load_wins_over_the_older_database_row_in_either_order()
    {
        var t = new VectorTable("m", Dim);
        t.BeginLoading();
        // 順序 1: 読み込みが先に行を入れ、そのあとで Add(ジョブが置き換えた新しい値)
        t.ApplyLoadedRow(Hash(1), Unit(10));
        Assert.True(t.Add(Hash(1), Unit(11)));
        // 順序 2: Add が先、そのあとで、読み始めた時点の古い DB の行が来る(これで上書きしてはいけない)
        Assert.True(t.Add(Hash(2), Unit(21)));
        t.ApplyLoadedRow(Hash(2), Unit(20));
        // 重なりのない行は入る
        t.ApplyLoadedRow(Hash(3), Unit(30));
        t.EndLoading();

        Assert.Equal(3, t.Count);
        VectorSnapshot s = t.Snapshot();
        Assert.Equal(Unit(11), s.Vector(LiveRow(s, Hash(1))).ToArray());
        Assert.Equal(Unit(21), s.Vector(LiveRow(s, Hash(2))).ToArray());
        Assert.Equal(Unit(30), s.Vector(LiveRow(s, Hash(3))).ToArray());
        Assert.Equal(3, s.LiveCount);
    }

    [Fact]
    public void A_load_row_outside_a_load_is_refused_and_a_finished_load_stops_shielding_adds()
    {
        var t = new VectorTable("m", Dim);
        Assert.Throws<InvalidOperationException>(() => t.ApplyLoadedRow(Hash(1), Unit(10)));
        t.BeginLoading();
        t.Add(Hash(1), Unit(10));
        t.EndLoading();
        Assert.True(t.Add(Hash(1), Unit(11)));         // 読み込みの後の Add は、ふつうの上書き
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Malformed_vectors_are_rejected_loudly()
    {
        var t = new VectorTable("m", Dim);
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), new float[Dim - 1]));
        float[] nan = Unit(10);
        nan[3] = float.NaN;
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), nan));
        float[] inf = Unit(10);
        inf[3] = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), inf));
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), new float[Dim]));                                  // ノルム 0
        Assert.Throws<InvalidDataException>(() => t.Add(Hash(1), Unit(10).Select(x => x * 2).ToArray()));         // 正規化されていない
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void A_snapshot_is_not_changed_by_later_adds_even_when_the_storage_grows()
    {
        var t = new VectorTable("m", Dim);
        for (int i = 0; i < 1000; i++) t.Add(Hash(i), Unit(i * 0.1));
        VectorSnapshot before = t.Snapshot();
        float[] expected = before.Vector(999).ToArray();
        for (int i = 1000; i < 3000; i++) t.Add(Hash(i), Unit(i * 0.1));  // 1024 を超えて配列が作り直される
        Assert.Equal(1000, before.Count);
        Assert.Equal(expected, before.Vector(999).ToArray());
        Assert.Equal(Hash(999), before.Hashes[999]);
        Assert.Equal(3000, t.Snapshot().Count);
        Assert.Equal(Unit(2999 * 0.1), t.Snapshot().Vector(2999).ToArray());
    }

    [Fact]
    public async Task Concurrent_adds_and_snapshot_reads_never_see_a_torn_or_partial_vector()
    {
        var t = new VectorTable("m", Dim);
        int total = 4000;
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            int reads = 0;
            do
            {
                VectorSnapshot s = t.Snapshot();
                for (int i = 0; i < s.Count; i += 37)
                {
                    int id = Convert.ToInt32(s.Hashes[i].TrimStart('0'), 16);
                    Assert.Equal(Unit(id * 0.05), s.Vector(i).ToArray());   // 足された時点の完全なベクトル
                }
                if (reads++ == 0) started.SetResult();                       // 読み手が動き始めてから、追加を始める
            } while (!stop.IsCancellationRequested);
            return reads;
        }, TestContext.Current.CancellationToken);
        await started.Task;
        Parallel.For(1, total + 1, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => t.Add(Hash(i), Unit(i * 0.05)));
        await stop.CancelAsync();
        Assert.True(await reader > 0);
        Assert.Equal(total, t.Count);
    }

    // ---------------------------------------------------------------- DB からの読み込み

    [Fact]
    public async Task LoadAsync_reads_only_the_current_models_rows_in_hash_order_and_becomes_ready()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            string other = await RegisterModelAsync(db, Spec("other-family"));
            await AddVectorAsync(db, current, Hash(3), Unit(30));
            await AddVectorAsync(db, current, Hash(1), Unit(10));
            await AddVectorAsync(db, other, Hash(2), Unit(20));          // 別のモデルの行は読まない(古いモデルの結果は DB に残す)

            var t = new VectorTable(current, Dim);
            Assert.Equal(VectorTableState.NotLoaded, t.State);
            await t.LoadAsync(db, TestContext.Current.CancellationToken);
            Assert.Equal(VectorTableState.Ready, t.State);
            Assert.Equal(2, t.Count);
            VectorSnapshot s = t.Snapshot();
            Assert.Equal([Hash(1), Hash(3)], s.Hashes.Take(2).ToArray());  // (model_id, hash) の索引の順
            Assert.Equal(Unit(30), s.Vector(1).ToArray());
            Assert.False(t.Contains(Hash(2)));

            // 以後の追加は表に足せる
            t.Add(Hash(4), Unit(40));
            Assert.Equal(3, t.Count);
        }
    }

    [Fact]
    public async Task LoadAsync_with_a_blob_of_the_wrong_length_fails_loudly_and_marks_the_table_failed()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(10));
            await AddVectorRowAsync(db, current, Hash(2), new byte[Dim * 4 - 4]);   // 次元 × 4 バイトでない

            var t = new VectorTable(current, Dim);
            var e = await Assert.ThrowsAsync<InvalidDataException>(() => t.LoadAsync(db, TestContext.Current.CancellationToken));
            Assert.Contains(Hash(2), e.Message, StringComparison.Ordinal);
            Assert.Contains((Dim * 4).ToString(), e.Message, StringComparison.Ordinal);
            Assert.Equal(VectorTableState.Failed, t.State);
            Assert.Contains(Hash(2), t.FailureMessage!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoadAsync_with_a_dimension_that_does_not_match_the_stored_vectors_fails()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(10));
            var t = new VectorTable(current, Dim * 2);                          // モデルの次元と保存されたベクトルが違う
            await Assert.ThrowsAsync<InvalidDataException>(() => t.LoadAsync(db, TestContext.Current.CancellationToken));
            Assert.Equal(VectorTableState.Failed, t.State);
        }
    }

    [Fact]
    public async Task LoadAsync_rejects_a_stored_vector_that_is_not_normalized()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(10).Select(x => x * 3).ToArray());
            var t = new VectorTable(current, Dim);
            var e = await Assert.ThrowsAsync<InvalidDataException>(() => t.LoadAsync(db, TestContext.Current.CancellationToken));
            Assert.Contains("ノルム", e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_cancelled_load_is_not_a_failure_and_can_be_retried()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            for (int i = 0; i < 50; i++) await AddVectorAsync(db, current, Hash(i), Unit(i));
            var t = new VectorTable(current, Dim);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.LoadAsync(db, cts.Token));
            Assert.NotEqual(VectorTableState.Failed, t.State);
            await t.LoadAsync(db, TestContext.Current.CancellationToken);
            Assert.Equal(VectorTableState.Ready, t.State);
            Assert.Equal(50, t.Count);
        }
    }

    [Fact]
    public async Task Adds_during_a_load_overlapping_the_database_rows_are_tolerated()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string current = await RegisterModelAsync(db);
            await AddVectorAsync(db, current, Hash(1), Unit(10));
            var t = new VectorTable(current, Dim);
            t.Add(Hash(1), Unit(10));            // 読み込みの前に、埋め込みの進みで足された同じ行
            await t.LoadAsync(db, TestContext.Current.CancellationToken);
            Assert.Equal(1, t.Count);
        }
    }
}
