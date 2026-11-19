# Ohanami.Bench: 検索の回帰ベンチ

製品の検索(`OhanamiCore.Search`)に、SigLIP2 の評価(`spikes/S2-siglip2`。以下「評価の記録」)の known-item と qrels を向けて回し、
「変更が precision@10 と known-item を下げていないこと」を確かめる。**手元専用**(私物の画像に依存する)。
数字を上げるために回さない(偏った小さなクエリに過適合するため。ADR 0037)。

用語: 「VLM の評価」は `spikes/S1-vlm`(VLM の説明文・OCR を 1030 枚に対して作った実験)、「評価の記録」は `spikes/S2-siglip2`(SigLIP2 の検索の数字と、known-item・qrels の入力)。どちらも手元のデータで、公開していない。

## 使い方

```
# 環境変数: OHANAMI_IMAGES_DIR = 画像のフォルダ(読み取りだけ)、OHANAMI_MODELS_DIR = models/ に当たるディレクトリ
# ビルドに OHANAMI_RUNTIME_DIR(CUDA / cuDNN の DLL の置き場。App と同じ)が要る
dotnet run --project tools/Ohanami.Bench -- run --label before-change   # 走査・埋め込み(済みは再利用)・測定・保存。基準線があれば比較まで
dotnet run --project tools/Ohanami.Bench -- baseline                    # 直近の run を基準線にする
dotnet run --project tools/Ohanami.Bench -- compare                     # 基準線と直近の run を比べ、下がったクエリを一覧にする
```

