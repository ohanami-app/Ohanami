# 第三者のソフトウェアの表示(THIRD-PARTY-NOTICES)

Ohanami 本体のコードは MIT License([LICENSE](LICENSE))である。配布の zip には、第三者のソフトウェアも入っている。この文書は、その一覧と、それぞれのライセンスの場所を示す。ライセンスの全文は、`licenses/` フォルダ(zip でも同じ場所)にある。

次の 2 つは、MIT の対象ではない。それぞれの会社の規約で配布している(→「3. MIT の対象ではないもの」)。

- NVIDIA の DLL(CUDA・cuDNN)
- Microsoft の Visual C++ ランタイムの DLL

zip に入っていないもの(モデルのファイル)は、「5. モデル」に書く。

## 1. zip に入っているもの

| 名前 | 版 | ライセンス | 著作権者 | 入手元 | 全文 |
|---|---|---|---|---|---|
| .NET ランタイム(self-contained。`Microsoft.NETCore.App` と `Microsoft.WindowsDesktop.App`。zip の DLL の大半) | 10.0.15 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime 、 https://github.com/dotnet/wpf | [dotnet-runtime-MIT.txt](licenses/dotnet-runtime-MIT.txt)、[dotnet-windowsdesktop-MIT.txt](licenses/dotnet-windowsdesktop-MIT.txt)、[dotnet-runtime-ThirdPartyNotices.txt](licenses/dotnet-runtime-ThirdPartyNotices.txt) |
| ONNX Runtime(`onnxruntime.dll`、`onnxruntime_providers_cuda.dll`、`onnxruntime_providers_shared.dll`) | 1.31.1 | MIT | Microsoft Corporation | https://github.com/microsoft/onnxruntime | [onnxruntime-MIT.txt](licenses/onnxruntime-MIT.txt)、[onnxruntime-ThirdPartyNotices.txt](licenses/onnxruntime-ThirdPartyNotices.txt) |
| SkiaSharp(`SkiaSharp.dll`)と、ネイティブの `libSkiaSharp.dll`(中に Skia = BSD-3-Clause、Google Inc. ほか) | 4.154.0 | MIT(SkiaSharp)。ネイティブの部品は BSD-3-Clause ほか(全文は右の欄) | Microsoft Corporation、Xamarin, Inc.、The Skia Authors ほか | https://github.com/mono/SkiaSharp 、 https://skia.org | [SkiaSharp-MIT.txt](licenses/SkiaSharp-MIT.txt)、[SkiaSharp-ThirdPartyNotices.txt](licenses/SkiaSharp-ThirdPartyNotices.txt) |
| Microsoft.Data.Sqlite(`Microsoft.Data.Sqlite.dll`) | 10.0.14 | MIT | .NET Foundation and Contributors / Microsoft | https://github.com/dotnet/efcore | [Microsoft.Data.Sqlite-MIT.txt](licenses/Microsoft.Data.Sqlite-MIT.txt) |
| SQLitePCLRaw(`SQLitePCLRaw.*.dll` の 3 つ) | 2.1.13 | Apache-2.0 | Eric Sink、SourceGear, LLC(Copyright 2014-2024) | https://github.com/ericsink/SQLitePCL.raw | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| SQLite(`e_sqlite3.dll`。SQLitePCLRaw が運ぶ) | SQLitePCLRaw 2.1.13 の同梱版 | パブリックドメイン | SQLite の作者たち | https://www.sqlite.org/copyright.html | (ライセンス文は無い。「2. 全文のないもの」) |
| System.Numerics.Tensors | 9.0.0 | MIT | .NET Foundation and Contributors | https://github.com/dotnet/runtime | [System.Numerics.Tensors-MIT.txt](licenses/System.Numerics.Tensors-MIT.txt)、[System.Numerics.Tensors-ThirdPartyNotices.txt](licenses/System.Numerics.Tensors-ThirdPartyNotices.txt) |
| llama.cpp(`llama-server.exe`、`llama-server-impl.dll`、`llama.dll`、`ggml*.dll`、`mtmd.dll`、`llama-common.dll`。`ggml-cpu-*.dll` は CPU の種類ごとの 14 個) | b12110 | MIT | Copyright (c) 2023-2026 The ggml authors | https://github.com/ggml-org/llama.cpp | [llama.cpp-MIT.txt](licenses/llama.cpp-MIT.txt) |
| LLVM OpenMP(`libomp.dll`。llama.cpp の同梱物) | llama.cpp b12110 の同梱版 | Apache-2.0 with LLVM Exceptions | LLVM の作者たち | https://openmp.llvm.org | [LLVM-OpenMP.txt](licenses/LLVM-OpenMP.txt) |
| NVIDIA CUDA の実行時の DLL(`cudart64_13.dll`、`cublas64_13.dll`、`cublasLt64_13.dll`。llama.cpp の同梱物) | CUDA 13 | NVIDIA CUDA Toolkit EULA(MIT ではない) | NVIDIA Corporation | https://docs.nvidia.com/cuda/eula/ | [NVIDIA-CUDA-EULA.txt](licenses/NVIDIA-CUDA-EULA.txt)(「3.」) |
| NVIDIA cuDNN の DLL(`cudnn*64_9.dll` の 7 個) | 9.28.0.42(CUDA 13 用) | NVIDIA cuDNN Software License Agreement(MIT ではない) | NVIDIA Corporation | https://developer.nvidia.com/cudnn | [NVIDIA-cuDNN-SLA.txt](licenses/NVIDIA-cuDNN-SLA.txt)(「3.」) |
| Microsoft Visual C++ ランタイム(`msvcp140.dll`、`msvcp140_1.dll`、`msvcp140_atomic_wait.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`) | 14.44.35211.0 | Visual Studio 2022 Community の使用許諾条件の 4 項(MIT ではない) | Microsoft Corporation | https://aka.ms/vs/17/redistribution | [VisualStudio2022-Community-license.txt](licenses/VisualStudio2022-Community-license.txt)(「3.」) |

