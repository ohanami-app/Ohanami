namespace Ohanami.Core.Data;

/// <summary>
/// スキーマ v1 の DDL。この定義が正(方針は ADR 0005)。
/// 列挙の列(format / kind / stage / status / source / job_kind)には CHECK 制約を付けない(ADR 0005)。
/// 付けるのは、構造の制約(NOT NULL、PRIMARY KEY、FOREIGN KEY、UNIQUE)と、変わらない形式の制約
/// (length(hash) = 64 と hash = lower(hash)[大文字の 16 進が混ざると同じ画像が別の識別子に割れるため]、真偽値の enabled IN (0, 1))だけ。
/// tags.name は ASCII の大文字小文字を区別しない(UNIQUE (name COLLATE NOCASE, kind)。日本語には効かない)。
/// image_vectors の UNIQUE は (model_id, hash) の順(現行の model_id の全行を読む用途が、索引の先頭で絞れる)。値の検査は <see cref="DbEnums"/> の明示的な変換が行う。
/// 時刻は UTC の ISO 8601 文字列。
/// </summary>
internal static class SchemaV1
{
    public const int Version = 1;

    /// <summary>作成後に存在するべき表(スキーマの検査用)。</summary>
    public static readonly string[] Tables =
    [
        "folders", "images", "image_paths", "file_issues", "models", "runs",
        "stage_status", "image_vectors", "tags", "image_tags",
    ];

    public const string Ddl = """
        CREATE TABLE folders (
          id           INTEGER PRIMARY KEY,
          path         TEXT NOT NULL UNIQUE COLLATE NOCASE,
          enabled      INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
          added_at     TEXT NOT NULL,
          last_scan_at TEXT,
          last_error   TEXT
        ) STRICT;

        CREATE TABLE images (
          hash          TEXT PRIMARY KEY
                        CHECK (length(hash) = 64)
                        CHECK (hash = lower(hash)),
          size          INTEGER NOT NULL,
          format        TEXT NOT NULL,
          width         INTEGER,
          height        INTEGER,
          first_seen_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE image_paths (
          path          TEXT PRIMARY KEY COLLATE NOCASE,
          hash          TEXT NOT NULL REFERENCES images(hash),
          folder_id     INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
          mtime_ticks   INTEGER NOT NULL,
          size          INTEGER NOT NULL,
          missing_since TEXT
        ) STRICT;
        CREATE INDEX ix_image_paths_hash   ON image_paths(hash);
        CREATE INDEX ix_image_paths_folder ON image_paths(folder_id);

        CREATE TABLE file_issues (
          path        TEXT PRIMARY KEY COLLATE NOCASE,
          folder_id   INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
          kind        TEXT NOT NULL,
          message     TEXT NOT NULL,
          mtime_ticks INTEGER NOT NULL,
          size        INTEGER NOT NULL,
          updated_at  TEXT NOT NULL
        ) STRICT;

        CREATE TABLE models (
          id         TEXT PRIMARY KEY,
          kind       TEXT NOT NULL,
          name       TEXT NOT NULL,
          spec_json  TEXT NOT NULL,
          created_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE runs (
          id           INTEGER PRIMARY KEY,
          job_kind     TEXT NOT NULL,
          started_at   TEXT NOT NULL,
          ended_at     TEXT,
          app_version  TEXT NOT NULL,
          runtime_json TEXT NOT NULL
        ) STRICT;

        CREATE TABLE stage_status (
          hash          TEXT NOT NULL REFERENCES images(hash),
          stage         TEXT NOT NULL,
          status        TEXT NOT NULL,
          model_id      TEXT NOT NULL REFERENCES models(id),
          run_id        INTEGER NOT NULL REFERENCES runs(id),
          settings_json TEXT,
          error         TEXT,
          attempts      INTEGER NOT NULL DEFAULT 1,
          updated_at    TEXT NOT NULL,
          PRIMARY KEY (hash, stage)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE image_vectors (
          hash      TEXT NOT NULL REFERENCES images(hash),
          model_id  TEXT NOT NULL REFERENCES models(id),
          embedding BLOB NOT NULL,
          UNIQUE (model_id, hash)
        ) STRICT;

        CREATE TABLE tags (
          id   INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          kind TEXT NOT NULL,
          UNIQUE (name COLLATE NOCASE, kind)
        ) STRICT;

        CREATE TABLE image_tags (
          hash    TEXT NOT NULL REFERENCES images(hash),
          tag_id  INTEGER NOT NULL REFERENCES tags(id),
          source  TEXT NOT NULL,
          score   REAL,
          PRIMARY KEY (hash, tag_id, source)
        ) STRICT, WITHOUT ROWID;
        """;
}
