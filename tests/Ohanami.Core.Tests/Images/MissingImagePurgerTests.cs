using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Search;
using Ohanami.Core.Tags;
using Ohanami.Core.Tests.Captions;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Images;

/// <summary>
/// 見つからない画像の記録を消す(ADR 0007): 対象(現存するパスが無い画像。パスの行が 0 行のものを含む。根に届かない・無効なフォルダの配下は対象外)、
/// 子の表から順に消す(外部キー)、索引・手動タグ・ベクトルの表からも消える、対象は消すトランザクションの中で決める。実物の SQLite(FTS5)を使う。
/// </summary>
public sealed class MissingImagePurgerTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private readonly List<string> _removed = [];
    private readonly MissingImagePurger _purger;
    private readonly ManualTags _tags;
    private static CancellationToken Ct => CaptionFixture.Ct;
    private static string H(int i) => CaptionFixture.Hash(i);

    public MissingImagePurgerTests()
    {
        _purger = new MissingImagePurger(_fx.Db, _fx.Index, hashes => _removed.AddRange(hashes));
        _tags = new ManualTags(_fx.Db, _fx.Index);
    }

    public void Dispose() => _fx.Dispose();

    private Task Exec(string sql) => _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, sql), Ct);

    private Task AddFolderAsync(int id, bool enabled = true) =>
        Exec($"INSERT INTO folders(id, path, enabled, added_at) VALUES ({id}, 'C:\\lib{id}', {(enabled ? 1 : 0)}, '2026-11-06T00:00:00.000Z')");

    private Task AddPathAsync(int image, int folder, string name, bool missing) =>
        Exec($"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ('C:\\lib{folder}\\{name}', '{H(image)}', {folder}, 1, 100, {(missing ? "'2026-11-06T01:00:00.000Z'" : "NULL")})");

    private async Task AddResultsAsync(int image)
    {
        await _fx.SaveAsync(image, _fx.ModelA, "ocr" + image, "説明" + image, ["タグ" + image]);   // captions + stage_status(ocr, caption) + 索引
        await Exec("INSERT OR IGNORE INTO models(id, kind, name, spec_json, created_at) VALUES ('embed-x', 'image-embedding', 'n', '{}', 't')");
        await Exec($"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{H(image)}', 'embed', 'done', 'embed-x', 1, NULL, NULL, 1, '2026-11-06T00:00:00.000Z')");
        await Exec($"INSERT INTO image_vectors(hash, model_id, embedding) VALUES ('{H(image)}', 'embed-x', zeroblob(8))");
    }

    private long Rows(string table, int image) => _fx.Count($"SELECT count(*) FROM {table} WHERE hash = '{H(image)}'");

    /// <summary>1 = 現存するパスあり、2 = 見つからない印のパスだけ、3 = パスの行が無い、4 = 現存と見つからないが混在、5 = 無効なフォルダの配下(印なし)。</summary>
    private async Task BuildLibraryAsync()
    {
        await _fx.AddImagesAsync(5);
        await AddFolderAsync(1);
        await AddFolderAsync(2, enabled: false);
        await AddPathAsync(1, 1, "present.png", missing: false);
        await AddPathAsync(2, 1, "gone.png", missing: true);
        // 3 はパスの行が無い(フォルダの解除のあと、など)
        await AddPathAsync(4, 1, "gone-copy.png", missing: true);
        await AddPathAsync(4, 1, "present-copy.png", missing: false);
        await AddPathAsync(5, 2, "in-disabled-folder.png", missing: false);   // 無効なフォルダは走査されないので、印が付かない
        for (int i = 1; i <= 5; i++) await AddResultsAsync(i);
    }

    [Fact]
    public async Task The_target_is_images_without_any_present_path_including_images_with_no_path_rows()
    {
        await BuildLibraryAsync();

        Assert.Equal(2, await _purger.CountAsync(Ct));      // 2(印のパスだけ)と 3(パスの行が無い)
    }

    // ---------------------------------------------------------------- 一覧(読むだけ。ADR 0039)

    private Task AddPathAtAsync(int image, string name, string? missingSince) =>
        Exec($"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ('C:\\lib1\\{name}', '{H(image)}', 1, 1, 100, {(missingSince is null ? "NULL" : $"'{missingSince}'")})");

    private static DateTimeOffset At(string t) => DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>1 = 現存、2 = 見つからない(T1)、3 = パスの行が無い、4 = 見つからないパスが 2 つ(T2 と T3)、5 = 見つからない(T0。最も古い)、6 = 現存と見つからないの混在(対象外)。</summary>
    private async Task BuildListLibraryAsync()
    {
        await _fx.AddImagesAsync(6);
        await AddFolderAsync(1);
        await AddPathAtAsync(1, "present.png", null);
        await AddPathAtAsync(2, "gone-2.png", "2026-11-06T01:00:00.000Z");
        await AddPathAtAsync(4, "gone-4-old.png", "2026-11-06T02:00:00.000Z");
        await AddPathAtAsync(4, "gone-4-new.png", "2026-11-06T03:00:00.000Z");
        await AddPathAtAsync(5, "gone-5.png", "2026-11-06T00:30:00.000Z");
        await AddPathAtAsync(6, "gone-6.png", "2026-11-06T04:00:00.000Z");
        await AddPathAtAsync(6, "present-6.png", null);
        await AddResultsAsync(2);      // 説明文などがある
        await AddResultsAsync(4);
        await _tags.AddAsync(H(2), "手動A", Ct);
        await _tags.AddAsync(H(2), "手動B", Ct);
        await _tags.AddAsync(H(3), "手動C", Ct);   // 3 は手動タグだけ(VLM の結果なし)
    }

    [Fact]
    public async Task The_list_has_the_same_target_as_the_count_and_the_purge_newest_missing_first_with_no_date_last()
    {
        await BuildListLibraryAsync();

        MissingImageList list = await _purger.ListAsync(ct: Ct);

        Assert.Equal(await _purger.CountAsync(Ct), list.Total);                      // 数える条件と同じ
        Assert.Equal(4, list.Total);
        Assert.Equal([H(4), H(2), H(5), H(3)], list.Items.Select(i => i.Hash).ToArray());   // T3 → T1 → T0 → 日時なし
        Assert.Equal(2, list.WithManualTags);                                        // 2 と 3
        // 消すものと同じ集合
        await _purger.PurgeAsync(Ct);
        Assert.Equal(list.Items.Select(i => i.Hash).Order(StringComparer.Ordinal).ToArray(), _removed.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_list_item_carries_the_last_known_path_the_date_the_manual_tags_and_the_caption_flag()
    {
        await BuildListLibraryAsync();

        MissingImageList list = await _purger.ListAsync(ct: Ct);

        MissingImageItem four = list.Items.Single(i => i.Hash == H(4));
        Assert.Equal(@"C:\lib1\gone-4-new.png", four.LastPath);                    // 印の日時が新しいほうのパス
        Assert.Equal(At("2026-11-06T03:00:00Z"), four.MissingSince);
        Assert.Equal(0, four.ManualTagCount);
        Assert.True(four.HasCaption);
        MissingImageItem two = list.Items.Single(i => i.Hash == H(2));
        Assert.Equal(2, two.ManualTagCount);
        Assert.True(two.HasCaption);
        MissingImageItem three = list.Items.Single(i => i.Hash == H(3));          // パスの行が無い
        Assert.Null(three.LastPath);
        Assert.Null(three.MissingSince);
        Assert.Equal(1, three.ManualTagCount);
        Assert.False(three.HasCaption);                                              // 手動タグだけ
        MissingImageItem five = list.Items.Single(i => i.Hash == H(5));
        Assert.Equal((0, false), (five.ManualTagCount, five.HasCaption));
    }

    [Fact]
    public async Task The_list_limit_cuts_the_items_but_the_total_and_the_manual_tag_count_are_taken_before_the_cut()
    {
        await BuildListLibraryAsync();

        MissingImageList list = await _purger.ListAsync(limit: 2, Ct);

        Assert.Equal([H(4), H(2)], list.Items.Select(i => i.Hash).ToArray());
        Assert.Equal(4, list.Total);
        Assert.Equal(2, list.WithManualTags);                                        // 切った 3 も数える
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _purger.ListAsync(limit: 0, Ct));
    }

    [Fact]
    public async Task An_empty_library_gives_an_empty_list()
    {
        MissingImageList list = await _purger.ListAsync(ct: Ct);

        Assert.Empty(list.Items);
        Assert.Equal((0, 0), (list.Total, list.WithManualTags));
    }

    [Fact]
    public async Task Images_under_a_folder_that_cannot_be_reached_or_is_disabled_are_not_listed_because_they_are_not_marked()
    {
        await BuildLibraryAsync();   // 5 は無効なフォルダの配下(印なし)、1 と 4 は現存

        MissingImageList list = await _purger.ListAsync(ct: Ct);

        Assert.Equal(new[] { H(2), H(3) }.Order(StringComparer.Ordinal).ToArray(), list.Items.Select(i => i.Hash).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Purging_removes_the_images_and_everything_hanging_off_them_and_leaves_the_others_untouched()
    {
        await BuildLibraryAsync();
        long imagesBefore = _fx.Count("SELECT count(*) FROM images");

        int removed = await _purger.PurgeAsync(Ct);

        Assert.Equal(2, removed);
        Assert.Equal(imagesBefore - 2, _fx.Count("SELECT count(*) FROM images"));
        foreach (int gone in new[] { 2, 3 })
        {
            foreach (string table in new[] { "images", "image_paths", "stage_status", "image_vectors", "captions", "search_text", "image_tags" })
                Assert.Equal(0, Rows(table, gone));
        }
        foreach (int kept in new[] { 1, 4, 5 })
        {
            Assert.Equal(1, Rows("images", kept));
            Assert.Equal(3, Rows("stage_status", kept));    // embed + ocr + caption
            Assert.Equal(1, Rows("image_vectors", kept));
            Assert.Equal(1, Rows("captions", kept));
            Assert.Equal(1, Rows("search_text", kept));
        }
        Assert.Equal(2, Rows("image_paths", 4));            // 現存するパスのある画像は、見つからない印のパスも含めて、そのまま
        Assert.Equal(new[] { H(2), H(3) }, _removed.OrderBy(h => h, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, await _purger.CountAsync(Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM pragma_foreign_key_check"));   // 外部キーの違反が残らない
    }

    [Fact]
    public async Task The_full_text_index_rows_are_removed_and_the_remaining_images_are_still_found()
    {
        await BuildLibraryAsync();
        Assert.Equal([H(2)], _fx.Hits("ocr2"));

        await _purger.PurgeAsync(Ct);

        Assert.Empty(_fx.Hits("ocr2"));
        Assert.Empty(_fx.Hits("ocr3"));
        Assert.Equal([H(1)], _fx.Hits("ocr1"));
        Assert.Equal(3, _fx.IndexDump().Count);
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM fts_docs"));       // 派生データも、残りの 3 枚だけ
    }

    [Fact]
    public async Task Manual_tags_of_removed_images_go_and_a_tag_used_by_nobody_else_is_deleted_but_a_shared_one_stays()
    {
        await BuildLibraryAsync();
        await _tags.AddAsync(H(2), "消える", Ct);          // 2 だけに付いている
        await _tags.AddAsync(H(2), "共有", Ct);
        await _tags.AddAsync(H(1), "共有", Ct);            // 1(残る)にも付いている

        await _purger.PurgeAsync(Ct);

        Assert.Equal(0, _fx.Count($"SELECT count(*) FROM image_tags WHERE hash = '{H(2)}'"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal("共有", _fx.Text("SELECT name FROM tags"));
        Assert.Equal(["共有"], (await _tags.GetAsync(H(1), Ct)).ToArray());
    }

    [Fact]
    public async Task An_image_with_only_a_manual_tag_and_no_caption_is_removed_too()
    {
        await _fx.AddImagesAsync(1);
        await _tags.AddAsync(H(1), "タグだけ", Ct);        // caption も stage_status も無い。索引には手動タグの文書がある
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));

        Assert.Equal(1, await _purger.PurgeAsync(Ct));      // パスの行が無いので対象

        Assert.Equal(0, _fx.Count("SELECT count(*) FROM images"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task The_targets_are_decided_inside_the_deleting_transaction_so_an_image_that_came_back_in_between_is_kept()
    {
        await BuildLibraryAsync();
        Assert.Equal(2, await _purger.CountAsync(Ct));
        await Exec("UPDATE image_paths SET missing_since = NULL WHERE hash = '" + H(2) + "'");   // 数えたあと、走査がそのファイルを見つけた

        int removed = await _purger.PurgeAsync(Ct);

        Assert.Equal(1, removed);
        Assert.Equal(1, Rows("images", 2));
        Assert.Equal(0, Rows("images", 3));
    }

    [Fact]
    public async Task Purging_when_there_is_nothing_to_purge_changes_nothing_and_does_not_call_back()
    {
        await _fx.AddImagesAsync(1);
        await AddFolderAsync(1);
        await AddPathAsync(1, 1, "a.png", missing: false);

        Assert.Equal(0, await _purger.PurgeAsync(Ct));

        Assert.Empty(_removed);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM images"));
    }

    [Fact]
    public async Task Removed_images_are_taken_out_of_the_in_memory_vector_table_through_the_callback()
    {
        await BuildLibraryAsync();
        var table = new VectorTable("embed-x", 4);
        for (int i = 1; i <= 5; i++) table.Add(H(i), Unit(i % 4));
        var purger = new MissingImagePurger(_fx.Db, _fx.Index, hashes => { foreach (string h in hashes) table.Remove(h); });
        VectorSnapshot before = table.Snapshot();

        await purger.PurgeAsync(Ct);

        Assert.Equal(3, table.Count);
        Assert.False(table.Contains(H(2)));
        Assert.True(table.Contains(H(1)));
        VectorSnapshot after = table.Snapshot();
        Assert.Equal(3, after.LiveCount);
        Assert.Equal(5, before.LiveCount);                                   // 先に取ったビューは変わらない
        Assert.DoesNotContain(Enumerable.Range(0, after.Count).Where(i => after.Valid[i]).Select(i => after.Hashes[i]), h => h == H(2) || h == H(3));
    }

    private static float[] Unit(int axis)
    {
        var v = new float[4];
        v[axis] = 1f;
        return v;
    }
}

/// <summary>メモリ上のベクトルの表からの除去(ADR 0007。<see cref="VectorTable.Remove"/>)。</summary>
public sealed class VectorTableRemoveTests
{
    private const int Dim = 4;
    private static string H(int i) => CaptionFixture.Hash(i);

    private static float[] Unit(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    [Fact]
    public void Remove_takes_the_image_out_of_the_count_and_the_lookup_but_keeps_an_earlier_snapshot_intact()
    {
        var table = new VectorTable("m", Dim);
        table.Add(H(1), Unit(0));
        table.Add(H(2), Unit(1));
        VectorSnapshot early = table.Snapshot();

        Assert.True(table.Remove(H(1)));

        Assert.Equal(1, table.Count);
        Assert.False(table.Contains(H(1)));
        Assert.False(table.Remove(H(1)));                    // もう無い
        Assert.False(table.Remove(H(9)));                    // 最初から無い
        Assert.True(early.Valid[0]);                         // 先に取ったビューの印は、取った時点のまま
        Assert.Equal(2, early.LiveCount);
        VectorSnapshot late = table.Snapshot();
        Assert.False(late.Valid[0]);
        Assert.True(late.Valid[1]);
        Assert.Equal(1, late.LiveCount);
    }

    [Fact]
    public void An_image_can_be_added_again_after_it_was_removed()
    {
        var table = new VectorTable("m", Dim);
        table.Add(H(1), Unit(0));
        table.Remove(H(1));

        Assert.True(table.Add(H(1), Unit(2)));

        Assert.Equal(1, table.Count);
        Assert.True(table.Contains(H(1)));
        VectorSnapshot s = table.Snapshot();
        Assert.Equal(1, s.LiveCount);
        int row = Enumerable.Range(0, s.Count).Single(i => s.Valid[i]);
        Assert.Equal(1f, s.Vector(row)[2]);
    }

    [Fact]
    public void A_removal_during_loading_is_not_undone_by_the_database_row_read_before_it()
    {
        var table = new VectorTable("m", Dim);
        table.Add(H(1), Unit(0));
        table.BeginLoading();

        table.Remove(H(1));
        table.ApplyLoadedRow(H(1), Unit(0));                 // 読み込みが、消す前の DB の行を読んでいた
        table.ApplyLoadedRow(H(2), Unit(1));
        table.EndLoading();

        Assert.False(table.Contains(H(1)));
        Assert.True(table.Contains(H(2)));
        Assert.Equal(1, table.Count);
    }
}
