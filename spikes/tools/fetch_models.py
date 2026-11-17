"""Hugging Face からモデル(GGUF / ONNX など)を取得し、models/MANIFEST.tsv に記録する。

使い方:  python -I spikes/tools/fetch_models.py     (標準ライブラリと curl だけを使う)

動作:
  1. PLAN に書かれたリポジトリごとに、固定したリビジョン(コミットの sha)の情報を HF の API で取得する。
  2. `https://huggingface.co/<repo>/resolve/<リビジョン>/<ファイル名>` から curl で取得する(-C - で再開できる)。
     URL にリビジョンを入れるのは、後でリポジトリ側が上書きされても同じファイルを指せるようにするため。
  3. サイズと SHA-256 を、HF の API が返す LFS の値と照合する(独立した別の出典との照合ではない)。
     LFS でない小さなファイルは SHA-256 が API に無いので、git の blob の SHA-1(API の blobId)で照合し、
     MANIFEST の sha256 列には実ファイルから計算した値を書く(sha256_match は SHA-1 の照合結果)。
  4. models/<リポジトリ名の後半>/<ファイル名> に置き(ファイル名にサブディレクトリが含まれていればそれも作る)、
     結果を models/MANIFEST.tsv に追記する。
     MANIFEST.tsv に同じ (repo, revision, filename) の行がすでにあれば、そのファイルは取得しない。

MANIFEST.tsv の形式(タブ区切り、UTF-8、1行目はヘッダ):
  repo           Hugging Face のリポジトリ名(例 Qwen/Qwen3-VL-8B-Instruct-GGUF)
  revision       取得したコミットの sha(40桁)
  filename       リポジトリ内のファイル名(置き場所は models/<repo の後半>/<filename>)
  size           バイト数
  sha256         LFS の SHA-256(16進、小文字)。LFS でないファイルは実ファイルから計算した値
  sha256_match   照合に成功すれば OK、そうでなければ MISMATCH
MISMATCH の行が1つでもあれば、そのファイルは使わないこと。

注意:
  - models/ は .gitignore 済み(巨大なので管理外)。MANIFEST.tsv も models/ の中にあるのでコミットされない。
  - PLAN のリビジョンは、取得したときのもの。モデルを増やす・更新するときは、ここのリビジョンを
    意図して書き換えること(リビジョンを省くと最新が変わるたびに別のファイルになり、再現できなくなる)。
  - 取得が途中で止まっても(PC のスリープなど)、再実行すれば続きから再開する。
"""
import hashlib
import json
import os
import subprocess
import time
import urllib.request

# このファイルは spikes/tools/ にあるので、リポジトリのルートは2つ上
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "models"))

