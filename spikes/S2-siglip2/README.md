# S2: SigLIP2 を C# で動かす

SigLIP2 を C# と ONNX Runtime で動かし、Python の参照と一致するか、日本語の検索語でどれだけ使えるかを測った検証。決めたことと根拠は ADR にある(モデルと動かし方は 0011・0012、前処理とトークナイザは 0013、検索語の扱いは 0014、CUDA の DLL は 0015・0016、評価の方法は 0037、全文検索との統合は 0033)。
ここにあるのは、手順 3(トークナイザ)と手順 4(画像の前処理 + 3段の一致)の実装と正解データ、
および手順 1・2・5(ORT の GPU での確認、Dispose、1030枚のループ、known-item)と段階 3 の測定コマンド(`S2Siglip2.Probe`。GPU が空いてから実行する)。

## 手順と段階

手順 1〜5 は、SigLIP2 をプロセスの中で動かせるかの確認。

1. ONNX Runtime の起動、cuDNN なしでも全ノードが CUDA EP に載るか、1 枚を通せるか、ロード時間
2. セッションの Dispose 後に VRAM が戻るか、1030 枚の連続処理で VRAM・メモリが増えないか、1 枚の時間、テキスト側を CPU で動かしたときの RAM
3. トークナイザ(自前の BPE)が、Python の tokenizers とトークン列まで完全に一致するか
4. 画像の前処理が Pillow とどこまで一致するか(前処理後の画素 → モデルの出力 → 最初から最後まで、の 3 段に分けて測る)
5. 日本語の検索語で、探したい 1 枚が何位に出るか(known-item)と、上位 10 件の当たりの割合(precision@10)

段階 0〜3 は、手順 5 で日本語の検索語が英語より弱かったので、補う方法を順に試したもの。

- 段階 0: SigLIP2 に渡す日本語の検索語を、3 種類のテンプレートで文にして、known-item を取り直す(GPU なし。結果と採否は ADR 0014)
- 段階 1: VLM の出力に、検索用の属性語の配列 `tags` を足して、OCR の精度が落ちないか・検索語の語が含まれるようになるかを見る(ADR 0022)
- 段階 2: 段階 1 の形で、1030 枚すべてに OCR・説明文・tags を作る。ループや打ち切りは失敗として記録する(ADR 0023)
- 段階 3: 製品の形(全文検索・SigLIP2・RRF)で、known-item と precision@10 を測り、判定の線に当てる(ADR 0033・0037)。`stage3-eval` が実行する

## 構成

```
S2Siglip2/                  クラスライブラリ(net10.0)
  TextCleaner.cs              参照の clean_text(空白の整理 → ASCII 句読点の削除 → 小文字化)
  Generated/PythonTextTables.g.cs   Python の空白(29文字)と小文字化(1392組)の表。reference/gen_text_tables.py が生成
  Siglip2Tokenizer.cs         tokenizer.json の merges による自前の BPE(Rust tokenizers の動作を写したもの)
  ImagePreprocessing.cs       IImageDecoder / SkiaImageDecoder、PillowResample(ImagingResample の写し)、前処理
  Siglip2Model.cs             ONNX Runtime(CPU の実行プロバイダだけ)で画像側・テキスト側を動かす
S2Siglip2.Probe/            測定用のコンソール(下の「測定コマンド」)
S2Siglip2.Tests/            xUnit(xunit.v3)。モデルは models/ にあるものを、MANIFEST.tsv の sha256 で確かめてから使う(無ければ失敗)
reference/                  Python の参照スクリプトと、その正解データ(venv は置かない)
  gen_tokenizer_corpus.py     トークナイザの正解(821件)→ data/tokenizer-corpus.tsv
  gen_text_tables.py          空白・小文字化の表 → C# のソース
  gen_image_corpus.py         画像を合成(34枚)+ Pillow のリサイズ結果 → data/images, data/golden, data/image-corpus.tsv
  gen_embeddings.py           Python の onnxruntime(CPU)の埋め込み → data/*-embeddings.f32
  compare_decoded.py          C# のデコード結果と Pillow のデコード結果の差(補助。環境変数 S2_DUMP_DECODED)
  compare_private.py          手元の画像(私物。コミットしない)での、C# と Pillow の前処理結果のハッシュ一致(補助。PrivateImagesTests と組)
```

## 実行

```
cd spikes/S2-siglip2
dotnet test --project S2Siglip2.Tests          # 19 件(うち1件は手元の画像用で、環境変数が無ければ skip)。ORT の CPU 実行を含むので約 2 分
dotnet run -c Release --project S2Siglip2.Tests -- -reporter verbose -diagnostics   # 測定値(Output)も表示
```

