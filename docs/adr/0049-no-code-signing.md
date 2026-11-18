# 0049. 配布の zip には電子署名をせず、警告への対応は README の案内で行う

状態: 採用

## 背景

使う人から見た問題は、たとえば次のとおり。

- GitHub の Release から zip を取って展開し、`Ohanami.App.exe` を開くと、青い「Windows によって PC が保護されました」が出る。「詳細情報」を押すと「実行」が出るので、押せば起動する。署名のない実行ファイルを、ブラウザーで取って開いたときの、Windows の SmartScreen の警告である。
- Windows 11 の「スマート アプリ コントロール」(Smart App Control。以下 SAC)が「オン」の PC では、警告ではなく、起動そのものが止まる(根拠の「SAC で測ったこと」)。「実行」を押す道が無い。

電子署名(Authenticode。実行ファイル・DLL に、発行者の証明書で署名をつけること)があれば、この 2 つは軽くなる。この決定は、署名をつけないことを選び、その理由と代償を書く。

zip(0042)の実行コードのうち、署名が無いのは次のとおり。署名の有無は、`Get-AuthenticodeSignature` で確かめた(llama.cpp は、同梱のビルドの、許可の表にある DLL・exe。SQLitePCLRaw は、NuGet のパッケージの DLL)。

| 出どころ | 署名の無いもの |
|---|---|
| Ohanami 自身 | `Ohanami.App.exe`・`Ohanami.App.dll`・`Ohanami.Core.dll`(3 つ) |
| llama.cpp の公式のビルド | `llama-server.exe`・`llama.dll`・`ggml*.dll`・`libomp.dll` など 23 個 |
| SQLitePCLRaw | `e_sqlite3.dll` と、.NET の DLL 3 つ(4 つ) |

NVIDIA(CUDA・cuDNN)と Microsoft(VC++ ランタイム・.NET)の DLL には、各社の署名がある。

署名には、それぞれ条件と代償がある。下の「検討した案」に、条件を書く。

## 決定

**配布の zip には、電子署名をつけない。SmartScreen の警告と、SAC で使えないことは、README の「起動で警告が出たとき」に書いて、利用者に伝える。**

- README が伝えること: 警告が出たときは「詳細情報」→「実行」。SAC が「オン」のときは、この PC では使えない(個別のアプリだけを許す方法は無い)。確かめ方と、「オフ」にしたときの扱い。
- Release の文(`tools/release/notes/<版>.md`)にも、同じ要点を短く書く。
- 署名の鍵・承認の仕組みを持たないので、Release は `GITHUB_TOKEN` だけで作れる(0048)。

## 検討した案

| 案 | 採らなかった理由 |
|---|---|
| SignPath Foundation(オープンソース向けの無料の署名) | 3 つの条件に当たる。①「The project may not contain any proprietary, non open-source component」(「System Libraries」は同梱してよい、とも書く)。zip に入れる NVIDIA の DLL(CUDA・cuDNN)は、NVIDIA の規約が、オープンソースのライセンスの対象にすることを禁じる(0045)。②「The team must only sign software artifacts built from their own source code.」署名できるのは、自分のソースから作った物だけである。llama.cpp・SQLitePCLRaw は、自分のソースではないので署名できず、SAC には効かない。③「For executable programs that may be downloaded and executed based on our signature, we require a certain verifiable reputation.」(評判の基準の中身は、文書に無い)。証明書の名義は SignPath Foundation で、利用者に見える発行元もその名前になる。条件を満たしても、最初の起動の警告は、当面消えない見込みである(証明書はプロジェクトごとに別の 1 枚で、新しいプロジェクトには、評判のない証明書が来る。根拠の「SignPath の証明書」)。変わるのは、警告に出る発行元が「不明」から「SignPath Foundation」になることと、評判が版をまたいで(その証明書の間は)たまることである |
| Microsoft Store(MSIX) | 警告を、初日から消せる唯一の道ではある(根拠の smartscreen-reputation の表の「Microsoft Store: No warning」)。ただし、MSIX は、AppData に新しく作るファイルを、パッケージ専用の場所に移し、アンインストールで消す(データの置き場と、モデルを手で置く案内が変わる)。提出と審査の手間も増える。0042 の配布の形(zip)を、作り替える大きさの変更になる |
| Azure Artifact Signing(旧 Trusted Signing) | 日本の個人は使えない(個人は米国とカナダだけ)。日本の組織は使えるが、組織の検証が要る。今は、検証に出せる組織・事業の登録が無い。同梱物も含めて、全部を署名でき、SAC にも効く見込みなので、登録ができたら取れる案である。月 $9.99 から |
| OV・EV の証明書(オープンソース向けの個人の証明書を含む)を買って署名する | 署名しても、評判がたまるまで警告は出る(「EV certificates no longer bypass SmartScreen」。評判は「several weeks and hundreds of clean installs」)。証明書の費用と、鍵を守る手間が、続く。個人の証明書の、個々の販売元の条件は、調べていない |
| NVIDIA の DLL を zip から外し、SignPath の条件に合わせる | 配り方(0042)と、利用者への約束の文(0043)が、大きく変わる。それでも、llama.cpp などは署名できない |

## 根拠

出典は、いずれも公開の文書。引用は短くしてある。