llama.cpp には、次の部品のソースが入っている(llama.cpp の README と `vendor/` による)。ビルド済みの配布物に実際にどれが入っているかは、確かめていない。ライセンスは、念のため、すべて付ける。

| 名前 | ライセンス | 著作権者 | 全文 |
|---|---|---|---|
| cpp-httplib | MIT | Copyright (c) 2017 yhirose | [cpp-httplib-MIT.txt](licenses/cpp-httplib-MIT.txt) |
| nlohmann/json | MIT | Copyright (c) 2013-2025 Niels Lohmann | [nlohmann-json-MIT.txt](licenses/nlohmann-json-MIT.txt) |
| xxHash | BSD-2-Clause | Copyright (C) 2012-2023 Yann Collet | [xxHash-BSD-2-Clause.txt](licenses/xxHash-BSD-2-Clause.txt) |
| rotate-bits | MIT | Copyright (c) 2021 William Casarin | [rotate-bits-MIT.txt](licenses/rotate-bits-MIT.txt) |
| stb_image、miniaudio、subprocess.h、sha256(Igor Pavlov)、sha1(Steve Reid) | パブリックドメイン | 各作者 | (ライセンス文は無い) |

## 2. 全文のないもの

- **SQLite**: パブリックドメインで、ライセンス文は無い。「SQLite is in the Public Domain」(https://www.sqlite.org/copyright.html)。
- **SQLitePCLRaw**: パッケージに、ライセンスの全文のファイルは入っていない(ライセンスの式 `Apache-2.0` と、著作権の行だけ)。`Apache-2.0.txt` は、Apache License 2.0 の全文である。
- **Microsoft.Data.Sqlite**: パッケージにライセンスのファイルは入っていない(ライセンスの式 `MIT` だけ)。`Microsoft.Data.Sqlite-MIT.txt` は、同じ作者のリポジトリ(dotnet/efcore)の LICENSE.txt である。

## 3. MIT の対象ではないもの

### NVIDIA の DLL

次の DLL は、Ohanami のコードではなく、NVIDIA Corporation が作ったものである。Ohanami の MIT License は、これらの DLL に及ばない。これらは、NVIDIA の規約([NVIDIA-CUDA-EULA.txt](licenses/NVIDIA-CUDA-EULA.txt)、[NVIDIA-cuDNN-SLA.txt](licenses/NVIDIA-cuDNN-SLA.txt))が再配布を認める実行時のファイルで、改変せずに入れてある。

`cudart64_13.dll`、`cublas64_13.dll`、`cublasLt64_13.dll`、`cudnn64_9.dll`、`cudnn_engines_precompiled64_9.dll`、`cudnn_engines_runtime_compiled64_9.dll`、`cudnn_engines_tensor_ir64_9.dll`、`cudnn_graph64_9.dll`、`cudnn_heuristic64_9.dll`、`cudnn_ops64_9.dll`

これらの DLL について、NVIDIA の規約は、次のことを求める。

- Ohanami からだけ使う。単体で取り出して配らない。
- 逆アセンブル・逆コンパイル・リバースエンジニアリングをしない。
- NVIDIA の著作権などの表示を消さない。
- オープンソースのライセンスの対象にしない(Ohanami の MIT License は、Ohanami のコードだけに及ぶ)。
- NVIDIA の推薦を、示さない。
- cuDNN は、NVIDIA の GPU のある PC のためにだけ使う。

条文の全文は、上の 2 つのファイルにある。

### Microsoft の Visual C++ ランタイム

次の DLL は、Microsoft Corporation が作ったもので、Ohanami の MIT License は及ばない。Microsoft の Visual Studio 2022 Community の使用許諾条件の 4 項(配布可能なコード)に従って、改変せずに入れてある。

`msvcp140.dll`、`msvcp140_1.dll`、`msvcp140_atomic_wait.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`

この DLL を使う人は、次の条件に同意したものとする。

- 単体で取り出して、配ったり貸したりしない(Ohanami の一部としてだけ配る)。
- 逆コンパイル・逆アセンブル・リバースエンジニアリングをしない。
- Microsoft とその供給者の、著作権などの表示を消さない。
- Microsoft の商標を、アプリの名前に使わない。Microsoft の推薦を、示さない。
- この DLL は、現状のまま(as is)で提供される。保証は無い。Microsoft とその供給者は、間接損害・特別損害などの責任を負わない(責任の範囲は、使用許諾条件の原文のとおり)。

使用許諾条件の全文は、[VisualStudio2022-Community-license.txt](licenses/VisualStudio2022-Community-license.txt) にある。この DLL を含む Ohanami を、さらに配る人は、受け取る人にも、同じ条件への同意を求める。

## 4. 本体のコードの中の、第三者から写した部分

Ohanami のコードの一部は、次の第三者のソフトウェアのコードを、C# に写したものである。元のコードは、配布物に入っていない(Python の実行環境は要らない)。写した部分のソースの冒頭にも、同じ表示を書いてある。

- **Pillow**(`src/Ohanami.Core/Embedding/ImagePreprocessing.cs` の `PillowResample`。サムネイルの縮小も、同じ部品を使う): Pillow の `Resample.c`(`ImagingResample` の bicubic)の、係数の事前計算・22 ビットの固定小数点・縦横 2 パス・8 ビットへのクリップの流れを、C# に写したもの。Pillow と同じ結果になることを、テストで確かめている。ライセンスは MIT-CMU License。
  > The Python Imaging Library (PIL) is Copyright © 1997-2011 by Secret Labs AB; Copyright © 1995-2011 by Fredrik Lundh and contributors. Pillow is the friendly PIL fork. It is Copyright © 2010 by Jeffrey 'Alex' Clark and contributors.

  許諾文の全文: [Pillow-MIT-CMU.txt](licenses/Pillow-MIT-CMU.txt)。https://github.com/python-pillow/Pillow
- **tokenizers**(Hugging Face。`src/Ohanami.Core/Embedding/Siglip2Tokenizer.cs` の BPE の部分(`BpeWord`)): Rust の tokenizers の `Model::merge_word` と `Word::merge_all` の動作を、C# の優先度つきキューで写したもの。ライセンスは Apache License 2.0(tokenizers の作者たち)。全文: [Apache-2.0.txt](licenses/Apache-2.0.txt)。https://github.com/huggingface/tokenizers

## 5. モデル(zip に入っていない。利用者が「ダウンロード」を押したときに取る)

モデルのファイルは zip に入らない(ADR 0042、0043)。Ohanami は、次のものを取り、使う。

| 名前 | 用途 | ライセンス | 作者 | 入手元 |
|---|---|---|---|---|
| Qwen3-VL-8B-Instruct(GGUF 版とビジョンの mmproj) | 画像の説明文(VLM) | Apache-2.0 | Qwen Team、Alibaba Cloud | https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF (元の重み: https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct) |
| SigLIP 2(`ViT-SO400M-16-SigLIP2-384`)の ONNX 変換版 | 特徴ベクトル検索(画像と文の埋め込み) | Apache-2.0 と CC BY 4.0(下に書く) | Google(SigLIP 2、big_vision) | https://huggingface.co/ohanami-app/siglip2-so400m-patch16-384-onnx (変換元の重み: https://huggingface.co/timm/ViT-SO400M-16-SigLIP2-384 のコミット `fec784dabb3081a5f101fc74eefaf9d1ed08237b`) |

### SigLIP 2 の帰属

- 原作: SigLIP 2 So400m/16(384 px、多言語)。作者は Google(SigLIP 2 / big_vision)。
- 変換: 元の重みを、Ohanami の作者が ONNX に変換した(2 つのグラフ。バッチは 1。出力は L2 正規化)。重みは、再学習も量子化もしていない。変換の手順は、Ohanami のリポジトリの `tools/convert-siglip2` にある。元の作者の承認や推薦を、意味しない。
- ライセンス: Hugging Face のモデルの札(`timm/ViT-SO400M-16-SigLIP2-384`)は Apache License 2.0。作者の SigLIP 2 の説明(big_vision の `README_siglip2.md`)は、ソフトウェアを Apache 2.0、そのほかの素材を CC BY 4.0(https://creativecommons.org/licenses/by/4.0/legalcode)としている。どちらが重みに当たるかは、公開された資料からは決められないので、両方の表示をつける。Apache License 2.0 の全文は [Apache-2.0.txt](licenses/Apache-2.0.txt)。
- 計算した値: Ohanami のソースと実行ファイルは、SigLIP2 で計算した値を含む(参照の語の埋め込みの平均。`HubnessReferenceMean.g.cs`。ADR 0051)。この値にも、上の帰属が及ぶ。
- 出典: Tschannen et al., "SigLIP 2: Multilingual Vision-Language Encoders with Improved Semantic Understanding, Localization, and Dense Features", arXiv:2502.14786(2025)。Zhai et al., "Sigmoid Loss for Language Image Pre-Training", arXiv:2303.15343(2023)。コード: https://github.com/google-research/big_vision

## 6. 試験に使った第三者のソフトウェア

開発では、次のものも、実行して比べた(配布物には入らない)。

- Immich が公開している SigLIP 2 の ONNX: 自前の ONNX の出力と比べた(画像側は、バイト単位で同一だった)。
- Python の tokenizers と Pillow: テストの正解データを作るのに使った(製品とテストは Python に依存しない)。
