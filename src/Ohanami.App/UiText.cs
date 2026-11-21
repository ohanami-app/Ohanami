using System;
using System.Text.RegularExpressions;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 画面に出す文から、内部の語(例外の型名・sha256 の値・ハッシュ・llama-server・自己検査・サロゲート・HRESULT・列挙の英語名)を取り除く(ADR 0038。画面は何が起きたかを日本語で短く出す)。
/// 元の文(詳しい理由)は、呼び出し側がログに残す(<c>ICoreLog</c>)。このクラスは**表示用に言い換えるだけ**で、Core の文は変えない
/// (Core の例外のメッセージは、ログにそのまま残る。画面から消した分だけ、ログを見れば分かる)。
/// 知らない形の文は、そのまま通す(隠さない)。どの言い換えも、表示だけの変換(<see cref="Plain(string?)"/> の結果を判定に使わない)。
/// </summary>
internal static class UiText
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string Sha = "[0-9a-fA-F]{64}";

    /// <summary>先頭の例外の型名(`InvalidOperationException: ` など)。</summary>
    private static readonly Regex TypePrefix = new(@"^(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*(?:Exception|Error): ", Opts);

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // sha256 の値(ログには元の文が残る)。「、sha256 {値}」「期待 sha256 {値}。」「: 期待 {値}、実際 {値}」
        (new Regex("、sha256 " + Sha, Opts), ""),
        (new Regex(" ?期待 sha256 " + Sha + "。", Opts), ""),
        (new Regex(":? ?期待 " + Sha + "、実際 " + Sha, Opts), ""),
        (new Regex("の sha256 が違う", Opts), "の中身が想定と違います (SHA-256 が一致しません)"),
        // 期待・実際の値を消したあと、「(場所: …)」が直に続くと括弧が 2 つ続く(「…(SHA-256 が一致しません)(場所: …)」)ので、1 つにまとめる(「(SHA-256 が一致しません。場所: …)」)
        (new Regex(@"\(SHA-256 が一致しません\)\(場所: ", Opts), "(SHA-256 が一致しません。場所: "),
        // VLM の実行プログラム(パスの中の名前は残す: どこに置けばよいかの手がかり)
        // 手がかりになる名前(llama-server)は、括弧で残す(ADR 0038。直したあとの文に、もう一度かからないよう、「(」の直後は除く)
        (new Regex(@"llama-server\.exe が無い", Opts), "VLM の実行ファイル (llama-server) が無い"),
        (new Regex(@"llama-server\.exe の中身", Opts), "VLM の実行ファイル (llama-server) の中身"),
        (new Regex(@"(?<![\\/\w.(-])llama-server が", Opts), "VLM の処理プログラム (llama-server) が"),
        (new Regex(@"(?<![\\/\w.(-])llama-server(?![\w.)-])", Opts), "VLM の処理プログラム (llama-server)"),
        // 列挙の英語名(「ジョブ Vlm のランナーが無い」→ 段階の名前。ジョブ・ランナーは画面の語ではない)
        (new Regex("ジョブ Embed のランナーが無い", Opts), ProcessingWords.StageName(JobKind.Embed) + "のインデックスを作る部品が無い"),
        (new Regex("ジョブ Vlm のランナーが無い", Opts), ProcessingWords.StageName(JobKind.Vlm) + "のインデックスを作る部品が無い"),
        // 命令の形(「止めてから始める」「止めてから消す」)は、お願いの形に
        (new Regex("止めてから始める", Opts), "止めてから始めてください"),
        (new Regex("止めてから消す", Opts), "止めてから消してください"),
        // 列挙の英語名(「ジョブは実行中(状態 Running)」)
        (new Regex(@"\(状態 [A-Za-z]+\)", Opts), ""),
        // 走査のときと内容が違った画像の、ハッシュの前半(「(ハッシュ 1a2b… ≠ 3c4d…。走査のあとで書き換えられた)」)
        (new Regex(@"\(ハッシュ [0-9a-fA-F]+… ≠ [0-9a-fA-F]+…。", Opts), "("),
        // Windows のシェルの HRESULT
        (new Regex(@"[A-Za-z]+ が失敗\(HRESULT (0x[0-9A-Fa-f]+)\)", Opts), "Windows のシェルがエラーを返しました (HRESULT $1)"),
        // 起動時の動作確認(Core の「自己検査」。調べる手がかりになるので、括弧で残す)
        (new Regex(@"(?<![(（])自己検査", Opts), "動作確認 (自己検査)"),
        // 特徴ベクトルの読み込み(Core の文の「ベクトル」を、画面では「特徴ベクトル」と呼ぶ。「埋め込み」は本文に出さない: ADR 0038)
        (new Regex("ベクトルの読み込みに失敗(?:した|している)", Opts), "特徴ベクトルを読み込めませんでした"),
        (new Regex("ベクトルの読み込みがまだ", Opts), "特徴ベクトルの読み込みがまだ"),
        (new Regex("ベクトルを読み込み中", Opts), "特徴ベクトルを読み込み中"),
        // 手動タグの名前が拒まれた理由
        // 括弧の中にあるときは、その括弧に言い添える(二重の括弧にしない)。「。」「(」の直後は、直したあとの文なので、かけ直さない
        (new Regex(@"\(対になっていないサロゲート\)", Opts), "(文字コードとして壊れています。対になっていないサロゲート)"),
        (new Regex(@"(?<![(。])対になっていないサロゲート", Opts), "文字コードとして壊れています (対になっていないサロゲート)"),
        // 「索引」「ジョブ」「見つからない画像」は、画面の語ではない(インデックス・インデックス作成・フォルダに無い画像。ADR 0038 の語の表)
        // 「走査」は、画面では「スキャン」(ADR 0038。1 語 1 義。Core の文に出る。「再走査」も「再スキャン」になる)
        (new Regex("走査", Opts), "スキャン"),
        // 「全文検索」は、画面では「テキスト検索」(ADR 0038。Core の文に出る)
        (new Regex("全文検索", Opts), ProcessingWords.StageName(JobKind.Vlm)),
        (new Regex("索引", Opts), "インデックス"),
        (new Regex("ジョブ", Opts), "インデックス作成"),
        (new Regex("見つからない画像", Opts), "フォルダに無い画像"),
        // 理由の区切り: 画面は「: 」にそろえる(失敗の文の形「〜できませんでした: 理由」。ADR 0038)。Core の文に、「 — 」で区切ったもの(走査の「到達できなかった場所」の記録の「{ディレクトリ} — {理由}」)がある
        (new Regex(" — ", Opts), ": "),
    ];

    /// <summary>文の終わりの手前(語尾の言い換えの条件): 文末・「。」「、」・改行・「)」・「: 」・「(」の前(空白 1 つを挟んでもよい)。</summary>
    private const string SentenceEnd = @"(?=$|[。、\n)]|: | ?\()";

    /// <summary>
    /// 語尾の言い換え(「です・ます」調。ADR 0038。Core の文は、ログ向けの「だ・である」調なので、画面に出すときに、文の終わりの語尾だけを直す)。
    /// 知っている語尾だけを、文の終わり(<see cref="SentenceEnd"/>)で直す。知らない語尾は、そのまま通す(隠さない・当てずっぽうで直さない)。長い語尾から先に。
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] Endings =
    [
        (new Regex("できなかった" + SentenceEnd, Opts), "できませんでした"),
        (new Regex("らなかった" + SentenceEnd, Opts), "りませんでした"),
        (new Regex("かなかった" + SentenceEnd, Opts), "きませんでした"),
        (new Regex("([えけせてねへめれいきしちにひみり])なかった" + SentenceEnd, Opts), "$1ませんでした"),
        (new Regex("が(?:無い|ない)" + SentenceEnd, Opts), "がありません"),
        (new Regex("ではない" + SentenceEnd, Opts), "ではありません"),
        (new Regex("が空" + SentenceEnd, Opts), "が空です"),
        (new Regex("に失敗した" + SentenceEnd, Opts), "に失敗しました"),
        (new Regex("に失敗している" + SentenceEnd, Opts), "に失敗しています"),
        (new Regex("できない" + SentenceEnd, Opts), "できません"),
        (new Regex("しない" + SentenceEnd, Opts), "しません"),
        (new Regex("([えけせてねへめれ])ない" + SentenceEnd, Opts), "$1ません"),
        (new Regex("([いきしちにひみり])ていない" + SentenceEnd, Opts), "$1ていません"),
        (new Regex("ていない" + SentenceEnd, Opts), "ていません"),
        (new Regex("らない" + SentenceEnd, Opts), "りません"),
        (new Regex("かない" + SentenceEnd, Opts), "きません"),
        (new Regex("が違う" + SentenceEnd, Opts), "が違います"),
        (new Regex("と違う" + SentenceEnd, Opts), "と違います"),
        (new Regex("になった" + SentenceEnd, Opts), "になりました"),
        (new Regex("られた" + SentenceEnd, Opts), "られました"),
        (new Regex("れた" + SentenceEnd, Opts), "れました"),
        (new Regex("ている" + SentenceEnd, Opts), "ています"),
        (new Regex("終わった" + SentenceEnd, Opts), "終わりました"),
        (new Regex("終了した" + SentenceEnd, Opts), "終了しました"),
        (new Regex("は実行中" + SentenceEnd, Opts), "は実行中です"),
    ];

    /// <summary>
    /// 前に半角の空白が無い、半角の「(」(ADR 0038)。前の字が何であっても(日本語・英数字・記号)、この前に半角の空白を 1 つ入れる。
    /// 例外は、空白で区切った塊の頭(行や文の頭・空白の直後)と、開きの括弧とかぎ括弧(「(」「「」「『」「[」)の直後。
    /// </summary>
    private static readonly Regex ParenWithoutSpace = new(@"(?<=[^\s(\u300C\u300E\[])\(", Opts);

    private static readonly Regex NonSpaceRun = new(@"\S+", Opts);

    /// <summary>
    /// 半角の「(」の前に、半角の空白を 1 つ入れる(「検索準備完了 (…)」「llama-server (…)」)。パスは変えない: 空白で区切った塊が「\」か「/」を含むときは、パスとみなして、そのままにする
    /// (ファイル名の「(」を変えると、コピーしたパスが違ってしまう)。
    /// </summary>
    internal static string SpaceBeforeParen(string text) =>
        NonSpaceRun.Replace(text, m => m.Value.Contains('\\', StringComparison.Ordinal) || m.Value.Contains('/', StringComparison.Ordinal) ? m.Value : ParenWithoutSpace.Replace(m.Value, " ("));

    /// <summary>画面に出す形にする(null は空の文字列)。</summary>
    public static string Plain(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        string s = TypePrefix.Replace(message, "");
        foreach ((Regex pattern, string replacement) in Rules) s = pattern.Replace(s, replacement);
        foreach ((Regex pattern, string replacement) in Endings) s = pattern.Replace(s, replacement);
        return SpaceBeforeParen(s);
    }

    /// <summary>例外のメッセージを、画面に出す形にする。</summary>
    public static string Plain(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Plain(e.Message);
    }
}
