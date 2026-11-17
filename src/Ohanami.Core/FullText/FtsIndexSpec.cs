using System;
using System.Text;

namespace Ohanami.Core.FullText;

/// <summary>索引の状態(<see cref="FtsIndex"/>)。<c>VectorTableState</c> と同じ形。</summary>
internal enum FtsIndexState
{
    /// <summary>まだ規約を照合していない。</summary>
    NotChecked,

    /// <summary>規約が DB と違う(または fts_docs が無い)ので、captions から作り直している(全文の経路は NotReady を返す)。</summary>
    Rebuilding,

    /// <summary>規約が一致し、検索に使える。</summary>
    Ready,

    /// <summary>作り直しに失敗した(理由は <see cref="FtsIndex.FailureMessage"/>)。</summary>
    Failed,
}

/// <summary>
/// 索引の規約(<c>index_meta['fts_spec']</c> に保存する文字列と、<c>fts_docs</c> の形を決めるもの)。**索引の中身を決める規則はすべて入れる**:
/// tokenize の指定、列の構成、正規化の規則、分割の規則、区切り、文書の選び方、制御文字の置換の規則。どれかを変えたら、対応する版(または値)を変えると、
/// 開くときに規約が DB と違うと判って、索引が作り直される(上げ忘れると、索引と検索の分割がずれて、例外が出ないまま検索が壊れる)。
/// <para>
/// 列の構成は **1 列(<c>tokens</c>。文書全体を連結)**。3 列(ocr・説明文・tags)と比べて 1 列に決めた(ADR 0030):
/// 開発者の手元の約 1,000 枚の取り込みで、known-item・precision@10・セリフのどれも**まったく同じ結果**だった(FTS5 の BM25 は、列の重みが既定のままなら、列をまたいだ
/// トークン数の合計で計算するので、列を分けても順位は変わらない。列ごとの重みは、過適合の入口になるので調整しない)。3 列のコードは、判定のあとに消した。
/// </para>
/// </summary>
internal sealed record FtsIndexSpec
{
    /// <summary>現行の規約。</summary>
    public static FtsIndexSpec Current { get; } = new();

    /// <summary>index_meta のキー。</summary>
    public const string MetaKey = "fts_spec";

    /// <summary>fts_docs の表の名前(FTS5 の仮想表。影の表は <c>fts_docs_data</c> など)。</summary>
    public const string TableName = "fts_docs";

    /// <summary>fts_docs の列の名前(1 列)。</summary>
    public const string Column = "tokens";

    /// <summary>列の構成を表す、規約の中の文字列(1 列)。</summary>
    public const string Layout = "single";

    /// <summary>
    /// 正準文字列(LF 区切り、末尾も LF):
    /// <code>
    /// fts-spec/1
    /// tokenize=unicode61 remove_diacritics 1
    /// layout=single
    /// normalize=nfkc-lower-kana1
    /// tokenizer=bigram-pad-v1
    /// field-separator=|
    /// tag-separator=、
    /// doc-select=latest-caption-v1
    /// sanitize=sanitize-v1
    /// </code>
    /// </summary>
    public string CanonicalString()
    {
        var sb = new StringBuilder();
        sb.Append(FtsSpec.SpecHeader).Append('\n');
        sb.Append("tokenize=").Append(FtsSpec.TokenizerSpec).Append('\n');
        sb.Append("layout=").Append(Layout).Append('\n');
        sb.Append("normalize=").Append(FtsSpec.NormalizeVersion).Append('\n');
        sb.Append("tokenizer=").Append(FtsSpec.TokenizerVersion).Append('\n');
        sb.Append("field-separator=").Append(FtsSpec.Field).Append('\n');
        sb.Append("tag-separator=").Append(FtsSpec.Tag).Append('\n');
        sb.Append("doc-select=").Append(FtsSpec.DocSelectVersion).Append('\n');
        sb.Append("sanitize=").Append(FtsSpec.SanitizeVersion).Append('\n');
        return sb.ToString();
    }

    /// <summary><c>CREATE VIRTUAL TABLE</c> 文。tokenize の指定は <see cref="FtsSpec.TokenizerSpec"/>。</summary>
    public string CreateTableSql() =>
        $"CREATE VIRTUAL TABLE {TableName} USING fts5({Column}, tokenize = '{FtsSpec.TokenizerSpec}')";

    /// <summary><c>INSERT INTO fts_docs(rowid, tokens) VALUES ($id, $tokens)</c> 文。</summary>
    public string InsertSql() => $"INSERT INTO {TableName}(rowid, {Column}) VALUES ($id, $tokens)";
}
