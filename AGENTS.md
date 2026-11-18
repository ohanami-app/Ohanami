# Ohanami

Windows 向けのローカル完結型スクリーンショット検索アプリ。主な対象はゲームのスクショ。
画像ごとに OCR・説明文(VLM)・画像埋め込み(SigLIP2)を作り、セリフ・文脈・見た目のいずれでも検索できるようにする。
外部 API・クラウドは使わない。利用者への約束は次の文で、README・画面・ADR でもこの文のまま使う:

> 画像・検索語・インデックスは、PC の外に出ません。ネットワークに出るのは、モデルのファイルを取るときだけです。「ダウンロード」を押すと、Hugging Face にファイルを要求します (相手には、IP アドレスと、取ったファイルが伝わります)。モデルを手で置けば、一度も接続しません。

## 技術スタック

- .NET 10 / C#
- UI: WPF。XAML も Binding も使わず、すべてコードで組む(Web 系のフロントエンド、MAUI、WinUI は使わない)
- 画像埋め込み: SigLIP2 多言語モデルの ONNX を、ONNX Runtime(CUDA EP)でプロセス内実行する
- VLM: llama.cpp の `llama-server.exe` を同梱し、子プロセスとして起動・停止する(Ollama は使わない)
- 画像のデコード: SkiaSharp。リサイズは自前
- DB: SQLite(Microsoft.Data.Sqlite)
  - 全文検索: FTS5 + `unicode61`。日本語は C# 側で文字 bigram に分割してから投入する
  - ベクトル: sqlite-vec は使わず、普通の表に入れてメモリ上で総当たりする(1 万件で約 46MB)。件数が増えて必要になったら sqlite-vec に替える
- テスト: xunit.v3 を Microsoft.Testing.Platform で動かす(`global.json`)
- 対象環境: Windows 11 / NVIDIA RTX、VRAM 8GB 以上、RAM 16GB 以上(開発機は RTX 3070 8GB / RAM 64GB)

## ソリューションの構成

```
Ohanami.sln
src/
  Ohanami.Core/             ドメイン、インデックスのパイプライン、検索、DB アクセス。UI に依存しない
  Ohanami.App/              WPF のアプリ。Core を参照するだけ
tests/
  Ohanami.Core.Tests/       Core のテスト。正解データは TestData/
  Ohanami.App.Tests/        App のテスト。画面を出さずに測る。文言・色・窓の作り方の決まりを、ソースを読んで見張るテストを含む
  Ohanami.Bench.Tests/      tools/Ohanami.Bench のテスト
  Ohanami.FakeLlamaServer/  VLM の起動・停止のテストに使う、偽の llama-server
tools/
  Ohanami.Bench/            検索の回帰ベンチと採点
  convert-siglip2/          SigLIP2 を元の重みから ONNX に変換する Python(リポジトリの外の venv で動かす)
  siglip2-reference/        テストの正解データを作る Python(同上)
  runtime/                  ビルドとテストに要る runtime/ のファイルと tokenizer.json を、一次の配布元から取って置く PowerShell(Get-Runtime.ps1)
.github/workflows/          配布の zip を GitHub のランナーで作る・下書きの Release を公開するワークフロー(手で動かす。ADR 0048)
build/                      ビルドの共通の部品(CUDA の DLL を実行ファイルの隣に置く。Release の PathMap でビルドした PC のパスを DLL に残さない)
spikes/                     使い捨ての検証コード。ソリューションには入っていない
models/                     モデルのファイル。`MANIFEST.tsv` 以外は Git に入れない
runtime/                    同梱するバイナリ(CUDA・cuDNN・VC++ ランタイム・llama.cpp)。Git に入れない
```

UI と Core は必ず分離する。Core は `-windows` を付けない TargetFramework にしてあり、WPF / Windows UI 系の型を参照できない。参照しようとしないこと。

## 設計原則(厳守)

- **すべて明示的に**。DI コンテナの自動登録、リフレクション頼みのマジック、暗黙のデータバインディング規約に依存しない。
- **依存は最小限**。NuGet パッケージ・Python パッケージを足す前に、理由を説明してメンテナの承認を取る。
- **想定外は大声で落とす**。不明なスキーマ、ハッシュ不一致、未知のモデルの版などは握りつぶさず例外にする。黙って誤読するのが最悪。
- 推論結果は必ず「どのモデル・どのビルドで作ったか」を DB に記録する。
- 画像の識別子はパスではなく内容のハッシュ(ファイルのバイト列の SHA-256)。パスは属性。

