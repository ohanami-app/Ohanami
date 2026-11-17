# 0015. CUDA の DLL はアプリのディレクトリから読み、実行時に自己検査する

状態: 採用

## 背景

画像エンコーダ(画像を特徴ベクトルにする部分。0011)は、ONNX Runtime の CUDA 実行プロバイダ(NVIDIA GPU で計算する部品。CUDA EP)を使う。動かすには、ONNX Runtime 本体・CUDA EP のほかに、cuBLAS・cuBLASLt(行列計算のライブラリ)と cuDNN(ニューラルネットワーク用のライブラリ)の DLL(Windows の部品ファイル)が要る。これらは NuGet の ONNX Runtime には含まれない。

使う人から見た問題は、たとえば、開発者の PC では動くアプリが、友人の PC で起動すると「DLL が見つからない」と出て、画像の処理が始まらないことである。

開発機は PATH に CUDA Toolkit(v13.3・v12.5・v11.8)が通っている。同梱し忘れた DLL が PATH から黙って読まれ、別の PC で初めて動かなくなる事故が、構造的に隠れる。利用者の PC に CUDA Toolkit が入っているとは限らない。入っていれば、版の違う DLL を読んでしまう危険もある。

検索パスの扱いには、次の性質がある。

- cuDNN は名前だけで動的に読み込まれる(`LoadLibrary("cudnn64_9.dll")`)。ONNX Runtime の DLL の隣に置いても見つからない。
- cuBLAS は静的 import(プログラムの起動時に、依存する DLL として解決される形)で解決される。
- cuDNN だけが PATH にあって、アプリのディレクトリに無い状態では、ONNX Runtime は例外を出さずに、標準エラー出力(stderr)に `Invalid handle. Cannot load symbol cudnnCreate` を出して、プロセスごと異常終了する。

## 決定

CUDA 系の DLL は、PATH に頼らず、アプリ自身のディレクトリから読む。実行時に、読み込まれた DLL を検査する。

1. **配置**: ONNX Runtime・CUDA EP・cuBLAS・cuBLASLt・cuDNN の DLL を、すべてアプリの実行ファイルと同じディレクトリに平らに置く。`RuntimeIdentifier=win-x64` を指定したプロジェクトでは、ONNX Runtime のネイティブ DLL が実行ファイルの隣に平らにコピーされる。CUDA 系は `build/OhanamiCudaRuntime.targets` が、名前を 1 つずつ指定してコピーする(1 つでも無ければビルドが失敗する)。
2. **検索パス**: `OhanamiCore.OpenAsync` の最初に、`NativeRuntime.Configure` を呼ぶ(`src/Ohanami.Core/Native/NativeRuntime.cs`)。`SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_USER_DIRS | LOAD_LIBRARY_SEARCH_SYSTEM32)`(0xE00)と `AddDllDirectory(アプリのディレクトリ)` で、PATH を DLL の検索から外す。失敗したら `NativeRuntimeException`。プロセス全体の設定で、元に戻せない。
3. **検査のタイミング**: 画像エンコーダを作るときに行う。起動時には行わない(GPU のセッションは作らない。検索だけなら、テキスト側の CPU だけで使える状態になる)。手順は、`Configure` → 事前検査 → セッション作成 → ダミーの入力(0 のテンソル)で 1 回 Run → 自己検査。画像エンコーダを作り直すたびに行う。
4. **事前検査**(セッションを作る前。`NativeSelfCheck.RequiredFiles`): ONNX Runtime 本体・CUDA EP・cuBLAS・cuBLASLt・cuDNN の DLL が、アプリのディレクトリに平らにあるか。足りなければ、足りないファイル名を挙げて `NativeRuntimeException` にする。cuDNN が PATH にだけあると、ONNX Runtime がプロセスごと落ちる(上)ので、ONNX Runtime を呼ぶ前に止める。
5. **自己検査**(`NativeSelfCheck.Evaluate`。純粋な関数): `Process.Modules` から、名前が `cu*`・`nv*`・`onnxruntime*` のモジュールを報告の一覧に載せる。そのうち、次の DLL が、アプリのディレクトリの下から読まれていなければ違反にする。
   - `cu*`(cudart・cuBLAS・cuDNN など)と `onnxruntime*`
   - CUDA Toolkit 系統の `nv*`(`nvrtc`・`nvjitlink`・`nvfatbin`・`nvblas`・`nvtoolsext`・`nvinfer`・`nvonnxparser`・`nvvm`・`nvptxcompiler`)

   これらは、どこから読まれても(System32 でも)、アプリのディレクトリの下でなければ違反である。それ以外の `nv*`(ドライバ・オーバーレイの部品)は、報告の一覧には載せるが、違反にしない。`nvcudart` は対象に入れない(ドライバ側の `nvcudart_hybrid64.dll` と名前が重なる。Toolkit の cudart は `cudart64_*` で、`cu*` として検査される)。Run のあとに、必ずロードされているはずの DLL(`onnxruntime.dll`・`onnxruntime_providers_cuda.dll`・`cublas64_*`・`cudnn64_*`)がロードされているかも確かめる。`cudart` は必須に含めない(ONNX Runtime 1.31.1 の CUDA EP は、cudart をロードしない。ONNX Runtime に静的に入っていると推定しているが、中身は見ていない)。
