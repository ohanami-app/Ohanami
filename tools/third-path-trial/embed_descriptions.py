"""3 本目の経路(VLM の説明文のテキスト埋め込み)の試し測り(ADR 0035)の、埋め込みを作る Python。

製品とそのテストは Python に依存しない(手で一度だけ動かす試しの道具)。リポジトリの外の venv で動かす
(%LOCALAPPDATA%\\Ohanami\\venv-ruri。使うのは tokenizers・onnxruntime(CPU)・numpy だけ。torch は使わない)。

    <venv>\\Scripts\\python.exe -X utf8 tools/third-path-trial/embed_descriptions.py --db <bench.db の写し> --out <出力のディレクトリ> [--count-only]

- 入力: ベンチの DB(**写しを渡す**。読み取り専用で開く)の captions から、画像ごとに最新の caption(created_at が最新、同じなら rowid が大きい方。モデルは問わない。
  FtsSpec.LatestCaptionRowid と同じ選び方)の説明文と tags_json。クエリは、spikes/S2-siglip2 の known-items*.tsv(query_ja)と qrels.tsv(query_ja)。
- 入れ方は 2 つ(ADR 0035 の決め方。**同じ run で両方を作る**): **desc-tags(主)** = 「検索文書: 」+ 説明文 + 半角空白 + タグを「、」でつないだもの(タグが無ければ、空白もタグも付けない)、
  **desc-only(診断)** = 「検索文書: 」+ 説明文。クエリは「検索クエリ: 」+ クエリの文字列(cl-nagoya/ruri-v3-310m のカードの使い方)で、2 つの出力に同じものが入る。正規化は足さない(モデルのトークナイザのまま)。
  プーリングは平均(attention mask で重み付け)、最後に L2 正規化。**カードの使用例の類似度の行列を再現することは、tools/third-path-trial/ruri_gate.py で確かめてから使う**。
- **埋め込みの前に数える**(--count-only ならここまで): 説明文の件数・空の件数・入れ方ごとの重複(同じ文字列)を除いた種類の数・長さ(文字・トークン)。同じ文字列の文書は 1 回だけ埋め込む。
  空の説明文(空白だけを含む)の画像は母集団から外す(index.tsv に入れない)。
- 出力(<out>/desc-tags/ と <out>/desc-only/): それぞれ vectors.f32(行 × 次元の float32、リトルエンディアン、すべて長さ 1)、index.tsv(kind / key / row。kind は doc か query、key は画像のハッシュかクエリの文字列、
  同じ文字列の文書は同じ row)、meta.json(model・dim・入れ方・リビジョン・件数)。ベンチの `third-path` が読む(ThirdVectors。主は --embeddings、診断は --diagnostic-embeddings)。
  私物の説明文から作ったものなので、リポジトリの外に置く。
- 出力にも標準出力にも、説明文・クエリ・ファイル名は書かない(件数と数字だけ)。
"""
import argparse
import csv
import datetime
import hashlib
import json
import os
import sqlite3
import sys
import time

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

DOC_PREFIX = '検索文書: '
QUERY_PREFIX = '検索クエリ: '
MAX_TOKENS = 8192  # モデルの最大。超えるものは、黙って切らずに止める

# モデルの置き場: 環境変数 OHANAMI_MODELS_DIR があればそこ、無ければ <リポジトリ>/models
DEFAULT_MODEL_DIR = os.path.join(os.environ.get('OHANAMI_MODELS_DIR') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'models'),
                                 'ruri-v3-310m__trial')

VARIANTS = ('desc-tags', 'desc-only')  # 主、診断

LATEST_CAPTION_SQL = """
SELECT c.hash, c.description, c.tags_json
FROM captions c
WHERE c.rowid = (SELECT l.rowid FROM captions l WHERE l.hash = c.hash ORDER BY l.created_at DESC, l.rowid DESC LIMIT 1)
ORDER BY c.hash
"""


def read_known_queries(path):
    """known-items*.tsv の query_ja(ベンチと同じに、前後の空白を除く。空は読み飛ばす)。ファイルが無ければ空。"""
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8', newline='') as f:
        rows = list(csv.reader(f, delimiter='\t'))
    header = [h.lstrip('\ufeff').strip() for h in rows[0]]
    i = header.index('query_ja')
    out = []
    for r in rows[1:]:
        if r and len(r) > i and r[i].strip():
            out.append(r[i].strip())
    return out


def read_qrels_queries(path):
    """qrels.tsv の query_ja(ベンチと同じに、そのまま。前後の空白は除かない)。"""
    with open(path, encoding='utf-8', newline='') as f:
        lines = f.read().split('\n')
    out = []
    for line in lines[1:]:
        line = line.rstrip('\r')
        if line:
            out.append(line.split('\t')[0])
    return out


