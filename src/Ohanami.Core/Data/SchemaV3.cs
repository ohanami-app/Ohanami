namespace Ohanami.Core.Data;

/// <summary>
/// スキーマ v3。<c>search_text.model_id</c> を NULL 可にするため、<c>search_text</c> を作り直す(DROP → CREATE)。
/// <para>
/// 理由: 手動タグだけがある画像(caption が無い。VLM の前の画像)も全文検索の索引に入れる(ADR 0036)。その画像の索引文書には
/// 「どのモデルの caption から作ったか」が無い。現行の VLM の id を入れるのは嘘になり、VLM を設定しない構成では models に行も無い。
/// caption から作った索引文書の <c>model_id</c> には、caption のモデルの id が入る。
/// </para>
/// <para>
/// <c>search_text</c> は派生データ(captions と手動タグから作り直せる)なので、中身は引き継がない。マイグレーションは、規約の記録(<c>index_meta['fts_spec']</c>)も消す。
/// 開くときの照合(<c>FtsIndex.EnsureAsync</c>)が「規約が無い」と判って、fts_docs と search_text を作り直す。作り直しが終わるまで、
/// fts_docs の rowid は新しい search_text の id を指さないので、全文の経路は NotReady のまま使わせない(<c>FtsIndex</c> の状態)。
/// 既存の表(v1・v2 のほかの表)は 1 文字も変えない。
/// </para>
/// 新規作成も、v1 の DDL → v2 → v3 の順に同じマイグレーションを通る(<see cref="DatabaseSchema"/>)ので、新規の DB と移行した DB の定義は食い違わない。
/// 新しい表は無い(<see cref="Tables"/> は空)。<c>search_text</c> は v2 の表の一覧に入っている。
/// </summary>
internal static class SchemaV3
{
    public const int Version = 3;

    /// <summary>v3 で増える表(スキーマの検査用)。無い(search_text は作り直すだけ)。</summary>
    public static readonly string[] Tables = [];

    /// <summary>作り直したあとの <c>search_text</c> の定義。この 1 か所だけ(新規も移行もここを通る)。v2 との違いは <c>model_id</c> が NULL 可なこと。</summary>
    public const string SearchTableDdl = """
        CREATE TABLE search_text (
          id        INTEGER PRIMARY KEY,
          hash      TEXT NOT NULL UNIQUE REFERENCES images(hash),
          norm_text TEXT NOT NULL,
          model_id  TEXT REFERENCES models(id)
        ) STRICT;
        """;

    /// <summary>版 2 → 3 のマイグレーション。</summary>
    public static Migration FromV2 { get; } = new(2, (conn, tx) =>
    {
        DatabaseSchema.Exec(conn, tx, "DROP TABLE search_text");
        DatabaseSchema.Exec(conn, tx, SearchTableDdl);
        // 派生データを作り直させる(fts_docs の rowid は、古い search_text の id を指している)
        DatabaseSchema.Exec(conn, tx, "DELETE FROM index_meta WHERE key = 'fts_spec'");
    });
}
