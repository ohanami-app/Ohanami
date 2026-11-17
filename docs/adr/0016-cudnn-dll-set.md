# 0016. cuDNN の DLL は 7 つを同梱する

状態: 採用

## 背景

画像エンコーダ(画像を特徴ベクトルにする部分。0011)は、ONNX Runtime の CUDA 実行プロバイダ(NVIDIA GPU で計算する部品)で動く。このモデルには、画像から特徴を取り出す畳み込み(Conv)の計算が 1 つだけある。パッチ埋め込みの `/visual/trunk/patch_embed/proj/Conv`(カーネル 16・ストライド 16・重なりなし)で、これが cuDNN を要求する。cuDNN が無いと、セッションは作れて(ONNX Runtime のログで数えた実行時の 1013 ノードが、すべて CUDA EP に載り)のに、最初の Run で例外になる(`cuDNN is unavailable ... LoadLibrary failed for cudnn64_9.dll`)。黙って CPU に落ちることは無い。

cuDNN 9.28.0.42(CUDA 13 版)の DLL は 10 個で、合計 602,715,232 バイト(574.8 MiB)ある。配布物の大きさの主な要因の 1 つである。使う人から見た問題は、ダウンロードが大きくなる一方で、足りない DLL があると、画像の処理が最初の 1 枚で止まることである。実行時に本当に要る DLL を、測って決める必要があった。

測定には落とし穴がある。開発機は PATH に CUDA Toolkit があり、同梱の漏れが PATH 上の DLL に隠れて偽の合格になる(0015)。テキスト側は CPU で動かす(0011)ので、cuDNN とは関係が無い。

## 決定

配布物に入れる cuDNN の DLL は、次の 7 つにする(合計 511,926,032 バイト = 488.2 MiB)。

- `cudnn64_9.dll`
- `cudnn_graph64_9.dll`
- `cudnn_ops64_9.dll`
- `cudnn_heuristic64_9.dll`
- `cudnn_engines_precompiled64_9.dll`
- `cudnn_engines_runtime_compiled64_9.dll`
- `cudnn_engines_tensor_ir64_9.dll`

入れない DLL は `cudnn_adv64_9.dll`・`cudnn_cnn64_9.dll`・`cudnn_ext64_9.dll` の 3 つ(合計 90,789,200 バイト = 86.6 MiB)。

cuDNN は名前だけで動的に読み込まれる(`LoadLibrary("cudnn64_9.dll")`)ので、ONNX Runtime の DLL の隣に置いても見つからない。アプリの実行ファイルの隣に置き、検索パスは 0015 の方法で与える。

## 検討した案

- **cuDNN を同梱しない**: 上のとおり、最初の Run で失敗する。
- **10 個すべてを同梱する**: 動く。ただし、実行時に読まれない 3 つ(86.6 MiB)が無駄になる。
- **7 個から `cudnn_heuristic64_9.dll` も外す(さらに 90.4 MiB 減)**: Run は成功するが、埋め込みがわずかに変わる(1 − cos = 8.1e-10、差の最大 5.4e-6)。外すなら、速度・等価性・別の GPU での確認が要る。確かめたのは 1 台・1 入力・バッチ 1 だけで、速度は測っていない。そのため採らない。
- **パッチ埋め込みの Conv を Reshape + MatMul に書き換え、cuDNN を丸ごと省く(488.2 MiB 減)**: 要る Conv が 1 つだけなので、原理的にはできる。ただし ONNX の中身が変わり、自前変換(0012)の出力の SHA-256 から外れて、等価性の検証をやり直すことになる。試していないので見送る。

## 根拠

測り方: 使い捨てのコンソールで、製品と同じ CUDA EP の設定(0011)の ONNX Runtime 1.31.1 を使い、画像側の `visual/model.onnx` を開いて、乱数の合成入力(1×3×384×384)で 1 回 Run した。そのあと `Process.Modules` で、実際に読み込まれた DLL を列挙した。プロセスの PATH は System32・Windows・dotnet のディレクトリだけにし、CUDA・cuDNN のディレクトリを外した。GPU は RTX 3070。

DLL を 1 つずつ外して、Run(1 回の推論)が通るか(通ったときは結果が変わるか)を調べた。表の「1 − cos」は、2 つのベクトルの違いの指標で、0 なら同じ向き(同じ結果)である。

| DLL | サイズ(MiB) | 読み込まれた時点 | 外したとき |
|---|---|---|---|
| `cudnn_engines_precompiled64_9` | 209.2 | 最初の Run | (外していない) |
| `cudnn_graph64_9` | 120.1 | セッション作成 | (外していない) |
| `cudnn_heuristic64_9` | 90.4 | 最初の Run | Run は成功。埋め込みは変わる(1 − cos = 8.1e-10) |
| `cudnn_adv64_9` | 85.1 | 読まれない | Run は成功 |
| `cudnn_engines_runtime_compiled64_9` | 36.5 | 最初の Run | 失敗(patch_embed の Conv で `CUDNN_STATUS_SUBLIBRARY_LOADING_FAILED`) |
| `cudnn_ops64_9` | 31.6 | セッション作成 | (外していない) |
| `cudnn_cnn64_9` | 1.4 | 読まれない | Run は成功 |
| `cudnn64_9` | 0.26 | セッション作成 | (外していない) |
| `cudnn_engines_tensor_ir64_9` | 0.15 | 最初の Run | 失敗(同じ) |
| `cudnn_ext64_9` | 0.12 | 読まれない | Run は成功 |

10 個のうち読み込まれたのは 7 個だった。外しても Run が成功し、埋め込みも変わらないのは、読み込まれなかった 3 個(adv・cnn・ext)だけである。小さい `runtime_compiled` と `tensor_ir` は外せない。

DLL の出どころは、NVIDIA の配布アーカイブ `https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.28.0.42_cuda13-archive.zip`(zip の SHA-256 は `b1da81fc9b1e6196205a13ca59cba7b3a321d907c03e90a19d9665867d762137`)。

サイズは DLL の実ファイルのバイト数から出した。MiB は 1,048,576 バイト。

## 結果

- 配布する cuDNN は 488.2 MiB で、10 個すべてを入れる場合(574.8 MiB)より 86.6 MiB 少ない。zip の deflate 相当で圧縮すると、`cudnn_engines_precompiled64_9` は 209.2 → 177.0 MiB、`cudnn_graph64_9` は 120.1 → 49.3 MiB になる。
- `cudnn_heuristic64_9` の 90.4 MiB を抱える。
- 開発のビルドも配布も、7 個だけをアプリの実行ファイルの隣にコピーする(`build/OhanamiCudaRuntime.targets`。名前を 1 つずつ列挙し、1 つでも無ければビルドが失敗する)。実行前の事前検査(0015 の `NativeSelfCheck.RequiredFiles`)も、同じ 7 個を要求する。2 つが同じ集合であることは、`DistributionLayoutTests` が見張る(0042)。
- この DLL の集合は、この ONNX・この入力の形・この cuDNN の版で測った結果である。

## 見直す合図

- 配布の大きさを削る必要が出たとき。`cudnn_heuristic64_9` を外す(速度・等価性・別の GPU で確かめる)か、Conv の書き換えを試す(自前変換の出力と等価性の検証をやり直す)。
- cuDNN・ONNX Runtime・モデルの版を替えたとき。読み込まれる DLL の集合は版で変わりうるので、同じ手順で測り直す。
- 入力の形やバッチの大きさを変えたとき、または別のモデルを載せたとき。`adv`・`cnn`・`ext` が要るようになる可能性がある。