- DB・サムネイル・結果は `%LOCALAPPDATA%\Ohanami\bench\`(`--root` で変更。リポジトリの中は拒否する)。結果は `runs/<時刻>-<label>/`、基準線は `baseline/`。
- 結果は `summary.txt`(要約)、`known-items.tsv`、`precision.tsv`(クエリ × 経路ごとの表)、`scores.tsv`(経路ごとのスコア。下の「スコアと所要時間」)、`top10.tsv`(上位 10 件の枠ごとの表。下の「繰り返し出る画像」)。経路は行の値なので、経路が増えても列は変わらず行が増える。
- 入力は `spikes/S2-siglip2/`(評価の記録)の `known-items.tsv`(見た目 45 件)、`known-items-dialogue.tsv`(セリフ 10 件)、`qrels.tsv`。
  任意の `known-items-one-image.tsv`(集合 `one-image`。形式は known-items.tsv と同じ: UTF-8、タブ区切り、filename / query_ja / query_en。query_en は空でよい)も読む。**無ければ**測らない。
  あるのに形式が違えば例外(「無い」と「壊れている」を区別する)。評価の記録の 2 つのファイルには手を加えず、`check-s2` と見た目・セリフの集計は変わらない。summary には one-image を別の集合として出し、scores.tsv の `targets` にも `one-image` が入る。
  基準線に one-image の行が無い(古い基準線など)ときは、`compare` は one-image を「新しい行」として扱い(下がったと数えず、例外にもしない。注意の行に出す)、one-image を含む基準線を取ったあとから比べる。
- 終了コード: 0 = 正常、1 = 失敗(入力の誤り・埋め込みの失敗など)、3 = 比較で基準線より下がった集計がある。

## VLM の評価の出力の取り込みと、評価の記録の再現の確認

```
# VLM の評価の run(spikes/S1-vlm の出力)を captions と全文索引に取り込む。DB はベンチ用(製品の DB には触らない)
dotnet run --project tools/Ohanami.Bench -- import-s1 --s1-run <VLM の評価の run のフォルダ>
dotnet run --project tools/Ohanami.Bench -- run --label import-check                # 全文の経路を含めて測る
dotnet run --project tools/Ohanami.Bench -- check-s2 --s2 spikes/S2-siglip2/results/<評価の記録の結果のフォルダ>   # 直近の run を評価の記録の数字と突き合わせる
```

- `import-s1` は、VLM の評価が使った設定(プロンプト・スキーマ・温度・画像トークン・起動フラグ)が `VlmCatalog` の値と一致するか 7 項目を確かめ、
  1 つでも違えば取り込まない(黙って別設定の結果を混ぜない)。失敗した画像(length など)は `caption` の失敗として記録する。
  確かめられない点(モデルのファイルのハッシュは VLM の評価の run に無い)は「未確認」として出力する。
  確かめる 7 項目: (1) プロンプトとスキーマ(実際に送る文字列。評価の `prompt.txt`・`schema.json` と、各画像の実際のリクエストの両方)、(2) モデルのファイルの名前・サイズと、`models/MANIFEST.tsv` の SHA-256(評価の run に SHA-256 が無いので、**弱い確認**)、
  (3) llama-server のビルド、(4) 起動引数(製品が足す `--image-max-tokens` と `--no-webui` だけが評価との差)、(5) temperature、(6) 失敗の種類と `max_tokens`(失敗はすべて `finish_reason=length`。成功した画像の出力が製品の上限より小さい)、(7) 画像トークンの上限が、評価のどの画像にも効いていないこと。
  出力の「確認していない点」には、モデルのファイルが評価のときと同じバイト列だったか・評価の画像がすべて PNG で JPEG と WebP が無いこと・`--no-webui` の影響・評価の再試行の run を取り込まないこと、が出る。
- `check-s2` の合格条件(評価の記録の数字を、結果を見る前に決めた許容で突き合わせる): 全文の known-item の順位が一致、融合の known-item 29 ± 2、precision@10(融合 0.477 ± 0.02、全文 0.297 ± 0.01)、セリフ(全文 7/10 一致、融合 8 ± 1)。
- precision@10 の経路別(融合なし)の上位 10 は、同点をファイル名の順序(ordinal)で切る(評価の記録の「画像番号」と同じ並び)。
- 判断の記録: `fts_docs` は 1 列(single)に決めた(ADR 0030)。3 列(triple)も作って比べたところ、FTS5 の BM25 は列の重みが既定では全列の総語数を使うため、
  順位は 1 列と同一だった(融合の precision@10・known-item・セリフとも差 0)。事前に決めた規則(3 列が +0.02 以上上回る場合だけ採る)を満たさないので、3 列のコードは消した。

## 測るもの

- known-item: 探したい 1 枚の順位(同点は平均順位)。上位 20 位 / 5 位 / 1 位以内の数と中央値。経路ごとと融合後(`fused`)。
- precision@10: qrels の各クエリの上位 10 件。未判定は外れとして数え(下限)、未判定がすべて当たりのときを上限として併記する。
  分母は常に 10 × クエリ数(経路が 10 件返せなければ、足りない分は外れ)。
- 比較: 同じ (集合, クエリ, ファイル, 経路) の順位が下がったもの(20 位の線の外へ落ちたものを先頭に)、当たりの数が減ったクエリ。
  新しく上位に入った画像が qrels に無いと「未判定」になる(外れとして数えるので、当たりの数が減って見える)。判定が要るかは、未判定の数の増減で見る。

## スコアと所要時間

検索の調整の判断材料(ADR 0034 の線の置き方など)。製品の `SearchResponse` が持つ経路ごとのスコア(`PathHit.Score`。見た目は、順位を決めた点 = 補正をかけた検索語では cosine − μ·r(ADR 0051)、かけない検索語では cosine。全文は `-rank` = BM25 の符号反転)を、そのまま書き出す。

- `scores.tsv`: 列は query / filename / targets / judged / path / score / rank / top10 / path_top1 / score_corrected(製品の検索の結果の旗 `PathOutcome.ScoreCorrected`。fused は空)。1 行 = 1 つの (クエリ, 画像) の 1 つの経路(fused は RRF の合計)。
  対象は、そのクエリの **qrels の画像の全組**と、**そのクエリを使う known-item の目的の画像**。見た目の経路は全画像に点(順位を決めた点)が付くので、判定済みの画像はすべて値を持つ。
  全文の母集団に入っていない画像は score・rank が空。`targets` は目的にする集合(visual / dialogue。両方なら `dialogue|visual`)、`judged` は qrels の判定(1 = 当たり、0 = 外れ、空 = 未判定)、
  `top10` はその経路の上位 10 件に入っていたか(precision@10 と同じ取り方 = `Metrics.TopOf`)、`path_top1` はそのクエリでのその経路の 1 位のスコア(1 位との差を、この行だけから出せる)。
- summary の追加部分:
  - **返した分の当たり率**(`PrecisionSummary` の 当たり ÷ 返した件数。未判定は外れ = 下限、すべて当たり = 上限)。経路ごと。全文の偽陽性の量の目安。従来の「下限」は分母が常に 10 × クエリ数で、経路が少ししか返さないと薄まる。
  - **見た目の点の分布**(補正をかけた検索語とかけない検索語は水準が違うので、2 つの群に分けて出す。群の分け方は、検索の結果の旗(`score_corrected`)。関門をここで計算し直さない。旗の列が無い古い形式の run は、3 つ目の群「補正の有無が分からない」にする。件数・最小・10%・25%・中央値・75%・90%・最大。分位点は線形補間): 判定が当たり / 外れ、それぞれのうち**見た目の経路の上位 10 件に入っていたもの**(ADR 0034 の「効き目の下限」を測る母集団と、守る集合)、
    known-item の目的(見た目・セリフ)、目的のうち融合の上位 300 件以内(ADR 0034 の守る集合)、各クエリの 1 位の点、「1 位との差」(目的の画像)。続けて、同じ区間(幅 0.01、範囲が広ければ 2 倍ずつ)の文字のヒストグラム。
  - **全文のスコア**: 母集団に入っていた数(主)と、プールしたスコアの分布(BM25 はクエリごとに尺度が違うので目安)。
  - **所要時間**: 入力の読み込み / Core の起動 / 走査 / 埋め込み / ベクトルの読み込み待ち / 全文検索の索引 / 検索 / 集計 と合計。検索 1 回あたりの平均と最大(`Notes` の行)。
- 互換: `known-items.tsv` と `precision.tsv` の列は変えていない。`compare` と `baseline` は、この 2 つ(と基準線では summary.txt)だけを使う。
  古い形式の基準線(scores.tsv が無い。scores.tsv があっても、旗の列が無い 9 列の形式)も読めて、scores.tsv のある新しい run と比べられる(`scores.tsv` があれば読む。無ければ null)。`baseline` は run に scores.tsv があれば基準線にも写す(比較には使わない)。
  scores.tsv があって壊れていれば、黙って無視せず例外にする。
- 補正をかけない検索語の群で、件数が少ない・クエリが違う母集団を「同じ尺度」として並べるのは、cosine が 1 つのモデルの値だから(クエリの型で尺度が動くかは未確認)。補正をかけた検索語の群の点は、cosine から画像ごとの定数 μ·r を引いた値で、同じ画像からは検索語が違っても同じ量を引くので、その群の中では同じ尺度として並べる。2 つの群は水準が違うので、並べて比べない。分布は形を見るためのもので、線は引かない。

## 繰り返し出る画像(`top10.tsv`)

特徴ベクトル検索では、どのクエリでも上位に来る画像(繰り返し出る画像)がある(ADR 0051)。その量を、`run`・`third-path` の summary に出す。

- `top10.tsv`: 列は query / path / pos / filename / text / judge。1 行 = 1 つのクエリのその経路の上位 10 件の 1 枠(`Metrics.TopOf`。precision@10 と同じ取り方)。
  `text` はその画像が全文検索にヒットしていたか(1 / 0)、`judge` は qrels の判定(`1` = 当たり、`0` = 外れ、`u` = 未判定、`-` = そのクエリが qrels に無い)。私物のクエリ・ファイル名を含むので、リポジトリの外のままにする。
  古い run には無い(読むと null)。あって壊れていれば例外。
- ① 繰り返し(経路ごと。対象は **qrels のある検索語だけ** = precision@10 を測る検索語。known-item だけの検索語は数えない。枠の数 = 検索語の数 × 10(全文は返した分だけ)): 異なる画像の数、「繰り返し画像」(その検索語のうち 5 語以上で上位 10 件に入る画像)の数とその枠の数・割合、1 枚が出る検索語数の最大。summary には、検索語の数と「検索語 × 10」を併記する。比べるために、known-item だけの検索語も含めた全検索語で数えた値(比べる定義)も並べて出す(② の繰り返しの行も同じ)。
- ② 融合の上位 10 件の当たり率の内訳(**qrels のクエリだけ**。当たり率は 2 つ並べて、どちらかとは決めない: 未判定を外れとして数えた値 = 当たり ÷ 枠、判定済みの枠だけで数えた値 = 当たり ÷(当たり + 判定済みの外れ)): 「全文に一致した枠」(その検索語でテキスト検索の順位がある画像)/ 見た目の経路だけの枠、
  クエリごとの全文一致の枚数(0 / 1〜9 / 10)、繰り返し画像の枠 / それ以外、全文 × 繰り返しの 4 通り。繰り返しは ① と同じ数え方(qrels のある検索語の中で 5 語以上)で決める。
- `compare` は、基準線と今回の両方に `top10.tsv` があるときだけ ① ② の前後を出す。順位の比較ではないので、終了コード 3 の判定には入れない。片方にだけあれば、注意を 1 行出す。

## 特徴ベクトル検索の補正の μ(`hubness-mean`。ADR 0051)

製品の検索は、日本語の文字を含み 8 文字以下の検索語で、特徴ベクトル検索の点から μ·r(r は画像のベクトル)を引く。μ は、参照の語の一覧(`hubness-reference-ja.txt`)のテキスト埋め込みの平均で、Core の生成ソース(`src/Ohanami.Core/Embedding/Generated/HubnessReferenceMean.g.cs`)に入っている。

```
# models/ が要る(テキスト側のモデルを CPU で動かす。DB・GPU・画像は使わない。411 語の埋め込みで約 81 秒。実行の内訳を出力する)
dotnet run --project tools/Ohanami.Bench -- hubness-mean [--reference <一覧>] [--models <dir>] [--out <生成する .g.cs>]
```

- 一覧は、測った 411 語のまま凍結してある。語・順序・空行・コメントのどれを変えても一覧の SHA-256 が変わり、通常のテスト(Core.Tests の `HubnessCorrectionTests`)が「生成物の SHA-256 が一覧と違う」で落ちる。変えたら、このコマンドで μ を作り直す。語を変えたときは、検索の動きが変わるので、変更の前の基準線と比べる(ADR 0037・0051)。
- 生成ソースには、作ったときの model_id・一覧の SHA-256・語数も入る。モデルを替えたときも、model_id が合わずに製品が起動で例外にする(`VisualPath`)ので、このコマンドで作り直す。
- 作り直した値と、リポジトリの生成ソースの一致は、`Category=Model` のテスト(`HubnessMeanModelTests`)が確かめる。
- `run` の summary は、補正の出どころ(一覧の SHA-256・語数・model_id)を 1 行で書く。

## 3 本目の試し測り(ADR 0035)

3 本目の経路(VLM の説明文のテキスト埋め込み。Ruri-v3-310m)を足した融合 `fused3` が、今の `fused` と比べてどうかを測る**試験用のコマンド**。製品のコードは変えない。

    dotnet run --project tools/Ohanami.Bench -- third-path --embeddings <dir> [--diagnostic-embeddings <dir>] [--classes <tsv>] [--label x] [--root ...]

- 入力: `--embeddings` = **主の入れ方**(説明文 + タグ。`desc-tags`)、`--diagnostic-embeddings` = 診断の入れ方(説明文だけ。`desc-only`。任意)。どちらも、Python(tools/third-path-trial/)が書いた `vectors.f32`・`index.tsv`・`meta.json`(`ThirdVectors`。書式は README の下の注と ThirdPath.cs の説明)。私物の説明文から作ったものなので、リポジトリの外(既定は `<root>/third-path`)。
  `--classes` = 今の外れの分類の割り当て。**測る前に、リポジトリの外のファイルに固定する**。形は 2 つ(1 行目で見分ける):
  (1) `set\tquery\tfilename\tclass`、(2) `set\tindex\tfused_rank_at_classification\tclass\tdoubtful`(ファイル名・クエリを含まない。`index` は集合の中の known-item の並びの 1 始まり。
  `fused_rank_at_classification` は分類を固定したときの融合の順位で、**今の融合の順位と一致しなければ止まる**=番号の数え方の取り違えを黙って通さない)。`doubtful` が 1 のものは分類を「A?」のように別の行にし、「(迷いの合計)」も出す。
  今の外れに割り当てが無ければ止まる(割り当ての行がある集合の中で)。割り当てに集合ごと出てこない集合(例: context)の外れは、止めずに「未分類」にする。使ったファイルの SHA-256 を要約に書く。
- 流れ: 製品の検索を全件で流し(`run` と同じ。走査・埋め込みは済みを再利用)、**先に、同じ `RankedPath.Rrf`(k = 60、重みなし)で今の 2 本(見た目・全文)を作り直して、製品の fused の順位・スコアと全クエリで一致することを確かめる**
  (一致しなければ止まる)。一致したら、3 本目(cosine の降順。同点は平均順位。説明文が空の画像は母集団に入らない)を足して `fused3` を作る。
  診断の埋め込みがあれば、同じ作りで `third-desc`・`fused3-desc` を作り、**known-item の順位の行だけ**を出す(precision・scores・判定には使わない。後知恵の診断)。主と診断が同じ入れ方(meta.json の variant)なら止まる。
- 出力(`<root>/runs/<時刻>-<label>/`): `known-items.tsv`・`precision.tsv`・`scores.tsv`(経路は 融合 `fused`・`visual`・`fulltext` に、`third` と `fused3` が増える。`compare` は、基準線に無い経路を比べずに注意に出す)、
  `summary.txt`(数字だけ: known-item ごとの順位の表、U・H3・G・L、precision@10 の Δ・上がった/下がったクエリ数・全文の返した件数の 3 層ごとの Δ・クエリごとの差からの対の標準誤差。診断があれば、「診断(後知恵)」の節に、known-item の順位と U・H3・G・L)、
  `third-path-known-items.tsv`(クエリ・ファイル名つき。私物の文字を含むので、リポジトリの外のままにする)。**判定・採否はしない**(数字を並べるだけ。書き分けは人が決める)。
- `index.tsv` の書式: 1 行目 `kind\tkey\trow`。kind は `doc`(key = 画像のハッシュ)か `query`(key = クエリの文字列。known-item は前後の空白を除き、qrels はそのまま)。同じ説明文の画像は同じ row。`vectors.f32` は行 × 次元の float32(リトルエンディアン)で、すべて長さ 1。
- 集合 `context`: spikes/S2-siglip2/known-items-context.tsv(任意。形式は known-items.tsv と同じ。`one-image` と同じ作り)。あれば `run` も `third-path` も、別の集合として測って要約に出す。

## 前提

- 画像はファイル名で指す(NFC)。フォルダが入れ子でファイル名が重なると測れない(例外で止める)。
- 全画像が走査・埋め込み済みであること(読めない・未対応・消えた・失敗があれば、黙って測らず止める)。
- qrels の読み込みと「未判定」の数え方は評価の記録と同じ規則(同じ組が食い違えば例外、ファイル名は NFC)。source 列は保持する(`Qrels.Render` で書き戻せる)。

## 判定ページ(評価の記録の `QrelsPage` の移植)

```
dotnet run --project tools/Ohanami.Bench -- judge        # 直近の run について、<root>/judge/judge-<時刻>-<id>.html を作る(OHANAMI_IMAGES_DIR が要る)
dotnet run --project tools/Ohanami.Bench -- judge --path fused3 <run のフォルダ>   # その経路(third-path の fused3 など)の上位 10 件だけから、未判定を集める
```

- run の上位 10 件(融合・各経路)のうち、**qrels に無い画像だけ**を、クエリごとに並べる。経路ごとの順位(「融合 2位 / 画像 7位 / 全文 3位」)と、known-item の目的の画像(枠)も出す。
  未判定が無ければページは作らない。
- 単体の HTML: 外部のスクリプト・スタイル・フォント・CDN を読まない。画像は元のファイルを `file:///` で参照するだけ(コピーしない)。出力は `--root`(リポジトリの外)。
- 当たりの画像にチェックを付ける(当たりが無いクエリは「当たり 0 件で確定」)。下の欄に qrels の形の TSV が出る(source は `judge-page:<時刻>`)ので、
  `spikes/S2-siglip2/qrels.tsv` の末尾に貼り足す。判定したクエリでは、表示されてチェックの無い画像は外れ(0)として書き出す。判定していないクエリは書き出さない。
