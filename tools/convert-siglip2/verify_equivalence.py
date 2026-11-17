"""数値の等価性の検証(受け入れ条件 4b): Immich の ONNX と自前の ONNX に同じ入力を入れて、出力の 1 - cos を比べる。

onnxruntime(CPU)だけで動く。torch は要らない。SigLIP2 の評価の参照用の venv(%LOCALAPPDATA%\\Ohanami\\venv-s2)で python -I -X utf8 で実行する。
入力:
  画像  spikes/S2-siglip2/reference/data/golden/<name>.png(34 枚。Pillow のリサイズ結果)を、gen_embeddings.py と同じ正規化でテンソルにしたもの
  テキスト  spikes/S2-siglip2/reference/data/tokenizer-corpus.tsv の全行(821 件)の正解の id 列(int32 [1,64])。gen_embeddings.py が使った 64 件を含む
受け入れ条件: すべての入力で 1 - cos <= 1e-5。1 件でも超えたら終了コード 1。
"""
import argparse
import os
import sys
import time

import numpy as np
import onnxruntime as ort
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DATA = os.path.join(REPO, "spikes", "S2-siglip2", "reference", "data")
THRESHOLD = 1e-5


def session(path, threads):
    so = ort.SessionOptions()
    so.intra_op_num_threads = threads
    so.inter_op_num_threads = 1
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def normalize(arr_u8):
    x = arr_u8.astype(np.float32) / np.float32(255.0)
    x *= np.float32(1.0 / 0.5)
    x -= np.float32(0.5 / 0.5)
    return np.ascontiguousarray(x.transpose(2, 0, 1))


def one_minus_cos(a, b):
    a = a.astype(np.float64)
    b = b.astype(np.float64)
    return 1.0 - float(a @ b / (np.linalg.norm(a) * np.linalg.norm(b)))


def report(title, vals):
    vals = np.array(vals)
    print(f"{title}: {len(vals)} 件、1 - cos の最大 {vals.max():.3e}、平均 {vals.mean():.3e}、しきい値 {THRESHOLD:.0e} を超えたもの {int((vals > THRESHOLD).sum())} 件", flush=True)
    return int((vals > THRESHOLD).sum()) == 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default=os.path.join(REPO, "models", "ViT-SO400M-16-SigLIP2-384__webli"), help="基準(Immich)")
    ap.add_argument("--b", default=os.path.join(REPO, "models", "ViT-SO400M-16-SigLIP2-384__ohanami"), help="自前")
    ap.add_argument("--threads", type=int, default=8)
    args = ap.parse_args()
    t0 = time.time()
    ok = True

    # ---- 画像
    names = [line.split("\t")[0] for line in open(os.path.join(DATA, "image-corpus.tsv"), encoding="utf8").read().splitlines()[1:]]
    tensors = [normalize(np.asarray(Image.open(os.path.join(DATA, "golden", f"{n}.png")).convert("RGB")))[None] for n in names]
    outs = {}
    for label, d in (("a", args.a), ("b", args.b)):
        s = session(os.path.join(d, "visual", "model.onnx"), args.threads)
        outs[label] = [s.run(None, {"image": t})[0][0] for t in tensors]
        del s
    diffs = [one_minus_cos(x, y) for x, y in zip(outs["a"], outs["b"])]
    ok &= report("画像(合成 34 枚の前処理済みテンソル)", diffs)
    print("  最大のもの:", names[int(np.argmax(diffs))], f"max|差| = {max(float(np.abs(x - y).max()) for x, y in zip(outs['a'], outs['b'])):.3e}", flush=True)
    print(f"  ({time.time() - t0:.0f}s)", flush=True)

    # ---- テキスト
    rows = [line.split("\t") for line in open(os.path.join(DATA, "tokenizer-corpus.tsv"), encoding="utf8").read().splitlines()[1:]]
    ids = [np.array([[int(v) for v in r[4].split(" ")]], dtype=np.int32) for r in rows]
    assert all(i.shape == (1, 64) for i in ids)
    touts = {}
    for label, d in (("a", args.a), ("b", args.b)):
        s = session(os.path.join(d, "textual", "model.onnx"), args.threads)
        touts[label] = [s.run(None, {"text": i})[0][0] for i in ids]
        del s
    tdiffs = [one_minus_cos(x, y) for x, y in zip(touts["a"], touts["b"])]
    ok &= report("テキスト(トークナイザのコーパスの全行)", tdiffs)
    print("  最大のもの: 行", rows[int(np.argmax(tdiffs))][0], rows[int(np.argmax(tdiffs))][1], f"max|差| = {max(float(np.abs(x - y).max()) for x, y in zip(touts['a'], touts['b'])):.3e}", flush=True)
    # gen_embeddings.py が使った 64 件(Immich 由来の正解ファイル)とも比べる
    meta = [line.split("\t") for line in open(os.path.join(DATA, "text-embeddings.tsv"), encoding="utf8").read().splitlines()[1:]]
    golden = np.fromfile(os.path.join(DATA, "text-embeddings.f32"), dtype="<f4").reshape(-1, 1152)
    by_no = {r[0]: i for i, r in enumerate(rows)}
    g = [one_minus_cos(golden[k], touts["b"][by_no[m[0]]]) for k, m in enumerate(meta)]
    ok &= report("テキスト 64 件(既存の正解ファイル text-embeddings.f32 と自前)", g)
    gi = np.fromfile(os.path.join(DATA, "image-embeddings.f32"), dtype="<f4").reshape(-1, 1152)
    ok &= report("画像 34 件(既存の正解ファイル image-embeddings.f32 と自前)", [one_minus_cos(gi[k], outs["b"][k]) for k in range(len(names))])
    print(f"総合: {'受け入れ条件を満たす' if ok else '★ しきい値を超えたものがある'}  ({time.time() - t0:.0f}s)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
