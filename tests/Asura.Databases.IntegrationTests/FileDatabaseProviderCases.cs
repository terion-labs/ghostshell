using Asura.Application;

namespace Asura.Databases.IntegrationTests;

internal static class FileDatabaseProviderCases
{
    public static DatabaseProviderCase Sqlite { get; } = new FileDatabaseProviderCase(
        "sqlite",
        "SQLite",
        ".sqlite",
        static path => $"Data Source={path};Pooling=False",
        "SELECT 1",
        CreateSqliteSeed(),
        new DatabaseProviderExpectations(
            CanEdit: true,
            HasIndexes: true,
            HasIdentity: true,
            HasGeneratedColumn: true,
            RequiredValueKinds: new HashSet<DatabaseValueKind>
            {
                DatabaseValueKind.SignedInteger,
                DatabaseValueKind.Text,
                DatabaseValueKind.Decimal,
                DatabaseValueKind.Boolean,
                DatabaseValueKind.Timestamp,
                DatabaseValueKind.Binary,
                DatabaseValueKind.Json,
            },
            ScoreIndex: new DatabaseIndexExpectations(
                FirstColumnDescending: true,
                PredicateFragment: "IS NOT NULL")));

    public static DatabaseProviderCase DuckDb { get; } = new FileDatabaseProviderCase(
        "duckdb",
        "DuckDB",
        ".duckdb",
        static path => $"Data Source={path}",
        "SELECT 1",
        CreateDuckDbSeed(),
        new DatabaseProviderExpectations(
            CanEdit: true,
            HasIndexes: true,
            HasIdentity: false,
            HasGeneratedColumn: true,
            RequiredValueKinds: new HashSet<DatabaseValueKind>
            {
                DatabaseValueKind.SignedInteger,
                DatabaseValueKind.Text,
                DatabaseValueKind.Decimal,
                DatabaseValueKind.Boolean,
                DatabaseValueKind.TimestampWithZone,
                DatabaseValueKind.Binary,
                DatabaseValueKind.Json,
            },
            ExpectedScorePrecision: 12,
            ExpectedScoreScale: 2,
            ScoreIndex: new DatabaseIndexExpectations(
                FirstColumnDescending: false)));

    private static DatabaseSeed CreateSqliteSeed() => new(
        [
            """
            CREATE TABLE "viewer_rows" (
                "id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "code" TEXT NOT NULL UNIQUE,
                "title" TEXT NOT NULL,
                "score" NUMERIC(12, 2) NOT NULL,
                "enabled" BOOLEAN NOT NULL,
                "note" TEXT NULL,
                "status" TEXT NOT NULL DEFAULT 'draft',
                "created_at" TIMESTAMP NOT NULL DEFAULT '2025-01-01T00:00:00Z',
                "payload" JSON NOT NULL DEFAULT '{}',
                "blob_value" BLOB NULL,
                "computed_label" TEXT GENERATED ALWAYS AS ("title" || ':' || "code") STORED
            )
            """,
            "CREATE INDEX \"idx_viewer_rows_score\" ON \"viewer_rows\" (\"score\" DESC) WHERE \"note\" IS NOT NULL",
            """
            INSERT INTO "viewer_rows"
                ("code", "title", "score", "enabled", "note", "status", "created_at", "payload", "blob_value")
            VALUES
                ('alpha', 'Alpha', -100.00, 1, 'one', 'draft', '2025-01-01T00:00:00Z', '{"slot":1}', X'0102'),
                ('beta', 'Beta', 0.00, 0, 'Robert''); DROP TABLE viewer_rows;--', 'published', '2025-01-02T00:00:00Z', '{"slot":2}', NULL),
                ('literal', 'literal%_!needle', 100.00, 1, NULL, 'draft', '2025-01-03T00:00:00Z', '{"slot":3}', NULL),
                ('omega-a', 'Omega', 300.00, 1, 'four', 'draft', '2025-01-04T00:00:00Z', '{"slot":4}', NULL),
                ('omega-b', 'unicode-🧪', 300.00, 0, 'comma,"quote"', 'draft', '2025-01-05T00:00:00Z', '{"slot":5}', NULL)
            """,
            """
            WITH RECURSIVE values_to_insert(value) AS (
                SELECT 6 UNION ALL SELECT value + 1 FROM values_to_insert WHERE value < 205
            )
            INSERT INTO "viewer_rows" ("code", "title", "score", "enabled", "note")
            SELECT printf('row-%03d', value),
                   'Row ' || value,
                   1000 + value,
                   value % 2 = 0,
                   CASE WHEN value % 3 = 0 THEN NULL ELSE 'filler' END
            FROM values_to_insert
            """,
            "CREATE TABLE \"viewer_keyless\" (\"position\" INTEGER NOT NULL, \"label\" TEXT NULL)",
            """
            WITH RECURSIVE values_to_insert(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM values_to_insert WHERE value < 205
            )
            INSERT INTO "viewer_keyless"
            SELECT value, 'keyless-' || value FROM values_to_insert
            """,
            "CREATE VIEW \"viewer_rows_view\" AS SELECT \"id\", \"code\", \"title\" FROM \"viewer_rows\"",
            "CREATE TABLE \"viewer\"\"odd.table\" (\"select\" INTEGER NOT NULL)",
            "INSERT INTO \"viewer\"\"odd.table\" (\"select\") VALUES (42)",
        ],
        HostileTable: "viewer\"odd.table");

