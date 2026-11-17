using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Search;
using Ohanami.Core.Tags;
using Ohanami.Core.Tests.Captions;
using Ohanami.Core.Tests.Data;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests.Tags;

/// <summary>
/// 手動タグと全文検索の経路(ADR 0036): 索引文書の組み立て(手動タグが無ければ、タグの欄を足さない文字列)、規約の文字列、
/// 検索結果の詳細(TextDetail)の 4 つ目の欄、caption が無い画像の詳細、検索のあとでタグが外された場合。
/// </summary>
public sealed class ManualTagSearchTests : IDisposable
{
    private readonly CaptionFixture _fx = new();
    private readonly ManualTags _tags;
    private static CancellationToken Ct => CaptionFixture.Ct;

    public ManualTagSearchTests()
    {
        _tags = new ManualTags(_fx.Db, _fx.Index);
    }

    public void Dispose() => _fx.Dispose();

    private async Task SetUpImagesAsync(int count)
    {
        await _fx.AddImagesAsync(count);
        long folder = await AddFolderAsync(_fx.Db);
        for (int i = 1; i <= count; i++) await AddImageAsync(_fx.Db, folder, Hash(i), [$@"C:\shots\{i}.png"]);
    }

    private SearchService FullTextOnly() => new(_fx.Db, [new FullTextPath(_fx.Db, _fx.Index)], new VectorTable("none", Dim));

