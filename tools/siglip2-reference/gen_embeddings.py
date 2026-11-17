"""参照の埋め込み(Python の onnxruntime CPU の出力)を一度だけ生成する。

使い方(リポジトリの外の venv で。モデルは models/ にある):
    <venv>/Scripts/python.exe -I -X utf8 tools/siglip2-reference/gen_embeddings.py

入力:  models/ViT-SO400M-16-SigLIP2-384__ohanami/{visual,textual}/model.onnx(MANIFEST.tsv で sha256 を照合済みのもの)
       tests/Ohanami.Core.Tests/TestData/golden/<name>.png(Pillow のリサイズ結果。gen_image_corpus.py が作った)
       tests/Ohanami.Core.Tests/TestData/tokenizer-corpus.tsv(正解の id 列。gen_tokenizer_corpus.py が作った)
出力:  tests/Ohanami.Core.Tests/TestData/image-embeddings.f32   画像コーパスの順(image-corpus.tsv)に、1152 個の float32(リトルエンディアン)を並べたもの
       tests/Ohanami.Core.Tests/TestData/text-embeddings.tsv    使ったコーパスの行(no 列)の一覧
       tests/Ohanami.Core.Tests/TestData/text-embeddings.f32    その順に、1152 個の float32 を並べたもの
       tests/Ohanami.Core.Tests/TestData/embeddings-meta.txt    onnxruntime の版など

画像: 参照のテンソル = gen_image_corpus.py の normalize の結果。入力名は `image`、形は [1, 3, 384, 384]。
テキスト: 入力名は `text`、int32、形は [1, 64]。id 列は Python の tokenizers が出した正解(tokenizer-corpus.tsv)。
どちらも出力名は `embedding`(ONNX のグラフ内で L2 正規化済み)。onnxruntime は CPU 版(torch は使わない)。
他の測定(VLM の評価の GPU の測定など)を乱さないよう、スレッド数は 4 に絞る。
"""
import json
import os
import sys
import time

import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
# モデルの置き場。環境変数 OHANAMI_MODELS_DIR があればそこ(git の worktree など、models/ が無い場所で動かすとき)、無ければ <リポジトリ>/models
MODELS = os.environ.get("OHANAMI_MODELS_DIR") or os.path.join(REPO, "models")
MODEL_REPO = "ViT-SO400M-16-SigLIP2-384__ohanami"
MODEL_DIR = os.path.join(MODELS, MODEL_REPO)
DATA = os.path.join(REPO, "tests", "Ohanami.Core.Tests", "TestData")
OUT = DATA
THREADS = 4

# テキストの埋め込みに使う、tokenizer-corpus.tsv の行(カテゴリごとに先頭から何件か)
TEXT_PICK = {
    "ja_short": 12, "en": 6, "mixed": 8, "digits": 4, "width": 4, "rare": 6, "ascii_punct": 4, "fullwidth_punct": 3,
    "empty": 2, "added_tokens": 3, "long": 2, "long_boundary": 3, "rand_ja": 4, "rand_mixed": 3,
}


def session(path):
    so = ort.SessionOptions()
    so.intra_op_num_threads = THREADS
    so.inter_op_num_threads = 1
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def normalize(arr_u8):
    x = arr_u8.astype(np.float32) / np.float32(255.0)
    x *= np.float32(1.0 / 0.5)
    x -= np.float32(0.5 / 0.5)
    return np.ascontiguousarray(x.transpose(2, 0, 1))


def main():
    t0 = time.time()
    # ---- 画像
    vis = session(os.path.join(MODEL_DIR, "visual", "model.onnx"))
    inp = vis.get_inputs()[0]
    out = vis.get_outputs()[0]
    assert inp.name == "image" and list(inp.shape) == [1, 3, 384, 384] and inp.type == "tensor(float)", (inp.name, inp.shape, inp.type)
    assert out.name == "embedding" and list(out.shape) == [1, 1152], (out.name, out.shape)
    names = [line.split("\t")[0] for line in open(os.path.join(DATA, "image-corpus.tsv"), encoding="utf8").read().splitlines()[1:]]
    embs = []
    for i, name in enumerate(names):
        arr = np.asarray(Image.open(os.path.join(DATA, "golden", f"{name}.png")).convert("RGB"))
        tensor = normalize(arr)[None]
        t = time.time()
        e = vis.run(None, {"image": tensor})[0][0].astype("<f4")
        embs.append(e)
        print(f"image {i + 1}/{len(names)} {name} {time.time() - t:.1f}s |e|={np.linalg.norm(e):.6f}", flush=True)
    with open(os.path.join(OUT, "image-embeddings.f32"), "wb") as f:
        f.write(np.stack(embs).astype("<f4").tobytes())
    del vis

    # ---- テキスト
    txt = session(os.path.join(MODEL_DIR, "textual", "model.onnx"))
    inp = txt.get_inputs()[0]
    out = txt.get_outputs()[0]
    assert inp.name == "text" and list(inp.shape) == [1, 64] and inp.type == "tensor(int32)", (inp.name, inp.shape, inp.type)
    assert out.name == "embedding" and list(out.shape) == [1, 1152], (out.name, out.shape)
    rows = [line.split("\t") for line in open(os.path.join(DATA, "tokenizer-corpus.tsv"), encoding="utf8").read().splitlines()[1:]]
    picked, seen = [], {}
    for r in rows:
        cat = r[1]
        if seen.get(cat, 0) < TEXT_PICK.get(cat, 0):
            picked.append(r)
            seen[cat] = seen.get(cat, 0) + 1
    tembs = []
    for r in picked:
        ids = np.array([[int(v) for v in r[4].split(" ")]], dtype=np.int32)
        t = time.time()
        e = txt.run(None, {"text": ids})[0][0].astype("<f4")
        tembs.append(e)
        print(f"text no={r[0]} [{r[1]}] {time.time() - t:.2f}s |e|={np.linalg.norm(e):.6f}", flush=True)
    with open(os.path.join(OUT, "text-embeddings.f32"), "wb") as f:
        f.write(np.stack(tembs).astype("<f4").tobytes())
    with open(os.path.join(OUT, "text-embeddings.tsv"), "w", encoding="utf8", newline="\n") as f:
        f.write("no\tcategory\n")
        for r in picked:
            f.write(f"{r[0]}\t{r[1]}\n")
    with open(os.path.join(OUT, "embeddings-meta.txt"), "w", encoding="utf8", newline="\n") as f:
        f.write(json.dumps({
            "onnxruntime": ort.__version__, "numpy": np.__version__, "pillow": Image.__version__ if hasattr(Image, "__version__") else "",
            "providers": ["CPUExecutionProvider"], "intra_op_num_threads": THREADS,
            "image_count": len(names), "text_count": len(picked), "dim": 1152,
        }, ensure_ascii=False, indent=1) + "\n")
    print(f"完了 {time.time() - t0:.0f}s。画像 {len(names)} 件、テキスト {len(picked)} 件")


if __name__ == "__main__":
    sys.exit(main())
