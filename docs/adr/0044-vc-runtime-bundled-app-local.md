# 0044. VC++ のランタイムは、アプリのフォルダに同梱する

状態: 採用

## 背景

使う人から見た問題は、たとえば次のとおり。友人が、ゲームしか入れていない新しい Windows の PC に、配布の zip を展開して起動する。すると、画像の処理が始まらず「VCRUNTIME140_1.dll が見つからない」と出る。あるいは、VLM(画像から文字・説明文・タグを作る AI モデル。0018)の llama-server(0017)が起動せず、説明文が作られない。開発機には Visual Studio があり、VC++ のランタイムが入っているので、この失敗に気づけない。

VC++ のランタイムは、Visual C++ でビルドされたプログラムが使う、C++ の標準ライブラリなどの DLL である(`msvcp140.dll`・`vcruntime140.dll` など)。配布物のうち、ONNX Runtime(0011)と llama.cpp(0017)の DLL は、これを使う。

self-contained の .NET(.NET のランタイムを同梱する形。0042)が持つのは、`vcruntime140_cor3.dll`(.NET 自身のための、名前の違う写し)だけで、ONNX Runtime と llama.cpp の分は含まれない。

配布物の DLL は、アプリの exe と同じフォルダに平らに置く(0042)。実行時は、PATH を DLL の検索から外す(0015)。VC++ のランタイムが無い PC では、PATH からも System32 からも拾えない。

## 決定

VC++ のランタイムの DLL を、アプリのフォルダ(exe の隣)に同梱する(app-local の配置)。

- 同梱するのは、次の 5 つ(x64。Visual Studio 2022 の再配布ファイル `Microsoft.VC143.CRT`)。ファイルの版は 14.44.35211.0。変更せずに置く。
  `msvcp140.dll`・`msvcp140_1.dll`・`msvcp140_atomic_wait.dll`・`vcruntime140.dll`・`vcruntime140_1.dll`(合計 818,320 バイト)
