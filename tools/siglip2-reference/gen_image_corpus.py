"""画像の前処理の正解データ(合成した画像 + Pillow のリサイズ結果 + テンソルのハッシュ)を一度だけ生成する。

使い方(リポジトリの外の venv で):
    <venv>/Scripts/python.exe -I -X utf8 tools/siglip2-reference/gen_image_corpus.py

出力(すべてコミットする。私物の画像は使わない。画像はすべてこのスクリプトが乱数(シード固定)で合成する):
    tests/Ohanami.Core.Tests/TestData/images/<name>.<ext>        合成した入力画像(PNG / JPEG / WebP)
    tests/Ohanami.Core.Tests/TestData/golden/<name>.png          参照のリサイズ結果(384×384 RGB の可逆 PNG)
    tests/Ohanami.Core.Tests/TestData/image-corpus.tsv           名前・形式・サイズ・メモ・入力の sha256・デコード結果(RGB8、元の解像度)の sha256・テンソル(float32 CHW)の sha256

== 参照の仕様(前処理の手順) ==
  デコード + RGB 化
      image = Image.open(path); image.load()
      if image.mode != "RGB": image = image.convert("RGB")      ← リサイズの【前】。アルファは合成せずに捨てる。パレットは展開する
  リサイズ(preprocess_cfg.json は resize_mode=squash、interpolation=bicubic)
      image.resize((384, 384), resample=BICUBIC)                  アスペクト比は無視、クロップなし。Pillow の ImagingResample
  正規化
      x = float32(rgb) / 255.0;  x *= 1/0.5;  x -= 0.5/0.5;  CHW に並べ替え
  EXIF の向きは見ない(exif_transpose を呼ばない)。ICC プロファイルや gAMA は無視する(Pillow もそうする)。

== 16bit PNG の扱い ==
  Pillow の 16bit → RGB は版・モードによって結果が変わる(グレーの I;16 → RGB は壊れる)ので、Pillow には頼らない。
  参照は numpy で v8 = v16 >> 8(上位バイト。libpng の png_set_strip_16 と同じ)に落としてから、同じリサイズをかける。
  これは SkiaSharp(Skia の PNG デコーダ)の 16→8 と同じ(実測: compare_decoded.py で差 0)。丸め(round(v16 / 257))との差は
  最大 1 だが、Ohanami は Skia の挙動 = 上位バイトを仕様にする(埋め込みへの影響は 1/255 以下で、決めの問題)。

== 画像コーパスの種類 ==
  PNG(RGB / 半透明 RGBA / パレット / パレット + tRNS / グレースケール / グレースケール + アルファ / ICC 付き / gAMA 付き / 16bit RGB / 16bit グレー)、
  JPEG(baseline 4:2:0 / 4:4:4 / progressive / グレー / EXIF の向き付き / ICC 付き / 奇数サイズ)、
  WebP(lossy / lossless / 半透明 lossless)、64×64 と 1×1(拡大の経路)、384×384(同じサイズ = Pillow はコピーする)、
  2561×1083 と 1916×1077(大判)、極端なアスペクト比(3000×40、40×3000)
"""
import hashlib
import io
import os
import struct
import sys
import zlib

import numpy as np
from PIL import Image, ImageCms, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DATA = os.path.join(REPO, "tests", "Ohanami.Core.Tests", "TestData")
IMG_DIR = os.path.join(DATA, "images")
GOLD_DIR = os.path.join(DATA, "golden")
TSV = os.path.join(DATA, "image-corpus.tsv")
SIZE = 384

JP_FONT_CANDIDATES = [r"C:\Windows\Fonts\meiryo.ttc", r"C:\Windows\Fonts\YuGothM.ttc", r"C:\Windows\Fonts\msgothic.ttc"]


def font(size):
    for p in JP_FONT_CANDIDATES:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default(size)


