"""正しさの関門: cl-nagoya/ruri-v3-310m のモデルカードの使用例(5 文)の類似度の行列を、試しの Python(tokenizers + onnxruntime CPU + numpy)で再現する。

プーリングは平均(attention mask で重み付け)。5 文は、カードのとおり(プレフィックスは文字列に含まれている: 先頭 2 文は空、
「トピック: 」「検索クエリ: 」「検索文書: 」)。ONNX の出力の形(last_hidden_state か、プーリング済みか)も確かめる。
"""
import json, os, sys
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

# モデルの置き場: 環境変数 OHANAMI_MODELS_DIR があればそこ、無ければ <リポジトリ>/models
D = os.path.join(os.environ.get('OHANAMI_MODELS_DIR') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'models'),
                 'ruri-v3-310m__trial')
SENTENCES = [
    "川べりでサーフボードを持った人たちがいます",
    "サーファーたちが川べりに立っています",
    "トピック: 瑠璃色のサーファー",
    "検索クエリ: 瑠璃色はどんな色？",
    "検索文書: 瑠璃色（るりいろ）は、紫みを帯びた濃い青。名は、半貴石の瑠璃（ラピスラズリ、英: lapis lazuli）による。JIS慣用色名では「こい紫みの青」（略号 dp-pB）と定義している[1][2]。",
]
CARD = np.array([
    [1.0000, 0.9603, 0.8157, 0.7074, 0.6916],
    [0.9603, 1.0000, 0.8192, 0.7014, 0.6819],
    [0.8157, 0.8192, 1.0000, 0.8701, 0.8470],
    [0.7074, 0.7014, 0.8701, 1.0000, 0.9746],
    [0.6916, 0.6819, 0.8470, 0.9746, 1.0000],
])

tok = Tokenizer.from_file(os.path.join(D, 'tokenizer.json'))
cfg = json.load(open(os.path.join(D, 'tokenizer_config.json'), encoding='utf-8'))
print('tokenizer_config の主な項目:', {k: cfg[k] for k in cfg if k in ('model_max_length', 'padding_side', 'cls_token', 'sep_token', 'pad_token', 'add_prefix_space', 'clean_up_tokenization_spaces')})
print('tokenizers の post_processor:', tok.post_processor is not None, '/ padding:', tok.padding, '/ truncation:', tok.truncation)

enc = [tok.encode(s) for s in SENTENCES]  # add_special_tokens は既定(tokenizer.json の post_processor が CLS/SEP を足す)
for s, e in zip(SENTENCES, enc):
    print(f'{len(e.ids):4d} トークン  先頭 {e.tokens[:6]} ... 末尾 {e.tokens[-3:]}')
L = max(len(e.ids) for e in enc)
pad_id = tok.token_to_id(cfg.get('pad_token', '[PAD]')) if isinstance(cfg.get('pad_token'), str) else tok.token_to_id('[PAD]')
ids = np.full((len(enc), L), pad_id, dtype=np.int64)
mask = np.zeros((len(enc), L), dtype=np.int64)
for i, e in enumerate(enc):
    ids[i, :len(e.ids)] = e.ids
    mask[i, :len(e.ids)] = 1

so = ort.SessionOptions()
sess = ort.InferenceSession(os.path.join(D, 'model.onnx'), so, providers=['CPUExecutionProvider'])
print('入力:', [(i.name, i.type, i.shape) for i in sess.get_inputs()])
print('出力:', [(o.name, o.type, o.shape) for o in sess.get_outputs()])
feed = {'input_ids': ids}
names = [i.name for i in sess.get_inputs()]
if 'attention_mask' in names:
    feed['attention_mask'] = mask
outs = sess.run(None, feed)
for o, meta in zip(outs, sess.get_outputs()):
    print('実際の出力', meta.name, o.shape, o.dtype)

hidden = outs[0]
if hidden.ndim == 3:
    m = mask[:, :, None].astype(np.float32)
    pooled = (hidden * m).sum(axis=1) / np.clip(m.sum(axis=1), 1e-9, None)
    print('出力は last_hidden_state(3 次元)。平均のプーリングは自前')
else:
    pooled = hidden
    print('出力はプーリング済み(2 次元)')
emb = pooled / np.linalg.norm(pooled, axis=1, keepdims=True)
sim = emb @ emb.T
np.set_printoptions(precision=4, suppress=True, linewidth=140)
print('再現した類似度の行列:'); print(sim)
print('カードの値:'); print(CARD)
diff = np.abs(sim - CARD)
print('差の絶対値の最大: %.5f(カードは 4 桁に丸めてある。0.00005 を超えるなら丸めでは説明がつかない)' % diff.max())
print('行列の表(カードの値 / 再現の値):')
for i in range(5):
    print('  ' + '  '.join(f'{CARD[i, j]:.4f}/{sim[i, j]:.4f}' for j in range(5)))
print('判定:', '一致(丸めの範囲)' if diff.max() <= 0.00051 else '不一致')
