# 正解データ(テスト用。約 10MB。すべてコミット済み)

`tools/siglip2-reference/` の Python スクリプトが作った。私物の画像は入っていない(画像はスクリプトが乱数(シード固定)で合成したもの)。
作り方、出典、再現の確認は `tools/siglip2-reference/README.md`。

| ファイル | 内容 | 使うテスト |
|---|---|---|
| `tokenizer-corpus.tsv` | 821 件。入力 → clean_text → 64 個の id(Python の tokenizers の出力) | TokenizerTests |
| `images/` | 合成した入力画像 34 枚(PNG / JPEG / WebP。EXIF の向き、半透明、16bit、パレットなど) | ImagePreprocessingTests |
| `golden/` | 上の画像の、Pillow による 384×384 RGB のリサイズ結果(可逆 PNG) | ImagePreprocessingTests |
| `image-corpus.tsv` | 画像ごとの名前・形式・サイズ・入力のハッシュ・デコード結果のハッシュ・テンソルのハッシュ | ImagePreprocessingTests |
| `image-embeddings.f32`、`text-embeddings.f32`、`text-embeddings.tsv`、`embeddings-meta.txt` | 出荷するモデルの onnxruntime CPU の出力(画像 34 件、テキスト 64 件。1152 個の float32 ずつ) | (今は使わない) |

トークナイザ・前処理の一致の範囲は、このデータが覆うところまで(トークナイザ 821 件、画像 34 枚)。