- チェックの状態はブラウザの localStorage に残る(ページの識別子は候補の中身から決まるので、同じ内容で作り直しても残る)。貼り足したあとは `run` をやり直して、未判定が 0 になったか確かめる。

## vlm-check(製品の LlamaServer + VlmClient での実物の確認)

```
# GPU を使う(VRAM 約 6GB。ほかの GPU の処理を止めてから)。OHANAMI_RUNTIME_DIR(llama-server.exe の置き場)、OHANAMI_MODELS_DIR、OHANAMI_IMAGES_DIR が要る
dotnet run --project tools/Ohanami.Bench -- vlm-check --s1-run <spikes/S1-vlm/runs/qwen3vl-8b-q4km-tags-all> [--stage compare,tokens,big,formats] [--random 40] [--seed N] [--image-max-tokens N]
```

- 起動の前に、製品と同じ検証(モデル 2 ファイルの sha256、llama-server.exe の sha256 と `--version`)をする。起動は製品と同じ引数。
- `compare`: VLM の評価の samples.tsv の 33 枚 + 評価で成功した画像からの無作為の数十枚(seed で固定)を流し、`ocr`・`description`・`tags` が VLM の評価の run と完全一致する数を数える。差があれば終了コード 5、差の内容は `diffs.txt`。
- `tokens`: 文章のトークン数(`/tokenize` と、小さい画像の `prompt_tokens` からの逆算)と、VLM の評価の画像トークンの最大。
- `big`: 4K(3840×2160)の合成画像 1 枚の処理時間と VRAM のピーク。**VRAM は llama-server のプロセスごとの値**(性能カウンタ `GPU Process Memory(pid_*)\Dedicated Usage` を 200ms 間隔。下の「VRAM の測り方」)。足切り 6,656MiB の外なら終了コード 6。測れなかったら「測れなかった」で終了コード 7(GPU 全体の値には戻さない)。GPU 全体の値 − 起動前の値は参考に並べる(合否に使わない)。
- `formats`: PNG・JPEG・WebP・アルファつき PNG/WebP・EXIF の向きつき JPEG の合成画像を流し、WebP などをそのまま送ったときの応答も記録する。
- **llama-server を更新するときの手順**: `VlmCatalog` のビルドの定数を替える前に、新しい exe で `vlm-check` を実行し、差があれば内容を見て更新するか決める。
- 結果は `<root>/vlm-check/<時刻>-m<N>/`(`report.txt`、`compare.tsv`、`diffs.txt`、`synthetic/`)。VLM の評価の私物の画像の OCR を含むので、リポジトリには入れない(`--root` がリポジトリの中なら拒否)。
- Skia の日本語フォントの検索がカレントディレクトリに `ffcache*` というキャッシュのファイルを作ることがある。リポジトリの外のディレクトリで実行する。

## stage-count(スリープ・強制終了の確認のスクリプトが使う)

```
dotnet run --project tools/Ohanami.Bench -- stage-count --folder <場面のフォルダ> [--stage embed|ocr|caption] [--data <dir>]
```

フォルダの画像(現存するパス)のうち、段階が done のものの数を、数字だけの 1 行で出す(既定の段階 caption、既定の `--data` = `%LOCALAPPDATA%\Ohanami\sleep-check`)。`sleep-report` と同じ DB の読み方(読み取り専用。App が動いていてもよい)。
`tools/kill-app-when.ps1` と `tools/kill-llama-when.ps1` が「done が N 枚になったら」を待つのに、1 秒おきに呼ぶ(sqlite3 の CLI に頼らないため)。数えられないときは、メッセージを出して終了コード 1。

## VRAM の測り方(vlm-full の D、vlm-check の big)

llama-server の VRAM は、**プロセスごとの値**で見る: Windows の性能カウンタ `\GPU Process Memory(pid_<pid>_luid_…_phys_0)\Dedicated Usage`(WDDM のビデオメモリ管理が数えた、そのプロセスの専用メモリ)。
`nvidia-smi` のプロセスごとの値は WDDM では N/A になる。GPU 全体の `memory.used` の 200ms ピーク − 起動前、という測り方は、ほかのプロセスの分が混ざる: 通しの実測では、途中でブラウザで動画を見た分(310〜530MiB)が混ざって
6,783MiB になった(プロセスごとの値は 6,058MiB)。そのため、**合否はプロセスごとの値**で決め、GPU 全体の値は参考として並べるだけにしてある。