def scene(w, h, seed):
    """グラデーション + 図形 + 細かい縞 + 文字 + 市松模様。縮小・拡大のどちらでもフィルタの差が出るようにする。"""
    rng = np.random.default_rng(seed)
    x = np.linspace(0, 1, w, dtype=np.float32)[None, :, None]
    y = np.linspace(0, 1, h, dtype=np.float32)[:, None, None]
    ph = rng.uniform(0, 6.28, size=3)
    base = np.concatenate([
        (0.5 + 0.5 * np.sin(6.28 * x * 1.3 + ph[0])) * np.ones_like(y),
        (0.5 + 0.5 * np.sin(6.28 * y * 1.7 + ph[1])) * np.ones_like(x),
        (0.5 + 0.5 * np.sin(6.28 * (x + y) * 0.9 + ph[2])),
    ], axis=2)
    img = Image.fromarray((base * 255).astype(np.uint8), "RGB")
    d = ImageDraw.Draw(img)
    for _ in range(14):
        x0, y0 = int(rng.integers(0, w)), int(rng.integers(0, h))
        x1, y1 = x0 + int(rng.integers(5, max(6, w // 3))), y0 + int(rng.integers(5, max(6, h // 3)))
        col = tuple(int(v) for v in rng.integers(0, 256, size=3))
        if rng.integers(0, 2):
            d.rectangle([x0, y0, x1, y1], fill=col)
        else:
            d.ellipse([x0, y0, x1, y1], fill=col, outline=(255, 255, 255))
    for _ in range(40):
        d.line([int(rng.integers(0, w)), int(rng.integers(0, h)), int(rng.integers(0, w)), int(rng.integers(0, h))],
               fill=tuple(int(v) for v in rng.integers(0, 256, size=3)), width=int(rng.integers(1, 3)))
    # 細かい縞と市松(高周波。縮小のアンチエイリアスの差が出る)
    sx, sy = w // 8, h // 8
    if sx >= 8 and sy >= 8:
        for i in range(0, sx, 2):
            d.line([i, 0, i, sy], fill=(255, 255, 255), width=1)
        for i in range(0, sy, 2):
            for j in range(0, sx, 2):
                if (i // 2 + j // 2) % 2 == 0:
                    d.point([(w - sx + j, h - sy + i)], fill=(0, 0, 0))
    # セリフ枠風の半透明ウィンドウと日本語の文字
    if w >= 200 and h >= 100:
        box = [w * 0.1, h * 0.65, w * 0.9, h * 0.95]
        overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(overlay).rounded_rectangle(box, radius=max(4, h // 40), fill=(255, 255, 255, 170))
        img = Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")
        d = ImageDraw.Draw(img)
        d.text((box[0] + 8, box[1] + 6), "ミラベーラ", font=font(max(10, h // 24)), fill=(120, 20, 80))
        d.text((box[0] + 8, box[1] + 6 + max(12, h // 18)), "「この帽子にもポップコーンの効果があるのだろうか」", font=font(max(10, h // 22)), fill=(40, 40, 40))
    return img


def to_rgb_array(img):
    return np.asarray(img if img.mode == "RGB" else img.convert("RGB"))


# ---------------------------------------------------------------- 自前の PNG 書き出し(16bit・gAMA 用)

def _chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


def write_png(path, arr, color_type, bitdepth, extra_chunks=()):
    """arr: (h, w) か (h, w, c) の配列。bitdepth 16 のときは uint16(ビッグエンディアンで書く)。"""
    h, w = arr.shape[:2]
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    a = arr.reshape(h, w, channels)
    if bitdepth == 16:
        raw = a.astype(">u2").tobytes()
        row_bytes = w * channels * 2
    else:
        raw = a.astype(np.uint8).tobytes()
        row_bytes = w * channels
    rows = b"".join(b"\x00" + raw[i * row_bytes:(i + 1) * row_bytes] for i in range(h))  # フィルタ 0(なし)
    out = b"\x89PNG\r\n\x1a\n"
    out += _chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, bitdepth, color_type, 0, 0, 0))
    for tag, data in extra_chunks:
        out += _chunk(tag, data)
    out += _chunk(b"IDAT", zlib.compress(rows, 9))
    out += _chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(out)


def rgb16_from(arr8, rng):
    """8bit RGB から、下位バイトにも情報がある 16bit RGB を作る(v16 = v8 * 257 + 小さな乱数 ≤ 65535)。"""
    v = arr8.astype(np.int64) * 257
    noise = rng.integers(-100, 101, size=v.shape)
    return np.clip(v + noise, 0, 65535).astype(np.uint16)


# ---------------------------------------------------------------- 参照の処理

def reference_resize(rgb_img):
    return rgb_img.resize((SIZE, SIZE), resample=Image.BICUBIC)


def normalize(arr_u8):
    """float32 のまま /255 → *2 → -1 の順に正規化する。CHW float32。"""
    x = arr_u8.astype(np.float32) / np.float32(255.0)
    x *= np.float32(1.0 / 0.5)
    x -= np.float32(0.5 / 0.5)
    return np.ascontiguousarray(x.transpose(2, 0, 1))


def main():
    os.makedirs(IMG_DIR, exist_ok=True)
    os.makedirs(GOLD_DIR, exist_ok=True)
    rng = np.random.default_rng(20261105)
    entries = []  # (name, path, fmt, note, golden_source)  golden_source: None なら Pillow で開く / ndarray(uint8 RGB)ならそれをリサイズ

    def add(name, ext, fmt, note, saver, golden_array=None):
        path = os.path.join(IMG_DIR, f"{name}.{ext}")
        saver(path)
        entries.append((name, path, fmt, note, golden_array))

    # --- PNG: 通常の RGB。解像度の3つの山と大判
    for (w, h, seed) in [(862, 491, 1), (1148, 652, 2), (1297, 735, 3), (1916, 1077, 4), (2561, 1083, 5)]:
        img = scene(w, h, seed)
        add(f"png_rgb_{w}x{h}", "png", "PNG RGB", "スクリーンショット風の合成画像", lambda p, img=img: img.save(p))
    # --- 極端なアスペクト比
    add("png_rgb_3000x40", "png", "PNG RGB", "極端に横長", lambda p: scene(3000, 40, 6).save(p))
    add("png_rgb_40x3000", "png", "PNG RGB", "極端に縦長", lambda p: scene(40, 3000, 7).save(p))
    # --- 拡大の経路と同じサイズ
    add("png_rgb_64x64", "png", "PNG RGB", "極小(64→384 の拡大)", lambda p: scene(64, 64, 8).save(p))
    add("png_rgb_1x1", "png", "PNG RGB", "1×1(拡大の端)", lambda p: Image.new("RGB", (1, 1), (200, 30, 90)).save(p))
    add("png_rgb_384x384", "png", "PNG RGB", "同じサイズ(Pillow はコピーを返す)", lambda p: scene(384, 384, 9).save(p))
    add("png_rgb_383x385", "png", "PNG RGB", "ほぼ同じサイズ(1画素ずつ違う)", lambda p: scene(383, 385, 10).save(p))
    # --- アルファ
    base = scene(640, 360, 11)
    alpha_x = np.linspace(0, 255, 640, dtype=np.float32)[None, :]
    alpha_y = np.linspace(255, 0, 360, dtype=np.float32)[:, None]
    alpha = np.clip((alpha_x + alpha_y) / 2, 0, 255).astype(np.uint8)
    rgba = base.copy()
    rgba.putalpha(Image.fromarray(alpha, "L"))
    add("png_rgba_semitransparent_640x360", "png", "PNG RGBA", "アルファが 0〜255 の勾配(合成せず捨てる。下の RGB が値のまま残る)", lambda p: rgba.save(p))
    # --- パレット
    pal = scene(256, 256, 12).quantize(colors=16, method=Image.Quantize.MEDIANCUT)
    add("png_palette_256x256", "png", "PNG P", "16色のパレット画像", lambda p: pal.save(p))
    add("png_palette_trns_256x256", "png", "PNG P + tRNS", "パレット + 透過(透過は無視して色だけ使う)", lambda p: pal.save(p, transparency=0))
    # --- グレースケール
    gray = scene(800, 450, 13).convert("L")
    add("png_gray_800x450", "png", "PNG L", "グレースケール", lambda p: gray.save(p))
    la = gray.convert("LA")
    la.putalpha(Image.fromarray(np.tile(np.linspace(0, 255, 800, dtype=np.uint8)[None, :], (450, 1)), "L"))
    add("png_graya_800x450", "png", "PNG LA", "グレースケール + アルファ", lambda p: la.save(p))
    # --- ICC / gAMA
    def icc_bytes(kind):
        b = bytearray(ImageCms.ImageCmsProfile(ImageCms.createProfile(kind)).tobytes())
        b[24:36] = bytes(12)  # ヘッダの作成日時を 0 にして、再生成しても同じバイト列になるようにする
        return bytes(b)

    srgb = icc_bytes("sRGB")
    xyz = icc_bytes("XYZ")
    icc_img = scene(500, 300, 14)
    add("png_icc_srgb_500x300", "png", "PNG RGB + ICC(sRGB)", "sRGB の ICC 付き(無視されて同じ値)", lambda p: icc_img.save(p, icc_profile=srgb))
    add("png_icc_xyz_500x300", "png", "PNG RGB + ICC(XYZ)", "RGB に XYZ の ICC を付けた(色管理されると大きく変わる。無視されること)", lambda p: icc_img.save(p, icc_profile=xyz))
    arr_g = to_rgb_array(scene(500, 300, 15))
    add("png_gama_500x300", "png", "PNG RGB + gAMA", "gAMA = 1/2.2 付き(無視されること)", lambda p: write_png(p, arr_g, 2, 8, [(b"gAMA", struct.pack(">I", 45455))]))
    # --- 16bit
    arr8 = to_rgb_array(scene(600, 400, 16))
    arr16 = rgb16_from(arr8, rng)
    v8_rgb16 = (arr16 >> 8).astype(np.uint8)
    add("png_rgb16_600x400", "png", "PNG RGB 16bit", "16bit RGB(参照は numpy の v16 >> 8。Pillow には頼らない)",
        lambda p: write_png(p, arr16, 2, 16), golden_array=v8_rgb16)
    gray16 = rgb16_from(arr8[:, :, :1].repeat(3, axis=2), rng)[:, :, 0]
    v8_gray16 = (gray16 >> 8).astype(np.uint8)
    add("png_gray16_600x400", "png", "PNG L 16bit", "16bit グレー(参照は numpy の v16 >> 8 を3チャンネルに複製)",
        lambda p: write_png(p, gray16, 0, 16), golden_array=np.stack([v8_gray16] * 3, axis=2))

    # --- JPEG
    j1 = scene(862, 491, 21)
    add("jpeg_420_q85_862x491", "jpg", "JPEG baseline 4:2:0", "標準(4:2:0)", lambda p: j1.save(p, quality=85, subsampling=2))
    j2 = scene(1280, 720, 22)
    add("jpeg_444_q95_1280x720", "jpg", "JPEG baseline 4:4:4", "4:4:4", lambda p: j2.save(p, quality=95, subsampling=0))
    add("jpeg_420_q75_1280x720", "jpg", "JPEG baseline 4:2:0", "低めの品質", lambda p: j2.save(p, quality=75, subsampling=2))
    add("jpeg_progressive_1000x700", "jpg", "JPEG progressive", "プログレッシブ", lambda p: scene(1000, 700, 23).save(p, quality=88, progressive=True))
    add("jpeg_gray_640x480", "jpg", "JPEG L", "グレースケール JPEG", lambda p: scene(640, 480, 24).convert("L").save(p, quality=90))
    exif_img = scene(640, 360, 25)
    ex = Image.Exif()
    ex[0x0112] = 6  # Orientation = 6(90度回転)。Pillow は exif_transpose を呼ばなければ見ない
    add("jpeg_exif_orientation6_640x360", "jpg", "JPEG + EXIF Orientation=6", "EXIF の向きは無視されること", lambda p: exif_img.save(p, quality=90, exif=ex))
    add("jpeg_icc_xyz_500x300", "jpg", "JPEG + ICC(XYZ)", "ICC は無視されること", lambda p: icc_img.save(p, quality=90, icc_profile=xyz))
    add("jpeg_odd_1001x667", "jpg", "JPEG 4:2:0 奇数サイズ", "奇数サイズ + 4:2:0(端のクロマ)", lambda p: scene(1001, 667, 26).save(p, quality=85, subsampling=2))
    add("jpeg_tiny_64x64", "jpg", "JPEG", "極小", lambda p: scene(64, 64, 27).save(p, quality=90))

    # --- WebP
    w1 = scene(900, 600, 31)
    add("webp_lossy_q80_900x600", "webp", "WebP lossy", "lossy", lambda p: w1.save(p, "WEBP", quality=80))
    add("webp_lossless_900x600", "webp", "WebP lossless", "lossless", lambda p: w1.save(p, "WEBP", lossless=True))
    w2 = scene(640, 360, 32)
    w2.putalpha(Image.fromarray(alpha, "L"))
    add("webp_lossless_rgba_640x360", "webp", "WebP lossless RGBA", "半透明", lambda p: w2.save(p, "WEBP", lossless=True))
    add("webp_lossy_tiny_64x64", "webp", "WebP lossy", "極小", lambda p: scene(64, 64, 33).save(p, "WEBP", quality=80))

    # ---- 正解データ
    rows = []
    for name, path, fmt, note, golden_array in entries:
        if golden_array is None:
            im = Image.open(path)
            im.load()
            width, height = im.size
            src_mode = im.mode
            rgb = im if im.mode == "RGB" else im.convert("RGB")  # リサイズの前に RGB 化
        else:
            im = Image.open(path)
            width, height = im.size
            src_mode = im.mode
            rgb = Image.fromarray(golden_array, "RGB")
        out = reference_resize(rgb)
        gold_path = os.path.join(GOLD_DIR, f"{name}.png")
        out = Image.fromarray(np.asarray(out), "RGB")  # info(ICC など)を引き継がない新しい画像にして保存する
        out.save(gold_path, optimize=True)
        arr = np.asarray(out)
        assert arr.shape == (SIZE, SIZE, 3) and arr.dtype == np.uint8
        tensor = normalize(arr)
        assert tensor.dtype == np.float32 and tensor.shape == (3, SIZE, SIZE)
        with open(path, "rb") as f:
            src_sha = hashlib.sha256(f.read()).hexdigest()
        decoded_sha = hashlib.sha256(np.ascontiguousarray(np.asarray(rgb)).tobytes()).hexdigest()
        rows.append([name, os.path.basename(path), fmt, src_mode, str(width), str(height), note, src_sha,
                     decoded_sha, hashlib.sha256(tensor.tobytes()).hexdigest()])
        print(f"{name:40s} {fmt:28s} {width}x{height}  mode={src_mode}")

    with open(TSV, "w", encoding="utf8", newline="\n") as f:
        f.write("name\tfile\tformat\tpillow_mode\twidth\theight\tnote\tsource_sha256\tdecoded_rgb_sha256\ttensor_sha256\n")
        for r in rows:
            f.write("\t".join(r) + "\n")
    total_src = sum(os.path.getsize(p) for _, p, *_ in entries)
    total_gold = sum(os.path.getsize(os.path.join(GOLD_DIR, f"{n}.png")) for n, *_ in entries)
    print(f"{len(rows)} 枚。入力画像 {total_src / 1e6:.1f} MB、正解 PNG {total_gold / 1e6:.1f} MB")


if __name__ == "__main__":
    sys.exit(main())
