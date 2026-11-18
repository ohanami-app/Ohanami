# release: 配布物(self-contained の zip)を作る

手で動かす PowerShell の道具。zip を GitHub などに上げることはしない。配布の zip は、GitHub Actions(`.github/workflows/release.yml`)が、ランナーで公開の木から作り、下書きの Release にする(決定は `docs/adr/0048-release-built-by-actions.md`)。手元で `Publish-Release.ps1` を動かすのは、通し稽古と、許可の表の更新(`-ProposeTable`)のため。置き方と表の決定の理由は `docs/adr/0042-release-layout-and-allowed-files.md`。

`tools/release/notes/<版>.md` は、その版の Release の文の下書き(版は `src/Directory.Build.props` の `Version`)。ワークフローが、zip の名前・大きさ・SHA-256・コミットを後ろに足して、下書きの Release の文にする。文に `@` は書かない。

| ファイル | 中身 |
|---|---|
| `Publish-Release.ps1` | publish → 許可の表との突き合わせ → ビルドした PC のパスの検査 → zip → zip の SHA-256 |
| `allowed-files.tsv` | 配布物に入ってよいファイルの表(1 行 1 ファイル。パス・SHA-256・出どころ。出どころが `ohanami`・`repository` の行は SHA-256 が空) |

## 動かし方

```
pwsh tools/release/Publish-Release.ps1 -Version 0.1.0 -RuntimeDirectory <runtime/ に当たるディレクトリ>
```

- 出力先は、リポジトリの外(既定は `%TEMP%\ohanami-release`)。空でなければ `-Force` が要る。
- `runtime/`(CUDA・cuDNN・VC++ ランタイム・llama.cpp。Git に入れない)は、git の worktree には無いので、本体のものを `-RuntimeDirectory`(または環境変数 `OHANAMI_RUNTIME_DIR`)で指す。
- どこかが食い違えば、その時点で止まり、zip は作らない(終了コード 1)。`-NoZip` で、突き合わせまでで終わる。

## 突き合わせの内容

1. 出力のファイル名が、表のファイル名と、過不足なく一致する(表に無いファイルがあれば失敗。pdb・lib・開発機の物はここで止まる)。
2. 表に SHA-256 があるファイル(第三者のバイナリ・.NET のランタイム・NuGet のマネージド)は、出力の SHA-256 が表と同じ。
   `ohanami`(このリポジトリのビルドで作る 5 ファイル)だけ、SHA-256 を書かない(ビルドのたびに変わるため)。
3. 1 ファイルが 2GiB 未満、zip が 2GiB 未満(GitHub Releases の上限)。
4. 出どころが `repository` の行(`LICENSE`・`THIRD-PARTY-NOTICES.md`・`licenses/`。決定は `docs/adr/0045-mit-license-and-third-party-notices.md`)は、リポジトリの同じパスのファイルと、バイト単位で同一。表に SHA-256 は書かない(文書を直すたびに、表を直さなくてよいように)。
5. どのファイルのバイト列にも、ビルドした PC のパスが無い。探す文字列は、`$env:USERPROFILE`・リポジトリのルートの絶対パス(区切りは `\` と `/`)と、`\<ユーザー名>\`・`/<ユーザー名>/`(区切りで挟んだ形。ユーザー名だけでは、短い名前が無関係なバイト列に当たるので探さない)。ASCII(UTF-8)と UTF-16、英字の大文字・小文字は区別しない。自分の DLL・exe には、`build/OhanamiPathMap.props`(Release の PathMap)が、pdb の場所とソースのパスを `/_/` に置き換えて、パスを残さない。第三者のファイルに当たったときは、止めたままにして、そのファイルを調べる。

## 許可の表を更新するとき

.NET の SDK・NuGet のパッケージの版を上げた、同梱するバイナリを替えた、というとき、突き合わせは失敗する(意図どおり)。

```
pwsh tools/release/Publish-Release.ps1 -RuntimeDirectory <...> -ProposeTable -Force
```

`<出力先>\allowed-files.proposed.tsv` に、実際の出力から作った表の案ができる(表そのものは書き換わらない)。
`allowed-files.tsv` との差分を目で確かめ、増えたファイル・変わった SHA-256 が、意図した版の更新であることを確かめてから、手で置き換える。
`DistributionLayoutTests`(`tests/Ohanami.Core.Tests/Native/`)が、表と `build/*.targets`・`NativeSelfCheck.RequiredFiles`・`VlmCatalog` の食い違いを、publish を回さずに見張る。

## 配布の前の確認: メモリ 16GB の PC で動くか

動作環境の「メモリ 16GB 以上」を、RAM の多い開発機で確かめるには、OS が使えるメモリを 16GB に絞って起動する。
**この確認は、まだ実機で行っていない。** 以下のうち、`bcdedit` の働きと注意は Microsoft の資料(<https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set>)で確かめた。「見込み」と書いたものは、まだ動かして確かめていない。

- `bcdedit /set removememory <MB>`: OS が使える総メモリから、その分を取り除く。`truncatememory <アドレス>` は、そのアドレス以上の物理メモリを無視する(メモリが連続していない部分があるぶん、16GB より少し減る見込み)。管理者権限が要り、再起動で有効になる。
- 元に戻すのは `bcdedit /deletevalue removememory`(再起動で戻る)。Microsoft の資料に、設定を誤ると起動しなくなりうる旨と、BitLocker と Secure Boot を無効にするか一時停止する必要があるかもしれない旨がある。
- 64GB の PC を 16GB にするなら `removememory 49152`(見込み)。効いたかは、タスクマネージャーか `systeminfo` の物理メモリで確かめる。
- 先に決めておくこと(見込み): 元に戻す手順と、BCD の退避(`bcdedit /export <ファイル>`)。BitLocker の回復キーを求められうる。ページファイルが自動管理なら、サイズも変わる。VRAM は絞られない。常駐するアプリも、16GB の PC に近づける。
- `msconfig` の「最大メモリ」は、UEFI + Secure Boot の PC で効かない・値が 0 に戻る・青い画面になる、という報告がある(二次資料のみ。公式の説明は見つからなかった)ので使わない。
- 再起動しない代わりに、空きメモリを埋めるツール(Sysinternals の `TestLimit` など)で近づける方法もある(見込み)。OS の動きは、本物の 16GB の PC と同じにはならない。

確かめること: 16GB の状態で、画像の数千枚にインデックスを作り(特徴ベクトル検索・テキスト検索の両方)、検索して、アプリが落ちない・極端に遅くならないこと。結果は、ここに書かず、確かめた人が報告する。
