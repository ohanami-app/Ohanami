using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Ohanami.Core.Tags;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 手動タグの画面(ADR 0036): 詳細画面の札(× で外す)・追加の欄・候補、一覧で選んだ複数への「タグを付ける」、拒む名前の理由、
/// 付けたあとの更新(詳細の手動タグの欄だけ描き直す。検索の結果は自動では検索し直さない)。Core は偽の窓口(ITagService)。
/// </summary>
public sealed class TagEditingTests
{
    private static string H(char c) => new(c, 64);

    private static SearchResult Result(char c, double rank, TextDetail? text = null)
    {
        var hits = new List<PathHit> { new("visual", "m", rank, 0.1, new VisualDetail(0.2, 0.2)) };
        if (text is not null) hits.Add(new PathHit("fulltext", null, rank, 3, text));
        return new SearchResult(H(c), [$@"C:\no-such-dir\{c}.png"], rank, 0.01, hits);
    }

    private static ImageDetail Detail(string hash) =>
        new(hash, 1000, "png", 4, 2, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero),
            [new ImagePathDetail($@"C:\no-such-dir\{hash[0]}.png", false, null, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 1000)],
            [new StageDetail(Stage.Embed, StageState.Done, "m", null, 1, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            null, []);

    /// <summary>偽の手動タグの窓口。名前の検査は本物(<see cref="ManualTagName"/>)と同じ。呼び出しを記録する。</summary>
    private sealed class FakeTags : ITagService
    {
        public Dictionary<string, SortedSet<string>> ByImage { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public Exception? FailWith { get; set; }
        public TaskCompletionSource? GateAdd { get; set; }
        public int GetCandidatesCalls { get; private set; }

        private SortedSet<string> Of(string hash) => ByImage.TryGetValue(hash, out var set) ? set : ByImage[hash] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public async Task<int> AddToManyAsync(IReadOnlyCollection<string> hashes, string name, CancellationToken ct)
        {
            string normalized = ManualTagName.Normalize(name);
            Calls.Add($"add [{string.Join(",", hashes.Select(h => h[0]))}] {normalized}");
            if (FailWith is { } ex) throw ex;
            if (GateAdd is { } gate) await gate.Task;
            int added = 0;
            foreach (string h in hashes) if (Of(h).Add(normalized)) added++;
            return added;
        }

        public Task<bool> RemoveAsync(string hash, string name, CancellationToken ct)
        {
            Calls.Add($"remove {hash[0]} {name}");
            if (FailWith is { } ex) throw ex;
            return Task.FromResult(Of(hash).Remove(name));
        }

        public Task<IReadOnlyList<string>> GetAsync(string hash, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. Of(hash)]);

        public Task<IReadOnlyList<TagCandidate>> GetCandidatesAsync(int limit, IEnumerable<string>? exclude, CancellationToken ct)
        {
            GetCandidatesCalls++;
            var excluded = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<TagCandidate> list = ByImage.Values.SelectMany(s => s).GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TagCandidate(g.Key, g.Count())).Where(c => !excluded.Contains(c.Name))
                .OrderByDescending(c => c.ImageCount).ThenBy(c => c.Name, StringComparer.Ordinal).Take(limit).ToArray();
            return Task.FromResult(list);
        }
    }

    private sealed class Harness
    {
        public ResultsPanel Panel { get; set; } = null!;
        public required FakeTags Tags { get; init; }
        public List<string> Notices { get; } = [];
        public TestLog Log { get; } = new();
        public List<string> PromptCalls { get; } = [];
        public int Searches { get; set; }
        public DetailView View => Panel.DetailViewForTests;
    }

