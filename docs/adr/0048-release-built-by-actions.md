# 0048. 配布の zip は、GitHub のランナーで公開の木から作り、Release は GitHub Actions が作る

状態: 採用

## 背景

使う人から見た問題は、たとえば次のとおり。

- 開発者が自分の PC で zip を作り、自分のアカウントで Release を作ると、Release の作者として、その個人のアカウントが表に出る。
- 手元の PC には、公開していないファイル・古いビルドの残り・手元だけの設定がある。zip にそれが入っても、気づけない。zip を作った木が、公開したコミットと同じかも、あとから確かめられない。
- 受け取った人が、zip の中の Ohanami.Core.dll を、公開のソースから自分で作り直して比べようとしても、手元の PC のパスなどでバイト列が変わると、比べられない(0042 が、配布物にパスを残さない検査を持つ)。

技術の制約と代償は、次のとおり。

- zip は約 1.1GB で、CUDA・cuDNN などの NVIDIA の DLL を含む(0042)。これらは Git に入れず、スクリプトで一次の配布元から取る(0046)。VC++ ランタイムの DLL は、Visual Studio の再配布のフォルダから取る(0044)。
- 許可の表(`tools/release/allowed-files.tsv`)の .NET のランタイムの SHA-256 は、特定の SDK で作った値である。

## 決定

**配布の zip は、GitHub のランナー(`windows-2025`)で、公開の木から作る。Release は、そのワークフロー(`github-actions[bot]`)が、下書きとして作る。公開は、別のワークフローが、下書きを公開するだけにする。**

- `.github/workflows/release.yml`: 手で動かす(`workflow_dispatch`)だけ。main からだけ動く。入力は「下書きを作るか」(既定 false。false の実行は、zip を作るところまでの通し稽古)。部品は `actions/checkout` と `actions/setup-dotnet` の 2 つだけで、どちらもコミットの SHA で固定し、横に版を書く。キャッシュは使わない(runtime/ も含む)。
  手順: `Get-Runtime.ps1 -SkipTokenizer`(0046)→ `Publish-Release.ps1`(0042)→ DLL の版が「版+ビルドしたコミット」であることの確認 → Ohanami 自身の 5 ファイルの SHA-256 をログに出す。下書きを作るときは、`tools/release/notes/<版>.md` の後ろに、zip の名前・大きさ・SHA-256・コミットを足した文で、`--target <コミットの SHA>` を付けて作る。同じ版の公開済みの Release か、タグがあれば止まる。
- `.github/workflows/release-publish.yml`: 手で動かすだけ。木から版を読み、下書きの Release を公開する。それだけをする。公開のあと、タグが下書きの対象のコミットを指すことを確かめる。
- 権限: 既定は `contents: read`。Release を作るジョブだけ `contents: write`。秘密は `GITHUB_TOKEN` だけ。
- 版は `src/Directory.Build.props` の `Version` が唯一の置き場(zip の名前・タグ `v<版>`・DLL の版の情報)。版を手で打つ欄は無い。
- SDK は `global.json` で 10.0.404 の 1 つに固定する(`rollForward: disable`)。許可の表の .NET の SHA-256 が、この SDK で作った値だから。SDK を上げるときは、`global.json` と表を同じ変更で直す。
- 行末は `.gitattributes` の `* text=auto eol=lf` で、どの環境の取り出しでも LF にそろえる。ランナーの git は、取り出しで CRLF に変えるため。
- Release のビルドは、PathMap でパスを消し、SourceLink を切る(`build/OhanamiPathMap.props`)。
- ワークフローの書き方の決まり(きっかけ・部品の固定と数・ランナーの画像・キャッシュ無し・権限・秘密)は、`ReleaseWorkflowTests` が、YAML を文字列で読んで見張る。

## 検討した案

| 案 | 採らなかった理由 |
|---|---|
| 手元で作って、その zip を Release に上げる | Release の作者が個人のアカウントになる。手元の PC の状態が入りうる。作った木が公開のコミットと同じか、確かめられない |
| タグの push で起動する | タグを打った時点で、Release の文・版・同じ版の有無を確かめる手順が無い。間違えたタグを消す手間が出る。手で動かして、下書きを人が確かめてから公開するほうが、止めやすい |
| push のたびに動かす | zip は約 1.1GB で、ランナーの時間と容量を使う。公開しない zip を毎回作る意味が無い |
| ワークフローで、テストも回す | この決定の目的は、zip を作る者と木を固定すること。テストは手元で回す(`tests/README.md`)。GPU のテストはランナーでは動かない |
| runtime/ をキャッシュする | キャッシュの中身が、取ったときの SHA-256 と同じかを、別に確かめる必要が出る。毎回、一次の配布元から取って SHA-256 を確かめるほうが単純(取る量は約 2GB) |
| ジョブを分けて、zip を作るジョブは読むだけの権限にする | ジョブの間で約 1.1GB の zip を渡すのに、別の部品(upload-artifact)が要る。使う部品を 2 つに絞る方を取った |
| 公開まで、1 つのワークフローでやる | 下書きの文と zip を、人が確かめる段が無くなる |

## 根拠

手元で次を確かめた(ランナーでは、まだ回していない)。

- 同じコミットを、別のフォルダに `core.autocrlf=true` で clone して `Publish-Release.ps1` を回したところ、473 ファイルすべてが、元の木で作った配布物と同じバイト列だった。SourceLink を切る前は、Ohanami.Core.dll と Ohanami.App.dll だけが違った。pdb に、ビルドした PC の絶対パスと、リポジトリの URL が入り(PathMap では置き換わらない)、pdb のチェックサムが DLL に入るためである。
- `core.autocrlf=true` で clone しても、`.gitattributes` の固定により、すべてのテキストのファイルが LF で取り出される。
- ワークフローの YAML は、YAML の読み取り(使い捨ての環境)で文法を確かめ、各手順の PowerShell は構文を確かめ、`gh` を差し替えて、版の読み取り・main 以外での停止・下書き/公開済み/無しの 3 場合・文の組み立て・公開の流れを動かした。

## 結果

- Release の作者は、個人のアカウントにならない。zip は、公開のコミットから、ランナーで作られる。
- 手元の `Publish-Release.ps1` は、通し稽古と、許可の表の更新(`-ProposeTable`)のためのもの。
- ワークフローは、ランナーで回すまで、実際の動きを確かめられない。ランナーの Visual Studio の再配布のフォルダが、許可の表の VC++ ランタイム(0044)と合わなければ、`Get-Runtime.ps1` が、見つけたフォルダと版を出して止まる。
- Actions のログは 90 日で消える。5 ファイルの SHA-256 は、Release の文にも、zip の SHA-256 と並べて残る。

## 見直す合図

- `windows-2025` の画像から Visual Studio 2022 が無くなるとき、または再配布のフォルダの版が替わるとき(0044 の表の SHA-256 を替える)。
- SDK を上げるとき(`global.json` と許可の表を同じ変更で直す)。
- 署名を入れるとき(0049。署名は、ランナーの外の鍵が要る)。
- Release の数が 100 を超えるとき(ワークフローの Release の一覧は、100 個まで)。
- 配布物が 2GiB に近づくとき(0042)。