    private async Task<TextDetail> DetailOfFirst(string query)
    {
        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery(query), Ct);
        PathHit hit = Assert.Single(r.Results[0].Hits);
        return Assert.IsType<TextDetail>(hit.Detail);
    }

    // ---------------------------------------------------------------- 索引文書の組み立て(純粋な関数)

    [Fact]
    public void Without_manual_tags_the_document_is_exactly_the_old_string_for_every_shape_of_caption()
    {
        foreach ((string o, string d, string[] t) in new[] { ("セリフ", "説明文", new[] { "銀髪", "ロングヘア" }), ("", "d", Array.Empty<string>()), ("o", "", new[] { "t" }), ("", "", Array.Empty<string>()) })
        {
            Assert.Equal(FtsDocumentBuilder.Join(o, d, t), FtsDocumentBuilder.Join(o, d, t, []));
            Assert.Equal(FtsDocumentBuilder.Build(o, d, t), FtsDocumentBuilder.Build(o, d, t, []));
        }
        Assert.Equal("|d|", FtsDocumentBuilder.Join("", "d", [], []));          // 手動タグが無いときの文字列(末尾が「|」)。4 つ目の欄の「|」を足さない
    }

    [Fact]
    public void With_manual_tags_a_fourth_field_is_appended_and_a_caption_less_image_gets_empty_leading_fields()
    {
        Assert.Equal("セリフ|説明文|銀髪、ロングヘア|猫、犬", FtsDocumentBuilder.Join("セリフ", "説明文", ["銀髪", "ロングヘア"], ["猫", "犬"]));
        Assert.Equal("o|d||猫", FtsDocumentBuilder.Join("o", "d", [], ["猫"]));       // VLM のタグが空でも、欄の位置は変わらない
        Assert.Equal("|||猫、犬", FtsDocumentBuilder.Join("", "", [], ["猫", "犬"]));  // caption が無い画像
        Assert.Throws<ArgumentNullException>(() => FtsDocumentBuilder.Join("o", "d", [], null!));
        FtsDocument doc = FtsDocumentBuilder.Build("", "", [], ["猫", "犬"]);
        Assert.Equal("|||猫、犬", doc.NormText);
        Assert.Contains("猫", doc.Tokens, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spec_string_names_the_new_document_selection_rule()
    {
        Assert.Equal("latest-caption+manual-tags-v1", FtsSpec.DocSelectVersion);
        Assert.Contains("doc-select=latest-caption+manual-tags-v1\n", FtsIndexSpec.Current.CanonicalString(), StringComparison.Ordinal);
        Assert.DoesNotContain("latest-caption-v1\n", FtsIndexSpec.Current.CanonicalString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_index_made_under_the_previous_spec_is_rebuilt_on_the_next_open_with_the_same_search_results_for_untagged_images()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ10連", "説明", ["銀髪"], minutes: 1);
        await _fx.SaveAsync(2, _fx.ModelA, "", "猫の絵", [], minutes: 2);
        var before = _fx.IndexDump();
        await _fx.TamperSpecAsync(FtsIndexSpec.Current.CanonicalString().Replace("latest-caption+manual-tags-v1", "latest-caption-v1", StringComparison.Ordinal));  // 手動タグの導入前のアプリが作った索引

        var reopened = new FtsIndex(_fx.Db, _fx.Log);
        await reopened.EnsureAsync(Ct);
        _fx.UseIndex(reopened);

        Assert.Equal(1, reopened.RebuildCount);
        Assert.Equal(before, _fx.IndexDump());                       // 手動タグの無い画像の文書は、作り直しても 1 文字も変わらない
        Assert.Equal([Hash(1)], _fx.Hits("ガチャ"));
    }

    // ---------------------------------------------------------------- 検索結果の詳細(TextDetail)

    [Fact]
    public async Task Text_detail_has_a_fourth_field_with_the_manual_tags_joined_and_highlighted()
    {
        await SetUpImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "セリフ", "説明", ["銀髪"]);
        await _tags.AddAsync(Hash(1), "お気に入り", Ct);
        await _tags.AddAsync(Hash(1), "ＡＢＣ猫", Ct);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("お気に入り abc");

        Assert.Equal([TextField.Ocr, TextField.Description, TextField.Tags, TextField.ManualTags], d.Matches.Select(m => m.Field).ToArray());
        FieldMatch manual = d.Matches[3];
        Assert.Equal("ABC猫、お気に入り", manual.Text);                              // 名前順(大文字・小文字を区別しない)、「、」でつなぐ
        Assert.Equal([new TextSpan(0, 3), new TextSpan(5, 5)], manual.Spans);       // ABC と お気に入り
        Assert.Empty(d.Matches[0].Spans);
        Assert.Empty(d.Matches[2].Spans);
    }

    [Fact]
    public async Task An_image_without_a_caption_gets_a_detail_with_empty_leading_fields_and_its_manual_tags()
    {
        await SetUpImagesAsync(2);
        await _tags.AddAsync(Hash(1), "ふわふわ", Ct);
        await _tags.AddAsync(Hash(1), "猫", Ct);
        await _fx.Index.EnsureAsync(Ct);

        SearchResponse r = await FullTextOnly().SearchAsync(new SearchQuery("ふわふわ"), Ct);

        SearchResult x = Assert.Single(r.Results);
        Assert.Equal(Hash(1), x.Hash);
        TextDetail d = Assert.IsType<TextDetail>(Assert.Single(x.Hits).Detail);
        Assert.Equal(4, d.Matches.Count);
        Assert.All(d.Matches.Take(3), m => { Assert.Equal("", m.Text); Assert.Empty(m.Spans); });
        Assert.Equal("ふわふわ、猫", d.Matches[3].Text);
        Assert.Equal([new TextSpan(0, 4)], d.Matches[3].Spans);
    }

    [Fact]
    public async Task A_caption_only_image_has_an_empty_fourth_field_so_the_detail_always_has_four_fields()
    {
        await SetUpImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "セリフ", "説明", ["銀髪"]);
        await _fx.Index.EnsureAsync(Ct);

        TextDetail d = await DetailOfFirst("セリフ");

        Assert.Equal(4, d.Matches.Count);
        Assert.Equal("", d.Matches[3].Text);
        Assert.Empty(d.Matches[3].Spans);
        Assert.Equal([new TextSpan(0, 3)], d.Matches[0].Spans);
    }

    [Fact]
    public async Task A_manual_tag_added_after_a_search_does_not_change_that_search_but_the_next_one_finds_it()
    {
        await SetUpImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャ", "", []);
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = FullTextOnly();
        Assert.Empty((await service.SearchAsync(new SearchQuery("お気に入り"), Ct)).Results);

        await _tags.AddAsync(Hash(2), "お気に入り", Ct);

        Assert.Equal([Hash(2)], (await service.SearchAsync(new SearchQuery("お気に入り"), Ct)).Results.Select(x => x.Hash).ToArray());
    }

    private static bool IsGoneWarning(string m) =>
        m.StartsWith("WARN ", StringComparison.Ordinal) && m.Contains("全文検索: 索引にある画像", StringComparison.Ordinal) && m.Contains("caption も手動タグも無い", StringComparison.Ordinal);

    [Fact]
    public async Task When_the_last_tag_is_removed_between_the_search_and_the_detail_the_detail_is_empty_and_one_warning_is_logged()
    {
        await SetUpImagesAsync(1);
        await _tags.AddAsync(Hash(1), "猫", Ct);
        await _fx.Index.EnsureAsync(Ct);
        PathSearchResult result = await new FullTextPath(_fx.Db, _fx.Index, _fx.Log).SearchAsync("猫", Ct);
        Assert.Equal(PathState.Ok, result.State);

        await _tags.RemoveAsync(Hash(1), "猫", Ct);        // 検索のあとで、利用者が最後のタグを外した(正当な競合)
        result.PrepareDetails([0]);
        Assert.DoesNotContain(_fx.Log.Messages, IsGoneWarning);      // 警告は詳細を作るとき(DetailOf)に出る

        TextDetail d = Assert.IsType<TextDetail>(result.DetailOf(0));
        Assert.Equal(4, d.Matches.Count);
        Assert.All(d.Matches, m => { Assert.Equal("", m.Text); Assert.Empty(m.Spans); });
        string warning = Assert.Single(_fx.Log.Messages, IsGoneWarning);       // 警告はちょうど 1 行
        Assert.Contains(Hash(1)[..12] + "…", warning, StringComparison.Ordinal);   // hash の先頭 12 文字
        Assert.DoesNotContain(Hash(1), warning, StringComparison.Ordinal);        // 全部は出さない
        Assert.Contains("続くなら索引の食い違い", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_caption_is_deleted_between_the_search_and_the_detail_the_same_empty_detail_and_warning_come_out()
    {
        await SetUpImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "セリフ", "説明", ["銀髪"]);
        await _fx.Index.EnsureAsync(Ct);
        PathSearchResult result = await new FullTextPath(_fx.Db, _fx.Index, _fx.Log).SearchAsync("セリフ", Ct);
        await _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "DELETE FROM captions"), Ct);   // 「見つからない画像の記録を消す」(ADR 0007)で起きうる

        result.PrepareDetails([0]);
        TextDetail d = Assert.IsType<TextDetail>(result.DetailOf(0));

        Assert.All(d.Matches, m => Assert.Equal("", m.Text));
        Assert.Single(_fx.Log.Messages, IsGoneWarning);
    }

    [Fact]
    public async Task No_warning_is_logged_when_the_image_still_has_a_caption_or_a_manual_tag()
    {
        await SetUpImagesAsync(3);
        await _fx.SaveAsync(1, _fx.ModelA, "ふわふわ", "", []);
        await _tags.AddAsync(Hash(2), "ふわふわ", Ct);
        await _fx.SaveAsync(3, _fx.ModelA, "ふわふわ", "", []);
        await _tags.AddAsync(Hash(3), "ふわふわ", Ct);
        await _fx.Index.EnsureAsync(Ct);
        PathSearchResult result = await new FullTextPath(_fx.Db, _fx.Index, _fx.Log).SearchAsync("ふわふわ", Ct);

        result.PrepareDetails([0, 1, 2]);
        for (int i = 0; i < 3; i++) Assert.IsType<TextDetail>(result.DetailOf(i));

        Assert.DoesNotContain(_fx.Log.Messages, IsGoneWarning);
    }

    [Fact]
    public async Task Captions_and_manual_tags_of_the_detail_are_read_at_the_same_point_in_time()
    {
        await SetUpImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "セリフ", "説明", []);
        await _tags.AddAsync(Hash(1), "猫", Ct);
        await _tags.AddAsync(Hash(2), "犬", Ct);

        // 2 つの読み取りの間に、別の書き込み(画像 1 の caption を消し、画像 2 のタグを外す)が入っても、このトランザクションには見えない
        using var read = _fx.Db.CreateReadConnection();
        var (captions, tags) = FullTextPath.ReadDetailSources(read, [Hash(1), Hash(2)], betweenReads: () =>
        {
            _fx.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"DELETE FROM captions WHERE hash = '{Hash(1)}'"), Ct).GetAwaiter().GetResult();
            _tags.RemoveAsync(Hash(2), "犬", Ct).GetAwaiter().GetResult();
        });

        Assert.Contains(Hash(1), captions.Keys);                                  // 読み始めた時点の caption が見える
        Assert.Equal(["猫"], tags[Hash(1)]);
        Assert.Equal(["犬"], tags[Hash(2)]);                                      // 2 番目の読み取りも、同じ時点(外す前)の手動タグを見る
        // 終わったあとは、新しい状態が見える(トランザクションは閉じている)
        using var after = _fx.Db.CreateReadConnection();
        var (captions2, tags2) = FullTextPath.ReadDetailSources(after, [Hash(1), Hash(2)]);
        Assert.Empty(captions2);
        Assert.False(tags2.ContainsKey(Hash(2)));
    }

    [Fact]
    public async Task Tagging_does_not_change_which_untagged_images_match_a_query()
    {
        await SetUpImagesAsync(4);
        await _fx.SaveAsync(1, _fx.ModelA, "ガチャを引く", "", []);
        await _fx.SaveAsync(2, _fx.ModelA, "ガチャの結果", "", []);
        await _fx.SaveAsync(3, _fx.ModelA, "関係のない文書", "", []);
        await _fx.Index.EnsureAsync(Ct);
        string[] before = (await FullTextOnly().SearchAsync(new SearchQuery("ガチャ"), Ct)).Results.Select(x => x.Hash).OrderBy(h => h, StringComparer.Ordinal).ToArray();

        await _tags.AddAsync(Hash(4), "別のタグ", Ct);       // 画像 4 だけにタグを付ける

        string[] after = (await FullTextOnly().SearchAsync(new SearchQuery("ガチャ"), Ct)).Results.Select(x => x.Hash).OrderBy(h => h, StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);                                    // 一致する画像の集合は変わらない(順位は、平均の文書長が変わるので動きうる。ADR 0036)
        Assert.Equal([Hash(4)], (await FullTextOnly().SearchAsync(new SearchQuery("別のタグ"), Ct)).Results.Select(x => x.Hash).ToArray());
    }
}
