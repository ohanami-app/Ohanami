# 0042. 配布物は、アプリのフォルダに平らに置き、入ってよいファイルを名前と SHA-256 の表で見張る

状態: 採用

## 背景

利用者は、zip を 1 個ダウンロードして、展開して、実行する。使う人から見た問題は、たとえば次のとおり。

- 友人に zip を渡したら、起動した VLM が、友人の PC の別のフォルダ(別の版の llama.cpp)の DLL を読んで、結果が変わった、あるいは動かなかった。
- 配布物に、デバッグ用のファイル(pdb。作った PC のフォルダ名が入っている)が混ざっていて、ユーザー名が見える。
- DLL が 1 つ足りないだけで、起動が止まる。開発機では、PATH にある CUDA Toolkit が足りない DLL を黙って補うので、気づけない(0015)。

配布は、self-contained(.NET のランタイムを同梱して、利用者に .NET の導入を頼まない形)の zip を 1 個、GitHub Releases に置く。GitHub Releases は、1 ファイル 2GiB 未満である。
開発のビルド(Debug)と配布で、ファイルの置き方が違うと、開発機で動いたことが、配布物で動く保証にならない。

## 決定

**開発のビルドも配布も、必要なネイティブの DLL を、アプリの exe と同じフォルダに平らに置く。**

- ORT(ONNX Runtime)・CUDA・cuDNN(0016 の 7 個)・llama-server の一式(`llama-server.exe` と、その DLL。`ggml-cpu-*.dll` の 14 個は全部)を、ビルドが名前で 1 つずつ指定してコピーする(`build/OhanamiCudaRuntime.targets`・`build/OhanamiLlamaServer.targets`)。1 つでも無ければビルドが失敗する。cuBLAS・cudart は、ORT と llama-server で同じ 1 組を使う。
- `ggml-cpu-*.dll` を全部入れるのは、どれが読まれるかが、利用者の CPU で決まるため(llama.cpp が起動時に CPU の命令を調べて選ぶ)。実行する PC の分だけには絞れない。
- App は `llama-server.exe` を、アプリのフォルダから探す(`AppPaths`)。親のフォルダをたどって探さない。実行時に SHA-256 で確かめるのは `llama-server.exe` だけで、隣の DLL は確かめないので、親のどこかにある別の一式を拾わないようにする。環境変数 `OHANAMI_RUNTIME_DIR` を付けたときだけ、別の場所(`llama.cpp\b12110\llama-server.exe`)を使う(手元の確認・テスト用)。
- `SelfContained` は csproj に書かない(`false` のまま)。`true` と書くと、self-contained でないテストのプロジェクトが App を参照できず、ビルドが `NETSDK1151` で止まる。配布を作るときだけ、`dotnet publish -c Release -r win-x64 --self-contained true` と引数で指定する。

**配布物に入ってよいファイルは、名前を 1 つずつ書いた許可の表(`tools/release/allowed-files.tsv`)で決める。**

- 表の 1 行は、パス・SHA-256・出どころ。Ohanami 自身のビルドで作る 5 ファイル(`Ohanami.App.exe`・`.dll`・`.deps.json`・`.runtimeconfig.json`、`Ohanami.Core.dll`)だけ SHA-256 を書かない(ビルドのたびに変わる)。残りは、.NET のランタイム・NuGet のパッケージ・CUDA・cuDNN・llama.cpp を含め、すべて SHA-256 を書く。
- 手で動かす `tools/release/Publish-Release.ps1` が、publish → 出力のファイル名と表の突き合わせ(過不足があれば失敗)→ SHA-256 の突き合わせ → どのファイルにも、ビルドした PC のパス(ユーザーのフォルダ・リポジトリのルート・ユーザー名)が残っていないことの検査 → zip を作る → zip の SHA-256 を出す、の順に進む。1 つでも食い違えば止まり、zip は作らない。zip を GitHub に上げる操作は、この道具にはない。
- 同梱する第三者のバイナリの SHA-256 は、実行時には確かめず、配布物を作るときに表と突き合わせる。`llama-server.exe` だけは、実行時(0020)にも確かめる。表の `llama-server.exe` の SHA-256 は、実行時に確かめる定数(`VlmCatalog.LlamaServerExeSha256`)と同じである。
- 配布に要らないものは、publish の出力から外す(`build/OhanamiPublish.targets`): pdb(Ohanami 自身のものと `libSkiaSharp.pdb`)、`onnxruntime*.lib`、`onnxruntime_providers_tensorrt.dll`。
- ビルドが置くもの・実行時の検査・表の三者が、同じ集合を指すことは、テスト(`DistributionLayoutTests`)が、publish を回さずに見張る。

## 検討した案

