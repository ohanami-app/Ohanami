"""SigLIP2 (SO400M / patch16 / 384 / WebLI) を、元の重み(timm、Apache-2.0)から ONNX に変換する。

使い方(リポジトリの外の venv で。python -I で実行する):
    %LOCALAPPDATA%\\Ohanami\\venv-convert\\Scripts\\python.exe -I tools/convert-siglip2/convert.py

入力は models/ViT-SO400M-16-SigLIP2-384/ の固定したファイル(spikes/tools/fetch_models.py が取得し、MANIFEST.tsv に sha256 を記録したもの)だけ。
hub からは取らない(HF_HUB_OFFLINE=1)。入力の sha256 が MANIFEST.tsv と違えば止まる。

出力 models/ViT-SO400M-16-SigLIP2-384__ohanami/(試験的に使った Immich の ONNX と同じ構成):
    config.json                      open_clip のモデル設定(model_cfg)
    visual/model.onnx                画像側。image float32 [1,3,384,384] -> embedding float32 [1,1152](L2 正規化済み)
    visual/preprocess_cfg.json       前処理の設定
    textual/model.onnx + model.onnx_data   テキスト側。text int32 [1,64] -> embedding float32 [1,1152](L2 正規化済み)。外部データは1ファイル
    textual/tokenizer.json, tokenizer_config.json, special_tokens_map.json   入力のファイルのコピー(加工しない)
    CONVERSION.json                  入力の sha256、パッケージの版、このスクリプトと requirements.txt の sha256、producer、opset、実測(時間・メモリ)
出力の各ファイルの sha256 は models/MANIFEST.tsv に追記する。

場所の切り替え(環境変数。付けなければ上のとおり <リポジトリ>/models/ を使う):
    OHANAMI_MODELS_DIR    入力の置き場と MANIFEST.tsv の場所(models/ に当たるディレクトリ)
    OHANAMI_CONVERT_OUT   出力先(空か存在しないディレクトリ)。指定したときは MANIFEST.tsv に追記せず、
                          出力先の隣の MANIFEST-rows.tsv に、追記するはずだった行を書く

仕様(試験的に使った Immich の ONNX と揃える。open_clip と torch の公開 API による):
    fp32、opset 19、TorchScript 経路(dynamo=False)、固定形状(バッチ 1)、入力名 image / text、出力名 embedding、
    encode_image / encode_text を normalize=True で呼ぶ(L2 正規化がグラフに入る)。後処理(fp16 化、最適化、onnxsim)はしない。
"""
import ctypes
import datetime
import hashlib
import importlib.metadata
import json
import os
import platform
import shutil
import sys
import time

# hub に触れない(ネットワークに出ない)
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

