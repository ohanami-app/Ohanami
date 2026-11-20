# Ohanami.RemoveData

Ohanami が PC に残したデータ(データベース・インデックス・手動タグ・サムネイル・ログ・ダウンロードしたモデル)を消す道具。開発者向けの説明で、利用者向けの使い方は、ルートの `README.md` の「データの置き場と、消し方」にある。

- `Ohanami-RemoveData.cmd` — 二重クリックで動く入口(5 行)。Windows PowerShell 5.1 を、完全なパスで、`-NoProfile -ExecutionPolicy Bypass -File` で起動して、同じ名前の `.ps1` を動かす。終わったら `pause` で窓を閉じずに待つ。
- `Ohanami-RemoveData.ps1` — 本体。Windows PowerShell 5.1 で動く(PowerShell 7 は要らない)。
- 配布の zip の、アプリの exe と同じフォルダに入る(`build/OhanamiPublish.targets`、`tools/release/allowed-files.tsv`)。C# のプロジェクトではない。ソリューションにも、ビルドの対象としては入っていない。

消す物の一覧(名前・型・どの書き手が書くか)と、決めた理由は、ADR 0052 にある。道具の名前と型の表は `.ps1` の先頭近くにあり、アプリが書く物を変えたら、その表と ADR を直す。

## ここで二重クリックしない

この `.cmd` は、既定の場所(`%LOCALAPPDATA%\Ohanami`)を対象にする。リポジトリのこのフォルダで動かすと、開発に使っている PC のデータを消しうる。動かして確かめるときは、次の守りを全部守る。

- 道具のコピーを作業用のフォルダに置いて、コピーを動かす。
- 必ず `-DataRoot <自分で作った一時フォルダ>` を付ける。
- 環境変数 `OHANAMI_DATA_DIR` と `OHANAMI_MODELS_DIR` を外した窓で動かす(付いたままだと、根を決めるときに効く)。
- 迷ったら、`-ListOnly`(一覧を出すだけ。何も消さない)で動かす。

`tests/Ohanami.RemoveData.Tests` は、この守りを入口のコード(`ScriptEntrance.cs`)に書いてあり、守りを破る呼び方は、テストの側で例外にする。

## 文字コードと改行の決まり

| ファイル | 文字コード | 改行 | 理由 |
|---|---|---|---|
| `Ohanami-RemoveData.ps1` | UTF-8、**BOM あり**(先頭の 3 バイトが `EF BB BF`) | LF(`.gitattributes` の `* text=auto eol=lf` が決める。取り出しでも LF) | BOM が無いと、Windows PowerShell 5.1 は、ファイルを ANSI(日本語 Windows では Shift_JIS)として読み、画面の日本語が化ける |
| `Ohanami-RemoveData.cmd` | ASCII だけ(非 ASCII の文字を 1 つも入れない) | **CRLF だけ**(`.gitattributes` の `eol=crlf`) | cmd.exe の標準の改行に合わせる(LF だけの `.cmd` は、行の読み方が環境で揺れる、と知られている。ここでは動かして確かめていない)。非 ASCII を持たないので、コードページに左右されない |

`.cmd` の中身は 5 行に決めてあり、テストが、1 文字の違いも見つける。

エディタや AI の編集ツールは、BOM を落とすことがある。保存したあと、次で確かめる。

```
# .ps1 の先頭 3 バイト(EF BB BF が出るはず)
[System.IO.File]::ReadAllBytes('src/Ohanami.RemoveData/Ohanami-RemoveData.ps1')[0..2] | ForEach-Object { '{0:X2}' -f $_ }
```

落ちたときは、先頭に `EF BB BF` を足して書き直す(中身は変えない)。確かめは、次の 3 か所でも自動で行う: `SourceGuardTests`(テスト)、`DistributionLayoutTests`(テスト)、`Publish-Release.ps1`(zip の中身を、表と突き合わせるとき)。

## テスト

```
dotnet test --project tests/Ohanami.RemoveData.Tests
```

- `RemoveDataRunTests` — `.ps1` を、Windows PowerShell 5.1 で実際に動かす。終了コードと、ファイルが残ったか消えたかで判定する(日本語の画面の文言では判定しない)。リンク(junction)・DB が開かれている・アプリが動いている(名前付き Mutex)・標準入力が閉じている・日本語と空白の入った根・`.cmd` 経由、などを含む。
- `RealWritersTests` — アプリの本物の書き手(`OhanamiDatabase`・`ThumbnailService`・`FileCoreLog`・`ModelDownloader`)で作った根を、`-Mode All -Yes` で消して、根が無くなることを確かめる。道具の名前の表が、アプリの実際の名前とずれていれば、ここで落ちる。
- `SourceGuardTests` — ソースを読んで見張る。BOM・`.cmd` の中身・使わない言葉(再帰の削除・文字列の実行・ほかのプロセスの起動・ファイルの書き込み・設定の変更・ネットワーク。コメントも数える)・削除の呼び出しが「引数が変数 1 つの `File.Delete`・`Directory.Delete`」の形だけであること・BOM が二重でないこと・既定の根の決め方・環境変数名・モデルのフォルダとファイル名が Core の定数と一致すること・プロセスの起動が入口の 1 ファイルだけであること。
- `RemoveDataSafetyTests` — 標準入力に流した行での対話(全角を含む)・ドライブ直下と `\\?\` の根の拒否・アプリが動いている場合の検出(根の大文字小文字・末尾の区切りの違い、DB・`-wal`・`-shm` が開かれている場合の受け皿)・読めないフォルダ(`icacls` で、一時フォルダの中だけに作り、終わったら外す)・空のフォルダだけが残る場合・根が junction の場合。
- `EntranceRulesTests` — 入口の守りそのものを確かめる。引数は、決めた綴りだけ通す(略称・コロンの形・位置引数・重複は、動かさずに例外にする)。
- `tests/Ohanami.Core.Tests` の `WritePlacesGuardTests` — アプリがファイルやフォルダを書く場所の表。アプリが新しい物を書き始めると、落ちて、この道具と ADR を直すよう知らせる。`LlamaServerTests`・`ChildProcessGuardTests` は、llama-server のログと pid ファイルの名前を、書き手の側から見張る。

テストは、Windows PowerShell 5.1(`powershell.exe`)を子プロセスで起動するので、約 1 分かかる。削除を試すのは、テストが `%TEMP%` の下に作った根だけで、`-DataRoot` が無い・テストの一時フォルダの外を指す呼び出しは、`-ListOnly` でなければ入口が拒否する。

使わない言葉の見張りが、道具の改修で当たったら、その言葉を使わずに書き直す(見張りの側の言葉は、道具が、再帰の削除・任意の実行・ネットワーク・設定の変更をしない、という約束の裏付けなので、緩めない)。
