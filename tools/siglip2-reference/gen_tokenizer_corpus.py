"""トークナイザの正解データ(クエリ → id 列)を、Python で一度だけ生成して TSV にする。

使い方(リポジトリの外の venv で):
    <venv>/Scripts/python.exe -I -X utf8 tools/siglip2-reference/gen_tokenizer_corpus.py [tokenizer.json のパス]

入力:  models/ViT-SO400M-16-SigLIP2-384__ohanami/textual/tokenizer.json(MANIFEST.tsv で sha256 を照合済みのもの)
出力:  tests/Ohanami.Core.Tests/TestData/tokenizer-corpus.tsv

== 参照の仕様(前処理の手順) ==
  clean_text
      text = " ".join(text.split())                      空白の連なりを1つにまとめ、両端を除く(Python の str.split())
      text = text.translate(ASCII 句読点 32 字を削除).lower()   削除であって置き換えではない("don't" → "dont")
  tokenizer
      Tokenizer.from_file(tokenizer.json)
      enable_padding(length=64, pad_token="<pad>", pad_id=<pad> の id)
      enable_truncation(max_length=64)
      ids = tokenizer.encode(clean_text(text)).ids       末尾に <eos>(id 1)が付き、64 に切り詰め・<pad>(0)で埋める
  NFKC はかけない(語彙側の正規化が identity のため)。全角の句読点は残る。

== TSV の形式(タブ区切り、UTF-8、1行目はヘッダ。文字列の列は JSON 文字列(ASCII エスケープ)なので、改行・タブ・サロゲートも安全) ==
  no        通し番号(0 始まり)
  category  種類(下の CATEGORIES)
  text      入力の文字列(JSON 文字列)
  cleaned   clean_text の結果(JSON 文字列)。C# の Clean を単独で確かめるため
  ids       64 個の整数(空白区切り)

== 比較のコーパスから外したもの(既知の差) ==
  - U+0130(İ): Python の lower() は2文字("i̇")になる。Ohanami の小文字化は1対1の写像
  - U+03A3(Σ): Python の lower() は語末で ς、それ以外で σ(文脈で変わる)。Ohanami は常に σ
  - 不正なサロゲート: Python の tokenizers は例外を出す。C# 側は別のテストで「例外になること」を確かめる
"""
import json
import os
import random
import string
import sys

from tokenizers import Tokenizer

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
# モデルの置き場。環境変数 OHANAMI_MODELS_DIR があればそこ(git の worktree など、models/ が無い場所で動かすとき)、無ければ <リポジトリ>/models
MODELS = os.environ.get("OHANAMI_MODELS_DIR") or os.path.join(REPO, "models")
MODEL_REPO = "ViT-SO400M-16-SigLIP2-384__ohanami"
TOKENIZER_JSON = (sys.argv[1] if len(sys.argv) > 1 else
                  os.path.join(MODELS, MODEL_REPO, "textual", "tokenizer.json"))
OUT = os.path.join(REPO, "tests", "Ohanami.Core.Tests", "TestData", "tokenizer-corpus.tsv")
CONTEXT_LENGTH = 64
PUNCT_TRANS = str.maketrans("", "", string.punctuation)


def clean_text(text: str) -> str:
    text = " ".join(text.split())
    return text.translate(PUNCT_TRANS).lower()


def load_tokenizer(path: str = TOKENIZER_JSON) -> Tokenizer:
    tok = Tokenizer.from_file(path)
    pad_id = tok.token_to_id("<pad>")
    if pad_id is None:
        raise SystemExit("<pad> が語彙に無い")
    tok.enable_padding(length=CONTEXT_LENGTH, pad_token="<pad>", pad_id=pad_id)
    tok.enable_truncation(max_length=CONTEXT_LENGTH)
    return tok


def encode(tok: Tokenizer, text: str):
    return tok.encode(clean_text(text)).ids


_RAW = None


