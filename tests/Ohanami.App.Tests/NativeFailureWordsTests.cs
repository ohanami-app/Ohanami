using System;
using System.Collections.Generic;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 利用者に次の行動を言えるネイティブの失敗(NVIDIA の GPU を使えない・アプリのフォルダのファイルが足りない)の文が、画面に出る形(<c>UiText.Plain</c> のあと)で
/// そのまま読めること。文は Core が平らな日本語で作る(Core.Tests の NativeFailureTests が文面を固定している)ので、ここでは、画面の経路で崩れない・コードの綴りが出ない・
/// 準備の失敗の行に 1 度だけ出る、を確かめる。文面は、Core の文と同じものを書き写している(Core の文を変えたら、ここも変える。App は Core の内部の定数を参照できない)。
/// </summary>
public sealed class NativeFailureWordsTests
{
    private const string Gpu = "NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください (CUDA を初期化できませんでした)";

    private const string FilesWithList =
        "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした。足りないファイル: msvcp140.dll、msvcp140_1.dll、msvcp140_atomic_wait.dll、vcruntime140.dll、vcruntime140_1.dll)";

    private const string FilesNoList = "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした)";

    private const string LlamaFiles =
        "アプリのファイルが足りないか、壊れています。zip を展開し直してください (VLM の処理プログラムが必要な DLL を読めず、起動できませんでした。終了コード 0xC0000135。足りないファイル: msvcp140.dll、vcruntime140.dll)";

    /// <summary>すべきことを言う、文の先頭の部分(画面が省略記号で切っても、ここまでは見えるように。状態バーでの幅は NativeFailureVisibilityTests が測る)。</summary>
    private static readonly string[] ActionLeads =
    [
        "NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください",
        "アプリのファイルが足りないか、壊れています。zip を展開し直してください",
    ];

    private static readonly string[] Sentences = [Gpu, FilesWithList, FilesNoList, LlamaFiles];

    /// <summary>画面に出さないコードの綴り(ORT の文・例外の型名・CUDA の関数名・内部のパス)。</summary>
    private static readonly string[] CodeSpellings =
        ["ErrorCode", "TypeInitializationException", "type initializer", "DllNotFoundException", "cudaGetDeviceCount", "ProviderOptions", "onnxruntime\\core", "N:\\_work", "hostname=", "0x8007007E"];

    [Fact]
    public void Plain_keeps_the_sentences_as_they_are()
    {
        foreach (string sentence in Sentences) Assert.Equal(sentence, UiText.Plain(sentence));
    }

    [Fact]
    public void The_shown_sentences_name_the_next_action_and_carry_no_code_spellings()
    {
        foreach (string sentence in Sentences)
        {
            string shown = UiText.Plain(sentence);
            foreach (string spelling in CodeSpellings) Assert.DoesNotContain(spelling, shown, StringComparison.OrdinalIgnoreCase);
        }
        // すべきことが先頭にある(画面が省略記号で切っても、見える)
        Assert.StartsWith(ActionLeads[0], UiText.Plain(Gpu), StringComparison.Ordinal);
        foreach (string sentence in new[] { FilesWithList, FilesNoList, LlamaFiles }) Assert.StartsWith(ActionLeads[1], UiText.Plain(sentence), StringComparison.Ordinal);
    }

    [Fact]
    public void A_preparation_failure_line_shows_the_sentence_once_for_both_stages_with_the_stage_name()
    {
        foreach (string sentence in Sentences)
        {
            foreach (JobKind job in new[] { JobKind.Embed, JobKind.Vlm })
            {
                var failed = new EngineStatus(EnginePhase.Failed, sentence, null);
                var state = new IndexState(IndexRunState.Failed, job, [], failed, null, null, null, null, sentence, 0);

                string text = IndexStatusText.Build(state);
                IndexRows rows = IndexStatusText.BuildRows(state);

                string shown = UiText.Plain(sentence);
                Assert.Contains($"{ProcessingWords.StageName(job)}のインデックスを準備できませんでした: {shown}", text, StringComparison.Ordinal);
                Assert.Equal(1, Count(text, shown));                                                         // 準備の失敗と「止まった原因」で 2 度出さない
                Assert.DoesNotContain("止まった原因", text, StringComparison.Ordinal);
                Assert.Equal(shown, rows.Note);                                                                                  // 左パネルの 1 行は、頭を外して、理由の文から始まる
                Assert.StartsWith($"{ProcessingWords.StageName(job)}のインデックスを準備できませんでした: {shown}", rows.NoteFull, StringComparison.Ordinal);   // ヒントの全文は、段階の名前つき
            }
        }
    }

    [Fact]
    public void Without_a_current_job_the_prefix_is_the_plain_index_creation_line()
    {
        var failed = new EngineStatus(EnginePhase.Failed, Gpu, null);
        var state = new IndexState(IndexRunState.Failed, null, [], failed, null, null, null, null, Gpu, 0);

        Assert.Contains("インデックス作成を準備できませんでした: " + Gpu, IndexStatusText.Build(state), StringComparison.Ordinal);
    }

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
