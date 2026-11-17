# 3 本目の経路の試し測りの Python

ADR 0035。3 本目の経路 = VLM の説明文のテキスト埋め込み(cl-nagoya/ruri-v3-310m)。製品とそのテストは Python に依存しない(この Python は、手で一度だけ動かす試しの道具)。
埋め込みを作って(Python)、ベンチの `third-path` が読んで測る(C#。tools/Ohanami.Bench/README.md の「3 本目の試し測り」)。

## 環境(リポジトリの外)

- venv: `%LOCALAPPDATA%\Ohanami\venv-ruri`(`python -m venv`)。入れたのは **tokenizers・onnxruntime(CPU)・numpy** だけ。torch は入れない。
  tokenizers が huggingface-hub などを依存として連れてくるが、このスクリプトは使わない(モデルは、下の手順でリビジョンを固定して取る)。
- モデル(`models/ruri-v3-310m__trial/`。models/ は git の管理外。約 1.26GB): ONNX は onnx-community/ruri-v3-310m-ONNX の **fp32**(量子化の影響を混ぜない)、tokenizer 関係は cl-nagoya/ruri-v3-310m。
  HF のリビジョンと各ファイルの SHA-256(LFS のファイルは HF の記録と一致を確認)は、同じ場所の `MANIFEST.tsv` に記録してある。

  | ファイル | リポジトリ | リビジョン |
  |---|---|---|
  | model.onnx(fp32)・onnx-config.json | onnx-community/ruri-v3-310m-ONNX | fcf9f60dcb54b4271358de97c0688ec2b97325a6 |
  | tokenizer.json・tokenizer_config.json・special_tokens_map.json・config.json・modules.json・1_Pooling/config.json・sentence_bert_config.json・config_sentence_transformers.json・README.md | cl-nagoya/ruri-v3-310m | 18b60fb8c2b9df296fb4212bb7d23ef94e579cd3 |

## 正しさの関門(`ruri_gate.py`)

モデルカードの使用例(5 文。プレフィックスは文字列に入っている)の類似度の行列を、tokenizers + onnxruntime + numpy(平均のプーリング、L2 正規化)で再現して、カードの値と並べる。
手作りのプーリング・プレフィックス・特殊トークンの誤りは例外にならず、3 本目が弱く見えるだけになるので、**実データを埋め込む前に必ず通す**。

    %LOCALAPPDATA%\Ohanami\venv-ruri\Scripts\python.exe -X utf8 tools/third-path-trial/ruri_gate.py

## 埋め込み(`embed_descriptions.py`)

    ...\python.exe -X utf8 tools/third-path-trial/embed_descriptions.py --db <bench.db の写し> --out <リポジトリの外のディレクトリ> [--count-only]

- 入力は、ベンチの DB の写し(読み取り専用で開く)の captions の、画像ごとの最新の caption の説明文と tags_json。クエリは spikes/S2-siglip2 の known-items*.tsv と qrels.tsv の query_ja(手元のデータ)。
- **入れ方は 2 つ(ADR 0035 の決め方。1 回の実行で両方を作る)**:
  - `desc-tags`(**主**): 「検索文書: 」+ 説明文 + 半角空白 + タグを「、」でつないだもの(タグが空なら、空白もタグも付けない)。
  - `desc-only`(診断): 「検索文書: 」+ 説明文。
  クエリは「検索クエリ: 」+ クエリの文字列で、どちらにも同じものが入る。
- 先に数える(`--count-only` はここまで): 説明文の件数・空の件数・タグが空の枚数・入れ方ごとの重複を除いた種類の数・長さ(文字・トークン)。同じ文字列の文書は 1 回だけ埋め込む。空の説明文の画像は母集団から外す。
- 出力: `<out>/desc-tags/` と `<out>/desc-only/` に、それぞれ `vectors.f32`・`index.tsv`・`meta.json`(`variant` と文書の型を持つ。ベンチの `ThirdVectors` が読む)。
  私物の説明文から作ったものなので、リポジトリの外(ベンチの run の下など)に置く。標準出力には、説明文・クエリ・ファイル名を出さない。
- ベンチ: `third-path --embeddings <out>\desc-tags --diagnostic-embeddings <out>\desc-only`(主と診断)。
