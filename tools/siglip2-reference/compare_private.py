"""前処理の正解データの確認の補助: 手元の画像(私物。コミットしない)で、C# の前処理結果が Pillow と画素単位で一致するかを数える。

使い方:
    1. C# のテストで、C# の結果(384×384 RGB8 の sha256)を書き出す:
         set OHANAMI_PRIVATE_IMAGES=<画像のディレクトリ>
         set OHANAMI_PRIVATE_HASHES=<出力する TSV>
         dotnet test --project tests/Ohanami.Core.Tests --filter-class "*PrivateImagesTests"
    2. 参照の venv で:
         <venv>/Scripts/python.exe -I -X utf8 tools/siglip2-reference/compare_private.py <画像のディレクトリ> <出力した TSV>

Pillow 側の処理(Image.open → load → convert("RGB") → resize((384,384), BICUBIC))。一致しなかったファイルは、
最大絶対差を計算して表示する(C# 側は sha256 しか持たないので、差の大きさは出せない。差があれば C# の画像ダンプで調べる)。
"""
import hashlib
import os
import sys

import numpy as np
from PIL import Image


def main():
    images, tsv = sys.argv[1], sys.argv[2]
    rows = [line.rstrip("\n").split("\t") for line in open(tsv, encoding="utf8") if line.strip()]
    same = diff = err = 0
    bad = []
    modes = {}
    for name, sha in rows:
        im = Image.open(os.path.join(images, name))
        im.load()
        modes[im.mode] = modes.get(im.mode, 0) + 1
        rgb = im if im.mode == "RGB" else im.convert("RGB")
        out = np.asarray(rgb.resize((384, 384), resample=Image.BICUBIC))
        h = hashlib.sha256(out.tobytes()).hexdigest()
        if sha.startswith("ERROR"):
            err += 1
            bad.append((name, sha))
        elif h == sha:
            same += 1
        else:
            diff += 1
            bad.append((name, "ハッシュ不一致"))
    print(f"{len(rows)} 枚: 一致 {same}、不一致 {diff}、C# のエラー {err}。Pillow のモード内訳: {modes}")
    for name, why in bad[:30]:
        print("  ", name, why)


if __name__ == "__main__":
    sys.exit(main())