- **SmartScreen の評判**(learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation):
  - 「Publisher reputation」と「File hash reputation」の 2 つの信号を見る。ファイルのハッシュと、署名の証明書ごとに評判がたまる。
  - 署名しても、「a newly created binary could still show a SmartScreen warning until its hash or publisher certificate accumulates sufficient evidence of positive reputation」。
  - 署名のないファイルは、「Unsigned files must build reputation anew with every update」。版ごとに、評判が 0 からやり直しになる。
  - 表の「Microsoft Store: No warning — covered by Microsoft's certificate」と、「Valid Certificate (OV/EV): Warning」「No signature: Warning」。
  - 消費者向けに、評判を手で申請する仕組みは無い(「There is no need (or mechanism) to manually submit a file」)。
- **SmartScreen の見る範囲**(Microsoft Defender の担当者の個人ブログ。Microsoft の公式文書ではない。https://textslashplain.com/2024/11/15/best-practices-for-smartscreen-apprep/): 「SmartScreen only checks the reputation of the main file that the user executes」。利用者が実行した主ファイルだけを見る。
- **SAC**:
  - 上の smartscreen-reputation: 「Smart App Control will block execution of unsigned files unless the file has a positive reputation. Smart App Control signature checks apply to all executable files, not just those downloaded from the Internet.」
  - Microsoft の人の個人ブログ(公式文書ではない。https://textslashplain.com/2026/04/28/smart-app-control/): SAC は、OS のローダーが読む、すべてのコード(DLL・スクリプトを含む)の署名を見る。署名の無いコードは、クラウドの評判(Intelligent Security Graph)に問い合わせる。
  - したがって、署名した `Ohanami.App.exe` が、署名のない DLL を読む、または署名のない `llama-server.exe` を起動する場合の、SAC の扱いは、署名の範囲に依存する。署名のない子プロセスの exe の扱いを、明記した Microsoft の文書は、見つけていない。
- **SAC で測ったこと**: Windows Sandbox(Windows 11 Enterprise 26100、GPU なし)で、SAC を「オン」にして、配布の zip(Releases から取った物)を確かめた。
  - 「オン」にする方法: `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` の `VerifiedAndReputablePolicyState` を 1 にし、`CiTool --refresh` で、SAC のポリシー(VerifiedAndReputableDesktop)を有効にした。効いていることは、新しく作った署名のない小さな exe が止まることで確かめた。
  - `Ohanami.App.exe` の起動と、`llama-server.exe --version` の直接の実行は、どちらも止まった(「An Application Control policy has blocked this file」)。Mark of the Web の有無で、結果は変わらない。
  - zip の中の 448 個の実行ファイル(.exe・.dll)を 1 つずつ読み込むと、署名の有効な 418 個は読めた。署名のない 30 個は、すべて止まった: Ohanami 自身 3、llama.cpp 23、SQLitePCLRaw 4。CodeIntegrity のイベントログ(SAC のブロックの記録)の一覧も、この 30 個と一致した。
  - 測っていないこと: Sandbox では Microsoft Defender が動いておらず、クラウドの評判に問い合わせが届いたかは分からない。ただ、配る版ごとにハッシュが新しく、評判は無い。`Ohanami.App.exe` が先に止まるので、アプリの子プロセスとしての `llama-server.exe` の起動は、試せていない。
- **SignPath の証明書**: SignPath で署名している公開プロジェクト 3 つ(Rainmeter・Ditto・fastfetch)の配布物の署名を読んだ。名義は 3 つとも「CN=SignPath Foundation」(GlobalSign 発行、OV)で、証明書の拇印は 3 つとも別だった。有効期間は約 1 年強。規約の「the certificate provided to this project」とも合う。評判は、「individual file hashes and/or the certificates used to sign those files」にたまる(Microsoft の SmartScreen の担当者の個人ブログ。公式文書ではない。https://textslashplain.com/2024/11/15/best-practices-for-smartscreen-apprep/)ので、新しい証明書は評判を作り直す。
- **署名できる範囲**: SignPath の規約(https://signpath.org/terms)の、上の引用。Azure Artifact Signing の quickstart(https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)は、Public Trust の証明書を、日本を含む国の「organizations」に出し、「Individual developers must be located in the United States or Canada」と書く。
- **Microsoft Store**: MSIX の文書(https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)に、AppData に新しく作るファイルは、「a private per-user, per-package location」に置かれ、パッケージの外から見える位置に重ねて見せる、アンインストールで、そのファイルも消える、とある。
- 確かめていないこと: Azure Artifact Signing が、どんな組織(法人格の要否)を受けるか。SignPath が、NVIDIA の DLL を同梱した配布を、実際に断るか(規約の文言から、当たると読んだだけ)。

## 結果

- 版ごとに、SmartScreen の警告が出る。署名がないので、評判は、版ごとに 0 から始まる。
- SAC が「オン」の PC では、使えない(Ohanami 自身が止まる。仮に Ohanami 自身に署名しても、llama.cpp と SQLitePCLRaw の部品が止まる)。利用者が SAC を「オフ」にするかは、本人が決める。README に、その旨を書く。
- 署名の鍵・承認の仕組みが要らないので、Release は `GITHUB_TOKEN` だけで作れる(0048)。ワークフローに、署名の秘密を渡さない。
- zip を受け取った人が、中身を確かめる手段は、署名ではなく、Release の文の SHA-256(0048)である。

## 見直す合図

- 利用者が増え、評判を示せるようになり、SignPath が NVIDIA の同梱を認める見込みが立ったとき。
- Microsoft Store で配ると決めたとき。
- 法人や事業の登録が、別の理由でできたとき(Azure Artifact Signing)。
- NVIDIA の DLL を同梱しない配り方(0042・0043)に変えるとき。
- SmartScreen・SAC の扱いが変わったとき(Microsoft の文書を読み直す)。