def parse_tags(tags_json, what):
    """tags_json(文字列の配列の JSON)を読む。形が違えば、黙って空にせず止める。"""
    tags = json.loads(tags_json)
    if not isinstance(tags, list) or any(not isinstance(t, str) for t in tags):
        raise SystemExit(f'{what}: tags_json が文字列の配列でない')
    return tags


def doc_text(variant, description, tags):
    """入れ方ごとの文書の文字列(プレフィックスつき)。desc-tags はタグが無ければ説明文だけと同じ。"""
    if variant == 'desc-only' or not tags:
        return DOC_PREFIX + description
    return DOC_PREFIX + description + ' ' + '、'.join(tags)


def pct(sorted_values, p):
    pos = (len(sorted_values) - 1) * p
    lo, hi = int(np.floor(pos)), int(np.ceil(pos))
    return sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * (pos - lo)


def stats(name, values):
    if not values:
        print(f'  {name}: 0 件')
        return
    v = sorted(values)
    print(f'  {name}: n={len(v)} 最小 {v[0]} / 中央値 {pct(v, .5):.1f} / 平均 {sum(v) / len(v):.1f} / 90% {pct(v, .9):.1f} / 最大 {v[-1]}')


class Embedder:
    def __init__(self, model_dir, threads=None):
        self.tok = Tokenizer.from_file(os.path.join(model_dir, 'tokenizer.json'))
        so = ort.SessionOptions()
        if threads:
            so.intra_op_num_threads = threads
        self.sess = ort.InferenceSession(os.path.join(model_dir, 'model.onnx'), so, providers=['CPUExecutionProvider'])
        names = [i.name for i in self.sess.get_inputs()]
        assert names == ['input_ids', 'attention_mask'], names
        self.pad = self.tok.token_to_id('<pad>')
        assert self.pad is not None

    def encode(self, texts):
        return [self.tok.encode(t) for t in texts]

    def embed(self, enc):
        """enc = tokenizers の Encoding のリスト → L2 正規化した平均プーリングの埋め込み(float32、行 × 768)。"""
        L = max(len(e.ids) for e in enc)
        ids = np.full((len(enc), L), self.pad, dtype=np.int64)
        mask = np.zeros((len(enc), L), dtype=np.int64)
        for i, e in enumerate(enc):
            ids[i, :len(e.ids)] = e.ids
            mask[i, :len(e.ids)] = 1
        hidden = self.sess.run(None, {'input_ids': ids, 'attention_mask': mask})[0]
        m = mask[:, :, None].astype(np.float32)
        pooled = (hidden * m).sum(axis=1) / np.clip(m.sum(axis=1), 1e-9, None)
        return (pooled / np.linalg.norm(pooled, axis=1, keepdims=True)).astype(np.float32)

    def embed_all(self, texts, batch, label):
        """texts を、トークン長の近いもの同士でバッチにして埋め込む。戻り値は texts と同じ順。"""
        enc = self.encode(texts)
        too_long = [len(e.ids) for e in enc if len(e.ids) > MAX_TOKENS]
        if too_long:
            raise SystemExit(f'{label}: {len(too_long)} 件がモデルの最大 {MAX_TOKENS} トークンを超える(最大 {max(too_long)})。黙って切らない')
        order = sorted(range(len(texts)), key=lambda i: len(enc[i].ids))
        out = np.zeros((len(texts), 768), dtype=np.float32)
        t0 = time.time()
        for s in range(0, len(order), batch):
            idx = order[s:s + batch]
            out[idx] = self.embed([enc[i] for i in idx])
            if (s // batch) % 10 == 0:
                print(f'  {label}: {min(s + batch, len(order))} / {len(order)}({time.time() - t0:.0f} 秒)', flush=True)
        return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--db', required=True, help='ベンチの DB の写し(読み取り専用で開く)')
    ap.add_argument('--out', required=True, help='出力のディレクトリ(リポジトリの外)。中に desc-tags/ と desc-only/ を作る')
    ap.add_argument('--spikes', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'spikes', 'S2-siglip2'))
    ap.add_argument('--model-dir', default=DEFAULT_MODEL_DIR)
    ap.add_argument('--batch', type=int, default=16)
    ap.add_argument('--threads', type=int, default=None)
    ap.add_argument('--count-only', action='store_true', help='埋め込みの前の数え上げだけで終わる')
    a = ap.parse_args()

    con = sqlite3.connect(f'file:{a.db}?mode=ro', uri=True)
    rows = con.execute(LATEST_CAPTION_SQL).fetchall()
    con.close()
    emb = Embedder(a.model_dir, a.threads)

    # ---- 埋め込みの前に数える
    captions = {h: (d if d is not None else '', parse_tags(t, '最新の caption') if d is not None else []) for h, d, t in rows}
    empty = [h for h, (d, _) in captions.items() if not d.strip()]
    docs = {h: v for h, v in captions.items() if v[0].strip()}
    print(f'説明文: caption のある画像 {len(captions)} 枚、空(空白だけを含む){len(empty)} 枚(母集団から外す)、埋め込む画像 {len(docs)} 枚')
    stats('説明文の長さ(文字)', [len(d) for d, _ in docs.values()])
    print(f'  タグが空の画像: {sum(1 for _, t in docs.values() if not t)} 枚')
    texts = {v: {h: doc_text(v, d, t) for h, (d, t) in docs.items()} for v in VARIANTS}
    for v in VARIANTS:
        unique = sorted(set(texts[v].values()))
        print(f'入れ方 {v}: 同じ文字列を除いた種類 {len(unique)}(重複 {len(docs) - len(unique)} 枚)')
        stats('  文書のトークン数(プレフィックスと特殊トークンを含む。同じ文字列は 1 回)', [len(e.ids) for e in emb.encode(unique)])

    queries = []
    seen = set()
    for name in ('known-items.tsv', 'known-items-dialogue.tsv', 'known-items-one-image.tsv', 'known-items-context.tsv'):
        for q in read_known_queries(os.path.join(a.spikes, name)):
            if q not in seen:
                seen.add(q); queries.append(q)
    for q in read_qrels_queries(os.path.join(a.spikes, 'qrels.tsv')):
        if q not in seen:
            seen.add(q); queries.append(q)
    print(f'クエリ: {len(queries)} 種類(known-item の 4 つのファイルと qrels。ファイルが無ければ飛ばす)')
    stats('クエリの長さ(文字)', [len(q) for q in queries])
    if a.count_only:
        print('--count-only: ここで終わる')
        return

    # ---- 埋め込み(クエリは 1 回。2 つの入れ方に同じものを書く)
    q_vec = emb.embed_all([QUERY_PREFIX + q for q in queries], a.batch, 'クエリ')
    for q in queries:
        assert '\t' not in q and '\n' not in q and '\r' not in q, 'クエリにタブか改行が入っている'
    manifest = os.path.join(a.model_dir, 'MANIFEST.tsv')
    revisions = {}
    if os.path.exists(manifest):
        for r in csv.DictReader(open(manifest, encoding='utf-8'), delimiter='\t'):
            revisions[r['file']] = {'repo': r['repo'], 'revision': r['revision'], 'sha256': r['sha256']}
    for v in VARIANTS:
        unique = sorted(set(texts[v].values()))
        doc_vec = emb.embed_all([u for u in unique], a.batch, f'文書({v})')
        row_of_text = {u: i for i, u in enumerate(unique)}
        vectors = np.vstack([doc_vec, q_vec]).astype('<f4')
        norms = np.linalg.norm(vectors, axis=1)
        assert np.all(np.abs(norms - 1) < 1e-4), (float(norms.min()), float(norms.max()))
        out = os.path.join(a.out, v)
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, 'vectors.f32'), 'wb') as f:
            f.write(vectors.tobytes())
        with open(os.path.join(out, 'index.tsv'), 'w', encoding='utf-8', newline='') as f:
            f.write('kind\tkey\trow\n')
            for h in sorted(docs):
                f.write(f'doc\t{h}\t{row_of_text[texts[v][h]]}\n')
            for i, q in enumerate(queries):
                f.write(f'query\t{q}\t{len(unique) + i}\n')
        meta = {
            'model': 'cl-nagoya/ruri-v3-310m (onnx-community/ruri-v3-310m-ONNX fp32)',
            'variant': v,
            'dim': int(vectors.shape[1]),
            'doc_template': '検索文書: {説明文} {タグを「、」でつないだもの}' if v == 'desc-tags' else '検索文書: {説明文}',
            'doc_prefix': DOC_PREFIX, 'query_prefix': QUERY_PREFIX, 'pooling': 'mean(attention mask)', 'normalized': True,
            'files': revisions,
            'counts': {'images_with_caption': len(captions), 'empty_descriptions': len(empty), 'embedded_images': len(docs), 'unique_texts': len(unique), 'queries': len(queries)},
            'created': datetime.datetime.now().astimezone().isoformat(timespec='seconds'),
            'onnxruntime': ort.__version__, 'numpy': np.__version__,
        }
        with open(os.path.join(out, 'meta.json'), 'w', encoding='utf-8') as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)
        print(f'書いた: {out}(vectors.f32 {vectors.shape[0]} 行 × {vectors.shape[1]}、index.tsv、meta.json)')


if __name__ == '__main__':
    main()
