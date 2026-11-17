# 開発に加わる方へ

Ohanami をソースからビルドして、テストを回し、変更を出すための手引きです。アプリの使い方は [README.md](README.md) を見てください。

## 要るもの

| もの | 内容 |
|---|---|
| OS | Windows 11(64 ビット)。WPF のアプリなので、Windows 以外ではビルドできません |
| .NET SDK | `global.json` が決める版(10.0.403 以上の 10.0.4xx) |
| GPU | 通常のビルドとテストには要りません。アプリを動かす・GPU のテスト・検索のベンチには、NVIDIA の RTX 系(VRAM 8GB 以上)が要ります |
| `runtime/` | 同梱する DLL(CUDA・cuDNN・VC++ ランタイム・llama.cpp)。Git に入れないので、自分で置きます(下) |
| `models/` | モデルのファイル。Git に入れません。通常のテストが要るのは、トークナイザの 1 ファイルだけです(下) |
| Visual Studio 2022 17.14 系(Community か Build Tools。「MSVC v143 - VS 2022 C++ x64/x86 ビルド ツール」を入れる) | 同梱する VC++ ランタイムの 5 つの DLL を、その再配布のフォルダから写します(版 14.44.35211.0。SHA-256 が `tools/release/allowed-files.tsv` と同じ物) |
| PowerShell 7(`pwsh`) | `tools/` の補助のスクリプト(`Get-Runtime.ps1` など)を動かします。Windows に最初から入っている Windows PowerShell 5.1 では動きません |
| Python | 正解データの作り直しと、モデルの変換にだけ使います。製品・テスト・ビルドは Python を呼びません。リポジトリの外に venv を作ります(`tools/` の各 README) |

NuGet のパッケージは、ビルドのときに取れます(`nuget.org` に接続できること)。

clone したら、最初に 1 回、次を動かします。下の `runtime/` の 3 つと、通常のテストに要る `models/` のトークナイザを、一次の配布元から取り、SHA-256 を確かめて置きます(約 1.9GB を取ります。VC++ ランタイムだけは、Visual Studio 2022 の再配布のフォルダから写します。詳しくは [tests/README.md](tests/README.md))。

```
pwsh tools/runtime/Get-Runtime.ps1
```

下の 2 節は、このスクリプトが置くものの中身と、手で置くときの手順です。

### runtime/ を置く

リポジトリの直下に `runtime/` を作り、次の 3 つを置きます。置き場所を替えるときは、環境変数 `OHANAMI_RUNTIME_DIR` で `runtime/` に当たるフォルダを指します(git の worktree など)。