## 作業ルール

- 不確かな外部仕様(llama-server のオプション、モデルの対応状況、ONNX の入出力の形など)は、推測で書かない。実際に確かめる。確かめられなければ TODO として明記する。
- 大きな変更の前には、方針を短く示して合意を取る。
- コメント・文書・コミット文は日本語、識別子は英語。

## 書き方の決まり

- コミット文・コメント・文書に、経緯を書かない。誰が承認したか、どの相談で決まったか、作業の段階の番号、決めた日付は、書かない。経緯は Git の履歴に残る。
- 決定と、その根拠(何を、何で、どう測り、どの線で決めたか)は、`docs/adr/` に ADR として書く。コードの理由は、そのコードの隣のコメントに書く。
- ADR は 1 決定 1 ファイル(`docs/adr/NNNN-短い英語の名前.md`)。節は「背景・決定・検討した案・根拠・結果・見直す合図」。状態は今有効なら「採用」、置き換えたら「置き換え(→ NNNN)」。前提を知らない人(半年後の自分を含む)が読んで分かるように書く:
  - 題は、決定を自然な日本語の 1 文で言う。作業の中だけの呼び名を題に使わない。
  - 背景には、使う人から見た問題を、作った例で書く。そのあとに技術の制約と代償を書く。
  - 作業の中で生まれた呼び名や略語は、最初に出たところで平たく説明する。検索の 2 つの方法は、画面の名前に合わせて「特徴ベクトル検索」と「テキスト検索」と呼ぶ。
  - 数字の表の前に、その指標が何を測るかを 1 行で書く。手元のデータで測った数字には、データを公開していないことを添える。
  - 同じ事実は 1 つの ADR に置き、ほかの ADR は番号で指す。
- 例は作る。手元のスクリーンショットの中身(セリフ・名前・クエリ・ファイル名)を、コード・テスト・文書・コミット文に書かない。テストの例や正解データは、作った文にする。
- 手元の評価のデータ(手元の画像とそこから作ったもの)は、直下の `.gitignore` の「スパイクの評価に使う手元のデータ」の節の、無視されている場所に置く。無視されているものを `git add -f` しない。

## ビルドとテスト

`runtime/` と、通常のテストに要る `models/` の `tokenizer.json` は、Git に入れていない。clone したら、最初に 1 回、`pwsh tools/runtime/Get-Runtime.ps1` で取って置く(SHA-256 を確かめる。詳しくは `tests/README.md`)。

ビルドは警告をエラーにする。

```
dotnet build Ohanami.sln -warnaserror
```

テストは xunit.v3 + Microsoft.Testing.Platform。リポジトリのルートで、プロジェクトごとに実行する。

```
dotnet test --project tests/Ohanami.Core.Tests
dotnet test --project tests/Ohanami.App.Tests
dotnet test --project tests/Ohanami.Bench.Tests
```

`Ohanami.Core.Tests` には、モデルの実物(`Category=Model`)と GPU(`Category=Gpu`)を使うテストがある。通常の実行はこの 2 つを除く。
絞り込み、環境変数、GPU のテストの手順は `tests/README.md`。

```
dotnet test --project tests/Ohanami.Core.Tests --filter-not-trait "Category=Model" --filter-not-trait "Category=Gpu"
```

| 環境変数 | 意味 |
|---|---|
| `OHANAMI_MODELS_DIR` | `models/` の場所。未指定ならリポジトリのルートの `models/`。git の worktree はモデルのファイルを持たないので、元のリポジトリの `models/` を指す |
| `OHANAMI_RUNTIME_DIR` | `runtime/` の場所。未指定ならリポジトリのルートの `runtime/`。App のビルドと、GPU のテストのビルドで使う |
| `OHANAMI_GPU_TESTS` | `1` のとき、GPU のテストが動く。ビルドにも効く: `1` でビルドしたときだけ、テストの実行ファイルの隣に CUDA・cuDNN・VC++ ランタイムの DLL をコピーする |

通常のテストは、合成したデータで仕組みが正しく動くかを見る。検索の精度は測らない。精度の評価は、手元のデータで `tools/Ohanami.Bench` と `spikes/` が行う。

## Python の範囲

- 製品とそのテストは、Python に依存しない。テストやビルドから Python を呼ばない。
- リポジトリの中の Python は、正解データを作る(`tools/siglip2-reference`)、モデルを変換する(`tools/convert-siglip2`)など、手で一度だけ動かす道具に限る。リポジトリの外の venv で動かす。venv をリポジトリに置かない。
