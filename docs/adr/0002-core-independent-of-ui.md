# 0002. 検索・インデックス作成・DB の本体(Core)は、画面(WPF)に依存させない

状態: 採用

## 背景

インデックス作成・検索・DB アクセスの本体(以下 Core)と、WPF の画面を同じ層に混ぜると、画面の都合が本体に流れ込み、本体のテストに画面が要るようになる。たとえば「検索結果の順位がおかしい」という不具合を直すとき、画面を起動せずにテストで再現したいが、画面と混ざっているとそれができない。画面の基盤を替えたくなったときの費用も大きくなる(0001)。

## 決定

アプリを 2 つのプロジェクトに分ける。

- `src/Ohanami.Core`: 画像・フォルダ・結果などの扱い、インデックス作成の一連の処理(フォルダの走査 → 特徴ベクトルと VLM の結果の作成)、検索、DB アクセス。`net10.0` で、`-windows` を付けず、`UseWPF`・`UseWindowsForms` も付けない。WPF や Windows の UI 系の型を参照できない。
- `src/Ohanami.App`: WPF の画面。Core を参照するだけで、NuGet の直接の参照は持たない。

Core の公開は、`OhanamiCore` とそれが持つサービス(フォルダ・インデクサー・検索・サムネイル・手動タグなど)に限り、それ以外の型は `internal` にする。App のテストと Core のテストだけが `InternalsVisibleTo` で内部に触れる。

Core から画面への通知は、その時点の状態を写した変更不可の値(不変の `record`)を渡す `event` で行う。Core は WPF の `Dispatcher` に触れない。イベントはスレッドプールのスレッドで呼ばれるので、UI のスレッドへの受け渡しは App の仕事である。公開 API はどのスレッドから呼んでもよく、時間のかかる操作は `Task` と `CancellationToken` を取る。

## 根拠

分離は、約束ではなく仕組みと検査で守る。3 段である。

1. Core のターゲットが `net10.0` なので、`Microsoft.WindowsDesktop.App`(PresentationFramework など)を参照するとコンパイルが通らない。
2. `tests/Ohanami.Core.Tests/CoreDependencyTests.cs` が、Core のアセンブリの参照一覧に PresentationFramework・PresentationCore・WindowsBase・System.Windows.Forms・System.Drawing などが無いことを確かめる。
3. 同じテストが、`Ohanami.Core.csproj` に `-windows`・`UseWPF`・`UseWindowsForms`・`UseWinUI` が無いことを XML として確かめる。

1 段目はコンパイラが保証する。2・3 段目は、プロジェクトの設定を誰かが変えたときに落ちる。

## 結果

- Core のテストは、画面を出さずに書ける。App 側のテストは、Core の内部の入口(偽の Core を開く入口)を使って、画面だけを測る。
- 画面の都合を Core に足したくなったときは、Core の公開面を増やす判断を、そのたびに明示的にすることになる。
- 公開のサービスが増えると、`OhanamiCore` の組み立て(`OpenAsync`)が長くなる。組み立ては明示的に書く方針(DI コンテナを使わない)である。

## 見直す合図

UI を別の基盤に替えるとき(0001 の見直す合図)。そのとき、Core の公開面を変えずに画面だけを作り直せたかが、この分離の成否の目安になる。
