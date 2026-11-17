namespace Ohanami.Core.Models;

/// <summary>
/// VLM に送るスキーマ(<c>response_format</c> の <c>json_schema.schema</c>)。「実際に送る形」= 空白なしの正準 JSON(整形つきのスキーマの JSON を読み込んで
/// <c>JsonNode.ToJsonString()</c> で書き出した文字列と同じ。キーの順は元のまま)。ADR 0022。<c>ocr</c> → <c>description</c> → <c>tags</c> の順に書かせる。
/// 変えると <see cref="VlmSpec.SchemaSha256"/> が変わり、新しい model_id になる(全件が要再作成)。一致はテストで固定している。
/// </summary>
internal static class VlmSchema
{
    public const string Json = """{"type":"object","properties":{"ocr":{"type":"string"},"description":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}},"required":["ocr","description","tags"],"additionalProperties":false}""";
}

/// <summary>
/// VLM の構成(静的な定義。<see cref="ModelCatalog"/> の VLM 版)。評価で決めた最終構成(ADR 0018)を指す:
/// Qwen3-VL-8B-Instruct Q4_K_M(公式 GGUF)+ mmproj F16。値(サイズ・sha256・リビジョン)は models/MANIFEST.tsv の
/// <c>Qwen/Qwen3-VL-8B-Instruct-GGUF</c> の行と同じで、テストで MANIFEST と突き合わせる。モデルを替えるときは、ここを意図して書き換える
/// (新しい model_id になり、全件が要再作成になる)。
/// </summary>
internal static class VlmCatalog
{
    /// <summary>models/ の下のディレクトリ名。開発中のモデルの置き場は <c>models/&lt;この名前&gt;</c>(<c>OHANAMI_MODELS_DIR</c> の方式)。</summary>
    public const string DirectoryName = "Qwen3-VL-8B-Instruct-GGUF";

    /// <summary>models.name に入れる名前。</summary>
    public const string Name = "Qwen3-VL-8B-Instruct Q4_K_M + mmproj F16";

    /// <summary>
    /// 画面に出す名前(「どのモデルで作ったか」を見せる。<see cref="OhanamiCore.ModelLabels"/> で App に渡す。ADR 0038)。量子化(Q4_K_M)・mmproj は添えない。
    /// モデルを替えるときは、定義(<see cref="Name"/>・<see cref="HfRepository"/>)と一緒にここも書き換える(ModelLabelsTests が、定義と食い違っていないかを見張る)。
    /// </summary>
    public const string DisplayName = "Qwen3-VL-8B-Instruct";

    /// <summary>model_id の接頭辞。</summary>
    public const string Family = "qwen3vl-8b-instruct-q4km";

    /// <summary>Hugging Face のリポジトリとコミットリビジョン(取得の固定用。ADR 0020)。</summary>
    public const string HfRepository = "Qwen/Qwen3-VL-8B-Instruct-GGUF";

    public const string HfRevision = "f982a07559d4a2f6c8744d840bf6fccab30eea96";

    public const string GgufFileName = "Qwen3VL-8B-Instruct-Q4_K_M.gguf";

    public const string MmprojFileName = "mmproj-Qwen3VL-8B-Instruct-F16.gguf";

    /// <summary>temperature(評価で決めた最終構成: 0。ADR 0018)。</summary>
    public const string Temperature = "0";

    /// <summary>
    /// <c>--image-max-tokens</c> の値。**確定**(ADR 0025): 開発者の手元の 1030 枚の画像トークンの最大は 2720(= usage.prompt_tokens の最大 3256 −
    /// 文章のトークン数 536)で、これ以上なので手元のどの画像にも効かない。4K(3840×2160)の画像は、画像トークン 3476・1 枚約 100 秒・VRAM はプロセス分 6,122MiB
    /// (足切り 6,656MiB の内側。ADR 0019)。model_id に入るので、**変えると全件が要再作成になる**。
    /// </summary>
    public const int ImageMaxTokens = 3584;

    /// <summary>
    /// llama-server の起動引数のうち、パスとポート(<c>-m</c>、<c>--mmproj</c>、<c>--host</c>、<c>--port</c>)を除いた、評価で決めた最終構成(ADR 0019)。
    /// <c>--image-max-tokens</c> と <c>--no-webui</c> は、製品が足す(評価の構成には無い)ので、ここには入れない。起動はこれを使い、
    /// 評価の出力の取り込みは、評価の run の <c>server_args</c> がこれと同じことを確かめる。
    /// </summary>
    public const string LaunchFlags = "-lm none -ngl 99 -c 8192 -np 1 -cram 0 --no-mmproj-offload";

    // ---- llama-server(model_id には入れない。runs.runtime_json の記録と、起動時の検査に使う)

    /// <summary>llama.cpp のビルド番号(<c>llama-server --version</c> の <c>build 12110, commit cd959bae9</c>)。</summary>
    public const int LlamaServerBuild = 12110;

    public const string LlamaServerCommit = "cd959bae9";

    /// <summary>llama-server.exe(<c>runtime/llama.cpp/b12110/</c>)の SHA-256。起動時の検証で使う(ADR 0020)。</summary>
    public const string LlamaServerExeSha256 = "00d50686815af38249d0218c91558fbd0e9093ac8c074bac86196907f4305e31";

    /// <summary>評価で決めた最終構成の VLM(ADR 0018)。</summary>
    public static VlmSpec Current { get; } = new(
        Family,
        new VlmFile(GgufFileName, "67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2", 5027784800),
        new VlmFile(MmprojFileName, "ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38", 1159029824),
        promptSha256: VlmSpec.PromptSha256Of(VlmPrompt.Text),
        schemaSha256: VlmSpec.SchemaSha256Of(VlmSchema.Json),
        temperature: Temperature,
        chatTemplateKwargs: "",
        imageMinTokens: null,
        imageMaxTokens: ImageMaxTokens,
        hfRepository: HfRepository,
        hfRevision: HfRevision);
}
