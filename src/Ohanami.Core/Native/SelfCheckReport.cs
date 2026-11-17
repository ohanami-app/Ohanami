using System.Collections.Generic;

namespace Ohanami.Core;

/// <summary>プロセスにロードされているネイティブ DLL 1 つ(名前・フルパス・ファイルの版)。</summary>
public sealed record LoadedModule(string Name, string Path, string? Version);

/// <summary>
/// GPU の自己検査の結果(ADR 0015)。画像エンコーダを初めて作るとき、最初の 1 枚を書く前に行う。
/// <see cref="Modules"/> は検査の対象になった DLL(cu* / nv* / onnxruntime*)。<see cref="Violations"/> が空なら <see cref="Passed"/>。
/// </summary>
public sealed record SelfCheckReport(
    bool Passed,
    string OrtVersion,
    IReadOnlyList<LoadedModule> Modules,
    IReadOnlyList<string> Violations);