`global.json` は、.NET 10 SDK で xunit.v3 の `dotnet test` を動かすために Microsoft.Testing.Platform を指定している(このディレクトリ配下だけに効く)。

## 測定コマンド(S2Siglip2.Probe)

どれも結果は `--out`(既定は `spikes/S2-siglip2/runs/<日時>-<種類>/`)に UTF-8 のファイルで書く。標準出力にも同じものを出す。
**GPU を使うのは `gpu-measure --provider cuda` だけ。GPU が空いているのを確かめてから実行すること**(他のプロセスの VRAM の変動が測定に混ざる)。

```
cd spikes/S2-siglip2

# 手順1・2: CUDA EP(cuDNN なし)の確認、Dispose、全画像のループ(1つのコマンド。embeddings.f32 も作る = known-item の準備を兼ねる)
dotnet run -c Release --project S2Siglip2.Probe -- gpu-measure
#   出力: report.txt(判定つき)、ort-verbose.log(ノードの配置)、loop.tsv(1枚ごとの時間・メモリ)、embeddings.f32 + embedding-names.tsv
#   主なオプション: --images <dir>(既定 spikes/images)  --limit N  --out <dir>  --arena kSameAsRequested  --gpu-mem-limit-mib N
#                   --strict-cuda(CPU にノードが落ちたらセッション作成を失敗させる)  --skip-dispose-test  --settle-seconds 5
#                   --cudnn-dir <dir>(cuDNN の DLL もコピーして「cuDNN あり」で測る)  --keep-path  --allow-cudnn-on-path
#   動作確認だけ(GPU を使わない): --provider cpu --limit 30   /   環境の点検だけ(セッションは作らない): --prepare-only

# DLL の点検だけ(セッションを作らない。どの DLL が足りないかを出す)
dotnet run -c Release --project S2Siglip2.Probe -- diagnose-dlls

# 手順5: known-item の順位(gpu-measure の出力ディレクトリと known-items.tsv から)
dotnet run -c Release --project S2Siglip2.Probe -- rank --embeddings <gpu-measure の出力> --descriptions ..\S1-vlm
uns\qwen3vl-8b-q4km
#   出力: known-items-summary.txt(判定)、known-items-result.tsv(クエリごとの順位・スコア・上位5枚のファイル名)
#   --text-provider cuda でテキスト側を GPU に(既定は CPU)

# 手順5 の追加: precision@10(当たりの枚数を、人が目で数える)
#   rank が precision-page.html も作る(日本語のクエリを重複なしにまとめ、クエリごとに上位10件。画像は spikes/images の元のファイルを file:// で参照)。
#   ブラウザで開き、当たりにチェック → ページ下の欄をコピーして spikes/S2-siglip2/precision-judgement.tsv に貼る(雛形は作成済み)。
dotnet run -c Release --project S2Siglip2.Probe -- precision-eval            # 平均 precision@10 と判定(≥ 0.7 使える、< 0.4 使えない、その間は微妙)
#   --allow-partial で判定済みのクエリだけの平均。雛形を作り直すなら precision-template --force(貼った結果が消えるので注意)

# 段階3の準備。判定は「クエリ × 画像 × 当たり/外れ」(qrels.tsv: query_ja / filename / relevant / source)で記録する。
#   作成済みの precision-page.html に「qrels の書き出し」を足す(作り直さない。localStorage の鍵 = 'precision10:' + data-page は中身に依存しない)。
#   元のファイルは precision-page.before-qrels.html に残る。二重に足すと例外。足したあと、ページ下の欄を spikes/S2-siglip2/qrels.tsv に貼る。
dotnet run -c Release --project S2Siglip2.Probe -- qrels-patch-page [--page runs/gpu-2-cudnn/precision-page.html]
#   qrels.tsv を、既知のクエリ・画像と照合し、precision-judgement.tsv の当たりの数と突き合わせる(食い違いがあれば終了コード 1)
dotnet run -c Release --project S2Siglip2.Probe -- qrels-check
#   段階3の評価: 全文検索(S3 の FtsIndex。ocr|description|tags を1文書、BM25)・SigLIP2(素のクエリ)・RRF(k = 60 固定)。known-item と precision@10、判定の規則、
#   判定用ページ(qrels にない画像だけ。ページ下の欄を qrels.tsv の末尾に貼り足す)を出す。--s1 は S1 の run の出力ディレクトリ。
#   全文検索は「tags なし(ocr|description)」と「tags あり(ocr|description|tag1、tag2、…)」の2通りで出し、統合(RRF)もそれぞれ出す(tags のない run では「tags なし」だけ)。
#   判定の規則は「tags あり」の統合に適用。tags の純寄与(tags ありだけで拾えた known-item、tags 由来の誤ヒット)と、「tags あり」が「tags なし」を precision@10 で下回るかも出す。
#   --qrels none は仕組みの確認用(すべて未判定)。--allow-missing-text は run に画像の出力そのものが無いとき(判定は無効と印字される)。
#   --failed-as-empty は、S1 の出力はあるが失敗した(打ち切り・ループ・JSON として読めない)画像を「テキストなし」として評価に含める。
#   指定すると、失敗した画像の数と一覧を summary の先頭に出す(この 2 つは別。--failed-as-empty だけなら、全画像に出力か失敗の記録があるので判定は有効)。
#   どちらも指定しないと、失敗した画像・出力が無い画像のどちらでも例外で止まる。
dotnet run -c Release --project S2Siglip2.Probe -- stage3-eval --s1 ../S1-vlm/runs/<run> [--qrels qrels.tsv] [--out <dir>]

# テキスト側(CPU EP)の RAM
dotnet run -c Release --project S2Siglip2.Probe -- ram-text
```