- 読み取りは `pdh.dll` の P/Invoke(NuGet を足さない。`System.Diagnostics.PerformanceCounter` は NuGet)。読み取り 1 回は約 0.1ms。200ms 間隔で、標本ごとに「いま動いている llama-server の pid」を引き直す(一時停止・再開や失敗で起動し直すと pid が変わる)。
  標本ごとに、見ているプロセスのうちの最大(起動し直しの途中で重なっても足さない)。読み取りは (pid, アダプタの luid) ごとの値で、**アダプタをまたいで足さない**。llama-server が 2 つ以上のアダプタで 0 でない値を持っていたら想定外なので、どちらも選ばず、D を「測れなかった」にして、アダプタごとの値を理由に並べる(0 のインスタンスが別のアダプタにあるだけなら競合ではない)。
- **測れなかったとき**(カウンタが読めない・例外が 1 回でもあった・llama-server が動いているのに値が一度も取れなかった・llama-server が動いているのを見なかった)は、D を「測れなかった」として報告に理由を出し、**GPU 全体の値には黙って戻さない**。
  値が取れたあとの取りこぼし(終了の直前・直後)は数えて報告するだけで、測れなかった扱いにはしない。始める前に 1 回読んでみて、読めなければ警告を出す(流すのは止めない。ほかの項目は有効)。
- `gpu-process-memory`: カウンタを 1 回読んで、(pid, アダプタ)ごとに一覧する。2 つ以上のアダプタで 0 でない値を持つプロセスも並べる(GPU は使わない)。読み取りが動くかの確認(`vlm-full`・`vlm-check` の前に実行するとよい)。読めなければ終了コード 7。
- 200ms より短いピークは取れない(`nvidia-smi` の方式と同じ限界)。
- 留意: このカウンタの値は、他のプロセスでは「いま VRAM に載っている量」と一致しないことがある(dwm が 8GB のカードより大きい 9.3GB と出た。同じアダプタの 1 つのインスタンスの値で、アダプタをまたぐ合計ではない)。llama-server は 6,056MiB で、`nvidia-smi` の `memory.used`(7,039MiB)− 他のプロセスの分(約 1,000〜1,100MiB)と合った(この確認は 1 回だけ)。実機の vlm-check big で、プロセスごとの値と GPU 全体の差を並べて見る。
- vlm-full の一時停止の確認 B の合否は GPU 全体 − 起動前 ≤ 700MiB。そのうえで参考として、(a) llama-server だった pid のインスタンスが残っていないか、(b) ベンチ自身(ORT の CUDA のセッション)の Dedicated Usage を report に並べる(合否には使わない。どの値なら解放と言えるかの線は、データを見てから決める)。

## 1030 枚の通し(GPU を約 3.3〜4.4 時間使う)

製品の経路(`OhanamiCore` の `Indexer` に `[Embed, Vlm]` を渡す。App と同じ)を、**利用者の実際のデータ(`%LOCALAPPDATA%\Ohanami\ohanami.db`)とは別の DB** に流して、VLM の評価の run と比べる。
App ではなくベンチから流すのは、計測(VRAM・RAM のピーク、一時停止の確認)と、合格の見方の自動判定を同じ手順にするため。

**必ず新しい root(新しい DB)で、走査 → 埋め込み → VLM を流す**(既定の `bench-full`。`import-s1` で VLM の評価を取り込んだ DB [`bench` の root] では流さない)。理由: 取り込みは全 1030 枚に
`stage_status(caption)` の行を作る(失敗の 16 枚は failed の行)ので、その DB で VlmRunner を始めても待ちは 0(`RetryFailed` で動くのは length の 16 枚だけ)。かといって、同じ DB で
`stage_status` を消して回すと、captions は `(model_id, hash)` で upsert されるので、取り込みの行が上書きされて、VLM の評価との比較の基準(取り込みの行)が消える。通しの DB と取り込みの DB は別にする。
VLM の評価との比較は、通しの DB の captions を **VLM の評価の run の JSON と、ファイル名で突き合わせる**(`captions-check`。取り込みの行とは比べない)。

時間の見込み: 3.2〜3.6 時間(VLM の評価の合計 3.32 時間から、length の 16 枚の分を差し替え)。再試行を重く見た見積もりは 1030 × 約 12 秒 ≈ 3.4 時間 + length の 16 枚の再試行(1 枚最長 3 回 × 60〜100 秒)で +0.5〜1 時間 = 3.9〜4.4 時間
(**「約 3.3 時間」は下限**)。夜間に流す前提で、合格線は 4.5 時間。

### 手順

1. 前提: GPU が空いている(`nvidia-smi` で VRAM が 1〜1.3GB の GPU 全体の値。App・ほかの llama-server・ベンチが動いていない)。**通しの間も、ほかで GPU を使わない(ブラウザの動画を含む)**: 一時停止の確認 B は今も「GPU 全体 − 起動前 ≤ 700MiB」で判定するので、ブラウザの分(310〜530MiB)が重なると、B が誤って不合格になりうる(D はプロセスごとの値なので影響しない)。環境変数 `OHANAMI_IMAGES_DIR`(spikes/images)、`OHANAMI_MODELS_DIR`、`OHANAMI_RUNTIME_DIR`。リポジトリの外のフォルダで実行する(Skia のキャッシュ)。スリープは製品の `SleepGuard` が抑止する(入れない。スリープの確認は、下の「スリープ・強制終了の手動確認の道具」)。
2. `vlm-full --preflight`: モデル 2 ファイルと llama-server.exe の検証と、走査だけ(GPU を使わない。約 20 秒)。DB は `%LOCALAPPDATA%\Ohanami\bench-full\db\bench.db`(`--root` で変更。利用者の実際のデータの場所は拒否する)。
3. `vlm-full`: 埋め込み(1030 枚)→ VLM。VLM の done が +100 枚になったら、**一時停止 → llama-server の終了と VRAM の解放の確認 → 再開を 1 回**。終わると「合格の見方(A〜E)」を自動で判定して表示する(終了コード: 0 = 全部合格、3 = 実行が Failed、6 = 不合格の項目がある、7 = 不合格はなく、D を測れなかっただけ)。途中で Ctrl-C すると、一時停止して終わる(同じコマンドをもう一度で、DB の続きから進む。その場合、一時停止の確認 B は行われない=実行を分けたことを報告に書く)。
4. `captions-check --s1-run <spikes/S1-vlm/runs/qwen3vl-8b-q4km-tags-all>`: DB の captions を、VLM の評価の JSON と全 1030 枚で比べる(取り込みの行ではなく評価の JSON。評価で失敗した 16 枚は、評価の再試行の run [dry 0.8 → rep 1.1] の結果と比べる)。合格の見方(1〜9)を自動で判定する。
5. ベンチの run(製品が作った captions での検索): `run --root %LOCALAPPDATA%\Ohanami\bench-full --label full-run` → `check-s2 --s2 <spikes/S2-siglip2/runs> --root %LOCALAPPDATA%\Ohanami\bench-full`。未判定が増えたら `judge --root …bench-full` で判定ページを作る。

### 合格の見方(結果を見る前に決めたもの。数字は VLM の評価の run から決まる)

通しの運転(`VlmFullLogic.Evaluate`):

| 項目 | 合格 | 見込み(根拠) |
|---|---|---|
| A. 完走 | VLM の待ち 0、最後の状態 Idle、埋め込みの失敗 0 | 1030 枚すべて done(1014 + 再試行で救える 16) |
| B. 一時停止と再開 | 一時停止した。15 秒以内に llama-server が終わる。VRAM が起動前 + 700MiB 以下に戻る。VLM の runs が 2 行以上 | 一時停止は kill、再開は新しい runs の行、という設計(ADR 0017・0028) |
| C. 合計の時間 | 4.5 時間以下(埋め込みを含む) | 見込み 3.2〜3.6 時間(VLM の評価の 3.32 時間 = 1030 × 約 12 秒に、length の 16 枚の差し替え: 評価は 4096 トークンで 1 枚約 70 秒の失敗、製品は max_tokens 2048 なので 1 回目の length は約 37 秒 + 2 回目で成功する 14 枚は 5〜19 秒 + 残り 2 枚は 3 回目まで)。再試行を重く見た見積もり 3.9〜4.4 時間(1030 × 12 秒 = 3.4 時間 + 再試行 +0.5〜1 時間。**「約 3.3 時間」は下限**)。合格線は、この見積もりの上限を含む 4.5 時間(結果を見る前に決めた) |
| D. VRAM のピーク | llama-server のプロセスごとの専用 VRAM(性能カウンタ Dedicated Usage)6,656MiB 以下。測れなければ「測れなかった」(不合格とは別。終了コード 7)。**足切りの値は、GPU 全体の 200ms ピーク − 起動前で決めたときのまま、測り方だけをプロセスごとの値にした**(理由は上の「VRAM の測り方」) | 6,060MiB 前後(通しの実測は 6,058。VLM の評価は GPU 全体 − 起動前で 6,096、vlm-check の 4K で 6,122)。GPU 全体 − 起動前は参考 |
| E. RAM のピーク | llama-server のプライベート 12,000MiB 以下、ワーキングセット 4,000MiB 以下 | 9,800 / 3,200MiB 前後(VLM の評価は 9,812 / 3,178) |

