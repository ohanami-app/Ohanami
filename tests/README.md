# テストの実行

リポジトリのルートで、プロジェクトごとに実行する(xunit.v3、Microsoft.Testing.Platform)。

| プロジェクト | 中身 | 要るもの |
|---|---|---|
| `tests/Ohanami.Core.Tests` | `Ohanami.Core` のテスト(DB、前処理、トークナイザ、検索、モデルの検証・ダウンロード、VLM の起動・停止、配布物の置き方の見張りなど)。正解データは `TestData/`(作り方は `tools/siglip2-reference/README.md`) | 通常の実行は、`models/` の `tokenizer.json` の 1 ファイルだけ(下) |
| `tests/Ohanami.App.Tests` | `Ohanami.App` のテスト。画面を出さずに測る。文言・色・窓の作り方の決まりを、ソースを読んで見張るテストを含む | `runtime/`(App のビルドが要る。下) |
| `tests/Ohanami.Bench.Tests` | `tools/Ohanami.Bench`(検索の回帰ベンチと採点)のテスト | `runtime/`(Bench のビルドが要る。下) |
| `tests/Ohanami.FakeLlamaServer` | テストではない。VLM の起動・停止のテストが使う、偽の llama-server(Core.Tests が参照して、ビルドする) | |

## 最初に 1 回: 要るファイルを置く

`runtime/` と `models/` のファイルは、Git に入っていない。次で取って置く(ネットワークに出るのは、このスクリプトだけ。取る先は、GitHub の llama.cpp のリリース、NVIDIA の配布元、Hugging Face の timm のリポジトリ)。

```
pwsh tools/runtime/Get-Runtime.ps1
```

- `runtime/llama.cpp/b12110/`(llama.cpp と CUDA の DLL)と `runtime/cudnn/9.28.0.42_cuda13/`(cuDNN)を取る(zip で約 1.9GB。置くのは約 1.2GB)。zip の SHA-256 と、置くファイルの SHA-256(`tools/release/allowed-files.tsv`)を確かめ、1 つでも違えば止まる。
- `models/ViT-SO400M-16-SigLIP2-384__ohanami/textual/tokenizer.json`(約 34MB)を取る。SHA-256 は `models/MANIFEST.tsv` と同じで、テストも、これで確かめる。
- VC++ ランタイムの 5 つの DLL(`runtime/vcruntime/14.44.35211_x64/`)は、取らない。Visual Studio 2022 または Build Tools の、再配布のフォルダから、表と SHA-256 が同じ物を探して写す。見つからなければ、取り方を案内して終了コード 1 で止まる。
- 置き場に、同じ名前で中身の違うファイルがあれば止まる(`-Force` で上書き)。そろっていれば、何も取らない。`-SkipRuntime`・`-SkipTokenizer`・`-SkipVc` で、一部を省ける(`Get-Help tools/runtime/Get-Runtime.ps1` に、オプションの一覧がある)。

`Ohanami.Core.Tests` の通常の実行だけなら、`-SkipRuntime -SkipVc` で足りる(トークナイザの 1 ファイルだけが要る。無ければ、テストは skip でなく**失敗**する)。`Ohanami.App`・`Ohanami.Bench` と、それらを参照するテスト(`Ohanami.App.Tests`・`Ohanami.Bench.Tests`)、ソリューション全体のビルドは、`runtime/` の全部が要る(`build/*.targets` が、1 ファイルずつ、無ければビルドを止める)。

## 実行

```
dotnet test --project tests/Ohanami.Core.Tests --filter-not-trait "Category=Model" --filter-not-trait "Category=Gpu"   # 通常の実行
dotnet test --project tests/Ohanami.App.Tests
dotnet test --project tests/Ohanami.Bench.Tests
```

`Ohanami.Core.Tests` には、`Category` の印が付いたテストがある。

```
dotnet test --project tests/Ohanami.Core.Tests                                        # すべて(下の 3 つを含む)
dotnet test --project tests/Ohanami.Core.Tests --filter-trait "Category=Model"        # モデルの実物を使うもの(約 2 分。CPU)
dotnet test --project tests/Ohanami.Core.Tests --filter-trait "Category=Gpu"          # GPU を使うもの(手元専用。下)
dotnet test --project tests/Ohanami.Core.Tests --filter-trait "Category=Private"      # 手元の画像を使うもの(手元専用)
```