def content_length(text: str) -> int:
    """切り詰め・パディングなしの、<eos> を除く token 数。"""
    global _RAW
    if _RAW is None:
        _RAW = Tokenizer.from_file(TOKENIZER_JSON)
    return len(_RAW.encode(clean_text(text)).ids) - 1


# ---------------------------------------------------------------- コーパス

CATEGORIES = {}


def add(category, *texts):
    CATEGORIES.setdefault(category, []).extend(texts)


def build_corpus(tok: Tokenizer):
    # --- 実際に打ちそうな日本語の短いクエリ
    add("ja_short",
        "青い綿雲", "綿雲", "ログインボーナス", "猫", "犬", "夕焼けの海", "戦闘中の画面", "ガチャ結果", "ミラベーラ",
        "セリフ", "雪原", "森の中の城", "赤い髪の女の子", "笑顔のキャラクター", "ボス戦", "レベルアップ", "ステータス画面",
        "ショップの画面", "ダンジョン", "武器庫", "白いTシャツ", "体操服", "水着", "制服", "ドレス", "魔法のエフェクト",
        "爆発", "夜の街", "雨", "桜", "海岸", "温泉", "教室", "図書館", "ゲームオーバー", "エンディング", "タイトル画面",
        "設定画面", "マップ", "インベントリ", "ミニゲーム", "釣り", "料理", "戦闘不能", "勝利", "メニュー", "アイテム一覧",
        "スキルツリー", "キャラクター選択", "会話シーン", "黒い背景に白い文字", "かわいい", "かっこいい", "怖い", "きれいな景色",
        "ねこ", "いぬ", "さくら", "うみ", "ゆきだるま", "すごくおおきなドラゴン", "ちいさなこびと", "あおいそら",
        "ガチャを回す", "木像が歩く", "靴下一足", "赤塗り", "子供扱い", "そうやな", "ほんまに", "なんでやねん",
        "ありがとう", "ごめんなさい", "おはよう", "こんにちは", "こんばんは", "さようなら", "いただきます", "ごちそうさま",
        "この帽子にもポップコーンの効果があるのだろうか", "そんなことになったら木彫りの熊が一頭歩く熊になって",
        "ひとりで行くのは危ないよ", "もうすぐ日が暮れる")
    # --- 英語
    add("en",
        "cat", "a blue fluffy creature", "login bonus", "battle screen", "sunset over the sea", "girl with red hair",
        "boss fight", "level up", "status screen", "shop", "dungeon", "snowy field", "castle in the forest",
        "The quick brown fox jumps over the lazy dog", "HELLO WORLD", "Hello, World!", "screenshot of a game",
        "anime style character", "a photo of a cat", "two cats", "NEW GAME", "GAME OVER", "Press START", "SKIP", "AUTO",
        "fantasy", "pixel art", "menu", "inventory", "skill tree", "I'm not sure", "it's a trap", "rock 'n' roll",
        "state-of-the-art", "e-mail", "Wi-Fi", "Dr. Smith", "U.S.A.", "3D", "2D", "HP", "MP", "EXP", "UP", "OK", "NG")
    # --- 日英混在
    add("mixed",
        "ガチャ10連", "HP回復", "LV99到達", "SSRキャラ", "SSR キャラ", "Wi-Fi設定", "MP50消費", "ステータスUP", "ATK+30",
        "DEF 120", "Lv.50", "ボスHP", "EXPが足りない", "NEW!マーク", "Aボタン", "Bボタンで戻る", "PS5版", "Switch版",
        "4Kスクリーンショット", "2D格闘", "3Dモデル", "RPGの戦闘画面", "FPS視点", "UIデザイン", "BGM", "CGギャラリー",
        "NPCとの会話", "アップデートVer.1.2.3", "ID:12345", "コスト5のカード", "HPバー", "MPゲージ", "Sランク", "SKIPボタン",
        "AUTOモード", "OFF", "ONにする", "iPhone", "Windows11", "PlayStation", "Nintendo Switch", "ゲームをプレイ中")
    # --- 数字
    add("digits",
        "ガチャ10連", "2023年", "1000000", "100000000000", "0", "00", "007", "3.14159", "0123456789", "1,000,000", "10%",
        "第3章", "2023年3月27日", "12:34", "1/2", "+5", "-10", "×3", "5000円", "99999", "1111111111", "２０２３", "１０連", "百万",
        "3rd", "1st place", "42", "4242", "2024-01-01", "No.1")
    # --- 全角英数・半角カナ(NFKC をかけないことの確認)
    add("width",
        "ＨＰ回復", "ＡＢＣ１２３", "ｈｅｌｌｏ", "ＨＥＬＬＯ", "ｶﾞﾁｬ", "ﾊﾟﾜｰｱｯﾌﾟ", "ｱｲｳｴｵ", "ﾚﾍﾞﾙｱｯﾌﾟ", "ﾒﾆｭｰ", "ｽﾃｰﾀｽ",
        "ＬＶ９９", "ＳＳＲ", "ＮＥＷ", "ｶﾀｶﾅ", "ｶﾞ", "ﾊﾟ", "ｳﾞ", "１２３４５", "ＡＴＫ＋３０", "ｼｮｯﾌﾟ", "ｰ", "ｯ",
        "㈱", "①②③", "㌔", "ℌ", "ﬁ", "Ⅻ", "ｔｅｓｔ　ｔｅｓｔ")
    # --- 絵文字・まれな漢字(byte fallback)
    add("rare",
        "😀", "🐱🐶", "👨‍👩‍👧‍👦", "🇯🇵", "❤️", "✨キラキラ✨", "𠮷野家", "𩸽", "𠀋", "龘", "靐", "𪚥", "𫝆",
        "é", "ü", "ñ", "ß", "œ", "Ç", "åäö", "Ελληνικά", "Привет мир", "안녕하세요", "你好世界", "مرحبا", "שלום", "สวัสดี",
        "Ꙭ", "ᚠᚢᚦ", "ꜩ", "☃", "♪♫", "→←↑↓", "★☆", "※", "〒", "〜", "…", "‥", "ー", "ｰ", "々", "〆", "ゝゞ", "ヽヾ", "゙", "が",
        "é", "\U0001f600\U0001f601", "\U00010400", "\U0001d11e", "", "\U000f0000", "\u0000")
    # --- ASCII 句読点(除去で空になるものを含む)
    add("ascii_punct",
        "don't", "!!!", "hello, world!", "a-b-c", "($)", "...", "!!?", "@#%^&*()", "_underscore_", "foo_bar", "a.b.c",
        "?!?!", "\"quoted\"", "'single'", "<tag>", "[bracket]", "{brace}", "a/b\\c", "a|b", "~tilde~", "`backtick`", "a+b=c",
        "100%", "$100", "#1", "@user", "a;b:c", "a,b", "(゜∀゜)", "!", "?", "-", "_", ".", ",", "'", "\"", "()", "[]", "{}", "<>", "!?.,;:",
        "ガチャ!", "ガチャ!!", "ガチャ?", "10連!", "HP - 回復", "A - B", "a - b - c", "hello !", "hello ! world", "( a )", "x ( y ) z",
        " - ", "a  -  b", "foo - ", " - bar", "- - -", "!a!", "a!b", "1 - 2", "50 - 50", "who's there?", "can't won't")
    # --- 全角句読点
    add("fullwidth_punct",
        "「こんにちは」", "、。！？", "『』〜…", "・", "「ガチャ」", "（かっこ）", "【重要】", "［四角］", "｛波｝", "〈山〉《谷》", "！！", "？？",
        "ガチャ！", "ガチャ？", "ねこ、いぬ", "ねこ。いぬ", "ねこ・いぬ", "ねこ＿いぬ", "ａ－ｂ", "「あ」「い」", "…", "……", "ー", "～", "＠", "＃", "＄",
        "￥", "＆", "＊", "＋", "＝", "＜＞", "”“", "’‘", "‐‑‒–—", "¡¿", "«»", "§¶", "©®™")
    # --- 空白・タブ・改行(連続、全角、各種の空白文字)
    all_ws = [chr(c) for c in range(0x3000 + 1) if chr(c).isspace()]
    add("whitespace",
        "a   b", "a\tb", "a\n\nb", "a\r\nb", "あ　い", " leading", "trailing ", "  both  ", "a b", " nbsp", "a\u0085b",
        "a b", "a b", "a　　b", "ねこ　　いぬ", "ねこ\nいぬ", "ねこ\tいぬ", "\t\ttab", "line1\nline2\nline3",
        "a \t\n\r b", "あ \n い", "タイトル\n\nサブタイトル", "﻿bom", "a​b", "a⁠b", "a­b", "a᠎b", "a‌b",
        "a‍b", "a️b")
    add("whitespace_each", *[f"a{c}b" for c in all_ws], *[f"{c}a{c}" for c in all_ws])
    # --- 空文字列・空白だけ
    add("empty", "", " ", "   ", "\t", "\n", "\t\n ", "　", " ", " 　 ", "\r\n\r\n")
    # --- added_tokens を踏む文字列
    add("added_tokens",
        "<mask>", "<table>", "<unused0>", "<unused98>", "<unused99>", "<2mass>", "<eos>", "<bos>", "<pad>", "<unk>",
        "<start_of_turn>", "<end_of_turn>", "[@BOS@]", "<strong>", "<td>", "</td>", "</table>", "<tr><td>a</td></tr>",
        "foo<mask>bar", "foo <mask> bar", "<MASK>", "<Mask>", "<mask><mask>", "<<mask>>", "<unused1><unused2>", "<table><tr><td>",
        "▁▁", "▁", "a▁▁b", "▁▁▁▁", "hello▁world", "<0x41>", "<0x0A>", "<0xZZ>",
        "ガチャ<mask>", "<mask>ガチャ", "<eos>ガチャ", "<unusedA>", "<unused100>", "< mask >", "<mas", "mask>", "<", ">", "<>")
    # --- 同じ rank の merge の順序(左から)を確かめる
    add("merge_order",
        "ああああ", "あああああ", "ああああああ", "wwww", "wwwww", "wwwwww", "1000000", "10000000", "1111111", "aaaaaaaa", "ーーーー", "ーーーーー",
        "あああああああ", "……………", "ははははは", "ふふふふふ", "ababab", "abababab", "ababababa", "ののののの", "ここここ", "ざざざざ", "そそそそそ",
        "ああいいああ", "うううええええ", "ABABABAB", "xyzxyzxyz", "ねこねこねこ", "ガチャガチャガチャ", "ああああああああああああ", "0000000000", "9999999")

    # --- 長文(64 を超える)と、切り詰めの境界(<eos> を除いて 62〜66 token になる文字列)
    long_ja = ("この帽子にもポップコーンの効果があるのだろうか。" * 10)
    add("long", long_ja, long_ja * 5, "あ" * 200, "ガチャ" * 100, "a" * 300, "word " * 100, "日本語のテキストが長く続く場合の動作を確認する。" * 40,
        "1234567890" * 30, "😀" * 50, "𠮷" * 100, ("ねこ " * 70).strip(), "ABC " * 70)
    rnd = random.Random(20261105)
    pool = [chr(c) for c in range(0x3041, 0x3097)] + [chr(c) for c in range(0x30A1, 0x30FB)] + [chr(c) for c in range(0x4E00, 0x4E00 + 400)]
    boundary = {n: None for n in (61, 62, 63, 64, 65, 66)}
    for _ in range(5000):
        # 1文字ずつ足していき、token 数がちょうど目標になった時点の文字列を採る
        s = ""
        for _ in range(200):
            s += rnd.choice(pool)
            L = content_length(s)
            if L in boundary and boundary[L] is None:
                boundary[L] = s
            if L > 66:
                break
        if all(v is not None for v in boundary.values()):
            break
    for L, s in boundary.items():
        if s is None:
            raise SystemExit(f"content_length={L} の文字列を作れなかった")
    add("long_boundary", *[boundary[L] for L in sorted(boundary)])

    # --- 乱数(シード固定)。文字種を混ぜる。U+0130 と U+03A3 は含めない
    hira = [chr(c) for c in range(0x3041, 0x3097)]
    kata = [chr(c) for c in range(0x30A1, 0x30FB)] + ["ー"]
    kanji_common = [chr(c) for c in range(0x4E00, 0x4E00 + 800)]
    kanji_rare = [chr(c) for c in range(0x20000, 0x20000 + 200)] + [chr(c) for c in range(0x3400, 0x3400 + 100)]
    ascii_l = list(string.ascii_letters + string.digits)
    ascii_p = list(string.punctuation)
    full = [chr(c) for c in range(0xFF21, 0xFF3B)] + [chr(c) for c in range(0xFF41, 0xFF5B)] + [chr(c) for c in range(0xFF10, 0xFF1A)]
    halfkana = [chr(c) for c in range(0xFF66, 0xFF9F)]
    wsp = [" ", " ", " ", "\t", "\n", "　", " "]
    fp = list("、。！？「」『』（）・〜…")
    emoji = [chr(c) for c in range(0x1F600, 0x1F650)]
    latin_ext = [chr(c) for c in range(0xC0, 0x180) if c not in (0xD7, 0xF7, 0x130)]
    greek_cyr = [chr(c) for c in range(0x391, 0x3C9) if c != 0x3A3] + [chr(c) for c in range(0x410, 0x450)]
    pools = [
        ("rand_ja", [hira, kata, kanji_common, fp, wsp], 30),
        ("rand_mixed", [hira, kata, kanji_common, ascii_l, ascii_p, wsp, fp, full], 30),
        ("rand_ascii", [ascii_l, ascii_p, wsp], 40),
        ("rand_exotic", [hira, kanji_rare, emoji, halfkana, latin_ext, greek_cyr, full, wsp, ascii_p], 25),
    ]
    for name, pool_list, maxlen in pools:
        items = []
        for _ in range(60):
            n = rnd.randint(1, maxlen)
            items.append("".join(rnd.choice(rnd.choice(pool_list)) for _ in range(n)))
        add(name, *items)