| 置き場 | 中身 | 取り方 |
|---|---|---|
| `runtime/llama.cpp/b12110/` | llama.cpp のビルド b12110 の、`llama-b12110-bin-win-cuda-13.4-x64.zip` と `cudart-llama-bin-win-cuda-13.4-x64.zip` の中身(2 つを同じフォルダに重ねて展開します) | https://github.com/ggml-org/llama.cpp/releases/tag/b12110 の Assets |
| `runtime/cudnn/9.28.0.42_cuda13/` | cuDNN 9.28.0.42(CUDA 13)の zip の `bin\` の DLL | `https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.28.0.42_cuda13-archive.zip`(約 1.2GB。zip の SHA-256 は `b1da81fc9b1e6196205a13ca59cba7b3a321d907c03e90a19d9665867d762137`) |
| `runtime/vcruntime/14.44.35211_x64/` | VC++ の再配布ファイル 5 つ(`msvcp140.dll`・`msvcp140_1.dll`・`msvcp140_atomic_wait.dll`・`vcruntime140.dll`・`vcruntime140_1.dll`。版 14.44.35211.0) | Visual Studio 2022 の `VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT\`。変更せずに置きます |

- 製品が使うのは、zip の中身のうち `tools/release/allowed-files.tsv`(1 行 1 ファイル。パス・SHA-256・出どころ)にあるファイルだけです。`Get-Runtime.ps1` は、その分だけを置きます。置いたファイルの SHA-256 は、この表と同じになるはずです。
- どれが要るかは、`build/*.targets` に 1 ファイルずつ書いてあります。足りないファイルが 1 つでもあると、ビルドが止まります(`OhanamiCudaRuntime.targets` など)。
- 版を替えるときの理由と決まりは、`docs/adr/` の 0015〜0017・0042・0044 にあります。
- `Get-Runtime.ps1` は、置き場ごとに、取得元と版を書いた `SOURCE.txt` を置きます。手で置くときも、同じように添えると、あとで見直せます。

### models/ を置く

- **通常のテスト**(Core.Tests)は、モデルの実物を使いません。トークナイザの 1 ファイル(約 34MB)だけが要ります。`models/ViT-SO400M-16-SigLIP2-384__ohanami/textual/tokenizer.json` に置きます。このファイルは、変換の入力(`timm/ViT-SO400M-16-SigLIP2-384` のコミット `fec784dabb3081a5f101fc74eefaf9d1ed08237b`)の `tokenizer.json` と、バイトまで同じです。

  ```
  New-Item -ItemType Directory -Force models\ViT-SO400M-16-SigLIP2-384__ohanami\textual
  curl.exe -L -o models\ViT-SO400M-16-SigLIP2-384__ohanami\textual\tokenizer.json https://huggingface.co/timm/ViT-SO400M-16-SigLIP2-384/resolve/fec784dabb3081a5f101fc74eefaf9d1ed08237b/tokenizer.json
  ```

  置いたファイルは、`models/MANIFEST.tsv`(Git に入っています)の SHA-256 で、テストが確かめます。

- **アプリを動かす・モデルの実物のテスト・GPU のテスト・検索のベンチ**には、2 組のモデルが全部要ります。アプリを起動すると、モデルが無いときに、セットアップの画面が出ます(ダウンロードするか、手で置きます。README の「最初の起動」)。
  - 特徴ベクトル検索のモデル(SigLIP2): アプリのセットアップの画面でダウンロードするか、`tools/convert-siglip2/README.md` の手順で、元の重みから自分で変換します(Python)。アプリは、`models/MANIFEST.tsv` と SHA-256 が合わないモデルを使いません。開発機以外の PC で変換して、同じバイトになるかは、確かめていません。
  - テキスト検索のモデル(Qwen3-VL-8B-Instruct): アプリのセットアップの画面でダウンロードするか、`Qwen/Qwen3-VL-8B-Instruct-GGUF` から手で置きます(取る版・ファイル名・SHA-256 は `src/Ohanami.Core/Models/VlmCatalog.cs`)。
  - 置き場は、既定ではアプリのフォルダから親へたどって最初に見つかる `models/<モデルのフォルダ名>` で、無ければ `%LOCALAPPDATA%\Ohanami\models\` です。環境変数 `OHANAMI_MODELS_DIR` で、`models/` に当たるフォルダを指せます(`src/Ohanami.App/AppPaths.cs`)。

## ソリューションの構成と共通の設定

構成の一覧は [AGENTS.md](AGENTS.md) にあります。次の約束があります。

- **Core は UI に依存しない**(ADR 0002)。`Ohanami.Core` の TargetFramework は `net10.0`(`-windows` を付けない)で、`UseWPF` も `UseWindowsForms` も付けません。WPF の型を書くと、コンパイルが通りません(1 段目)。そのうえで、`CoreDependencyTests` が、Core のアセンブリが WPF・WinForms などの UI のアセンブリを参照していないこと(2 段目)と、csproj に `-windows` の TargetFramework や UI の指定が無いこと(3 段目)を確かめます。
- **共通の設定は、`src/Directory.Build.props` と `tests/Directory.Build.props` にあります**。`Nullable` を有効、`ImplicitUsings` を無効(`using` は書きます)、`LangVersion` は `latest`、`TreatWarningsAsErrors` を有効にしています。警告を抑える `NoWarn` は入れません。直下に 1 つ置かず、`src/` と `tests/` に分けているのは、`spikes/`(使い捨ての検証コード。ソリューションに入っていない)のビルドの設定を変えないためです。`src/` の Release のビルドは、リポジトリのルートの絶対パスを `/_/` に置き換えます(`build/OhanamiPathMap.props` の `PathMap`。配る DLL・exe に、ビルドした PC のパスを残さないため)。Debug は置き換えないので、F5 とスタックトレースの行は、実際のパスのままです。
- **SDK は `global.json` で固定します**(`latestPatch`。パッチの版までは進みます)。警告をエラーにしているので、新しい SDK のアナライザで警告が増えると、ビルドが落ちるためです。SDK の版を上げるときは、増えた警告を直してから上げます。`global.json` には、`dotnet test` が Microsoft.Testing.Platform で動くようにする指定(`test.runner`)も入っています。
- **テストは xunit.v3 + Microsoft.Testing.Platform** です。`Microsoft.NET.Test.Sdk` と `xunit.runner.visualstudio` は参照していません(外しても、`dotnet test` で動くことを確かめました)。VSTest 経由で動かす道具(Rider など)で動かすには、この 2 つが要るかもしれません。その場合の設定は、未確認です。

## ビルドとテスト

ビルドは警告をエラーにします。テストは、リポジトリのルートで、プロジェクトごとに実行します。

```
dotnet build Ohanami.sln -warnaserror
dotnet test --project tests/Ohanami.Core.Tests --filter-not-trait "Category=Model" --filter-not-trait "Category=Gpu"
dotnet test --project tests/Ohanami.App.Tests
dotnet test --project tests/Ohanami.Bench.Tests
```

環境変数の表と、`Category` ごとの絞り込みは、[tests/README.md](tests/README.md) にあります。

| 対象 | 要るもの |
|---|---|
| `Ohanami.Core` のビルド、`Ohanami.Core.Tests` の通常の実行 | `models/` のトークナイザの 1 ファイルだけ(`runtime/` は要りません) |
| `Ohanami.App`・`Ohanami.App.Tests`・`Ohanami.Bench.Tests`・`tools/Ohanami.Bench` のビルド、ソリューション全体のビルド | `runtime/`(上の 3 つ) |
| `Category=Model` | SigLIP2 の 4 ファイル(約 4.6GB)。CPU で動きます |
| `Category=Gpu` | GPU・`runtime/`・SigLIP2・環境変数 `OHANAMI_GPU_TESTS=1`(手順は `tests/README.md`) |
| `Category=Private` | 手元の画像。環境変数 `OHANAMI_PRIVATE_IMAGES`・`OHANAMI_PRIVATE_HASHES` が無ければ、飛ばされます |

## 設計の原則と書き方

AGENTS.md の「設計の原則」と「書き方の決まり」を読んでください(人にも読んでもらう前提で書いてあります)。要点だけ挙げます。

- UI と Core を分ける。XAML・Binding・DI コンテナは使わず、画面はすべてコードで組む。
- 依存(NuGet・Python のパッケージ)を足す前に、理由を説明して、メンテナの承認を取る。
- 想定外は握りつぶさず、例外で止める。
- コミット文・コメント・文書は日本語、識別子は英語。経緯(誰が承認したか、どの相談で決まったか、作業の段階の番号、決めた日付)は書かない。例は、作った例を使う(手元のスクリーンショットの中身を書かない)。
- 画面の語は、`docs/adr/0038-wording-principles.md` の表に合わせる。語を替える・足すときは、その表も直す。

決定とその根拠は、`docs/adr/` に 1 決定 1 ファイルで書いてあります。決めたこと(使うモデル、検索の作り、画面の語、配布の形など)を変えるときは、先に ADR を読み、新しい決定は ADR を書いてください(書き方は AGENTS.md の「書き方の決まり」)。大きな変更の前には、方針を短く書いて、合意を取ってください。

## 検索の精度の評価

検索の精度は、通常のテストでは測りません(通常のテストは、合成したデータで、仕組みが正しく動くかを見ます)。精度は `tools/Ohanami.Bench`(回帰ベンチと採点)と `spikes/`(使い捨ての検証コード。ソリューションには入っていません)で測ります。これらは、**手元の画像のデータで回すもの**で、そのデータは公開していません。

自分の画像で回すには、次のデータを自分で作ります(置き場は、直下の `.gitignore` の「スパイクの評価に使う手元のデータ」の節の、無視されている場所です。無視されているものを `git add -f` しないでください)。

- 画像のフォルダ(環境変数 `OHANAMI_IMAGES_DIR`。読み取りだけです)。
- 探したい 1 枚と検索語の組(known-item): `spikes/S2-siglip2/known-items.tsv`。UTF-8・タブ区切り。1 行目に `filename`・`query_ja`(必須)と `query_en`(空でもよい)。
- 検索語ごとの当たり・外れの判定(qrels): `spikes/S2-siglip2/qrels.tsv`。1 行目は `query_ja<タブ>filename<タブ>relevant<タブ>source`(`relevant` は 1 か 0)。判定は、ベンチの `judge` コマンドが作る HTML のページで、チェックを付けて作れます。

使い方と、測るもの・出力は、`tools/Ohanami.Bench/README.md` にあります。ベンチの数字は、基準線と比べて、変更が下げていないことを確かめるためのものです。数字を上げるために回しません(少数の偏った検索語に合わせ込んでしまうため)。精度の数字を `docs/adr/` に書くときは、「開発者の手元の画像で測った。データは公開していない」と添えます。

## 配布物の作り方

zip の作り方と、入ってよいファイルの表の更新は、[tools/release/README.md](tools/release/README.md) にあります。