| Trait `Category` | 内容 | 要るもの |
|---|---|---|
| (なし) | 通常のテスト。合成したデータで、仕組みが正しく動くかを見る(検索の精度は測らない) | `tokenizer.json`(上。無ければ**失敗**する。skip しない) |
| `Model` | モデルの実物(4.58GB)のハッシュ検証と、CPU での埋め込みが Python の参照と一致すること | `models/ViT-SO400M-16-SigLIP2-384__ohanami/` の 4 ファイル一式(下) |
| `Gpu` | 画像エンコーダ(GPU)の作成・自己検査・参照との一致・Dispose 後の VRAM、実物の llama-server の起動 | 下の環境変数と、空いている NVIDIA GPU。実物のモデル(Qwen3-VL。下) |
| `Private` | 手元の画像(私物。コミットしない)での前処理の補助確認 | 環境変数 `OHANAMI_PRIVATE_IMAGES`、`OHANAMI_PRIVATE_HASHES`。無ければ skip |

失敗と skip の使い分け: リポジトリから取れる、あるはずのもの(`tokenizer.json`)が無いときは、通ったことにせず失敗にする。そのマシンにしか無いもの(私物の画像・実物のモデル・GPU・時間の測定)を使うテストは、環境変数で選んだときだけ動き、選ばないと skip する。

### モデルの実物(`Model`・`Gpu`)

- SigLIP2(4 ファイル、約 4.6GB): アプリのセットアップの画面でダウンロードする(押したときだけ接続する)か、`tools/convert-siglip2/README.md` の手順で、元の重みから自分で変換する(Python。リポジトリの外の venv)。置き場は `models/ViT-SO400M-16-SigLIP2-384__ohanami/`。
- Qwen3-VL-8B-Instruct(GGUF と mmproj): アプリのセットアップの画面でダウンロードするか、`Qwen/Qwen3-VL-8B-Instruct-GGUF` から手で置く(取る版・ファイル名・SHA-256 は `src/Ohanami.Core/Models/VlmCatalog.cs`)。

## 環境変数

| 名前 | 意味 |
|---|---|
| `OHANAMI_MODELS_DIR` | `models/` の場所。未指定ならリポジトリのルートの `models/`。git の worktree はモデルのファイルを持たないので、元のリポジトリの `models/` を指す。`Get-Runtime.ps1` も、これを見る |
| `OHANAMI_RUNTIME_DIR` | `runtime/` の場所。未指定ならリポジトリのルートの `runtime/`。App のビルドと、GPU のテストのビルドで使う。`Get-Runtime.ps1` も、これを見る |
| `OHANAMI_GPU_TESTS` | `1` のとき、GPU のテストが動く。**ビルドにも効く**: `1` でビルドしたときだけ、テストの exe の隣に CUDA・cuDNN・VC++ ランタイムの DLL をコピーする(`build/OhanamiCudaRuntime.targets`) |

## GPU のテストの実行手順(手元専用。テストを回す CI は無い。GitHub Actions は、配布の zip を作るだけ: ADR 0048)

1. GPU を使う他のプロセス(llama-server など)を止める。VRAM は GPU 全体(`nvidia-smi`)で測るので、他のプロセスの変動が混ざる。
2. 環境変数を設定して実行する(PowerShell)。

```
$env:OHANAMI_GPU_TESTS = '1'
$env:OHANAMI_RUNTIME_DIR = 'C:\...\Ohanami\runtime'   # worktree のとき
$env:OHANAMI_MODELS_DIR  = 'C:\...\Ohanami\models'    # worktree のとき
dotnet test --project tests/Ohanami.Core.Tests --filter-trait "Category=Gpu" --output Detailed
```

`--output Detailed` で、テストが出す数字(作成時間、Run の時間、VRAM、自己検査の DLL の一覧)が表示される。
GPU のテストは `NativeRuntime.Configure` を呼ぶ(プロセス全体の DLL の検索パスを変える)。`Category=Gpu` だけを選んで動かすのが前提。