VLM の評価との比較(`CaptionsCheckLogic.Evaluate`):

| 項目 | 合格 | 見込み(根拠) |
|---|---|---|
| 1. 完走 | 未処理 0 | — |
| 2. 評価との一致 | 差あり 3 枚まで、製品だけ失敗 0 | **1030/1030 が完全一致**(vlm-check の compare で 73/73。温度 0・同じビルド・同じ GPU。評価の成功の completion_tokens の最大は 1860 で、製品の max_tokens 2048 に収まる) |
| 3. 試行の回 | 期待と違う画像 0 | 1 回目 1014、2 回目 14、3 回目 2(評価の再試行の run: dry 0.8 で 16 枚中 14 枚、残りの 2 枚は rep 1.1 で成功) |
| 4. length の失敗 | 最終的な失敗 2 枚まで、理由は length だけ | 0 枚(評価の実験では 16 枚とも救えた)。評価の 16 枚の失敗は、**再試行で全部救える**見込み |
| 5. 再試行で救えた数 | 2 回目 14 ±1、3 回目 2 ±1 | 14、2 |
| 6. 時間 | 1 回目で成功した画像の p50 が評価の 0.75〜1.25 倍、p90 が 1.25 倍以下 | p50 12.0 秒、p90 16.3 秒(評価) |
| 7. runs | VLM の行 2 以上、全部 ended_at あり、runtime_json に build 12110 | 一時停止・再開で 2 行 |
| 8. 再試行の重複なし | attempts > 1 の ocr・caption の行 0 | 0(一時停止は「今の 1 枚は確定してから止まる」) |
| 9. 段階の一致 | ocr の done = caption の done | 1030 = 1030 |

ベンチの run(5 の手順。`check-s2` と同じ許容): 融合の known-item(見た目)29/45 ±2、融合の precision@10 の下限 0.477 ±0.02(取り込み直後の実測 0.474)、全文単独 0.297 ±0.01、セリフ 全文単独 7/10(一致)・融合 8/10 ±1。
**全文単独の順位が評価の記録と完全一致する項目は、不合格になりうる**: 評価の記録は 1014 枚の索引だったが、通しは再試行で救えた 16 枚も索引に入る。文書の数と平均の長さが変わるので、BM25 の順位が少しずれうる(差は小さい順位のずれのはず)。
他の 5 つの許容が外れたら調べる。16 枚が新しく上位 10 件に入って未判定が増えたら、`judge` で判定する(未判定の分は下限に外れとして入るので、下限が下がって見える)。

外れたときの扱い: 基準は動かさない。差のある画像は `diffs.txt`(私物の OCR を含む。リポジトリの外)で内容を見て、原因を報告する。

## 長時間の手動確認(スリープ・強制終了)

確認する動作は ADR 0026(スリープ・強制終了のあとの llama-server の扱い)。本物の Windows のスリープ・本物の llama-server・GPU が要るので、自動のテストにはできない。
人が App を動かして場面をこなし、そのあと `sleep-report` が材料を集めて合否を出す。全体で約 1 時間(任意の場面を除くと約 45 分)。
GPU を VLM と埋め込みで使うので、**1030 枚の通しのような、GPU を長く使う作業が終わってから**行う。

道具は、`sleep-fixture`(場面ごとの合成画像のフォルダを作る)、`sleep-report`(材料を集めて、時系列と合否を出す。読むだけ)、`stage-count`(上の節。スクリプトが使う)と、`tools/` の PowerShell 4 本
(`sleep-on-llama-start.ps1`・`kill-app-when.ps1`・`kill-llama-when.ps1`・`kill-llama-after-health.ps1`)。
`sleep-report` の `--scenario` は `sleep-vlm`・`sleep-load`・`sleep-embed`・`kill-app-after-kill`・`kill-app-final`・`kill-server`・`cutoff-stopped`・`cutoff-recovered`。
DB は読み取り専用で開くので、App が動いていてもよい。App を `OHANAMI_DATA_DIR` で起動した場所を、`--data`(既定 `%LOCALAPPDATA%\Ohanami\sleep-check`)で指す。

### 何を確かめるか

| 確かめたいこと | 場面 |
|---|---|
| VLM の処理中にスリープして復帰しても、ジョブが最後まで流れ、画像が失敗にならない(復帰のあとは、次の画像の前に `/health` を確かめる) | 1(短い) |
| スリープが 300 秒を超えると、復帰の直後にタイムアウトで起動し直しになる(正常のはず。タイマーがスリープ中の時間を数えるかは**未確認**) | 1b(任意) |
| llama-server の読み込み中(`/health` 待ち)にスリープしたとき、起動の失敗が理由つきで画面に出て、押し直して回復できる | 2 |
| 埋め込み(ORT)の処理中にスリープして復帰しても、埋め込みが続く(復帰後の最初の Run が失敗しても、作り直して続く) | 3 |
| App を強制終了すると、llama-server が一緒に消える(VRAM が戻る)。起動し直して続きが流れる | 4 |
| llama-server だけを kill すると、起動し直して同じ画像をやり直す(failed にならない) | 5 |
| サーバの異常で failed になった画像が 3 枚続くと、ジョブが止まって理由が出る。「失敗をやり直す」で回復する | 6 |
| `powercfg /requests` の SYSTEM に、ジョブの実行中だけ Ohanami が出る | 0 |
| 休止状態(`shutdown /h`)のあとも続く | 7(任意) |

### 準備(1 回だけ)

1. GPU が空いている(`nvidia-smi` で VRAM が 1GB 前後)。ゲームなど、GPU を使う他のものは閉じる。
2. App と Bench をビルドする(`OHANAMI_RUNTIME_DIR` が要る)。`stage-count` を使うスクリプトがあるので、**Bench を先にビルドしておく**。

   ```
   dotnet build src/Ohanami.App
   dotnet build tools/Ohanami.Bench
   ```
