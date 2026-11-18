using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 「画像・検索語・インデックスは PC の外に出ない。ネットワークに出るのは、利用者が押したときの、モデルのファイルの取得だけ」(AGENTS.md の約束)の見張り。
/// 製品のコード(src/ のすべて。コメントは除く)を読んで、ネットワークに触れてよい場所を、許可の表(式・許すファイル・理由)で決める。
/// 表に無いファイルが使えば落ちる(使うなら、約束を破らないことを確かめて、表に足す)。
/// <list type="bullet">
/// <item>ネットワークの API(HttpClient・System.Net・ソケット・WinInet など)は、127.0.0.1 の llama-server とやり取りする 2 ファイルと、モデルの取得の 1 ファイルだけ。</item>
/// <item><c>huggingface.co</c> の文字は、取得元の定数を持つカタログだけ。URL のリテラルも、そのカタログと、127.0.0.1 の llama-server だけ。</item>
/// <item>送るのは GET だけ(POST などは、127.0.0.1 の VLM への依頼のファイルだけ)。Cookie・認証は使わない。</item>
/// <item>ダウンロードを始める呼び出し(ModelDownloader を作る・DownloadAsync を呼ぶ)は、1 か所まで。画面ができたら、その 1 か所のファイル名を表に足す。</item>
/// </list>
/// 見張れない範囲: 型の名前を書かない呼び出し(リフレクション・別のアセンブリ経由)、文字列を組み立てたホスト名、NuGet のパッケージの中の通信
/// (Core・App は、NuGet の通信するパッケージを使っていない)。
/// </summary>
public sealed class NetworkBoundaryGuardTests
{
    private const string Downloader = "ModelDownloader.cs";

    /// <summary>ダウンロードを始める呼び出しの 1 か所(アプリの、セットアップの画面の口の製品の実装。呼ぶのは、画面の「ダウンロード」と「2 つともダウンロード」のボタンだけ)。</summary>
    private const string AppDownloads = "ModelDownloads.cs";

    /// <summary>(検出する式, その式を使ってよいファイル名, 理由)。表に無いファイルが使えば違反。</summary>
    private static readonly (string Pattern, string[] AllowedFiles, string Why)[] Rules =
    [
        (@"\bSystem\.Net\b|\bHttpClient\b|\bHttpClientHandler\b|\bSocketsHttpHandler\b|\bHttpMessageHandler\b|\bHttpRequestMessage\b|\bHttpResponseMessage\b|\bHttpWebRequest\b|\bWebRequest\b|\bWebClient\b|\bTcpClient\b|\bTcpListener\b|\bUdpClient\b|\bWebSocket\w*\b",
            ["LlamaServer.cs", "VlmClient.cs", Downloader],
            "ネットワークの API(LlamaServer・VlmClient は 127.0.0.1 の llama-server だけ。ModelDownloader はモデルの取得だけ)"),
        (@"\bwininet\b|\bwinhttp\b|\bws2_32\b|\burlmon\b|\bDnsQuery\b|\bgetaddrinfo\b", [], "OS のネットワークの API を直接呼ぶ(P/Invoke)"),
        (@"huggingface\.co", ["ModelCatalog.cs"], "取得元のホスト名(カタログの定数だけ)"),
        (@"https?://", ["ModelCatalog.cs", "LlamaServer.cs"], "URL のリテラル(ModelCatalog = 取得元。LlamaServer = 127.0.0.1)"),
        (@"\bhttp://(?!127\.0\.0\.1[:/""])", [], "TLS でない URL(127.0.0.1 の llama-server 以外)"),
        (@"\bHttpMethod\.(Post|Put|Delete|Patch|Head|Options|Trace|Connect)\b|\.(Post|Put|Delete|Patch)Async\(", ["VlmClient.cs"], "GET 以外の送信(127.0.0.1 の VLM への依頼だけ)"),
        (@"\bCookieContainer\b|\bUseCookies\s*=\s*true\b|\bAuthenticationHeaderValue\b|\bNetworkCredential\b|\bCredentialCache\b|\bAuthorization\b|\bUseDefaultCredentials\b",
            [], "Cookie・認証(送らない)"),
        (@"\bnew ModelDownloader\(", [AppDownloads], "ダウンロードを始める準備(画面の 1 か所だけ。画面ができたら、そのファイル名を足す)"),
        (@"\.DownloadAsync\(", [AppDownloads], "ダウンロードを始める呼び出し(画面の 1 か所だけ。画面ができたら、そのファイル名を足す)"),
    ];

