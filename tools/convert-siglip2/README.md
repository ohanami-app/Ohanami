# convert-siglip2: SigLIP2 を元の重みから ONNX に変換する

製品のビルド用の道具(スパイクではない)。timm の Apache-2.0 の重みから、試験的に使った Immich の ONNX と同じ構成の ONNX を作る。
open_clip と torch の公開 API だけで書いてある。製品とそのテストは Python に依存しない(変換は一度きり)。

## 入力と出力

- 入力(固定): `timm/ViT-SO400M-16-SigLIP2-384 @ fec784dabb3081a5f101fc74eefaf9d1ed08237b`(Apache-2.0)の
  `open_clip_model.safetensors`、`open_clip_config.json`、`tokenizer.json`、`tokenizer_config.json`、`special_tokens_map.json`。
  取得は `python -I spikes/tools/fetch_models.py`(`models/ViT-SO400M-16-SigLIP2-384/` に置き、`models/MANIFEST.tsv` に sha256 を記録)。
  変換は hub から取らず(`HF_HUB_OFFLINE=1`)、この固定ファイルだけを読む。sha256 が MANIFEST と違えば止まる。
- 出力: `models/ViT-SO400M-16-SigLIP2-384__ohanami/`
  `visual/model.onnx`(image float32 [1,3,384,384] → embedding [1,1152])、`visual/preprocess_cfg.json`、
  `textual/model.onnx` + `textual/model.onnx_data`(text int32 [1,64] → embedding [1,1152]。外部データは1ファイル)、`textual/tokenizer*.json` など、
  `config.json`、`CONVERSION.json`(入力の sha256、全パッケージの版、`convert.py` と `requirements.txt` の sha256、producer、opset、日付、実測)。
  出力の各ファイルの sha256 は `models/MANIFEST.tsv` に追記される(repo 列 = `ohanami-local/…`)。
- 仕様: fp32、opset 19、TorchScript 経路(`dynamo=False`)、固定形状(バッチ 1)、入力名 `image` / `text`、出力名 `embedding`、
  `encode_image` / `encode_text` を `normalize=True`(L2 正規化がグラフに入る)。後処理なし。

## 環境(リポジトリの外の venv。Python 3.10.11)

```
python -m venv %LOCALAPPDATA%\Ohanami\venv-convert
%LOCALAPPDATA%\Ohanami\venv-convert\Scripts\python.exe -I -m pip install --upgrade pip        # 26.3 で実行した
%LOCALAPPDATA%\Ohanami\venv-convert\Scripts\python.exe -I -m pip install --no-deps -r tools/convert-siglip2/requirements.txt
```

`requirements.txt` は `pip freeze` の全件(推移的な依存も固定)。最初の導入は
`pip install --uploaded-prior-to 2025-03-14 torch==2.6.0 torchvision==0.21.0 open_clip_torch==2.31.0 timm==1.0.15 onnx==1.17.0 safetensors huggingface_hub`
(Immich の ONNX が公開された 2025-03-13 より前の版に揃える)。torch は PyPI の Windows 版 = CPU 版(`2.6.0+cpu`)。GPU は使わない。
transformers は使わない(トークナイザは固定したファイルのコピーで足りる)。
`requirements.txt` だけで新しい venv に入れて、`pip check` が通り、`pip freeze` が `requirements.txt` と一致することを確かめてある。
注意: venv のパスが長いと、onnx のテストデータのパスが Windows の MAX_PATH を超えて導入に失敗する(`WinError 206`)。短いパス(上の例)に置く。

## 実行

```
%LOCALAPPDATA%\Ohanami\venv-convert\Scripts\python.exe -I tools/convert-siglip2/convert.py
```

出力先が空でなければ上書きせずに止まる。

場所を変えるときは環境変数を使う(付けなければ上のとおり、`<リポジトリ>/models/` を使い、`MANIFEST.tsv` に追記する)。

- `OHANAMI_MODELS_DIR`: 入力の置き場と `MANIFEST.tsv` の場所(`models/` に当たるディレクトリ)。
- `OHANAMI_CONVERT_OUT`: 出力先(空か存在しないディレクトリ)。指定したときは `MANIFEST.tsv` に追記せず、出力先の隣の `MANIFEST-rows.tsv` に、追記するはずだった行を書く。

## 検証(受け入れ条件: すべてで 1 − cos ≤ 1e-5)

- `verify_weights.py`(venv-convert): 自前と Immich の ONNX のファイル・グラフ・初期値の比較、Immich の初期値と safetensors のバイト比較。
- `verify_equivalence.py`(SigLIP2 の評価の参照用 venv `venv-s2`。onnxruntime CPU): Immich と自前の ONNX に同じ入力(合成画像 34 枚、トークナイザのコーパス 821 件)を入れて 1 − cos を比べる。
- C# 側: `S2_MODEL_REPO=ViT-SO400M-16-SigLIP2-384__ohanami` で、spikes/S2-siglip2 の Probe(`gpu-measure`、`stage3-eval` など)とテストが自前の ONNX を使う。
  正解の埋め込み(`reference/data/*-embeddings.f32`)は、自前の ONNX から作り直したものが Immich 用とバイト同一だったので、そのまま使える
  (テストは `S2_GOLDEN_EMBEDDINGS_DIR` を指定しなければ `reference/data` を読む。別の場所の正解を使うときだけ `S2_GOLDEN_EMBEDDINGS_DIR` を指定する)。
  正解を作り直すときは `spikes/S2-siglip2/reference/gen_embeddings.py` を `S2_MODEL_REPO` と `S2_EMBEDDINGS_OUT`(`reference/data` 以外)つきで実行する
  (Immich 用の既存の正解を上書きしないよう、`S2_EMBEDDINGS_OUT` を付けないと止まる)。

結果は `CONVERSION.json` を参照。
