namespace Ohanami.Core.Data;

/// <summary>
/// スキーマ v2(VLM の結果と全文検索の索引の表を足す)。v1 からのマイグレーション 1 段で、表を**追加するだけ**(既存の表は作り直さない)。
/// <list type="bullet">
/// <item><c>captions</c>: VLM の結果(ocr、説明文、tags)。(hash, model_id) で持つので、モデルを替えても古い結果は消えない。
///   成功(finish_reason = stop)だけを保存し、失敗は stage_status の failed で持つ(行を作らない)。</item>
/// <item><c>search_text</c>: 全文検索の索引文書(正規化後)。1 文字のクエリの LIKE 用。captions から作り直せる派生データ。<c>id</c> は fts_docs の rowid。</item>
/// <item><c>index_meta</c>: 派生データの規約の記録(fts_spec など)。</item>
/// </list>
/// <b>fts_docs(FTS5 の仮想表)はここに入れない</b>。列の数を回帰ベンチで決めるため、索引の規約文字列(index_meta['fts_spec'])で作る・作り直す派生データにした(ADR 0030)。
/// したがって <see cref="Tables"/> にも入れず、「新規と移行の sqlite_master が一致する」テストの比較からも除く。
/// <para>v3(<see cref="SchemaV3"/>)で <c>search_text</c> を作り直し、<c>model_id</c> を NULL 可にした(手動タグだけの画像のため)。このファイルの DDL は v2 の時点の定義のまま(移行の起点)。</para>
/// </summary>
internal static class SchemaV2
{
    public const int Version = 2;

    /// <summary>v2 で増える表(スキーマの検査用)。fts_docs とその影の表は含めない。</summary>
    public static readonly string[] Tables = ["captions", "search_text", "index_meta"];

    public const string Ddl = """
        CREATE TABLE captions (
          hash          TEXT NOT NULL REFERENCES images(hash),
          model_id      TEXT NOT NULL REFERENCES models(id),
          ocr           TEXT NOT NULL,
          description   TEXT NOT NULL,
          tags_json     TEXT NOT NULL,
          finish_reason TEXT NOT NULL,
          attempt       INTEGER NOT NULL CHECK (attempt >= 1),
          settings_json TEXT NOT NULL,
          prompt_tokens INTEGER,
          run_id        INTEGER NOT NULL REFERENCES runs(id),
          created_at    TEXT NOT NULL,
          UNIQUE (model_id, hash)
        ) STRICT;
        CREATE INDEX ix_captions_hash ON captions(hash);

        CREATE TABLE search_text (
          id        INTEGER PRIMARY KEY,
          hash      TEXT NOT NULL UNIQUE REFERENCES images(hash),
          norm_text TEXT NOT NULL,
          model_id  TEXT NOT NULL REFERENCES models(id)
        ) STRICT;

        CREATE TABLE index_meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;
        """;

    /// <summary>版 1 → 2 のマイグレーション。</summary>
    public static Migration FromV1 { get; } = new(1, (conn, tx) => DatabaseSchema.Exec(conn, tx, Ddl));
}