    private static DatabaseSeed CreateDuckDbSeed() => new(
        [
            "CREATE SEQUENCE \"viewer_rows_id_seq\" START 1",
            """
            CREATE TABLE "viewer_rows" (
                "id" BIGINT DEFAULT nextval('viewer_rows_id_seq') PRIMARY KEY,
                "code" VARCHAR NOT NULL UNIQUE,
                "title" VARCHAR NOT NULL,
                "score" DECIMAL(12, 2) NOT NULL,
                "enabled" BOOLEAN NOT NULL,
                "note" VARCHAR NULL,
                "status" VARCHAR NOT NULL DEFAULT 'draft',
                "created_at" TIMESTAMPTZ NOT NULL DEFAULT TIMESTAMPTZ '2025-01-01 00:00:00+00',
                "payload" JSON NOT NULL DEFAULT '{}',
                "blob_value" BLOB NULL,
                "computed_label" VARCHAR GENERATED ALWAYS AS ("title" || ':' || "code") VIRTUAL
            )
            """,
            "CREATE INDEX \"idx_viewer_rows_score\" ON \"viewer_rows\" (\"score\")",
            """
            INSERT INTO "viewer_rows"
                ("code", "title", "score", "enabled", "note", "status", "created_at", "payload", "blob_value")
            VALUES
                ('alpha', 'Alpha', -100.00, TRUE, 'one', DEFAULT, '2025-01-01T00:00:00Z', '{"slot":1}', from_hex('0102')),
                ('beta', 'Beta', 0.00, FALSE, 'Robert''); DROP TABLE viewer_rows;--', 'published', '2025-01-02T00:00:00Z', '{"slot":2}', NULL),
                ('literal', 'literal%_!needle', 100.00, TRUE, NULL, DEFAULT, '2025-01-03T00:00:00Z', '{"slot":3}', NULL),
                ('omega-a', 'Omega', 300.00, TRUE, 'four', DEFAULT, '2025-01-04T00:00:00Z', '{"slot":4}', NULL),
                ('omega-b', 'unicode-🧪', 300.00, FALSE, 'comma,"quote"', DEFAULT, '2025-01-05T00:00:00Z', '{"slot":5}', NULL)
            """,
            """
            INSERT INTO "viewer_rows" ("code", "title", "score", "enabled", "note")
            SELECT 'row-' || lpad(CAST(value AS VARCHAR), 3, '0'),
                   'Row ' || value,
                   1000 + value,
                   value % 2 = 0,
                   CASE WHEN value % 3 = 0 THEN NULL ELSE 'filler' END
            FROM range(6, 206) AS values_to_insert(value)
            """,
            "CREATE TABLE \"viewer_keyless\" (\"position\" BIGINT NOT NULL, \"label\" VARCHAR NULL)",
            "INSERT INTO \"viewer_keyless\" SELECT value, 'keyless-' || value FROM range(1, 206) AS values_to_insert(value)",
            "CREATE VIEW \"viewer_rows_view\" AS SELECT \"id\", \"code\", \"title\" FROM \"viewer_rows\"",
            "CREATE TABLE \"viewer\"\"odd.table\" (\"select\" BIGINT NOT NULL)",
            "INSERT INTO \"viewer\"\"odd.table\" (\"select\") VALUES (42)",
        ],
        HostileTable: "viewer\"odd.table");
}
