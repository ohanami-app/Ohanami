# siglip2-reference: 前処理・トークナイザ・埋め込みの正解データを作る Python のスクリプト

製品とそのテストは Python に依存しない。ここにあるのは、テストの正解データ(`tests/Ohanami.Core.Tests/TestData/`)を**一度だけ**作るためのスクリプトで、
データはコミット済み。スクリプトは出典と再現の手順を残すためにある。

## スクリプト

| スクリプト | 作るもの |
|---|---|
| `gen_image_corpus.py` | 合成した入力画像 34 枚(`TestData/images/`)、Pillow のリサイズ結果(`TestData/golden/`)、`TestData/image-corpus.tsv`(ハッシュなど) |
| `gen_tokenizer_corpus.py` | `TestData/tokenizer-corpus.tsv`(821 件。入力 → clean_text → 64 個の id) |
| `gen_text_tables.py` | `src/Ohanami.Core/Embedding/Generated/PythonTextTables.g.cs`(Python の空白・小文字化の表。C# に表だけを持たせる) |
| `gen_embeddings.py` | `TestData/image-embeddings.f32`、`TestData/text-embeddings.f32` ほか(onnxruntime CPU の出力。出荷するモデルで作る) |
| `compare_decoded.py`、`compare_private.py` | 補助。C# のデコード結果・手元の画像の前処理を Pillow と比べる |

## 実行

リポジトリの外の venv(Python 3.10.11)に、`tokenizers`・`Pillow`・`onnxruntime`(CPU)・`numpy` を入れる
(生成時の版: tokenizers 0.23.3、Pillow 12.3.0、onnxruntime 1.23.2、numpy 2.2.6。torch は使わない)。

```
python -m venv %LOCALAPPDATA%\Ohanamienv-s2
%LOCALAPPDATA%\Ohanamienv-s2\Scripts\python.exe -m pip install tokenizers Pillow onnxruntime numpy
%LOCALAPPDATA%\Ohanamienv-s2\Scripts\python.exe -I -X utf8 tools/siglip2-reference/<スクリプト>.py
```

モデル(`gen_tokenizer_corpus.py`、`gen_embeddings.py` が読む)は `models/ViT-SO400M-16-SigLIP2-384__ohanami/`。
git の worktree など、`models/` にモデルのファイルが無い場所で動かすときは、環境変数 `OHANAMI_MODELS_DIR` で元の `models/` を指す。

再現の確認: 上の 4 つのスクリプトを新しい場所で実行し直して、`TestData/` の 74 ファイルがすべてバイト単位で同じ、
`PythonTextTables.g.cs` が namespace とコメントの 2 か所以外は同じであることを確かめた。

## 参照の手順

スクリプトの「参照の仕様」は、前処理の手順そのもの:

- clean_text: 空白の整理、ASCII 句読点 32 字の削除、小文字化
- トークナイザ: tokenizer.json、padding 64、truncation 64、末尾に `<eos>`
- 画像のデコードと RGB 化: `Image.open` → `load` → RGB 化(リサイズの前。アルファは合成せずに捨てる)
- リサイズ: `preprocess_cfg.json` の resize_mode = squash、interpolation = bicubic(Pillow の `Image.resize`)
- 正規化: float32 で /255、× 2、− 1

SigLIP2 の評価(spikes/S2-siglip2)では、SigLIP2 の ONNX として Immich(`immich-app/ViT-SO400M-16-SigLIP2-384__webli`)が配布しているものを試験的に使い、出力を比べた。
Immich の ONNX は、ライセンスの記載が無いので、製品には採用していない。

製品が使うモデルは、元の重み(timm の Apache-2.0)から `tools/convert-siglip2/` で自前に変換したもの。画像側の ONNX は Immich のものとバイト同一、
テキスト側はグラフと全初期値が同一で、出力はビット単位で一致した(`tools/convert-siglip2/README.md`、`CONVERSION.json`)。
前処理の設定(`visual/preprocess_cfg.json`)は open_clip の preprocess_cfg と同じ内容。

注意: C# の `TextCleaner` は open_clip の canonicalize(`canonicalize_text(basic_clean(x))`)そのものではない(open_clip 2.31 のソースで確かめた)。
(1) ftfy による文字の修復と HTML の unescape を行わない。(2) open_clip は "_" を空白に置き換えてから句読点を削除する("a_b" → "a b")が、
`TextCleaner` は "_" も句読点として削除する("a_b" → "ab")。(3) open_clip は空白の整理を最後に行うが、`TextCleaner` は先に行うので、
句読点の削除のあとに空白が 2 つ並ぶことがある("a - b" → "a  b")。参照(Python の clean_text)に合わせたのは、空白の整理 → ASCII 句読点の削除 + 小文字化の
2 段だけで、コーパス 821 件で一致を確かめてある。モデルの学習時の前処理との違いが検索の質に効くかは確かめていない。
