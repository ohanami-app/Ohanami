using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Captions;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>全文検索の索引にまだ文書が無い画像(SearchService.FindWithoutTextIndexAsync。画面の「テキスト検索のインデックスがまだありません」の材料)。実物の SQLite。</summary>
public sealed class TextIndexLookupTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly CaptionFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private SearchService Service() => new(_fx.Db, [new FullTextPath(_fx.Db, _fx.Index)], new VectorTable("none", Dim));

    [Fact]
    public async Task Images_without_a_caption_are_returned_and_images_with_one_are_not()
    {
        await _fx.AddImagesAsync(4);
        await _fx.SaveAsync(1, _fx.ModelA, "ocr1", "説明", ["タグ"], 1);
        await _fx.SaveAsync(3, _fx.ModelA, "ocr3", "説明", [], 1);
        await _fx.Index.EnsureAsync(Ct);

        var missing = await Service().FindWithoutTextIndexAsync([Hash(1), Hash(2), Hash(3), Hash(4)], Ct);

        Assert.Equal([Hash(2), Hash(4)], missing);   // 画像 2・4 は VLM の前(索引に文書が無い)。入力の順
    }

    [Fact]
    public async Task Hashes_unknown_to_the_database_and_duplicates_are_handled_and_an_empty_input_gives_an_empty_result()
    {
        await _fx.AddImagesAsync(2);
        await _fx.SaveAsync(1, _fx.ModelA, "ocr1", "説明", [], 1);
        await _fx.Index.EnsureAsync(Ct);
        SearchService service = Service();

        Assert.Equal([Hash(2), Hash(99)], await service.FindWithoutTextIndexAsync([Hash(2), Hash(1), Hash(2), Hash(99)], Ct));   // 知らないハッシュは「文書が無い」。重複は 1 つ
        Assert.Empty(await service.FindWithoutTextIndexAsync([], Ct));
    }

    [Fact]
    public async Task More_hashes_than_one_sql_chunk_are_all_checked()
    {
        await _fx.AddImagesAsync(1);
        await _fx.SaveAsync(1, _fx.ModelA, "ocr1", "説明", [], 1);
        await _fx.Index.EnsureAsync(Ct);
        string[] many = [.. Enumerable.Range(2, 1200).Select(Hash), Hash(1)];   // 500 件ずつの塊を越える

        var missing = await Service().FindWithoutTextIndexAsync(many, Ct);

        Assert.Equal(1200, missing.Count);
        Assert.DoesNotContain(Hash(1), missing);
    }

    [Fact]
    public async Task A_cancelled_lookup_throws_and_a_null_input_is_rejected()
    {
        SearchService service = Service();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FindWithoutTextIndexAsync([Hash(1)], cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.FindWithoutTextIndexAsync(null!, Ct));
    }
}