    private static IEnumerable<string> SourceFiles()
    {
        string src = Path.Combine(RepoPaths.Root, "src");
        return Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ソース(コメントを除く)の中で、規則に当たるファイル名。</summary>
    internal static IEnumerable<string> FilesMatching(string pattern, IEnumerable<(string File, string Code)> sources) =>
        sources.Where(s => Regex.IsMatch(s.Code, pattern, RegexOptions.CultureInvariant)).Select(s => s.File).Distinct();

    private static List<(string File, string Code)> ReadSources() =>
        [.. SourceFiles().Select(f => (Path.GetFileName(f), CommentStripper.StripCSharp(File.ReadAllText(f))))];

    [Fact]
    public void Only_the_allowed_files_touch_the_network_and_nothing_else_in_the_product_does()
    {
        List<(string File, string Code)> sources = ReadSources();
        Assert.True(sources.Count >= 100, $"ソースを読めていない(読んだのは {sources.Count} 個)");

        var violations = new List<string>();
        foreach ((string pattern, string[] allowed, string why) in Rules)
        {
            foreach (string file in FilesMatching(pattern, sources))
            {
                if (!allowed.Contains(file, StringComparer.Ordinal)) violations.Add($"{file}: {why}");
            }
        }

        Assert.True(violations.Count == 0, "約束(ネットワークに出るのは、モデルの取得のときだけ)の外の使い方がある:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_allowed_files_really_exist_and_use_what_the_table_lets_them_use()
    {
        // 表が古くなって(ファイル名が変わって)、何も見張らないまま通ることを防ぐ
        List<(string File, string Code)> sources = ReadSources();
        foreach ((string pattern, string[] allowed, string why) in Rules)
        {
            foreach (string file in allowed)
                Assert.True(sources.Any(s => s.File == file), $"表のファイルが無い: {file}({why})");
        }
        Assert.Contains(Downloader, FilesMatching(@"\bHttpClient\b", sources));
        Assert.Contains("LlamaServer.cs", FilesMatching(@"\bHttpClient\b", sources));
        Assert.Contains("VlmClient.cs", FilesMatching(@"\bHttpClient\b", sources));
        Assert.Equal(["ModelCatalog.cs"], FilesMatching(@"huggingface\.co", sources));          // 取得元のホスト名は、カタログの 1 か所
    }

    [Fact]
    public void The_two_llama_server_files_only_name_the_loopback_address()
    {
        // LlamaServer・VlmClient が書く URL のホストは、127.0.0.1 だけ(外のホストの文字列を持たない)
        foreach ((string file, string code) in ReadSources().Where(s => s.File is "LlamaServer.cs" or "VlmClient.cs"))
        {
            foreach (Match m in Regex.Matches(code, @"https?://([^/:""\s{]+)"))
                Assert.True(m.Groups[1].Value == "127.0.0.1", $"{file}: 127.0.0.1 以外のホスト: {m.Value}");
        }
    }

    [Fact]
    public void The_downloader_sends_only_get_requests_and_has_no_other_hosts_in_its_code()
    {
        string code = ReadSources().Single(s => s.File == Downloader).Code;

        Assert.Contains("HttpMethod.Get", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"https?://", code);                                                 // ホストは、カタログの定数から(コードに書かない)
        Assert.Matches(@"UseCookies\s*=\s*false", code);
    }

    // ---------------------------------------------------------------- 見張り自身の確かめ(何も検出しないまま通ってしまわないように)

    [Theory]
    [InlineData("var c = new HttpClient();", @"\bHttpClient\b", 1)]
    [InlineData("using System.Net.Http;", @"\bSystem\.Net\b", 1)]
    [InlineData("// new HttpClient()", @"\bHttpClient\b", 0)]
    [InlineData("string s = \"https://huggingface.co/x\";", @"huggingface\.co", 1)]
    [InlineData("string s = \"http://example.test/x\";", @"\bhttp://(?!127\.0\.0\.1[:/""])", 1)]
    [InlineData("string s = \"http://127.0.0.1:8080\";", @"\bhttp://(?!127\.0\.0\.1[:/""])", 0)]
    [InlineData("await h.PostAsync(u, c);", @"\.(Post|Put|Delete|Patch)Async\(", 1)]
    [InlineData("await d.DownloadAsync(a, b, s, p, ct);", @"\.DownloadAsync\(", 1)]
    [InlineData("public Task<R> DownloadAsync(string a)", @"\.DownloadAsync\(", 0)]
    public void The_patterns_find_what_they_claim(string source, string pattern, int expectedFiles)
    {
        IEnumerable<(string, string)> sources = [("x.cs", CommentStripper.StripCSharp(source))];
        Assert.Equal(expectedFiles, FilesMatching(pattern, sources).Count());
    }

    [Fact]
    public void Every_rule_pattern_is_a_valid_regular_expression_and_has_a_reason()
    {
        foreach ((string pattern, string[] _, string why) in Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            _ = Regex.IsMatch("", pattern);
        }
    }
}
