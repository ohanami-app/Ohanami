using System;
using System.Collections.Generic;

namespace Ohanami.Core;

/// <summary>
/// 走査中の進捗(不変のスナップショット)。<c>Unreachable</c> は到達できなかった根・サブディレクトリの数、
/// <c>SkippedLinks</c> は辿らなかったリンク(シンボリックリンク / ジャンクションのディレクトリ)の数。
/// </summary>
public sealed record ScanProgress(
    long FolderId, int FilesSeen, int Hashed, int Added, int Changed, int Missing, int Unreadable, int Unreachable, int SkippedLinks);

/// <summary>
/// 走査 1 回の結果(指定した全フォルダの合計)。
/// <list type="bullet">
/// <item>Added: 新しく見つかったパス。Changed: 既知のパスで内容(ハッシュ)が変わったもの。Missing: 見えなくなって missing_since を付けたパス。</item>
/// <item>Unreadable: 開けなかったファイル(一時的な共有違反を含む)。TransientlyUnreadable: 今回は登録せず、次の走査に回したファイルの数(共有違反など開けなかったもの、
/// 読んだあとにハンドルの値が列挙の値と違ったもの、更新から 2 秒以内のもの。ADR 0010。監視の走査の 5 秒後の再走査の判断に使う)。Unsupported: 拡張子は画像だが中身が PNG / JPEG / WebP でないファイル。</item>
/// <item>Unreachable: 到達できなかった根・サブディレクトリ(その配下は今回の走査の対象外で、missing_since を付けない)。</item>
/// <item>SkippedLinks: 辿らなかったリンク(シンボリックリンク / ジャンクションのディレクトリ。循環を避けるため辿らない)。エラーではないので folders.last_error には入れない。
/// 利用者がリンクでフォルダをまとめていると、リンク先の画像は索引に入らないので、その理由を UI に出せるようにここに数える(黙ってスキップしない)。</item>
/// </list>
/// </summary>
public sealed record ScanSummary(
    int Added, int Changed, int Missing, int Unreadable, int Unsupported, int Unreachable, int SkippedLinks, TimeSpan Elapsed, int TransientlyUnreadable = 0);

/// <summary>
/// 走査を始めたきっかけ(ログと、走査の完了のイベントに載る)。
/// Startup = 起動時のフルスキャン、Manual = 手動(再走査のボタン・フォルダの追加)、Watcher = フォルダの監視のイベント、Periodic = 15 分ごとの定期、
/// Resume = スリープからの復帰、Missing = 操作の時点で元のファイルが見つからなかった(見つからない印を付けるための走査)、Retry = 一時的に読めなかったファイルの再走査(5 秒後)。
/// </summary>
public enum ScanTrigger { Startup, Manual, Watcher, Periodic, Resume, Missing, Retry }

/// <summary>
/// 走査が終わった(成功した)ことの知らせ(<see cref="Indexing.Indexer.ScanCompleted"/>)。<paramref name="FolderIds"/> は走査したフォルダ
/// (指定が無ければ、そのとき有効だったフォルダすべて)。App は、これで検索結果とフォルダの一覧を取り直す(Core の中で起きた走査も同じに扱う)。
/// </summary>
public sealed record ScanCompletedInfo(ScanTrigger Trigger, IReadOnlyList<long> FolderIds, ScanSummary Summary);
