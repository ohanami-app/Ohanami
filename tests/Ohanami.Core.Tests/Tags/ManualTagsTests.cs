using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Tags;
using Ohanami.Core.Tests.Captions;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Tags;

/// <summary>
/// 手動タグの保存と索引(ADR 0036): 付ける・外す・まとめて付ける、正規化と拒む名前、使われなくなった tags の行の掃除、
/// 付け外しと索引が 1 トランザクション、caption が無い画像もタグで引ける、手動タグが無い画像の文書はタグの欄を足さない、作り直しと増分が同じ中身。
/// 実物の SQLite(FTS5)を使う。
/// </summary>
public sealed class ManualTagsTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private readonly ManualTags _tags;
    private static CancellationToken Ct => CaptionFixture.Ct;
    private static string H(int i) => CaptionFixture.Hash(i);

    public ManualTagsTests()
    {
        _tags = new ManualTags(_fx.Db, _fx.Index);
    }

    public void Dispose() => _fx.Dispose();

    private static IReadOnlyList<string> Names(params string[] names) => names;

    // ---------------------------------------------------------------- 付ける・外す

    [Fact]
    public async Task Adding_a_tag_writes_a_manual_tag_row_and_a_manual_link_and_a_second_add_is_a_no_op()
    {
        await _fx.AddImagesAsync(2);

        Assert.True(await _tags.AddAsync(H(1), "猫", Ct));
        Assert.False(await _tags.AddAsync(H(1), "猫", Ct));        // すでに付いている

        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal("猫|manual", _fx.Text("SELECT name || '|' || kind FROM tags"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM image_tags"));
        Assert.Equal($"{H(1)}|manual", _fx.Text("SELECT hash || '|' || source FROM image_tags"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM image_tags WHERE score IS NULL"));
        Assert.Equal(Names("猫"), await _tags.GetAsync(H(1), Ct));
        Assert.Empty(await _tags.GetAsync(H(2), Ct));
    }

    [Fact]
    public async Task The_name_is_stored_normalized_with_the_typed_case_and_the_same_name_in_another_case_is_the_same_tag()
    {
        await _fx.AddImagesAsync(3);

        await _tags.AddAsync(H(1), "  ＨＰ回復 ", Ct);
        await _tags.AddAsync(H(2), "Cat", Ct);
        await _tags.AddAsync(H(3), "cat", Ct);                      // 表の NOCASE が同じ名前とみなす(ASCII だけ)
        await _tags.AddAsync(H(1), "CAT", Ct);

        Assert.Equal(Names("Cat", "HP回復"), await _tags.GetAsync(H(1), Ct));   // 大文字・小文字は最初に付けたときの表記のまま。名前順(大文字・小文字を区別しない)
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags WHERE name = 'Cat'"));
        Assert.Equal(Names("Cat"), await _tags.GetAsync(H(3), Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags WHERE name = 'HP回復'"));
    }

    [Fact]
    public async Task Names_that_differ_only_in_non_ascii_case_are_separate_tags_a_known_limit_of_the_nocase_collation()
    {
        await _fx.AddImagesAsync(1);
        await _tags.AddAsync(H(1), "Ä", Ct);
        await _tags.AddAsync(H(1), "ä", Ct);
        Assert.Equal(2, _fx.Count("SELECT count(*) FROM tags"));       // SQLite の NOCASE は ASCII だけ(ADR 0036 の既知の制限)
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a|b")]
    [InlineData("a、b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public async Task A_rejected_name_writes_nothing(string name)
    {
        await _fx.AddImagesAsync(1);
        await Assert.ThrowsAsync<InvalidTagNameException>(() => _tags.AddAsync(H(1), name, Ct));
        await Assert.ThrowsAsync<InvalidTagNameException>(() => _tags.AddToManyAsync([H(1)], name, Ct));
        await Assert.ThrowsAsync<InvalidTagNameException>(() => _tags.RemoveAsync(H(1), name, Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM image_tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
    }

    [Fact]
    public async Task A_name_of_65_characters_is_rejected_and_64_is_accepted()
    {
        await _fx.AddImagesAsync(1);
        Assert.True(await _tags.AddAsync(H(1), new string('あ', 64), Ct));
        await Assert.ThrowsAsync<InvalidTagNameException>(() => _tags.AddAsync(H(1), new string('い', 65), Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags"));
    }

    [Fact]
    public async Task An_unknown_image_is_an_error_and_nothing_is_written()
    {
        await _fx.AddImagesAsync(1);
        string ghost = new('f', 64);
        await Assert.ThrowsAsync<ArgumentException>(() => _tags.AddAsync(ghost, "猫", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _tags.RemoveAsync(ghost, "猫", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _tags.AddToManyAsync([H(1), ghost], "猫", Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));          // 1 枚でも無ければ、tags の行も作らない(1 トランザクション)
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM image_tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
    }

    [Fact]
    public async Task Removing_a_tag_unlinks_it_and_deletes_the_tag_row_only_when_no_image_uses_it_any_more()
    {
        await _fx.AddImagesAsync(2);
        await _tags.AddAsync(H(1), "猫", Ct);
        await _tags.AddAsync(H(2), "猫", Ct);
        await _tags.AddAsync(H(1), "犬", Ct);

        Assert.True(await _tags.RemoveAsync(H(1), "猫", Ct));
        Assert.Equal(Names("犬"), await _tags.GetAsync(H(1), Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags WHERE name = '猫'"));    // 画像 2 がまだ使っている
        Assert.True(await _tags.RemoveAsync(H(2), "猫", Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags WHERE name = '猫'"));    // 使われなくなったので消える
        Assert.True(await _tags.RemoveAsync(H(1), "犬", Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM image_tags"));
    }

    [Fact]
    public async Task Removing_a_tag_that_is_not_attached_or_does_not_exist_returns_false_and_changes_nothing()
    {
        await _fx.AddImagesAsync(2);
        await _tags.AddAsync(H(1), "猫", Ct);

        Assert.False(await _tags.RemoveAsync(H(2), "猫", Ct));       // タグはあるが、この画像には付いていない
        Assert.False(await _tags.RemoveAsync(H(1), "犬", Ct));       // タグが無い
        Assert.Equal(Names("猫"), await _tags.GetAsync(H(1), Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags"));
    }

    [Fact]
    public async Task Removing_by_a_name_in_another_case_removes_the_same_tag()
    {
        await _fx.AddImagesAsync(1);
        await _tags.AddAsync(H(1), "Cat", Ct);
        Assert.True(await _tags.RemoveAsync(H(1), "cat", Ct));
        Assert.Empty(await _tags.GetAsync(H(1), Ct));
    }

    [Fact]
    public async Task A_tag_row_that_is_still_linked_with_another_source_is_not_deleted_when_the_manual_link_goes()
    {
        await _fx.AddImagesAsync(2);
        await _tags.AddAsync(H(1), "猫", Ct);
        long tagId = _fx.Count("SELECT id FROM tags WHERE name = '猫'");
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"INSERT INTO image_tags(hash, tag_id, source, score) VALUES ('{H(2)}', {tagId}, 'propagated', 0.5)"), Ct);   // 将来の Propagated

        Assert.True(await _tags.RemoveAsync(H(1), "猫", Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags WHERE name = '猫'"));        // 将来の伝播のリンクが指しているので残る
        Assert.Empty(await _tags.GetAsync(H(2), Ct));                                      // 手動タグとしては数えない
    }

    [Fact]
    public async Task Tags_of_another_kind_or_source_do_not_count_as_manual_tags_for_the_list_candidates_or_the_index()
    {
        await _fx.AddImagesAsync(1);
        await _fx.Db.WriteAsync((c, tx) =>
        {
            TestDb.Exec(c, tx, "INSERT INTO tags(id, name, kind) VALUES (1, 'キャラ名', 'character'), (2, '伝播', 'manual')");
            TestDb.Exec(c, tx, $"INSERT INTO image_tags(hash, tag_id, source, score) VALUES ('{H(1)}', 1, 'manual', NULL), ('{H(1)}', 2, 'propagated', 0.9)");
        }, Ct);

        Assert.Empty(await _tags.GetAsync(H(1), Ct));
        Assert.Empty(await _tags.GetCandidatesAsync(10, null, Ct));
        await _fx.Index.EnsureAsync(Ct);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));                  // 手動タグではないので文書にしない
    }

    // ---------------------------------------------------------------- まとめて付ける

    [Fact]
    public async Task Adding_to_many_images_returns_the_number_newly_tagged_and_ignores_duplicate_hashes()
    {
        await _fx.AddImagesAsync(4);
        Assert.Equal(3, await _tags.AddToManyAsync([H(1), H(2), H(3), H(1)], "風景", Ct));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM tags"));
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM image_tags"));
        Assert.Equal(1, await _tags.AddToManyAsync([H(2), H(3), H(4), H(1)], "風景", Ct));   // 画像 1・2・3 はすでに付いていて、画像 4 だけ新しい
    }

    [Fact]
    public async Task Adding_to_many_images_updates_the_index_of_each_image_in_the_same_transaction()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(2, _fx.ModelA, "セリフ", "説明", ["銀髪"]);

        await _tags.AddToManyAsync([H(1), H(2), H(3)], "風景", Ct);

        Assert.Equal([H(1), H(2), H(3)], _fx.Hits("風景"));
        Assert.Equal(3, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text WHERE model_id IS NOT NULL"));   // caption のある画像 2 だけ model_id を持つ
    }

    [Fact]
    public async Task An_empty_selection_adds_nothing_and_still_validates_the_name()
    {
        Assert.Equal(0, await _tags.AddToManyAsync([], "猫", Ct));
        await Assert.ThrowsAsync<InvalidTagNameException>(() => _tags.AddToManyAsync([], "a|b", Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));
    }

    // ---------------------------------------------------------------- 索引(caption の無い画像・ある画像)

    [Fact]
    public async Task An_image_without_a_caption_is_found_by_its_manual_tag_with_a_null_model_id()
    {
        await _fx.AddImagesAsync(2);
        await _tags.AddAsync(H(1), "ふわふわ", Ct);

        Assert.Equal([H(1)], _fx.Hits("ふわふわ"));         // 2 文字以上の語(fts_docs の MATCH)
        Assert.Equal([H(1)], _fx.Hits("ふ"));              // 1 文字の語(search_text の LIKE)
        Assert.Equal("|||ふわふわ", _fx.Text("SELECT norm_text FROM search_text"));     // 空の ocr・説明文・tags のあとに、4 つ目の欄
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text WHERE model_id IS NULL"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM fts_docs"));
    }

    [Fact]
    public async Task A_caption_and_a_manual_tag_make_a_four_field_document_and_the_other_fields_do_not_leak_across_the_separator()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "HP回復の画面", ["銀髪", "ロングヘア"]);
        await _tags.AddAsync(H(1), "お気に入り", Ct);
        await _tags.AddAsync(H(1), "あ", Ct);

        Assert.Equal("がちゃ10連|hp回復の画面|銀髪、ろんぐへあ|あ、お気に入り", _fx.Text("SELECT norm_text FROM search_text"));   // 手動タグは名前順
        Assert.Equal([H(1)], _fx.Hits("お気に入り"));
        Assert.Empty(_fx.Hits("銀髪お気に"));                // 欄の区切りをまたいだ bigram は作らない
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));
    }

    [Fact]
    public async Task Removing_the_only_tag_of_an_image_without_a_caption_removes_its_index_row()
    {
        await _fx.AddImagesAsync(1);
        await _tags.AddAsync(H(1), "猫", Ct);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));

        await _tags.RemoveAsync(H(1), "猫", Ct);

        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM fts_docs"));
        Assert.Empty(_fx.Hits("猫"));
    }

    [Fact]
    public async Task Removing_the_last_manual_tag_of_a_captioned_image_gives_back_exactly_the_document_it_had_before()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "HP回復の画面", ["銀髪", "ロングヘア"]);
        var before = _fx.IndexDump();

        await _tags.AddAsync(H(1), "猫", Ct);
        Assert.NotEqual(before, _fx.IndexDump());
        await _tags.RemoveAsync(H(1), "猫", Ct);

        Assert.Equal(before, _fx.IndexDump());               // 文字列も fts の列も、タグを付ける前と同じ(4 つ目の欄は残らない)
    }

    [Fact]
    public async Task Saving_a_caption_for_an_image_that_already_has_manual_tags_keeps_the_tags_in_the_document()
    {
        await _fx.AddImagesAsync(1);
        await _tags.AddAsync(H(1), "猫", Ct);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text WHERE model_id IS NOT NULL"));

        await _fx.SaveAsync(1, _fx.ModelA, "セリフ", "説明", ["銀髪"]);

        Assert.Equal("せりふ|説明|銀髪|猫", _fx.Text("SELECT norm_text FROM search_text"));
        Assert.Equal(_fx.ModelA, _fx.Text("SELECT model_id FROM search_text"));            // caption ができたので model_id が入る
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM search_text"));
    }

    [Fact]
    public async Task A_document_without_manual_tags_is_the_same_string_as_before_manual_tags_existed()
    {
        await _fx.AddImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "HP回復の画面", ["銀髪", "ロングヘア"]);
        await _fx.SaveAsync(2, _fx.ModelA, "", "説明だけ", []);
        await _fx.SaveAsync(3, _fx.ModelA, "o", "d", ["t"]);
        await _tags.AddAsync(H(3), "付ける", Ct);
        await _tags.RemoveAsync(H(3), "付ける", Ct);

        Assert.Equal("がちゃ10連|hp回復の画面|銀髪、ろんぐへあ", _fx.Text($"SELECT norm_text FROM search_text WHERE hash = '{H(1)}'"));
        Assert.Equal("|説明だけ|", _fx.Text($"SELECT norm_text FROM search_text WHERE hash = '{H(2)}'"));
        Assert.Equal("o|d|t", _fx.Text($"SELECT norm_text FROM search_text WHERE hash = '{H(3)}'"));
    }

    [Fact]
    public async Task The_incremental_index_and_a_rebuild_from_captions_and_tags_have_exactly_the_same_contents()
    {
        await _fx.AddImagesAsync(5);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "説明", ["銀髪"], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "", "猫の絵", [], minutes: 2);
        await _tags.AddAsync(H(1), "お気に入り", Ct);
        await _tags.AddAsync(H(3), "キャプションなし", Ct);          // caption の無い画像
        await _tags.AddAsync(H(3), "猫", Ct);
        await _tags.AddToManyAsync([H(2), H(4)], "風景", Ct);
        await _tags.RemoveAsync(H(4), "風景", Ct);
        var incremental = _fx.IndexDump();
        Assert.Equal(3, incremental.Count);                        // 画像 1・2・3(4 は外した、5 は何も無い)

        await _fx.TamperSpecAsync();
        var rebuilt = new FtsIndex(_fx.Db, _fx.Log);
        await rebuilt.EnsureAsync(Ct);
        _fx.UseIndex(rebuilt);

        Assert.Equal(1, rebuilt.RebuildCount);
        Assert.Equal((3, 0), rebuilt.LastRebuild);
        Assert.Equal(incremental, _fx.IndexDump());
    }

    // ---------------------------------------------------------------- 1 トランザクション・索引の故障

    [Fact]
    public async Task An_unexpected_exception_while_indexing_rolls_back_the_tag_too()
    {
        await _fx.AddImagesAsync(1);
        await _fx.Index.EnsureAsync(Ct);
        _fx.Index.BuildDocument = (_, _, _, _) => throw new IOException("索引の途中で失敗(テスト)");   // InvalidOperation / InvalidData 以外 = 索引文書の問題ではない

        await Assert.ThrowsAsync<IOException>(() => _tags.AddAsync(H(1), "猫", Ct));

        Assert.Equal(0, _fx.Count("SELECT count(*) FROM tags"));         // タグの行も、リンクも残らない(索引と同じトランザクション)
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM image_tags"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
    }

    [Fact]
    public async Task A_document_that_cannot_be_built_keeps_the_tag_and_skips_the_index_row_with_a_warning()
    {
        await _fx.AddImagesAsync(1);
        await _fx.Index.EnsureAsync(Ct);
        _fx.Index.BuildDocument = (_, _, _, _) => throw new InvalidOperationException("作れない(テスト)");

        Assert.True(await _tags.AddAsync(H(1), "猫", Ct));                 // タグは保存する(索引は派生データ)

        Assert.Equal(Names("猫"), await _tags.GetAsync(H(1), Ct));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM search_text"));
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN ", StringComparison.Ordinal) && m.Contains("猫", StringComparison.Ordinal) && m.Contains("作れない", StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_the_index_is_failed_tags_are_still_saved_and_a_later_rebuild_brings_them_into_the_index()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ocr", "desc", []);
        await _fx.TamperSpecAsync();
        var broken = new FtsIndex(_fx.Db, _fx.Log) { BuildDocument = (_, _, _, _) => throw new IOException("作り直しの失敗(テスト)") };
        await Assert.ThrowsAsync<IOException>(() => broken.EnsureAsync(Ct));
        Assert.Equal(FtsIndexState.Failed, broken.State);
        var tags = new ManualTags(_fx.Db, broken);

        Assert.True(await tags.AddAsync(H(1), "猫", Ct));                    // 索引が Failed でも、タグは保存する

        Assert.Equal(Names("猫"), await tags.GetAsync(H(1), Ct));
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN ", StringComparison.Ordinal) && m.Contains("猫", StringComparison.Ordinal));
        broken.BuildDocument = FtsDocumentBuilder.Build;
        await broken.EnsureAsync(Ct);                                         // 直ったあとの作り直しで、タグが索引に入る
        _fx.UseIndex(broken);
        Assert.Equal([H(1)], _fx.Hits("猫"));
    }

    // ---------------------------------------------------------------- 画面用の読み出し

    [Fact]
    public async Task The_tags_of_an_image_are_listed_by_name_ignoring_ascii_case()
    {
        await _fx.AddImagesAsync(1);
        foreach (string n in new[] { "banana", "Apple", "cherry", "あいう", "アイウ", "Zed" }) await _tags.AddAsync(H(1), n, Ct);
        Assert.Equal(Names("Apple", "banana", "cherry", "Zed", "あいう", "アイウ"), await _tags.GetAsync(H(1), Ct));
    }

    [Fact]
    public async Task Candidates_are_ordered_by_how_many_images_use_them_then_by_name_and_can_exclude_names_and_be_limited()
    {
        await _fx.AddImagesAsync(4);
        await _tags.AddToManyAsync([H(1), H(2), H(3)], "風景", Ct);        // 3
        await _tags.AddToManyAsync([H(1), H(2)], "猫", Ct);               // 2
        await _tags.AddToManyAsync([H(1), H(2)], "犬", Ct);               // 2(猫と同数 → 名前順)
        await _tags.AddAsync(H(4), "Apple", Ct);                          // 1

        IReadOnlyList<TagCandidate> all = await _tags.GetCandidatesAsync(10, null, Ct);
        Assert.Equal(["風景", "犬", "猫", "Apple"], all.Select(c => c.Name).ToArray());     // 同数(犬・猫)は名前順(文字コード順: 犬 U+72AC < 猫 U+732B)
        Assert.Equal([3, 2, 2, 1], all.Select(c => c.ImageCount).ToArray());

        Assert.Equal(["風景", "犬"], (await _tags.GetCandidatesAsync(2, null, Ct)).Select(c => c.Name).ToArray());
        Assert.Equal(["犬", "猫"], (await _tags.GetCandidatesAsync(2, ["風景"], Ct)).Select(c => c.Name).ToArray());  // その画像に付いているタグは除ける
        Assert.Equal(["風景", "犬", "Apple"], (await _tags.GetCandidatesAsync(10, ["猫"], Ct)).Select(c => c.Name).ToArray());
        Assert.Equal(["風景", "犬", "猫"], (await _tags.GetCandidatesAsync(10, ["apple"], Ct)).Select(c => c.Name).ToArray());     // 除外は大文字・小文字を区別しない
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _tags.GetCandidatesAsync(0, null, Ct));
    }

    [Fact]
    public async Task The_candidates_are_empty_when_nothing_is_tagged()
    {
        Assert.Empty(await _tags.GetCandidatesAsync(5, null, Ct));
    }

    [Fact]
    public async Task Tags_from_many_images_are_read_in_one_call_for_the_search_detail()
    {
        await _fx.AddImagesAsync(3);
        await _tags.AddToManyAsync([H(1), H(2)], "猫", Ct);
        await _tags.AddAsync(H(2), "Apple", Ct);

        using var read = _fx.Db.CreateReadConnection();
        Dictionary<string, IReadOnlyList<string>> map = ManualTags.ForImages(read, [H(1), H(2), H(3), H(1)]);

        Assert.Equal(2, map.Count);                                          // タグの無い画像 3 は入らない
        Assert.Equal(Names("猫"), map[H(1)]);
        Assert.Equal(Names("Apple", "猫"), map[H(2)]);
    }

    [Fact]
    public async Task Tags_of_more_images_than_one_read_chunk_are_all_returned()
    {
        int n = CaptionStore.ReadChunk + 20;
        await _fx.AddImagesAsync(n);
        await _tags.AddToManyAsync(Enumerable.Range(1, n).Select(H).ToArray(), "多数", Ct);

        using var read = _fx.Db.CreateReadConnection();
        Assert.Equal(n, ManualTags.ForImages(read, Enumerable.Range(1, n).Select(H)).Count);
    }
}