| 案 | 採らなかった理由 |
|---|---|
| `SelfContained` を csproj に `true` と書く | 上のとおり、テストのプロジェクトのビルドが `NETSDK1151` で落ちる(測って確かめた) |
| llama-server を、`runtime/llama.cpp/<ビルド>/` のように別のフォルダに置き、親をたどって探す | 開発と配布で置き方が変わる。親のどこかの、隣の DLL を確かめていない一式を拾いうる |
| `ggml-cpu-*.dll` を、開発機の CPU に合う 1〜2 個に絞る | 利用者の CPU は、開発機と違う。読まれる 1 個は、実行する PC で決まる |
| ファイル名ではなく、拡張子や名前の型で許可する(`*.dll` など) | pdb・lib・開発機の物が紛れ込む穴になる。名前を 1 つずつ書いて、増減を、人が目で見て決める |
| single-file(1 個の exe にまとめる) | ネイティブの DLL は別のまま。ファイルの場所に頼る API が動かない。数百 MiB の DLL を抱える Ohanami には、利点が小さい |
| トリミング(使わない部品を削って小さくする) | WPF のアプリでは、`PublishTrimmed=true` で publish すると、.NET SDK 10.0.404 が `NETSDK1168`(WPF のトリミングは非対応)で止まる(測って確かめた) |
| ReadyToRun(起動を速くするために、アプリの部品を事前に機械語へ変える) | 下の根拠。メイン窓が出るまでの時間が変わらなかった(測った) |

## 根拠

開発機で、publish(Release・win-x64・self-contained。pdb・lib・TensorRT を外した形)を測った。

| 項目 | 値 |
|---|---|
| ファイル数 | 445(直下 289 + 言語別のリソースのフォルダ 13 個 × 12) |
| 展開後の大きさ | 1,568.1MiB |
| zip(圧縮は最適)の大きさ | 1,142,601,560 バイト(1,089.7MiB)。2GiB の上限まで、約 0.9GiB の余裕 |

内訳(MiB): .NET のランタイム 140.2(398 ファイル)/ CUDA の cuBLAS・cuBLASLt・cudart 522.8 / cuDNN 488.2 / ORT 204.6 / llama.cpp 196.3(23 ファイル)/ Skia 12.8 / SQLite 2.0 / Ohanami 1.0。
ReadyToRun にしても、ファイル数は 445 のまま、約 5MiB 増えるだけだった。フレームワークの部品は、元から事前に機械語へ変換してあり、効くのは Ohanami と NuGet のマネージドの部品だけである。
起動の時間は、publish の出力の `Ohanami.App.exe` を起動して、メイン窓が出るまで(プロセスを始めてから、最初の窓ができるまで)を測った。データは空の一時フォルダ、モデルは空のフォルダを指す(モデルが無いので、窓が出たあとに、準備できていない表示になる。そこまでは数えない)。ReadyToRun の有無を交互に 12 回ずつ(最初の 1 回は数えない)測ると、次のとおり。

| 項目 | ReadyToRun なし | ReadyToRun あり |
|---|---|---|
| メイン窓が出るまで(中央値) | 575ms | 576ms |
| 範囲 | 544〜749ms | 558〜638ms |

差は、ばらつきの範囲に収まる。なしの組、ありの組と順に測ると、2 回の測定で差の向きが逆に出た(1 回目はありが速く、2 回目はありが遅かった)。PC のそのときの状態の影響が大きいので、交互に測った。

置き方を確かめた。

- 実物の llama-server とモデルで、`RealLlamaServerTests` の 5 件が、開発のビルドの出力のフォルダと、publish の出力のフォルダの、どちらでも通った。
- PATH から CUDA を外した(`System32` と `Windows` だけにした)状態で、publish の出力の `llama-server.exe --list-devices` が、GPU を見つけた。同梱の DLL だけで足りる。
- GPU の画像エンコーダのテスト(`GpuEncoderTests`)が、cuDNN の 7 個だけを置いた状態で通った。
- 許可の表を、わざと壊して(1 行を消す・SHA-256 を替える・無いファイルの行を足す)突き合わせると、3 件とも失敗し、終了コード 1 で止まった。

## 結果

- 開発のビルドで動いた置き方が、そのまま配布物になる。VC++ のランタイムの DLL も、同じ置き方で同梱する(0044)。
- 配布物に、pdb・lib・開発機の物が混ざると、zip を作る前に止まる。
- .NET の SDK・NuGet のパッケージ・同梱するバイナリを替えると、突き合わせが失敗する(意図どおり)。`-ProposeTable` で、実際の出力から表の案を作り、差分を目で確かめて、表を手で置き換える。
- 展開後の約 1.5GiB のうち、約 2/3 は NVIDIA の DLL(cuBLAS・cuDNN)である。
- ReadyToRun は使わない(窓が出るまでの時間が変わらず、約 5MiB 増える)。
- この配布物で、窓が出ること(モデルが無い状態)までは確かめた。モデルがある状態での通しの確認(検索・VLM の処理)は、配布の形では行っていない。

## 見直す合図

- アプリの部品が増えて、窓が出るまでの時間が目に見えて延びたとき(ReadyToRun を、同じ測り方で測り直す)。
- llama.cpp・cuDNN・ORT・.NET の版を替えるとき(表の SHA-256 とファイル名が変わる)。
- 配布物が 2GiB に近づくとき(分割するか、同梱物を絞る)。
- 配布の形を変えるとき(インストーラー・MSIX)。