6. **結果の記録**: ロードされた DLL の名前・版・パスと ONNX Runtime の版を `SelfCheckReport` にして、状態(`IndexState.Engine`)に出し、ログに書き、`runs.runtime_json`(0006)に入れる。検査に失敗したら、エンジンは `Failed` になり、ジョブは 1 枚も書かずに止まる。
7. **常設のチェック**: `tools/run-app-without-cuda-path.ps1` で、PATH から CUDA 系の DLL があるディレクトリを外してアプリを起動する。外す対象はディレクトリ名でなく、中身で判定する(`cudart64_*`・`cublas*`・`cudnn*`・`cufft*`・`curand*`・`cusolver*`・`cusparse*`・`nvrtc*`・`nvJitLink*` のどれかがあるディレクトリ)。ドライバの `nvcuda.dll` は対象にしない。外した PATH は、起動する子プロセスの環境だけに設定する。手動の確認は、このスクリプトで起動する。

## 検討した案

- **プロセスの環境変数の PATH から、CUDA のディレクトリを外す**: 評価のときに使った方法。プロセスの環境を書き換えるので、子プロセスなど他に影響する。検索パスの設定だけで足りることが確かめられたので、採らない。
- **検査の対象を、すべての `nv*` にして、System32 とドライバの置き場だけを免除する**: 採らない。ウィンドウを出したアプリでは、ドライバが画面キャプチャ用の `nvspcap64.dll` を差し込み、System32 の免除でも足りない(NVIDIA App などの版によっては、Program Files の下から来る)。自己検査が落ちて、特徴ベクトルの作成が始まらなくなる。差し込まれる部品は版と環境で変わり、名前も置き場も追えない。そのため、「免除するものを足していく」のでなく、「違反にするものを Toolkit の系統に限る」。
- **起動時に自己検査をする**: 起動時には GPU のセッションを作らない方針(0011)に反する。画像エンコーダを作るときに行う。
- **cudart を必須のロードに含める**: ONNX Runtime の CUDA EP は cudart をロードしないので、含めると、正常な環境でも検査が落ちる。DLL のコピーは続ける。

## 根拠

検索パスの設定の効果は、使い捨ての ONNX Runtime 直接のコードで確かめた。環境は、PATH に CUDA v13.3・v12.5・v11.8 が入った開発機の実際の PATH のままである。

| 確かめたこと | 結果 |
|---|---|
| `SetDefaultDllDirectories` + `AddDllDirectory` のあと、`LoadLibraryW("cudnn64_9.dll")`(PATH に cuDNN がある) | 失敗する(Win32 エラー 126)。PATH は検索から外れた |
| cuBLAS をアプリのディレクトリから外し、PATH(CUDA v13.3)にだけある状態で、設定する | `onnxruntime_providers_cuda.dll` が `cublasLt64_13.dll` を解決できず、例外(Error 126)になる。設定しないと PATH から読まれて成功する。静的 import にも効く |
| DLL がそろっている状態で、ロードされた `cu*`・`cudnn*`・`onnxruntime*` | 16 個すべてがアプリのディレクトリの下(cuDNN は 10 個のうち 7 個。0016) |
| cuDNN だけが PATH にあり、アプリのディレクトリに無い状態で、設定する | 例外でなく、stderr に `Invalid handle. Cannot load symbol cudnnCreate` を出して、プロセスごと異常終了する。事前検査で ONNX Runtime を呼ぶ前に止める根拠 |
| WPF のウィンドウ | 設定のあとも、正常に表示・終了する |

`cufft64_12.dll`(cuFFT)と `nvrtc64_*.dll`(NVRTC。実行時のコンパイル)は、CUDA EP の DLL の中に文字列としては現れるが、このモデルでは、ロードされなかった。

`cudart64_13.dll` は、全部そろった正常な状態でも、ロードされなかった(アプリのディレクトリから外し、PATH にだけある状態でも動いた)。

## 結果

- 開発機の PATH に左右されず、同梱した DLL だけで動くことが、毎回の起動で確かめられる。同梱の漏れや、PATH の版違いの DLL を読む事故は、自己検査で落ちる。
- 事前検査の `RequiredFiles` は、cuDNN の 7 個(0016)を要求する。ビルドが exe の隣に置く cuDNN と同じ集合で、片方だけを変えると、`DistributionLayoutTests` が落ちる(0042)。
- 画像エンコーダを作るたび(一時停止からの再開、VLM(画像と文章を扱える AI モデル。ここでは、画像から、写っている文字・説明文・タグを作る。0018)との切り替え。0027)に、自己検査が走る。作成の約 1.5 秒(0011)に、短い検査が加わる。
- ONNX Runtime の cuBLAS・cuBLASLt(CUDA 13)は、llama.cpp の CUDA 13.4 版の同梱物(0017)と同名の同じ 1 組を、共有する(`build/OhanamiCudaRuntime.targets`)。ONNX Runtime 1.31.1 は CUDA 13.0 以降のランタイムを要し(公式の説明)、13.4 の DLL で動くことを測定で確かめた。別々に持つと、約 523MB が重複する。llama.cpp を CUDA 12 版にする案は、cuBLAS が 12 系と 13 系で別名になり、重複が増えるので採らない。
- 検索パスの設定は、プロセス全体で、元に戻せない。別の場所から DLL を読む処理を足すときは、`AddDllDirectory` で足す。
- ドライバが差し込む部品の名前は、検査の対象にならない。Toolkit 系の名前を持つ部品が将来差し込まれたときは、誤って落ちる可能性が残る。

## 見直す合図

- CUDA・cuDNN・ONNX Runtime の版を替えたとき。`RequiredFiles`、Run のあとに必須のロード、Toolkit 系の `nv*` の一覧を見直す。
- 自己検査が、正常な環境で誤って落ちたとき(ドライバの部品が Toolkit 系の名前に当たったなど)。
- 実行時に別のネイティブ DLL を足す機能を作るとき。