REPO_ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MODELS = os.environ.get("OHANAMI_MODELS_DIR") or os.path.join(REPO_ROOT, "models")
SRC_REPO = "timm/ViT-SO400M-16-SigLIP2-384"
SRC_REV = "fec784dabb3081a5f101fc74eefaf9d1ed08237b"
SRC_DIR = os.path.join(MODELS, SRC_REPO.split("/")[1])
SRC_FILES = ["open_clip_model.safetensors", "open_clip_config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json"]
MODEL_NAME = "ViT-SO400M-16-SigLIP2-384"  # open_clip の組み込みの設定名
OUT_OVERRIDE = os.environ.get("OHANAMI_CONVERT_OUT")
OUT_DIR = os.path.abspath(OUT_OVERRIDE) if OUT_OVERRIDE else os.path.join(MODELS, "ViT-SO400M-16-SigLIP2-384__ohanami")
OPSET = 19
IMAGE_SIZE = 384
CONTEXT_LENGTH = 64
MANIFEST_REPO = "ohanami-local/ViT-SO400M-16-SigLIP2-384__ohanami"


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(8 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


class Mem:
    """Windows のプロセスのピーク値(ワーキングセット / コミット)。ctypes で GetProcessMemoryInfo を呼ぶ。"""

    class _Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong), ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                    ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]

    @staticmethod
    def peak():
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        k32.GetCurrentProcess.restype = ctypes.c_void_p
        psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(Mem._Counters), ctypes.c_ulong]
        c = Mem._Counters()
        c.cb = ctypes.sizeof(c)
        if not psapi.GetProcessMemoryInfo(k32.GetCurrentProcess(), ctypes.byref(c), c.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        return {"peak_working_set_mib": round(c.PeakWorkingSetSize / 2**20), "working_set_mib": round(c.WorkingSetSize / 2**20),
                "peak_pagefile_mib": round(c.PeakPagefileUsage / 2**20)}


def read_manifest():
    path = os.path.join(MODELS, "MANIFEST.tsv")
    with open(path, encoding="utf8") as f:
        lines = [line.rstrip("\n") for line in f]
    return path, lines


def verify_inputs():
    """入力のファイルが MANIFEST.tsv の (repo, revision, filename) の sha256 と一致することを確かめる。違えば例外。"""
    _, lines = read_manifest()
    rows = {tuple(r.split("\t")[:3]): r.split("\t") for r in lines[1:] if r}
    out = {}
    for fn in SRC_FILES:
        key = (SRC_REPO, SRC_REV, fn)
        if key not in rows:
            raise SystemExit(f"MANIFEST.tsv に {key} が無い(spikes/tools/fetch_models.py で取得する)")
        row = rows[key]
        if row[5] != "OK":
            raise SystemExit(f"MANIFEST.tsv の {fn} が OK でない: {row}")
        path = os.path.join(SRC_DIR, fn)
        size = os.path.getsize(path)
        if size != int(row[3]):
            raise SystemExit(f"{fn}: サイズが MANIFEST と違う({size} != {row[3]})")
        got = sha256_of(path)
        if got != row[4]:
            raise SystemExit(f"{fn}: sha256 が MANIFEST と違う({got} != {row[4]})")
        out[fn] = {"size": size, "sha256": got}
    return out


def script_state():
    here = os.path.dirname(os.path.abspath(__file__))
    return {
        "convert_py_sha256": sha256_of(os.path.join(here, "convert.py")),
        "requirements_txt_sha256": sha256_of(os.path.join(here, "requirements.txt")),
    }


def installed_packages():
    return {d.metadata["Name"]: d.version for d in importlib.metadata.distributions()}


def dump_json(obj, path):
    # json.dumps の既定の書式(区切りが ", " と ": "、1行、ASCII エスケープあり)
    with open(path, "w", encoding="utf8", newline="") as f:
        f.write(json.dumps(obj))


def main():
    t_all = time.time()
    times = {}
    if os.path.exists(OUT_DIR) and os.listdir(OUT_DIR):
        raise SystemExit(f"{OUT_DIR} が空でない(上書きしない。消してから再実行する)")

    t = time.time()
    inputs = verify_inputs()
    times["verify_inputs_s"] = round(time.time() - t, 1)
    print("入力の sha256 を MANIFEST と照合: OK", times["verify_inputs_s"], "s", flush=True)

    import torch
    import onnx
    import open_clip

    torch.manual_seed(0)
    torch.backends.mha.set_fastpath_enabled(False)  # nn.MultiheadAttention の高速経路は ONNX にならない形になりうる

    with open(os.path.join(SRC_DIR, "open_clip_config.json"), encoding="utf8") as f:
        src_cfg = json.load(f)
    model_cfg, pre_cfg = src_cfg["model_cfg"], src_cfg["preprocess_cfg"]
    # open_clip の組み込みの設定が、固定した入力の model_cfg と同じであること(違えば、この版の open_clip では別のモデルになる)
    if open_clip.get_model_config(MODEL_NAME) != model_cfg:
        raise SystemExit("open_clip の組み込みの設定が open_clip_config.json の model_cfg と違う")
    if model_cfg["text_cfg"]["context_length"] != CONTEXT_LENGTH or model_cfg["vision_cfg"]["image_size"] != IMAGE_SIZE:
        raise SystemExit("context_length / image_size が想定と違う")

    # ---- 読み込み(固定した safetensors から。pretrained にファイルのパスを渡す = hub を使わない)
    t = time.time()
    model = open_clip.create_model(
        MODEL_NAME, pretrained=os.path.join(SRC_DIR, "open_clip_model.safetensors"), precision="fp32", device="cpu", jit=False,
        require_pretrained=True, force_preprocess_cfg={k: pre_cfg[k] for k in ("mean", "std", "interpolation", "resize_mode")})
    model.eval()
    for p in model.parameters():
        p.requires_grad_(False)
    preprocess_from_model = dict(open_clip.get_model_preprocess_cfg(model))
    times["load_model_s"] = round(time.time() - t, 1)
    n_visual = sum(p.numel() for p in model.visual.parameters())
    n_text = sum(p.numel() for n, p in model.named_parameters() if not n.startswith("visual."))
    mem_after_load = Mem.peak()
    print("読み込み", times["load_model_s"], "s  パラメータ 画像側", n_visual, "テキスト側ほか", n_text, mem_after_load, flush=True)

    os.makedirs(os.path.join(OUT_DIR, "visual"))
    os.makedirs(os.path.join(OUT_DIR, "textual"))
    work = os.path.join(OUT_DIR, "_work")  # torch の書き出し(外部データが断片になる)の一時置き場。最後に消す
    os.makedirs(os.path.join(work, "textual"))

    # ---- 画像側
    t = time.time()
    model.forward = lambda image: model.encode_image(image, normalize=True)
    dummy_image = torch.randn(1, 3, IMAGE_SIZE, IMAGE_SIZE, dtype=torch.float32)
    visual_path = os.path.join(OUT_DIR, "visual", "model.onnx")
    torch.onnx.export(model, (dummy_image,), visual_path, input_names=["image"], output_names=["embedding"],
                      opset_version=OPSET, dynamo=False, do_constant_folding=True)
    times["export_visual_s"] = round(time.time() - t, 1)
    mem_visual = Mem.peak()
    print("画像側 export", times["export_visual_s"], "s", mem_visual, flush=True)
    if os.path.getsize(visual_path) >= 2**31:
        raise SystemExit("visual/model.onnx が 2GB 以上(1ファイルにできない。想定外)")
    extra = [n for n in os.listdir(os.path.join(OUT_DIR, "visual")) if n != "model.onnx"]
    if extra:
        raise SystemExit(f"visual/ に想定外のファイルがある: {extra[:5]}")

    # ---- テキスト側(2GB を超えるので、torch は外部データを断片で書く。いったん読み直して1ファイルにまとめる)
    t = time.time()
    model.forward = lambda text: model.encode_text(text, normalize=True)
    dummy_text = torch.ones(1, CONTEXT_LENGTH, dtype=torch.int32)
    raw_path = os.path.join(work, "textual", "model.onnx")
    torch.onnx.export(model, (dummy_text,), raw_path, input_names=["text"], output_names=["embedding"],
                      opset_version=OPSET, dynamo=False, do_constant_folding=True)
    times["export_textual_raw_s"] = round(time.time() - t, 1)
    mem_text_raw = Mem.peak()
    print("テキスト側 export(断片)", times["export_textual_raw_s"], "s", mem_text_raw, flush=True)
    del model

    t = time.time()
    m = onnx.load(raw_path, load_external_data=True)
    onnx.save_model(m, os.path.join(OUT_DIR, "textual", "model.onnx"), save_as_external_data=True, all_tensors_to_one_file=True,
                    location="model.onnx_data", size_threshold=1024)
    times["merge_textual_s"] = round(time.time() - t, 1)
    mem_merge = Mem.peak()
    del m
    shutil.rmtree(work)
    print("テキスト側 外部データを1ファイルに", times["merge_textual_s"], "s", mem_merge, flush=True)

    # ---- 設定とトークナイザのファイル
    dump_json(model_cfg, os.path.join(OUT_DIR, "config.json"))
    preprocess = {"size": [IMAGE_SIZE, IMAGE_SIZE], "mode": "RGB", "mean": pre_cfg["mean"], "std": pre_cfg["std"],
                  "interpolation": pre_cfg["interpolation"], "resize_mode": pre_cfg["resize_mode"], "fill_color": 0}
    # open_clip がモデルに持たせた前処理の設定と、固定した入力の設定から組み立てたものが一致すること
    if {k: (list(v) if isinstance(v, tuple) else v) for k, v in preprocess_from_model.items()} != preprocess:
        raise SystemExit(f"前処理の設定が一致しない: model={preprocess_from_model} file={preprocess}")
    dump_json(preprocess, os.path.join(OUT_DIR, "visual", "preprocess_cfg.json"))
    for fn in ("tokenizer.json", "tokenizer_config.json", "special_tokens_map.json"):
        shutil.copyfile(os.path.join(SRC_DIR, fn), os.path.join(OUT_DIR, "textual", fn))

    # ---- 読み返して仕様を確かめる(onnx の構造チェックは行わず、入出力の名前・型・形・producer・opset を読む)
    def io_of(path):
        mm = onnx.load(path, load_external_data=False)
        def desc(v):
            tt = v.type.tensor_type
            return {"name": v.name, "elem_type": onnx.TensorProto.DataType.Name(tt.elem_type),
                    "shape": [d.dim_value if d.HasField("dim_value") else d.dim_param for d in tt.shape.dim]}
        return {"producer": f"{mm.producer_name} {mm.producer_version}", "ir_version": mm.ir_version,
                "opset": {o.domain or "ai.onnx": o.version for o in mm.opset_import},
                "inputs": [desc(i) for i in mm.graph.input if i.name not in {x.name for x in mm.graph.initializer}],
                "outputs": [desc(o) for o in mm.graph.output], "node_count": len(mm.graph.node),
                "ops": sorted({n.op_type for n in mm.graph.node})}
    io = {"visual": io_of(visual_path), "textual": io_of(os.path.join(OUT_DIR, "textual", "model.onnx"))}
    assert io["visual"]["inputs"] == [{"name": "image", "elem_type": "FLOAT", "shape": [1, 3, IMAGE_SIZE, IMAGE_SIZE]}], io["visual"]["inputs"]
    assert io["textual"]["inputs"] == [{"name": "text", "elem_type": "INT32", "shape": [1, CONTEXT_LENGTH]}], io["textual"]["inputs"]
    for k in ("visual", "textual"):
        assert io[k]["outputs"] == [{"name": "embedding", "elem_type": "FLOAT", "shape": [1, 1152]}], io[k]["outputs"]
        assert io[k]["opset"].get("ai.onnx") == OPSET, io[k]["opset"]
        assert "ReduceL2" in io[k]["ops"], "L2 正規化(ReduceL2)がグラフに無い"

    times["total_s"] = round(time.time() - t_all, 1)
    conv = {
        "date": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
        "inputs": {"repo": SRC_REPO, "revision": SRC_REV, "files": inputs},
        "script": script_state(),
        "python": sys.version, "platform": platform.platform(), "machine": platform.machine(),
        "packages": dict(sorted(installed_packages().items(), key=lambda kv: kv[0].lower())),
        "torch": {"version": torch.__version__, "cuda": torch.version.cuda, "threads": torch.get_num_threads()},
        "spec": {"precision": "fp32", "opset": OPSET, "exporter": "torch.onnx.export (TorchScript, dynamo=False)",
                 "do_constant_folding": True, "batch": "fixed 1", "normalize": True, "mha_fastpath": "disabled"},
        "outputs_io": io,
        "parameters": {"visual": n_visual, "text_and_projection": n_text},
        "timings_s": times,
        "memory": {"after_load": mem_after_load, "after_visual_export": mem_visual, "after_textual_export": mem_text_raw, "after_merge": mem_merge,
                   "final": Mem.peak()},
    }
    with open(os.path.join(OUT_DIR, "CONVERSION.json"), "w", encoding="utf8", newline="\n") as f:
        json.dump(conv, f, ensure_ascii=False, indent=2)
        f.write("\n")

    # ---- MANIFEST.tsv へ出力の sha256 を追記(OHANAMI_CONVERT_OUT を指定したときは、追記せず、出力先の隣の MANIFEST-rows.tsv に書く)
    manifest, lines = read_manifest()
    have = {tuple(r.split("\t")[:3]) for r in lines[1:] if r}
    script_rev = "self-built:" + conv["script"]["convert_py_sha256"][:12]
    new = []
    for dp, _, files in os.walk(OUT_DIR):
        for fn in sorted(files):
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, OUT_DIR).replace(os.sep, "/")
            new.append("\t".join([MANIFEST_REPO, script_rev, rel, str(os.path.getsize(full)), sha256_of(full), "OK"]))
    new.sort(key=lambda r: r.split("\t")[2])
    for r in new:
        if tuple(r.split("\t")[:3]) in have:
            raise SystemExit(f"MANIFEST.tsv にすでにある: {r.split(chr(9))[:3]}")
    if OUT_OVERRIDE:
        rows_path = os.path.join(os.path.dirname(OUT_DIR), "MANIFEST-rows.tsv")
        if os.path.exists(rows_path):
            raise SystemExit(f"{rows_path} がすでにある(上書きしない)")
        target = rows_path
    else:
        target = manifest
    with open(target, "a", encoding="utf8", newline="\n") as f:
        for r in new:
            f.write(r + "\n")
    print(("MANIFEST-rows.tsv に書いた:" if OUT_OVERRIDE else "MANIFEST.tsv に追記:"), len(new), "行", flush=True)
    for r in new:
        c = r.split("\t")
        print(f"  {c[2]}  {c[3]}  {c[4]}")
    print("全体", times["total_s"], "s  メモリのピーク", conv["memory"]["final"], flush=True)


if __name__ == "__main__":
    main()
