"""S2 手順4 の補助: C#(SkiaSharp)のデコード結果と、Pillow のデコード結果の差の大きさを見る。

使い方:
    1. C# のテストで、デコード結果をダンプする(環境変数 S2_DUMP_DECODED にディレクトリを指定):
         S2_DUMP_DECODED=<dir> dotnet run --project S2Siglip2.Tests -- -method "*Stage1_PreprocessedPixels_MatchPillow_WithinOne"
    2. 参照の venv で、このスクリプトを実行する:
         <venv>/Scripts/python.exe -I -X utf8 spikes/S2-siglip2/reference/compare_decoded.py <dir>

出力: 画像ごとに、元の解像度での「Skia のデコード結果と Pillow のデコード結果」の最大絶対差と、差のある画素の数。
16bit PNG は、Pillow(上位バイトを取る。v >> 8)と numpy の丸め(round(v/257))の両方との差も出す。
"""
import os
import struct
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
IMAGES = os.path.join(HERE, "data", "images")


def load_dump(path):
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        return np.frombuffer(f.read(), dtype=np.uint8).reshape(h, w, 3)


def read_png16(path):
    """自前の 16bit PNG(gen_image_corpus.py の write_png が書いたもの)を読む。フィルタ 0 だけ。"""
    import zlib
    with open(path, "rb") as f:
        b = f.read()
    pos = 8
    idat = b""
    w = h = depth = ctype = 0
    while pos < len(b):
        (ln,) = struct.unpack(">I", b[pos:pos + 4])
        tag = b[pos + 4:pos + 8]
        data = b[pos + 8:pos + 8 + ln]
        if tag == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", data[:10])
        elif tag == b"IDAT":
            idat += data
        pos += 12 + ln
    raw = zlib.decompress(idat)
    ch = {0: 1, 2: 3}[ctype]
    row = w * ch * 2
    rows = [raw[i * (row + 1) + 1:(i + 1) * (row + 1)] for i in range(h)]
    return np.frombuffer(b"".join(rows), dtype=">u2").reshape(h, w, ch).astype(np.uint16)


def main():
    dump = sys.argv[1]
    print(f"{'名前':36s} {'最大差':>6s} {'差のある画素':>14s}")
    for fn in sorted(os.listdir(dump)):
        if not fn.endswith(".rgb"):
            continue
        name = fn[:-4]
        skia = load_dump(os.path.join(dump, fn))
        src = [f for f in os.listdir(IMAGES) if f.startswith(name + ".")][0]
        path = os.path.join(IMAGES, src)
        if ("rgb16" in name or "gray16" in name) and src.endswith(".png"):
            v16 = read_png16(path)
            if v16.shape[2] == 1:
                v16 = np.repeat(v16, 3, axis=2)
            opts = {
                "v >> 8": (v16 >> 8).astype(np.uint8),
                "round(v/257)": ((v16.astype(np.uint32) * 255 + 32767) // 65535).astype(np.uint8),
                "floor(v*255/65535)": ((v16.astype(np.uint32) * 255) // 65535).astype(np.uint8),
            }
            im = Image.open(path)
            im.load()
            pil = np.asarray(im if im.mode == "RGB" else im.convert("RGB"))
            opts[f"Pillow({im.mode} → RGB)"] = pil
            for k, ref in opts.items():
                d = np.abs(skia.astype(int) - ref.astype(int))
                print(f"{name:36s} {int(d.max()):6d} {int((d.max(axis=2) > 0).sum()):14d}   (基準 = {k})")
        else:
            im = Image.open(path)
            im.load()
            ref = np.asarray(im if im.mode == "RGB" else im.convert("RGB"))
            d = np.abs(skia.astype(int) - ref.astype(int))
            print(f"{name:36s} {int(d.max()):6d} {int((d.max(axis=2) > 0).sum()):14d}")


if __name__ == "__main__":
    main()