    private static Harness NewPanel(IReadOnlyList<SearchResult> results, FakeTags? tags = null, Func<int, IReadOnlyList<TagCandidate>, string?>? prompt = null,
        Func<string, CancellationToken, Task<ImageDetail?>>? detail = null)
    {
        tags ??= new FakeTags();
        var h = new Harness { Tags = tags };
        FakeTags service = tags;
        h.Panel = new ResultsPanel(h.Log, h.Notices.Add, new FakeShell())
        {
            DetailFunc = detail ?? ((hash, _) => Task.FromResult<ImageDetail?>(Detail(hash) with { ManualTags = [.. service.ByImage.GetValueOrDefault(hash) ?? []] })),
            ImageFunc = (_, _) => Task.FromResult(ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[32]), false)),
            DisplayZone = TimeZoneInfo.Utc,
            TagService = service,
            PromptTag = (count, candidates) =>
            {
                h.PromptCalls.Add($"{count} 枚、候補 {string.Join(",", candidates.Select(c => c.Name))}");
                return Task.FromResult(prompt?.Invoke(count, candidates));
            },
            SearchFunc = (q, _) => { h.Searches++; return Task.FromResult(new SearchResponse(q.Text, results, [], 0, 0, TimeSpan.Zero)); },
        };
        h.Panel.SetResults(results);
        return h;
    }

    private static SearchResult[] Three() => [Result('a', 1), Result('b', 2), Result('c', 3)];

    private static FakeTags Seeded()
    {
        var tags = new FakeTags();
        tags.ByImage[H('a')] = new SortedSet<string>(["猫"], StringComparer.OrdinalIgnoreCase);
        tags.ByImage[H('b')] = new SortedSet<string>(["猫", "風景"], StringComparer.OrdinalIgnoreCase);
        tags.ByImage[H('c')] = new SortedSet<string>(["風景", "犬"], StringComparer.OrdinalIgnoreCase);
        return tags;
    }

    // ---------------------------------------------------------------- 詳細画面: 札・候補

    [Fact]
    public async Task The_detail_shows_the_tags_of_the_image_as_removable_chips_with_an_input_and_the_other_tags_as_candidates()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.Equal(["猫"], h.View.ChipNames);
            Assert.True(h.View.TagsEditable);
            Assert.True(h.View.TagInputVisible);
            Assert.True(h.View.RemoveButtonExists("猫"));
            Assert.Equal(["風景 (2)", "犬 (1)"], h.View.CandidateLabels);      // 使われている数の多い順。画像 a に付いている「猫」は出さない
            Assert.Equal("", h.View.TagError);
        });
    }

    [Fact]
    public async Task Typing_filters_the_candidates()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.TagInputText = "風";
            Assert.Equal(["風景 (2)"], h.View.CandidateLabels);
            h.View.TagInputText = "存在しない";
            Assert.Empty(h.View.CandidateLabels);
            h.View.TagInputText = "";
            Assert.Equal(["風景 (2)", "犬 (1)"], h.View.CandidateLabels);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Adding_a_typed_tag_writes_it_clears_the_input_redraws_the_chips_and_tells_the_user()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.TagInputText = "  ＨＰ回復 ";                                   // 名前は正規化される(全角 → 半角、前後の空白)
            h.View.PressAdd();
            Assert.True(await Wait.UntilAsync(() => h.View.ChipNames.Count == 2));

            Assert.Equal(["HP回復", "猫"], h.View.ChipNames);
            Assert.Equal("", h.View.TagInputText);
            Assert.Equal("", h.View.TagError);
            Assert.Equal("add [a] HP回復", Assert.Single(h.Tags.Calls));
            Assert.Contains(h.Notices, n => n == "手動タグ「HP回復」を付けました");
        });
    }

    [Fact]
    public async Task Adding_a_tag_that_is_already_there_says_so_and_adds_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));

            await h.Panel.AddDetailTagAsync("猫");

            Assert.Equal(["猫"], h.View.ChipNames);
            Assert.Contains(h.Notices, n => n == "手動タグ「猫」は、すでに付いています");
        });
    }

    [Fact]
    public async Task A_candidate_button_adds_that_tag_and_the_candidate_list_is_read_again()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));
            int before = h.Tags.GetCandidatesCalls;

            h.View.PressCandidate("風景");
            Assert.True(await Wait.UntilAsync(() => h.View.ChipNames.Contains("風景")));

            Assert.Equal(["犬 (1)"], h.View.CandidateLabels);                       // 付けた「風景」は候補から外れ、残りは犬だけ
            Assert.True(h.Tags.GetCandidatesCalls > before);
            Assert.Contains(h.Notices, n => n == "手動タグ「風景」を付けました");
        });
    }

    [Fact]
    public async Task The_x_button_removes_the_tag_and_it_comes_back_as_a_candidate()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('b'));
            Assert.Equal(["猫", "風景"], h.View.ChipNames);

            h.View.PressRemove("猫");
            Assert.True(await Wait.UntilAsync(() => h.View.ChipNames.Count == 1));

            Assert.Equal(["風景"], h.View.ChipNames);
            Assert.Equal("remove b 猫", Assert.Single(h.Tags.Calls));
            Assert.Contains(h.Notices, n => n == "手動タグ「猫」を外しました");
            Assert.Contains(h.View.CandidateLabels, l => l.StartsWith("猫 (", StringComparison.Ordinal));      // 画像 a がまだ使っているので候補に出る
        });
    }

    [Fact]
    public async Task Removing_the_last_tag_shows_none_and_a_tag_no_image_uses_disappears_from_the_candidates()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var tags = new FakeTags();
            tags.ByImage[H('a')] = new SortedSet<string>(["だけ"], StringComparer.OrdinalIgnoreCase);
            Harness h = NewPanel(Three(), tags);
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.PressRemove("だけ");
            Assert.True(await Wait.UntilAsync(() => h.View.ChipNames.Count == 0));

            Assert.Contains("(なし)", h.View.SideText, StringComparison.Ordinal);
            Assert.Empty(h.View.CandidateLabels);
        });
    }

    // ---------------------------------------------------------------- 拒む名前・失敗

    [Theory]
    [InlineData("", "空")]
    [InlineData("   ", "空")]
    [InlineData("a|b", "「|」")]
    [InlineData("a、b", "「、」")]
    public async Task A_rejected_name_shows_the_reason_under_the_input_and_writes_nothing(string typed, string reason)
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.TagInputText = typed;
            await h.Panel.AddDetailTagAsync(typed);

            Assert.StartsWith("手動タグの名前として使えません: ", h.View.TagError, StringComparison.Ordinal);
            Assert.Contains(reason, h.View.TagError, StringComparison.Ordinal);
            Assert.Equal(typed, h.View.TagInputText);                        // 入れた文字は残す(直せる)
            Assert.Empty(h.Tags.Calls);                                      // 窓口まで届かない(正規化の検査で止まる)
            Assert.Equal(["猫"], h.View.ChipNames);
            h.View.TagInputText = "直した";                                   // 入れ直すと、エラーは消える
            Assert.Equal("", h.View.TagError);
        });
    }

    [Fact]
    public async Task A_name_over_64_characters_is_rejected_with_the_count()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));

            await h.Panel.AddDetailTagAsync(new string('あ', 65));

            Assert.Contains("64 文字を超える (65 文字)", h.View.TagError, StringComparison.Ordinal);
            Assert.Empty(h.Tags.Calls);
        });
    }

    [Fact]
    public async Task A_failing_write_shows_the_message_logs_the_error_and_keeps_the_chips_and_the_typed_text()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FakeTags tags = Seeded();
            Harness h = NewPanel(Three(), tags);
            await h.Panel.OpenDetailAsync(H('a'));
            tags.FailWith = new InvalidOperationException("database is locked");

            h.View.TagInputText = "新しい";
            await h.Panel.AddDetailTagAsync("新しい");

            Assert.Equal("手動タグを付けられませんでした: database is locked", h.View.TagError);
            Assert.Equal("新しい", h.View.TagInputText);
            Assert.Equal(["猫"], h.View.ChipNames);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR タグを付けられなかった", StringComparison.Ordinal));

            await h.Panel.RemoveDetailTagAsync("猫");
            Assert.Equal("手動タグを外せませんでした: database is locked", h.View.TagError);
            Assert.Equal(["猫"], h.View.ChipNames);
        });
    }

    // ---------------------------------------------------------------- 編集できない状態

    [Fact]
    public async Task While_the_record_is_missing_or_the_read_failed_the_tags_are_shown_read_only_and_nothing_can_be_written()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var text = new TextDetail([new FieldMatch(TextField.Ocr, "x", []), new FieldMatch(TextField.Description, "", []), new FieldMatch(TextField.Tags, "", []), new FieldMatch(TextField.ManualTags, "猫、犬", [new TextSpan(0, 1)])]);
            Harness h = NewPanel([Result('a', 1, text)], Seeded(), detail: (_, _) => Task.FromResult<ImageDetail?>(null));

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.Equal(["猫", "犬"], h.View.ChipNames);                      // 検索の結果の欄から(読むだけ)
            Assert.False(h.View.TagsEditable);
            Assert.False(h.View.TagInputVisible);
            Assert.False(h.View.RemoveButtonExists("猫"));
            Assert.Equal(["猫"], h.View.HighlightedChipTexts);
            await h.Panel.AddDetailTagAsync("新しい");
            await h.Panel.RemoveDetailTagAsync("猫");
            Assert.Empty(h.Tags.Calls);
        });
    }

    [Fact]
    public async Task Tag_editing_does_nothing_when_no_detail_is_open_or_there_is_no_service()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.AddDetailTagAsync("猫");                              // 詳細が開いていない
            await h.Panel.RemoveDetailTagAsync("猫");
            Assert.Empty(h.Tags.Calls);

            await h.Panel.OpenDetailAsync(H('a'));
            h.Panel.TagService = null;
            await h.Panel.AddDetailTagAsync("猫");
            Assert.Empty(h.Tags.Calls);
        });
    }

    // ---------------------------------------------------------------- 付けたあとの更新

    [Fact]
    public async Task The_search_highlight_of_a_tag_disappears_after_an_edit_because_the_searched_ranges_no_longer_fit()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var text = new TextDetail([new FieldMatch(TextField.Ocr, "x", []), new FieldMatch(TextField.Description, "", []), new FieldMatch(TextField.Tags, "", []), new FieldMatch(TextField.ManualTags, "猫", [new TextSpan(0, 1)])]);
            FakeTags tags = new();
            tags.ByImage[H('a')] = new SortedSet<string>(["猫"], StringComparer.OrdinalIgnoreCase);
            Harness h = NewPanel([Result('a', 1, text)], tags);
            await h.Panel.OpenDetailAsync(H('a'));
            Assert.Equal(["猫"], h.View.HighlightedChipTexts);                  // 検索のときと同じタグ: 強調される

            await h.Panel.AddDetailTagAsync("犬");

            Assert.Equal(["犬", "猫"], h.View.ChipNames);
            Assert.Empty(h.View.HighlightedChipTexts);                          // 間違った場所を光らせない
        });
    }

    [Fact]
    public async Task Editing_redraws_only_the_tags_the_other_parts_of_the_detail_stay_and_the_search_is_not_run_again()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var text = new TextDetail([new FieldMatch(TextField.Ocr, "ガチャ10連", [new TextSpan(0, 3)]), new FieldMatch(TextField.Description, "", []), new FieldMatch(TextField.Tags, "", []), new FieldMatch(TextField.ManualTags, "", [])]);
            Harness h = NewPanel([Result('a', 1, text), Result('b', 2)], new FakeTags());
            await h.Panel.OpenDetailAsync(H('a'));
            string rankBefore = h.View.SideText[..h.View.SideText.IndexOf("手動タグ", StringComparison.Ordinal)];

            await h.Panel.AddDetailTagAsync("お気に入り");

            Assert.Equal(["ガチャ"], h.View.HighlightedTexts("文字 (OCR)"));        // 文字の欄の強調はそのまま(検索のときの文字列と範囲)
            Assert.Equal(rankBefore, h.View.SideText[..h.View.SideText.IndexOf("手動タグ", StringComparison.Ordinal)]);
            Assert.Equal("1 / 2", h.View.PositionText);                            // 位置も変わらない
            Assert.Equal(0, h.Searches);                                           // 検索の結果は、自動では検索し直さない
            Assert.Equal(H('a'), h.Panel.DetailHash);
        });
    }

    [Fact]
    public async Task A_write_that_finishes_after_moving_to_another_image_does_not_touch_the_image_now_shown()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FakeTags tags = Seeded();
            tags.GateAdd = new TaskCompletionSource();
            Harness h = NewPanel(Three(), tags);
            await h.Panel.OpenDetailAsync(H('a'));

            Task add = h.Panel.AddDetailTagAsync("遅い");                          // 書き込みが終わらないうちに
            h.Panel.NavigateDetail(+1);
            Assert.True(await Wait.UntilAsync(() => h.Panel.DetailHash == H('b') && h.View.TagsEditable));
            h.View.TagInputText = "b で入力中";
            tags.GateAdd.SetResult();
            await add;

            Assert.Equal(H('b'), h.Panel.DetailHash);
            Assert.Equal(["猫", "風景"], h.View.ChipNames);
            Assert.DoesNotContain("遅い", h.View.ChipNames);                         // 新しい画像の札に、前の画像の結果が混ざらない
            Assert.Equal("b で入力中", h.View.TagInputText);                          // 入力中の文字も消えない
            Assert.Contains("遅い", tags.ByImage[H('a')]);                            // 前の画像への書き込み自体は済んでいる
        });
    }

    [Fact]
    public async Task Moving_to_another_image_clears_the_input_and_the_error()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            await h.Panel.OpenDetailAsync(H('a'));
            await h.Panel.AddDetailTagAsync("a|b");
            Assert.NotEqual("", h.View.TagError);

            h.Panel.NavigateDetail(+1);

            Assert.Equal("", h.View.TagError);
            Assert.Equal("", h.View.TagInputText);
            await Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 一覧: 複数を選んで「タグを付ける」

    [Fact]
    public async Task The_list_tag_button_is_enabled_only_with_a_selection_and_shows_the_count_and_the_row_stays_in_place_while_the_detail_is_open()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded());
            Assert.False(h.Panel.TagButton.IsEnabled);
            Assert.Equal("手動タグを付ける…", h.Panel.TagButton.Content);

            h.Panel.ResultGrid.SelectAll();
            Assert.True(h.Panel.TagButton.IsEnabled);
            Assert.Equal("手動タグを付ける… (選択中 3 枚)", h.Panel.TagButton.Content);
            Assert.True(h.Panel.TagRowVisible);

            await h.Panel.OpenDetailAsync(H('a'));
            Assert.True(h.Panel.TagRowVisible);                                    // 隠さない: 隠すと一覧の高さが変わる。詳細画面は、その上に重なって見えなくする
            h.Panel.CloseDetail();
            Assert.True(h.Panel.TagRowVisible);

            h.Panel.SetResults([Result('x', 1)]);                                  // 結果が替わると選択は無くなる
            Assert.False(h.Panel.TagButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Tagging_the_selection_asks_for_a_name_with_the_candidates_adds_to_all_in_one_call_and_tells_how_many_were_new()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FakeTags tags = Seeded();
            Harness h = NewPanel(Three(), tags, prompt: (_, _) => "風景");
            h.Panel.ResultGrid.SelectAll();

            await h.Panel.TagSelectedAsync();

            Assert.Equal("3 枚、候補 猫,風景,犬", Assert.Single(h.PromptCalls));        // 使われている数の多い順(猫 2・風景 2・犬 1。同数は名前順)
            Assert.Equal("add [a,b,c] 風景", Assert.Single(h.Tags.Calls));         // 3 枚を 1 回で
            Assert.Contains(h.Notices, n => n == "手動タグ「風景」を 3 枚に付けました (新しく付いたのは 1 枚)。検索し直すと、手動タグでも見つけられます");   // b と c にはすでに付いている
            Assert.Equal(0, h.Searches);                                           // 検索の結果は自動では検索し直さない
        });
    }

    [Fact]
    public async Task A_prompt_that_throws_is_reported_in_the_banner_and_the_log_not_swallowed()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded(), prompt: (_, _) => throw new InvalidOperationException("ウィンドウを作れない"));
            h.Panel.ResultGrid.SelectAll();

            await h.Panel.TagSelectedAsync();

            Assert.True(h.Panel.BannerVisible);
            Assert.Equal("手動タグの名前を聞く画面を出せませんでした: ウィンドウを作れません", h.Panel.BannerText);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR タグの名前を聞く画面を出せなかった", StringComparison.Ordinal));
            Assert.Empty(h.Tags.Calls);
        });
    }

    [Fact]
    public async Task Cancelling_the_prompt_writes_nothing_and_an_empty_selection_only_tells_the_user()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded(), prompt: (_, _) => null);

            await h.Panel.TagSelectedAsync();                                      // 何も選んでいない
            Assert.Empty(h.PromptCalls);
            Assert.Contains(h.Notices, n => n.Contains("一覧で選んでください", StringComparison.Ordinal));

            h.Panel.ResultGrid.SelectAll();
            await h.Panel.TagSelectedAsync();                                      // キャンセル
            Assert.Single(h.PromptCalls);
            Assert.Empty(h.Tags.Calls);
        });
    }

    [Fact]
    public async Task A_name_the_prompt_returns_that_is_not_allowed_shows_the_reason_in_the_red_banner_and_writes_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), Seeded(), prompt: (_, _) => "a|b");
            h.Panel.ResultGrid.SelectAll();

            await h.Panel.TagSelectedAsync();

            Assert.True(h.Panel.BannerVisible);
            Assert.StartsWith("手動タグを付けられませんでした: ", h.Panel.BannerText, StringComparison.Ordinal);
            Assert.Contains("「|」", h.Panel.BannerText, StringComparison.Ordinal);
            Assert.Empty(h.Tags.Calls);
        });
    }

    [Fact]
    public async Task A_failing_bulk_write_shows_the_message_and_logs_the_error()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FakeTags tags = Seeded();
            tags.FailWith = new InvalidOperationException("disk full");
            Harness h = NewPanel(Three(), tags, prompt: (_, _) => "新しい");
            h.Panel.ResultGrid.SelectAll();

            await h.Panel.TagSelectedAsync();

            Assert.True(h.Panel.BannerVisible);
            Assert.Equal("手動タグを付けられませんでした: disk full", h.Panel.BannerText);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR タグを付けられなかった", StringComparison.Ordinal));
        });
    }
}
