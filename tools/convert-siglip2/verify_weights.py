"""重みの照合(受け入れ条件 4a)。venv-convert で python -I で実行する。

(1) 自前の ONNX と Immich の ONNX を、ファイル(sha256)とグラフ(ノード列・初期値の名前とバイト列)で比べる。
(2) Immich の ONNX の初期値(重み)を、固定した safetensors(timm の元の重み)のテンソルとバイト単位で比べる。
    名前が同じものはそのまま、名前が無い初期値(onnx__MatMul_NNNN など。重みを転置して MatMul にしたもの)は形が合うテンソルを転置して比べる。
結果は標準出力に出す。食い違いがあれば終了コード 1。
"""
import hashlib
import json
import os
import struct
import sys
import time

import numpy as np
import onnx
from onnx import numpy_helper

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MODELS = os.path.join(ROOT, "models")
SAFE = os.path.join(MODELS, "ViT-SO400M-16-SigLIP2-384", "open_clip_model.safetensors")
IMMICH = os.path.join(MODELS, "ViT-SO400M-16-SigLIP2-384__webli")
OURS = os.path.join(MODELS, "ViT-SO400M-16-SigLIP2-384__ohanami")


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(8 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def read_safetensors_header(path):
    with open(path, "rb") as f:
        n = struct.unpack("<Q", f.read(8))[0]
        header = json.loads(f.read(n))
    return header, 8 + n


def tensor_bytes(f, base, info):
    s, e = info["data_offsets"]
    f.seek(base + s)
    return f.read(e - s)


def initializer_bytes(t):
    # numpy_helper.to_array は外部データも読み込み済みなら配列にしてくれる。バイト列は C 順の小さいエンディアンの raw
    a = numpy_helper.to_array(t)
    return a.dtype, a.shape, np.ascontiguousarray(a)


def main():
    ok = True
    t0 = time.time()
    # ---- (1) ファイル単位
    print("== (1) ファイルの比較: 自前 vs Immich")
    v_same = sha256_of(os.path.join(OURS, "visual", "model.onnx")) == sha256_of(os.path.join(IMMICH, "visual", "model.onnx"))
    print("visual/model.onnx の sha256:", "同じ(バイト単位で同一)" if v_same else "違う")
    ok &= v_same

    # ---- グラフ・初期値(テキスト側)
    print("== テキスト側のグラフと初期値(自前 vs Immich)")
    a = onnx.load(os.path.join(OURS, "textual", "model.onnx"), load_external_data=True)
    b = onnx.load(os.path.join(IMMICH, "textual", "model.onnx"), load_external_data=True)
    ia = {t.name: t for t in a.graph.initializer}
    ib = {t.name: t for t in b.graph.initializer}
    print(f"初期値の数: 自前 {len(ia)}、Immich {len(ib)}。名前の集合は{'同じ' if set(ia) == set(ib) else '違う'}")
    ok &= set(ia) == set(ib)
    diff = 0
    for name in sorted(set(ia) & set(ib)):
        if numpy_helper.to_array(ia[name]).tobytes() != numpy_helper.to_array(ib[name]).tobytes():
            diff += 1
            print("  バイト列が違う初期値:", name)
    print(f"共通の初期値のうちバイト列が違うもの: {diff}")
    ok &= diff == 0
    na = [(n.op_type, list(n.input), list(n.output), [x.SerializeToString() for x in n.attribute]) for n in a.graph.node]
    nb = [(n.op_type, list(n.input), list(n.output), [x.SerializeToString() for x in n.attribute]) for n in b.graph.node]
    print(f"ノード数: 自前 {len(na)}、Immich {len(nb)}。ノード列(op・入出力名・属性)は{'同じ' if na == nb else '違う'}")
    if na != nb:
        ok = False
        for i, (x, y) in enumerate(zip(na, nb)):
            if x != y:
                print("  最初に違うノード", i, x[:3], y[:3])
                break
    print(f"opset: 自前 {[(o.domain, o.version) for o in a.opset_import]}、Immich {[(o.domain, o.version) for o in b.opset_import]}; "
          f"producer 自前 {a.producer_name} {a.producer_version}、Immich {b.producer_name} {b.producer_version}; ir_version {a.ir_version} / {b.ir_version}")

    # ---- (2) Immich の初期値 vs safetensors
    print("== (2) Immich の ONNX の初期値 vs 元の重み(safetensors)")
    header, base = read_safetensors_header(SAFE)
    st = {k: v for k, v in header.items() if k != "__metadata__"}
    print(f"safetensors: テンソル {len(st)} 個、dtype {sorted({v['dtype'] for v in st.values()})}")
    # 画像側: visual/model.onnx は同一ファイルなので、自前と同じ。Immich 側から読む
    vb = onnx.load(os.path.join(IMMICH, "visual", "model.onnx"), load_external_data=True)
    inits = {}
    for label, model in (("textual", b), ("visual", vb)):
        for t in model.graph.initializer:
            inits[(label, t.name)] = t
    by_shape = {}
    for k, v in st.items():
        by_shape.setdefault(tuple(v["shape"]), []).append(k)
    matched_name = matched_t = matched_other = 0
    unmatched = []
    used = set()
    with open(SAFE, "rb") as f:
        cache = {}
        def get(k):
            if k not in cache:
                info = st[k]
                cache[k] = np.frombuffer(tensor_bytes(f, base, info), dtype=np.float32).reshape(info["shape"]) if info["dtype"] == "F32" else None
                if len(cache) > 8:
                    cache.pop(next(iter(cache)))
            return cache[k]
        for (label, name), t in inits.items():
            arr = numpy_helper.to_array(t)
            if arr.dtype != np.float32:
                matched_other += 1  # 形状の定数(int64)など。重みではない
                continue
            if name in st and list(arr.shape) == st[name]["shape"]:
                if arr.tobytes() == get(name).tobytes():
                    matched_name += 1
                    used.add(name)
                    continue
                unmatched.append((label, name, "同名だがバイト列が違う"))
                continue
            # 名前が無い: 同じ形(転置も含む)のテンソルを探す
            found = None
            for cand_shape in (tuple(arr.shape), tuple(arr.shape[::-1])):
                for k in by_shape.get(cand_shape, []):
                    cm = get(k)
                    if cm is None:
                        continue
                    if cand_shape == tuple(arr.shape) and arr.tobytes() == cm.tobytes():
                        found = (k, "同じ形")
                    elif arr.ndim == 2 and cand_shape == tuple(arr.shape[::-1]) and arr.tobytes() == np.ascontiguousarray(cm.T).tobytes():
                        found = (k, "転置")
                    if found:
                        break
                if found:
                    break
            if found:
                matched_t += 1
                used.add(found[0])
            else:
                unmatched.append((label, name, f"形 {list(arr.shape)} に一致するテンソルが無い"))
    float_total = sum(1 for (l, n), t in inits.items() if numpy_helper.to_array(t).dtype == np.float32)
    print(f"Immich の float32 の初期値 {float_total} 個のうち: 名前が同じで一致 {matched_name}、名前なし(転置・同形)で一致 {matched_t}、不一致 {len(unmatched)}")
    for u in unmatched[:20]:
        print("  不一致:", u)
    ok &= len(unmatched) == 0
    unused = sorted(k for k in st if k not in used)
    print(f"safetensors のテンソルのうち、Immich の ONNX のどの初期値とも対応しなかったもの: {len(unused)}")
    for k in unused[:20]:
        print("  ", k, st[k]["shape"])
    print(f"経過 {time.time() - t0:.0f} s。総合: {'一致' if ok else '★ 不一致あり'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