測定の前提と、判定の規則(線は ADR 0011 の表と同じ):
- CUDA の DLL(cudart64_13 / cublas64_13 / cublasLt64_13)は `runtime/llama.cpp/b12110/`(S1 の zip の中身)から、ORT のネイティブ DLL の隣(`bin/.../runtimes/win-x64/native/`)にコピーする。cuDNN は置かない。
- この PC には CUDA Toolkit(13.3 / 12.5 / 11.8)が PATH にある。測定がそれらに助けられないよう、**このプロセスの PATH から CUDA / cuDNN の DLL があるディレクトリを外す**(外したものは report.txt に書く)。実際にロードされた CUDA の DLL とそのパスも report.txt に出る。
- (a) セッションを作れる (b) Run が通る (c) 全ノードが CUDA EP に載る(VERBOSE ログの配置の行を集計。CPU に落ちても例外は出ないので必ず見る)。cudnn_conv_algo_search=HEURISTIC。
- VRAM は `nvidia-smi -lms 200`(S1 の Monitors.cs と同じ方式)で GPU 全体を、`typeperf` で自プロセスの専用 VRAM を、「作成前 → 推論中 → Dispose 後」の3点で測る。Dispose 後が「作成前 + 600 MiB」以下なら合格。
- ループ: 100 枚目と最後の枚の VRAM(自プロセス)とワーキングセットの差が 100 MiB 以内なら合格。1枚の時間は「ファイル読み込み」「デコード + リサイズ + 正規化」「Run」に分けて出す。足切りは 1枚 360 ms(1万枚が1時間以内)。
- 失敗(セッション作成・Run)のときは、足りない DLL(静的 import と動的ロードされるもの)を、探した場所と Windows のエラーコードつきで出す。cuDNN が必要なら、`runtime/cudnn/9.28.0.42_cuda13/`(取得済みなら)を `--cudnn-dir` に渡して再試行する。

## 参照の Python(リポジトリの外の venv)

```
python -m venv %LOCALAPPDATA%\Ohanami\venv-s2
%LOCALAPPDATA%\Ohanami\venv-s2\Scripts\python.exe -m pip install tokenizers Pillow onnxruntime numpy
```

生成時の版: Python 3.10.11、tokenizers 0.23.3、Pillow 12.3.0、onnxruntime 1.23.2、numpy 2.2.6(torch は使わない)。
スクリプトは `python -I -X utf8 reference/<script>.py` で実行する。正解データは一度生成してコミットしてあり、`dotnet test` に Python は要らない。
画像はすべてスクリプトが乱数(シード固定)で合成したもの(私物の画像は入っていない)。再生成しても同じバイト列になる(ICC の作成日時は 0 に固定)。

## 既知の差(参照の Python と違う入力。比較のコーパスからは外してある)

- U+0130(İ): Python の `lower()` は2文字になる。Ohanami の小文字化は1対1の写像なので表にない。
- U+03A3(Σ): Python は語末で ς、それ以外で σ にする(文脈で変わる)。Ohanami は常に σ。
- 小文字化の表は Python 3.10 / Unicode 13.0 のもの。.NET 10 の `ToLowerInvariant` は Unicode が新しく、U+2C2F・Latin Extended-D・Vithkuqi など 40 文字だけを余計に写す(日本語のクエリには無関係)。
  参照を作った Python が新しい Unicode なら、この 40 文字だけ参照と違う可能性がある(未確認)。
- 16bit PNG の 16→8bit は上位バイト(`v >> 8`。libpng の strip_16、Skia と同じ)を仕様とした。丸め(round(v/257))とは最大 1 違う。