3. フィクスチャ(合成画像。私物ではない)を作る。場面ごとに別のフォルダで、画像の内容は全部違う(フォルダを App に足すたびに、新しい待ちができる)。

   ```
   dotnet run --project tools/Ohanami.Bench -- sleep-fixture
   ```

   既定の出力は `%LOCALAPPDATA%\Ohanami\sleep-check-fixture\`。フォルダと枚数は、`sleep-embed` 1500、`sleep-vlm` 30、`sleep-vlm-long` 20、`sleep-load` 8、`hibernate` 20、`kill-app` 30、`kill-server` 24、`cutoff` 8
   (`--embed-count`・`--vlm-count` で変える)。各画像は 1280×720 で、「`SLEEP-VLM 0007`」のような番号つきの文字と日本語が描いてある。
   `sleep-report` は、VLM の OCR にファイル名の番号が含まれるかで、画像の取り違えや、失われた結果を調べる。
4. App は、**普段のデータと別の場所**で起動する(普段の DB・ログを混ぜない)。この確認では、いつもこの起動の仕方にする。途中で App を閉じて起動し直すときも、同じ場所で起動する。

   ```
   $env:OHANAMI_DATA_DIR = "$env:LOCALAPPDATA\Ohanami\sleep-check"
   pwsh tools/run-app-without-cuda-path.ps1
   ```

   データ(`ohanami.db`・`logs\`・`llama-server.pid`)が、`%LOCALAPPDATA%\Ohanami\sleep-check\` にできる。
5. スリープの種類を確かめる(管理者でなくてよい): `powercfg /a`。スタートメニューの「スリープ」が S3 か、ハイブリッドスリープ・モダンスタンバイが使えるかは、機種で違う。
6. フォルダは、その場面のときに App に追加する(先に全部足さない)。各場面は、前の場面のジョブが終わってから始める。
7. **場面の頭に、前の場面のフォルダを外す**(下)。「VLM を始める」は、待ちのある**全部の画像**を処理するので、前の場面のフォルダが残っていると、そのぶんが混ざる。
   特に場面 3 の `sleep-embed`(1500 枚)は、埋め込みだけ済んで、VLM が待ちのまま残る。外さずに次の場面で「VLM を始める」を押すと、1500 枚が VLM にかかり、その場面の確認にならない(数時間かかる)。

スクリプトで自動にできる場面は、2・4・5・6(下)。どのスクリプトも `-WhatIf` を付けると、止めたり眠らせたりせず、何をするかだけを出す(引数と待ち方を、前もって確かめられる)。
スクリプトの置き場の既定のデータの場所は `%LOCALAPPDATA%\Ohanami\sleep-check`(`-DataDir` で変える)。スクリプトを自動で試すテストは無い。

#### 場面の前に毎回やること

1. 前の場面のジョブが終わっている(進捗が「待機中」)。
2. 左のパネルで、前の場面のフォルダを選び、「外す」を押して OK(画像のファイルは消えない。外したフォルダの画像は、待ちから消える)。
3. 待ちの件数が、これから足す場面の枚数だけになることを確かめる。外し終わった時点で、進捗の VLM の行が `0 / 0 枚` に近い。次に場面のフォルダを追加して走査が済んだら、
   「埋め込み」と「VLM」の行の待ちが、その場面の枚数(場面 1 なら 30)と一致する。一致しなければ、前の場面の分が残っている(フォルダの一覧を見る)。

#### 場面の終わりに毎回やること

場面が終わったら(または止まったら)、ジョブが**終わった状態**で次を実行する(`<場面>` と `<フォルダ名>` は、各場面に書いてある)。
結果は画面に出て、`<データの場所>\sleep-check\` にも保存される。

```
dotnet run --project tools/Ohanami.Bench -- sleep-report --scenario <場面> --folder "$env:LOCALAPPDATA\Ohanami\sleep-check-fixture\<フォルダ名>"
```

- 対象の時間は、既定で直近 180 分(`--minutes` で変える。`--since 2026-01-02T01:00:00Z` のように UTC の時刻でも指せる)。**スリープの記録は、復帰のあとにしか Windows に書かれない**ので、必ず復帰したあとで実行する。
- 出力は、時系列(Windows のスリープ・復帰と、ログの関係する行)→ DB(その場面のフォルダの件数)→ 今の状態(llama-server・App・pid ファイル・VRAM)→ 判定。
  判定は `[合格]`・`[不合格]`・`[注意]`・`[情報]`・`[対象外]`。不合格があれば、終了コード 6。
- `[注意]` と `[情報]` は、合否ではなく、判断の材料。記録に残す。`[不合格]` が出たら、**やり直す前に**、データの場所とログをそのまま残す(直したあとで再現を見るため)。

### 場面

ログの時刻は UTC(`logs\ohanami-yyyyMMdd.log`)。llama-server のログ(`logs\llama-server-*.log`)は、書き込み中は一覧で 0 KB に見えることがある(NTFS の一覧の更新が遅れるだけで、開けば中身がある)。

#### 場面 0: `powercfg /requests`(約 5 分。場面 1 の前後に一緒にやってよい)

**管理者の** PowerShell で `powercfg /requests` を実行する(管理者でないと「管理者権限が必要」と出て、見られない)。次の 4 つの時点で見る。

1. ジョブを始める前: SYSTEM の欄に Ohanami が**出ない**。
2. VLM のジョブの実行中(進捗が動いているとき): SYSTEM の欄に Ohanami の項目(`Ohanami.App.exe` を指す PROCESS の行のはず。表示の形は**未確認**)が**出る**。
3. 「一時停止」を押して止まったあと: **消える**。
4. ジョブが終わったあと(待ちが無い状態): **出ない**。

合格: 2 だけに出て、1・3・4 では出ない(`SleepGuard` は、ジョブが動いている間だけ `ES_SYSTEM_REQUIRED` を持つ。画面は消えてよいので、DISPLAY には出ない)。

#### 場面 1: VLM の処理中のスリープ(約 8 分。`--scenario sleep-vlm`、フォルダ `sleep-vlm`)

1. 前の場面のフォルダを外す。App に `sleep-vlm` を「追加…」する(走査が終わるのを待つ。待ちが 30 枚になっている)。
2. 「VLM を始める」を押す。埋め込みが終わり(数秒)、「実行中: VLM」で枚数が増え始めたら、続ける。
3. 10 枚ほど進んだところで(1 枚 4〜6 秒なので、始まって 1 分弱)、スタートメニューから「スリープ」にする。
4. 約 1 分待つ(300 秒未満。300 秒を超えるなら場面 1b)。
5. キーボードかマウスで復帰する。**App を触らず**、そのまま完了まで見る(約 2 分)。
6. 完了したら、`sleep-report --scenario sleep-vlm --folder …\sleep-vlm`。

合格(`sleep-report` の判定が全部 `[合格]`): スリープの記録がある。スリープの時点で VLM のジョブが動いていた。復帰のあとに caption の確定が続いた。caption・ocr が 30 枚全部 done で、failed なし。llama-server が残っていない。OCR に番号が含まれる。
見るもの(`[情報]`): 復帰のあとの llama-server の扱い。`[注意]` が出るのは、「/health に答えた」の直後に「kill して起動し直す」が並んでいるとき。

ログの読み方: 復帰の直後に `スリープからの復帰の通知を受けた(PowerEvents)。…`(復帰の通知が届いたこと。届いていなければ `[注意]`)。
そのあと、次の画像の前に llama-server が生きていれば `スリープからの復帰のあと、llama-server は /health に答えた。そのまま続ける`。
リクエストの途中でスリープしたときは、リクエストの結果しだいで、何も出ない(そのまま成功)か、`llama-server を kill して起動し直す: <パス>: <理由>` が出る。

#### 場面 1b(任意): 300 秒を超えるスリープ(約 12 分。`--scenario sleep-vlm`、フォルダ `sleep-vlm-long`)

場面 1 と同じ手順を、フォルダ `sleep-vlm-long`(20 枚)で行い、**スリープを 6 分以上**にする(3 枚ほど進んだところでスリープ)。`sleep-report --scenario sleep-vlm --folder …\sleep-vlm-long`。
リクエストの途中でスリープしていれば、復帰の直後にタイムアウトで ServerFailure になり、`kill して起動し直す: …タイムアウト…` が出て、同じ画像をやり直す(failed にならない)はず。
`sleep-report` は、300 秒を超えたスリープについて、「タイムアウトで起動し直したか」を `[情報]` で出す。

合格の条件は場面 1 と同じ(20 枚全部 done、failed なし)。タイムアウトの起動し直しが出るかは、リクエストの途中でスリープしたかによるので、出なくても不合格ではない(情報として記録する)。
**300 秒未満のスリープで「タイムアウト」が出たら、不具合**(場面 1 で見る)。

#### 場面 2: llama-server の読み込み中のスリープ(約 5 分。`--scenario sleep-load`、フォルダ `sleep-load`)

読み込みの窓は短い(画面には「VLM を準備中…」と出る。起動に 10 秒前後で、**未確認の見積り**)。窓に確実に入れるために、次の順で行う。

1. 前の場面のフォルダを外す。App に `sleep-load`(8 枚)を追加し、走査を待つ(待ちが 8 枚)。
2. **先に「埋め込みを始める」を押し**、終わるのを待つ(8 枚。数秒)。これで、次に押す「VLM を始める」では、埋め込みの待ちが無く、すぐ llama-server の起動が始まる。
3. スクリプトで行う場合: 別の PowerShell で `pwsh tools/sleep-on-llama-start.ps1` を起動して待たせる。新しい llama-server が現れた瞬間(100ms 間隔の監視)に、すぐスリープさせる(1 回だけ)。
   **今動いている llama-server は無視し、新しく現れたものだけに反応する**。ただし、App のものとほかのものを見分けないので、ほかに llama-server が動いていない状態で使う。
   手で行う場合: 別の PowerShell を開き、スリープのコマンドを**入力しておく**(Enter はまだ押さない)。

   ```
   Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState([System.Windows.Forms.PowerState]::Suspend, $false, $false)
   ```

   (スクリプトも、同じ `SetSuspendState(Suspend)` を使う。これが S3 になるか休止状態になるかは、`sleep-report` の `EffectiveState` と `HiberPagesWritten` でしか確かめられない。意味は未確認。うまくいかなければ、スタートメニューの「スリープ」を使う。)
4. App の「VLM を始める」を押す。手で行う場合は、画面に「VLM を準備中…」が出たらすぐ、3 の PowerShell で Enter を押す。
5. 約 1 分後に復帰し、App を触らずに様子を見る。**結果は、次のどちらでもよい**。
   - そのまま起動できて、8 枚が流れる。
   - 起動に失敗し、画面に「VLM の準備に失敗: …」または「停止(失敗)」と、理由が出る。→ もう一度「VLM を始める」を押して、8 枚が流れる。
6. `sleep-report --scenario sleep-load --folder …\sleep-load`。

合格: スリープの時点で llama-server が読み込み中だった(起動の開始がスリープの前、起動の完了がスリープの後)。8 枚全部 done、failed なし(押し直した場合も含む)。llama-server が同時に 2 つ以上居ない。ジョブのあと llama-server が残っていない。
見るもの(`[情報]`): 「そのまま起動できた」か「起動に失敗した(理由)」か。**起動に失敗したときの理由が画面に出ていること**は、画面で確かめる(`sleep-report` は見ない)。
「窓に入らなかった」(スリープの前に起動が終わっていた)と出たら、不合格ではなく**やり直し**(`[不合格]` はその意味)。

#### 場面 3: 埋め込み(ORT)の処理中のスリープ(約 8 分。`--scenario sleep-embed`、フォルダ `sleep-embed`)

1. 前の場面のフォルダを外す。App に `sleep-embed`(1500 枚)を追加し、走査を待つ(約 1〜2 秒)。
2. 「埋め込みを始める」を押す。進捗が動き始めたら、20〜30 秒後にスリープする(埋め込みは 1 枚 約 0.1 秒で、全体で 2〜3 分かかる**見込み**。数十秒で終わるなら `--embed-count` を増やして作り直す)。
3. 約 1 分待って復帰し、App を触らずに完了まで見る。
4. `sleep-report --scenario sleep-embed --folder …\sleep-embed`。
5. **終わったら、`sleep-embed` のフォルダを必ず外す**(埋め込みだけ済んで、VLM が 1500 枚待ちのまま残っている)。

合格: スリープの時点で embed のジョブが動いていた。復帰のあとに embed の確定が続いた。埋め込みの失敗のログが無い。embed が 1500 枚全部 done、failed なし。llama-server が残っていない(もともと居ない)。
見るもの(`[情報]`): `ORT のセッションの作り直し`。復帰後の最初の Run が失敗して、エンコーダがセッションを作り直して成功したときは、ログに `画像のセッションを作り直した(Run の失敗から。累計 N 回。理由: …)` が出る。
作り直しが無い(復帰後の最初の Run が成功した)のも正常。作り直しても 2 回目で成功すれば、failed にも不合格にもならない。理由の文言は、記録しておく。
そのあと、検索(特徴ベクトル検索)が動くことも、画面で確かめる(適当な語を入れて、結果が出ればよい)。

#### 場面 4: App の強制終了(約 10 分。`--scenario kill-app-after-kill` → `kill-app-final`、フォルダ `kill-app`)

1. 前の場面のフォルダを外す(特に `sleep-embed`)。App に `kill-app`(30 枚)を追加し、待ちが 30 枚になっていることを確かめる。「VLM を始める」を押す。VLM が 5〜10 枚進んだところで、次の 2 を行う。
   スクリプトで自動にするなら、**「VLM を始める」の前に**、別の PowerShell で次を起動しておく。done が 8 枚になった瞬間に App を強制終了するので、2 は要らない(`-AppPid` は、動いている Ohanami.App がちょうど 1 つなら省ける)。

   ```
   pwsh tools/kill-app-when.ps1 -Folder "$env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-app" -Threshold 8
   ```
2. **強制終了する**。**タスクマネージャーの「プロセス」タブの「タスクの終了」は、強制終了にならない**。ウィンドウに閉じるよう頼むので、App は**正常に終了する**(ログに「終了」が出て、`runs` の `ended_at` が付き、pid ファイルが消える)。
   これでは、この場面の確認(強制終了で llama-server が道連れになるか、`ended_at` が空のままか)にならない。強制終了にするには、次のどれか。
   - 「詳細」タブで `Ohanami.App.exe` を選んで「タスクの終了」(確認が出たら「プロセスの終了」)。
   - PowerShell で `Stop-Process -Id <Ohanami.App の pid> -Force`。
   - 1 のスクリプト(中身は `Stop-Process -Force`)。
3. 10 秒待つ。`nvidia-smi` で VRAM が戻っていること(1GB 前後)も見る。
4. `sleep-report --scenario kill-app-after-kill --folder …\kill-app`(App のログに「終了」の行があり、`ended_at` が付いているなら、強制終了になっていない。やり直す)。
5. App を、同じデータの場所で**起動し直す**(準備の 4)。前回の llama-server が残っていれば、起動のときに掃除される。「VLM を始める」を押して、残りが流れるのを待つ。
6. 完了したら、`sleep-report --scenario kill-app-final --folder …\kill-app`。

合格(4): App が終わっている。**llama-server が一緒に消えた**(ジョブオブジェクト)。VRAM が戻った。
合格(6): VLM の runs が 2 つ以上。ログに「孤児として残っていた」が無い(あれば、ジョブオブジェクトが効かず、pid ファイルで片づいた。動作はするが、ジョブオブジェクトの確認は不合格のまま)。30 枚全部 done、failed なし。llama-server が残っていない。
期待どおりの `[情報]`: 強制終了した実行の `runs` の行は、`ended_at` が空(正常に止まらなかった印)。強制終了の直後は、pid ファイルが残っている。
強制終了のあとに、App が**起動し直せない・DB が壊れた**ときは、不合格より重い異常として、すぐ知らせる。

#### 場面 5: llama-server だけを kill(約 6 分。`--scenario kill-server`、フォルダ `kill-server`)

1. 前の場面のフォルダを外す。App に `kill-server`(24 枚)を追加し、待ちが 24 枚になっていることを確かめる。「VLM を始める」を押す。VLM が数枚進んだところで、次の 2 を行う。
   スクリプトで自動にするなら、**「VLM を始める」の前に**、別の PowerShell で次を起動しておく。done が 8 枚になったあと、App の pid ファイルに記録された llama-server を、1 回だけ kill して終わる。2 は要らない。

   ```
   pwsh tools/kill-llama-when.ps1 -Folder "$env:LOCALAPPDATA\Ohanami\sleep-check-fixture\kill-server" -Threshold 8
   ```
2. 「詳細」タブで `llama-server.exe` を選び、「タスクの終了」(または 1 のスクリプト)。**1 回だけ**にする(30 秒以上あけて 2 回目をやると、同じ画像が 2 回続けて落ちて、failed になることがある。設計どおりだが、この場面の対象ではない)。
3. App を触らずに、完了まで見る。
4. `sleep-report --scenario kill-server --folder …\kill-server`。

合格: 「kill して起動し直す」のログがある(復帰とは別の理由)。24 枚全部 done、failed なし(落ちた画像は、やり直して成功する)。llama-server が残っていない。

#### 場面 6: 連続のサーバ異常の打ち切り(約 8 分。`--scenario cutoff-stopped` → `cutoff-recovered`、フォルダ `cutoff`)

環境の問題(GPU が他で使われた・ドライバが戻らない)で、全画像が failed になり続けるのを防ぐ確認。**リクエストの途中で、llama-server を起動のたびに落とし続ける**。
そのためのスクリプトを使う(App の pid ファイルに記録された llama-server だけを kill する。ほかのプロセスには触らない)。

1. 前の場面のフォルダを外す。App に `cutoff`(8 枚)を追加する(待ちが 8 枚)。
2. **別の PowerShell で**、先にスクリプトを起動しておく(待っている状態になる)。App を、`%LOCALAPPDATA%\Ohanami\sleep-check` 以外のデータの場所で起動したときは、`-DataDir` で指す。

   ```
   pwsh tools/kill-llama-after-health.ps1
   ```
3. App の「VLM を始める」を押す。スクリプトが、llama-server が `/health` に答えて 1.5 秒後に kill することを繰り返す(画像 1 枚につき 2 回、3 枚で 6 回。**3 分前後**かかる。llama-server の起動に、毎回 10 秒前後かかる)。
4. 画面が「停止(失敗)」になり、理由に「llama-server が 3 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない」と出ることを確かめる。スクリプトは、新しい llama-server が 120 秒現れないと、自分で終わる(止まっていなければ Ctrl+C)。
5. `sleep-report --scenario cutoff-stopped --folder …\cutoff`。
6. 「失敗をやり直す」を押す(failed の 3 枚が待ちに戻って、そのまま始まる。**スクリプトは終わらせた状態で**)。8 枚が流れるのを待つ。
7. `sleep-report --scenario cutoff-recovered --folder …\cutoff`。

合格(5): error のログに「3 枚続けて異常」がある。ジョブが失敗として終わった。**failed がちょうど 3 枚**(理由は「同じ画像で 2 回続けた」)。4 枚目以降は記録がない(未処理 5 枚)。llama-server が残っていない。
合格(7): 8 枚全部 done、failed なし。
スクリプトが 1 回も kill しない、または 3 枚より前に止まるときは、`/health` への答えと 1 枚の処理時間(約 4〜6 秒)の関係を疑う(`-DelaySeconds` を 1〜3 で変える)。

#### 場面 7(任意): 休止状態(約 8 分。`--scenario sleep-vlm`、フォルダ `hibernate`)

場面 1 と同じ手順を、フォルダ `hibernate`(20 枚)で行い、スリープの代わりに `shutdown /h`(休止状態)にする。VRAM の内容が失われるので、復帰後の GPU の状態が S3 と違うかもしれない。
`sleep-report --scenario sleep-vlm --folder …\hibernate`。結果は場面 1 と同じ見方で、`EffectiveState` の番号と `HiberPagesWritten` を記録する(S3 のスリープの値と比べて、休止状態で何が変わるか。番号の意味を裏づける材料になる)。

### 観点の一覧

結果を見るときのチェック。合否は `sleep-report` に実装してある(判定項目の名前は、出力の `[合格]` などの行と同じ)。

| 観点 | 場面 | `sleep-report` の判定項目 | 合格の見方 |
|---|---|---|---|
| 復帰後、画像が失敗にならずに最後まで流れる | 1・1b・2・5 | `caption: 全部 done、failed なし`、`ocr: …` | 全部 done、failed 0 |
| スリープの時点でジョブが動いていた(確認として有効か) | 1・1b・3 | `スリープの時点で … のジョブが動いていた` | その時刻に動いていた runs がある。不合格は「やり直し」 |
| 復帰後に確定が続く | 1・3 | `復帰のあとに … の確定が続いた` | スリープ後に確定が増えた |
| 復帰後に「/health に答えた」の直後に「kill して起動し直す」が並ばない | 1・1b | `「答えた」の直後に起動し直しが並ばない` | 並ばない。`[注意]` が出たら、復帰後の `/health` の確認で足りない症例(ADR 0026 の見直す合図) |
| 300 秒超のスリープの直後に、タイムアウトで ServerFailure(正常) | 1b | `長いスリープ(300 秒超)のあとの扱い(情報)` | 情報。起動し直して同じ画像をやり直し、failed にならない |
| 読み込み中(`/health` 待ち)のスリープ | 2 | `スリープの時点で llama-server が読み込み中だった`、`読み込み中のスリープのあとの結果(情報)` | 起動できる、または失敗して、押し直しで回復する |
| 連続のサーバ異常の打ち切りが働く | 6 | `3 枚続けて異常で止まった`、`failed がちょうど 3 枚`、`4 枚目以降は記録がない` | 3 枚で止まる。「失敗をやり直す」で回復できる |
| 復帰後にドライバが戻らない場合にも、同じ打ち切りが働く | 1(起きたら) | 全体の `[不合格]` と、ログの「3 枚続けて異常」 | 起きたら、画面に理由が出て止まる。人為的には場面 6 |
| `powercfg /requests` の SYSTEM に、実行中だけ出る | 0 | (道具は見ない。目で) | 場面 0 |
| ORT(埋め込み)が復帰後に続く | 3 | `ORT: 埋め込みの失敗のログがない`、`embed: 全部 done、failed なし` | 全部 done、失敗のログなし。作り直しの有無と理由は `ORT のセッションの作り直し(情報)` |
| 強制終了で llama-server が一緒に消える | 4 | `llama-server が一緒に消えた(ジョブオブジェクト)`、`VRAM が戻った` | プロセスなし、VRAM 1GB 前後。消えなければ、ジョブオブジェクトの割り当ての失敗(ログの警告)を見る |
| 強制終了のあとの起動で、孤児の掃除が要らなかった | 4 | `前回の llama-server が孤児として残っていなかった` | ログに「孤児として残っていた」が無い。あれば `[注意]`(pid ファイルで片づいたが、ジョブオブジェクトは効いていない) |
| llama-server だけを kill → 起動し直して、同じ画像をやり直す | 5 | `llama-server の kill で起動し直した`、`caption: 全部 done、failed なし` | 起動し直しのログ、failed 0 |
| 後始末(止めたあと、プロセス・pid ファイルが残らない) | 全部 | `ジョブのあと llama-server が残っていない` | プロセスなし、pid ファイルなし。pid ファイルだけ残れば `[注意]`(次の起動の掃除が片づける) |
| 復帰の通知(`PowerEvents`)が届く | 1・1b(情報: 2・3) | `復帰の通知(PowerEvents)が届いた` | 復帰の直後に 1 回以上(通知が 2 種類あるので、2 回出ることがある)。届かなければ `[注意]`(登録できなかった=起動時の警告か、取りこぼし。HTTP の失敗での復旧は、通知が無くても働く) |
| 画像の取り違え・失われた結果がない | 1・2・5・6 | `OCR にファイル名の番号が含まれる` | 90% 以上(OCR の誤読で 1〜2 枚外れる)。下回れば、結果が別の画像に入っていないかを見る |

### 観測の限界

- 復帰の通知(`PowerEvents`)が届いたことは、ログに出る(`VlmRunner.OnResumed`)。ただし、通知を受けるのは `VlmRunner` だけで、Core に VLM を設定していないときは何も出ない(通常の App は設定する)。
  復帰のあとの `/health` の確認は、次の画像の前にしか行わないので、リクエストの途中でスリープして、そのまま成功したときや、復帰のあとに画像が残っていないときは、通知の行だけが出る。
- ORT のセッションの作り直しは、ログに出る(`EmbedRunner`)。1 枚ごとの Embed の直後に、増えた回数を見て出すので、作り直した画像の次の行に出る。理由は、元の Run の例外のメッセージ。
  作り直しの**前**に何があったか(復帰の通知との前後)は、時系列の時刻で見る。
- Windows の `EffectiveState` の番号の意味は未確認。`sleep-report` は番号のまま出し、ラベルは付けない。Microsoft の文書で見つかったのは `SYSTEM_POWER_STATE` の列挙(2 = S1、3 = S2、4 = S3、5 = S4 休止状態、6 = S5)だけで、
  Power-Troubleshooter のイベント 1 の `EffectiveState` / `TargetState` がこの列挙かを書いた文書は、見つからなかった。短い S3 のスリープで `4`・`HiberPagesWritten=0` だったのは、4 = S3 と矛盾しないが、裏づけではない。
  `sleep-report` は、スリープが 2 回以上あるとき、その場面の処理(フォルダの画像を処理した実行)と重なるものを判定に使い、使わなかったスリープも一覧に明記する。
- スクリプトのうち `sleep-on-llama-start` は、**どの新しい llama-server にも反応する**(App のものと見分けない)。
- App が出す診断ログに、一時停止を押したことが残る(`ジョブ: 一時停止`。開始・再開の `ジョブ: VLM を始める`・`ジョブ: 再開` と同じ形)。`sleep-report` の時系列にも載る(判定には使わない)。
- `sleep-report` の判定は、ログの文言(`VlmRunner`・`LlamaServer`・`Indexer`・`EmbedRunner`・`ChildProcessGuard`)に依存する。文言を変えたら、`SleepCheckLogic` の定数も直す。
- スリープの記録は、Windows のイベントログ(Microsoft-Windows-Power-Troubleshooter、ID 1)から `wevtutil` で読む。復帰の**あとに**書かれるので、復帰の前には見えない。