def check_exclusions(text: str):
    if "İ" in text or "Σ" in text:
        raise SystemExit(f"除外した文字(U+0130 / U+03A3)が入っている: {text!r}")
    for c in text:
        if 0xD800 <= ord(c) <= 0xDFFF:
            raise SystemExit(f"サロゲートが入っている: {text!r}")
        if len(c.lower()) != 1:
            raise SystemExit(f"lower() が1文字にならない文字が入っている: U+{ord(c):04X}")


def main():
    tok = load_tokenizer()
    build_corpus(tok)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    n = 0
    counts = {}
    with open(OUT, "w", encoding="utf8", newline="\n") as f:
        f.write("no\tcategory\ttext\tcleaned\tids\n")
        for cat, texts in CATEGORIES.items():
            for t in texts:
                check_exclusions(t)
                cleaned = clean_text(t)
                ids = encode(tok, t)
                assert len(ids) == CONTEXT_LENGTH, (t, len(ids))
                f.write("\t".join([str(n), cat, json.dumps(t, ensure_ascii=True), json.dumps(cleaned, ensure_ascii=True),
                                   " ".join(map(str, ids))]) + "\n")
                n += 1
                counts[cat] = counts.get(cat, 0) + 1
    print(f"{n} 件を {OUT} に書いた")
    for cat, c in counts.items():
        print(f"  {cat}: {c}")


if __name__ == "__main__":
    sys.exit(main())