# (リポジトリ, 固定するリビジョン, ファイルの指定)
# ファイルの指定は、ファイル名のリスト、または {"all": True, "exclude": [除外する部分文字列...]}(全ファイルから除外を引く)
PLAN = [
    ("Qwen/Qwen3-VL-8B-Instruct-GGUF", "f982a07559d4a2f6c8744d840bf6fccab30eea96",
     ["Qwen3VL-8B-Instruct-Q4_K_M.gguf", "mmproj-Qwen3VL-8B-Instruct-F16.gguf", "Qwen3VL-8B-Instruct-Q8_0.gguf"]),
    ("unsloth/Qwen3.5-9B-GGUF", "3885219b6810b007914f3a7950a8d1b469d598a5",
     ["Qwen3.5-9B-Q4_K_M.gguf", "mmproj-F32.gguf", "Qwen3.5-9B-Q8_0.gguf"]),
    ("Qwen/Qwen3-VL-4B-Instruct-GGUF", "1cd86afb9a95c410a6038ab3b40d8b578c892266",
     ["Qwen3VL-4B-Instruct-Q8_0.gguf", "mmproj-Qwen3VL-4B-Instruct-F16.gguf"]),
    ("unsloth/Qwen3.5-4B-GGUF", "e87f176479d0855a907a41277aca2f8ee7a09523",
     ["Qwen3.5-4B-Q8_0.gguf", "mmproj-F32.gguf"]),
    ("Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF", "f54435e6cc31258f04b0969105c3f6badb197931",
     ["mmproj-Qwen3VL-30B-A3B-Instruct-F16.gguf", "Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf"]),
    # S2 用。画像側と埋め込み側が別の ONNX(テキスト側は外部データの断片つき)。RK NPU / ARM NN 向けは要らない。
    ("immich-app/ViT-SO400M-16-SigLIP2-384__webli", "19baa26af70bd3639ca0ca17d1560cb8056dd983",
     {"all": True, "exclude": ["rknpu", "armnn"]}),
    # 自前変換(tools/convert-siglip2/)の入力。Apache-2.0 の元の重み(open_clip 形式)。.bin は同じ内容の別形式なので取らない。
    ("timm/ViT-SO400M-16-SigLIP2-384", "fec784dabb3081a5f101fc74eefaf9d1ed08237b",
     ["open_clip_model.safetensors", "open_clip_config.json", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json"]),
]

manifest = os.path.join(ROOT, "MANIFEST.tsv")
rows = []
if os.path.exists(manifest):
    rows = [line.rstrip("\n") for line in open(manifest, encoding="utf8")][1:]
done = {tuple(r.split("\t")[:3]) for r in rows}


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(8 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def git_blob_sha1(path):
    """git の blob の SHA-1(先頭に b"blob <バイト数>" と NUL を付けたもの)。HF API の blobId と比べる。"""
    size = os.path.getsize(path)
    h = hashlib.sha1()
    h.update(b"blob " + str(size).encode("ascii") + bytes([0]))
    with open(path, "rb") as f:
        while True:
            b = f.read(8 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def write_manifest():
    with open(manifest, "w", encoding="utf8", newline="\n") as f:
        f.write("repo\trevision\tfilename\tsize\tsha256\tsha256_match\n")
        for r in rows:
            f.write(r + "\n")


def resolve_files(sib, spec):
    if isinstance(spec, dict):
        if not spec.get("all"):
            raise SystemExit(f"ファイルの指定が不正: {spec}")
        return sorted(n for n in sib if not any(x in n for x in spec.get("exclude", [])))
    return list(spec)


def main():
    os.makedirs(ROOT, exist_ok=True)
    for repo, rev, spec in PLAN:
        # リビジョンを指定した API。返ってくる sha が PLAN と違えば、想定外なので止める。
        with urllib.request.urlopen(f"https://huggingface.co/api/models/{repo}/revision/{rev}?blobs=true") as r:
            info = json.load(r)
        if info["sha"] != rev:
            raise SystemExit(f"{repo}: リビジョンが一致しない(要求 {rev}、応答 {info['sha']})")
        sib = {s["rfilename"]: s for s in info["siblings"]}
        d = os.path.join(ROOT, repo.split("/")[1])
        os.makedirs(d, exist_ok=True)
        for fn in resolve_files(sib, spec):
            if (repo, rev, fn) in done:
                continue
            s = sib[fn]
            size = s["size"]
            lfs = s.get("lfs")
            if lfs is not None and lfs["size"] != size:
                raise SystemExit(f"{repo}/{fn}: API の size と lfs.size が食い違う")
            path = os.path.join(d, *fn.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            url = f"https://huggingface.co/{repo}/resolve/{rev}/{fn}"
            t0 = time.time()
            print(time.strftime("%H:%M:%S"), "START", repo, fn, size, flush=True)
            for _ in range(5):
                have = os.path.getsize(path) if os.path.exists(path) else 0
                if have == size:
                    break
                subprocess.run(["curl", "-sL", "--fail", "-C", "-", "-o", path, url])
            got_size = os.path.getsize(path) if os.path.exists(path) else -1
            if got_size == size:
                got = sha256_of(path)
                if lfs is not None:
                    want = lfs["sha256"]
                    ok = "OK" if got == want else "MISMATCH"
                else:
                    want = got  # LFS でないファイルの SHA-256 は、実ファイルから計算した値を記録する
                    ok = "OK" if git_blob_sha1(path) == s["blobId"] else "MISMATCH"
            else:
                want, ok = "", "MISMATCH"
            print(time.strftime("%H:%M:%S"), ok, fn, f"size={got_size}/{size}", f"{time.time() - t0:.0f}s", flush=True)
            rows.append("\t".join([repo, rev, fn, str(size), want, ok]))
            write_manifest()
    print("ALL DONE", flush=True)


if __name__ == "__main__":
    main()