- 取得元は、Visual Studio 2022 の再配布のフォルダ(`VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT\`)。Microsoft の署名(Authenticode)が有効なことを確かめる。OS の System32 にある DLL や、debug 版・`onecore` の変種は、再配布の対象でないので使わない。
- 置き場は `runtime/vcruntime/<版>/`(Git に入れない。cuDNN と同じ)。同じフォルダの `SOURCE.txt` に、取得元・版・SHA-256 を書く。
- ビルドが、名前を 1 つずつ指定して exe の隣にコピーする(`build/OhanamiVcRuntime.targets`)。コピー元が 1 つでも無ければ、ビルドが失敗する。開発のビルドも配布も同じ置き方である(0042)。
- 配布物に入ってよいファイルとして、許可の表(`tools/release/allowed-files.tsv`)に SHA-256 つきで 5 行を足す。
- 自己検査(0015)は、これらの DLL がどこから・どの版で読まれたかを報告に載せる(`NativeSelfCheck.ReportedPrefixes`)が、違反にはしない。

## 検討した案

- **利用者に、VC++ の再配布パッケージ(`vc_redist.x64.exe`)を先にインストールしてもらう**: Microsoft が推奨する中央配備である。Windows Update で更新される利点がある。ただし、zip を展開して実行する形(0042)と合わない。インストールには管理者の権限が要る。今回は採らない。
- **OS(System32)にあるものに頼る**: 新しい PC には無いことがある。開発機では動くので、失敗に気づけない。
- **System32 の DLL をコピーして同梱する**: 再配布の対象は、Visual Studio の再配布のフォルダと、Microsoft が配るパッケージに限られる。OS の中身は対象でない。
- **ONNX Runtime と llama.cpp を、ランタイムを静的に取り込む設定で自分でビルドし直す**: 配布元のビルドを使う決定(0017・0020)に反する。ビルドと保守の費用が大きい。
- **ONNX Runtime が要る最小の版より古い再配布ファイルを同梱する**: 動かない。版を決める根拠は下の表。

## 根拠

### 要る DLL と、持ち主

配布物(同梱前の 445 ファイル)のうち、DLL と exe の 287 個(マネージドのものを含む)について、通常の import の表と、遅延ロードの import の表を読んだ。表は、そのファイルが起動時に読み込む DLL の名前の一覧である。

| 持ち主 | 要る DLL |
|---|---|
| `onnxruntime.dll` | `msvcp140`、`msvcp140_1`、`vcruntime140`、`vcruntime140_1` |
| `onnxruntime_providers_cuda.dll` | `msvcp140`、`msvcp140_atomic_wait`、`vcruntime140`、`vcruntime140_1` |
| `onnxruntime_providers_shared.dll` | `vcruntime140` |
| `llama.dll`・`llama-common.dll`・`llama-server-impl.dll`・`ggml-cuda.dll` | `msvcp140`、`vcruntime140`、`vcruntime140_1` |
| `ggml.dll`・`ggml-base.dll`・`ggml-cpu-*.dll`(14 個)・`mtmd.dll` | `msvcp140`、`vcruntime140` |
| `llama-server.exe` | `vcruntime140` |

アプリの exe・.NET のネイティブ(coreclr・clrjit・hostfxr など)・WPF のネイティブは、UCRT の API セット(`api-ms-win-crt-*`)だけを使う。UCRT は Windows 10 以降の OS の部品なので、再配布しない(Microsoft の文書による)。Skia・SQLite・OpenMP・cuBLAS・cuDNN は、OS の DLL だけを使う。VC++ の 5 つ以外に、配布物にも System32 にも無い DLL を要求するファイルは、0 個だった。

### 最小の版

Microsoft の文書は、再配布ファイルは、アプリの部品を作った最新のビルドツール以上の版でなければならない、と定める。

| 対象 | 作った道具 | 読み取り方 |
|---|---|---|
| ONNX Runtime 1.31.1(`onnxruntime*.dll`)・`ggml-cuda.dll` | MSVC 14.44.35207 | PE のヘッダ(リンカの版 14.44、コンパイラの版の記録の最大 35207) |
| llama.cpp の、そのほかの DLL・exe | Clang 20.1.8(`llama-server --version` の出力) | MSVC の STL の版は、読み取れなかった |

そこで、14.44.35207 以上を要るものとし、取得できる 14.44.35211 を同梱する。llama.cpp の分は、5 つの DLL が import する全シンボル(`msvcp140` 257 個・`vcruntime140` 23 個・ほか 4 個)が、14.44.35211 の export にすべてあることを確かめた(欠けるシンボルは 0)。

### 取得元

| ファイル | 版 | SHA-256 | 署名 |
|---|---|---|---|
| `msvcp140.dll` | 14.44.35211.0 | `0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9` | Microsoft Windows(有効) |
| `msvcp140_1.dll` | 14.44.35211.0 | `bfad5aef4c63a669e3c140655cdfdf395b6c979b400a447bd5dcb65ed8826c3d` | Microsoft Windows Software Compatibility Publisher(有効) |
| `msvcp140_atomic_wait.dll` | 14.44.35211.0 | `640b2aefced484d0368eea5bdd06addd0658a3a70a49256e560d6923b404a479` | Microsoft Windows Software Compatibility Publisher(有効) |
| `vcruntime140.dll` | 14.44.35211.0 | `d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066` | Microsoft Windows(有効) |
| `vcruntime140_1.dll` | 14.44.35211.0 | `1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f` | Microsoft Windows(有効) |

`vcruntime140.dll` の SHA-256 は、.NET の self-contained が同梱する `vcruntime140_cor3.dll` と一致する(バイト同一)。

### 文書に書いてあること

法的な判断ではなく、Microsoft の文書に書いてあることを写す。

- 再配布してよいファイル(Redistribute Visual C++ Files、Visual Studio 2022 Redistribution の「Distributable List」): Visual Studio の `VC\redist` のフォルダとその下のファイルは、変更せずに、プログラムと一緒に配布してよい。ただし `debug_nonredist` の下は配布できない。
- app-local の配置(Determine which DLLs to redistribute): 「アプリと同じディレクトリに置く、ローカルの配備に使ってよい」。ただし、更新の責任が配る側に移るので推奨されず、中央配備(再配布パッケージ)が推奨されている。
- 版(同じ文書と C++ binary compatibility): 再配布ファイルは、使ったツールセットと同じか新しい版であること。2015・2017・2019・2022 の再配布ファイルは互いに互換で、新しいものが古いツールセットのアプリに使える。逆は未サポート。
- Visual Studio 2022 Community の使用許諾条件(第 4 節 DISTRIBUTABLE CODE。取得元の製品の条件): 再配布のリストにあるコードのオブジェクトコードを、自分のアプリと一緒に配布してよい。配布する側が守る要件は 2 つ。(1) 配る DLL に、アプリが重要な主要機能を足すこと。(2) 配布する人と利用者に、この使用許諾と同じ程度に、配る DLL を保護する条件に同意させること。禁止は 3 つ。(1) 試験版(Preview・Beta)の部品の配布。(2) Microsoft の商標を、アプリの名前などに使うこと。(3) 配る DLL を、ソースの開示などを求めるライセンスの対象にすること(「Excluded License」)。Community の使用許諾は、個人が自分のアプリを開発・テストするために使うことを認める。
- 一次の資料:
  - https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
  - https://learn.microsoft.com/en-us/cpp/windows/determining-which-dlls-to-redistribute
  - https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution
  - https://learn.microsoft.com/en-us/cpp/porting/binary-compat-2015-2017
  - https://visualstudio.microsoft.com/license-terms/vs2022-ga-community/(本文は、ページに埋め込まれた文書 `https://visualstudio.microsoft.com/wp-content/uploads/2021/11/Visual-Studio-2022-Community-License-EN.docx`。インストールされた製品のパッケージの `license` の項も、同じ URL を指す)

### 探索の実測

アプリのフォルダに 5 つを置くと、実際に読まれるのが、そのフォルダの物になることを確かめた。PATH と System32 の VC++ ランタイム(14.51.36247.0)がある開発機で、実行中のプロセスのモジュール一覧から、ロードされた DLL のパスを読んだ。

| 対象 | 同梱なし | アプリのフォルダに 5 つを置く |
|---|---|---|
| `llama-server.exe`(publish の出力のまま。実物のモデルで起動し、`/health` が 200) | System32(14.51.36247.0) | exe の隣(14.44.35211.0) |
| アプリのプロセスと同じ設定(0015)で ONNX Runtime・CUDA 実行プロバイダを動かすテスト(5 つすべて) | System32(14.51.36247.0) | アプリのディレクトリ(14.44.35211.0)。テストは通った |

アプリの側は、0015 の検索パスの設定(アプリのディレクトリ・`AddDllDirectory`・System32)で、アプリのディレクトリが先頭に来る。llama-server は別プロセスなので 0015 の設定の対象外で、通常の探索(exe の隣が先)で拾う。

## 結果

- VC++ のランタイムが無い PC でも、zip を展開して起動できる(見込み。まっさらな Windows での確認は、まだしていない)。
- zip が約 0.8MiB(818,320 バイト)増える。展開後は 450 ファイル。
- DLL の更新の責任が、配る側に移る。app-local の DLL は Windows Update で更新されない。VC++ のセキュリティ更新があれば、配布物を作り直す。
- 同じ名前の DLL が、アプリの DLL より先に、別のモジュール(IME・常駐ソフトの注入など)から System32 の物としてロード済みだと、ローダーは同名のロード済みの物を使う。そのとき System32 の版が 14.44.35207 より古いと、ONNX Runtime が動かない可能性がある(推測。開発機では起きていない)。自己検査の報告に、読み込まれた DLL のパスと版が残るので、原因を追える。
- 使用許諾条件の 2 つの配布の要件のうち、(2)(利用者への条件)に沿った表示は、配布物の `THIRD-PARTY-NOTICES.md` に書く(0045)。
- ビルドが、`runtime/vcruntime/<版>/` を要る。ない環境では、ビルドが失敗する(CUDA・cuDNN と同じ)。

## 見直す合図

- ONNX Runtime・llama.cpp・.NET を替えたとき。作った道具の版(PE のヘッダ)と、要る DLL の一覧を、同じ手順で読み直す。
- 要る最小の版が、同梱している版(14.44.35211)を超えたとき。新しい再配布ファイルに替える(版のディレクトリと、許可の表の SHA-256 を替える)。
- VC++ ランタイムのセキュリティ更新が出たとき。
- まっさらな Windows で、同梱したのに起動しない報告が出たとき。上の「先に読み込まれる」場合が原因の可能性がある。中央配備への切り替えも検討する。
