using Asura.Application;

namespace Asura.Databases.IntegrationTests;

internal static class ClickHouseProviderCase
{
    private const string Password = "Asura_Test1!";

    public static DatabaseProviderCase Definition { get; } = new ContainerDatabaseProviderCase(
        "clickhouse",
        "ClickHouse 26.3.17.56 LTS",
        "clickhouse/clickhouse-server:26.3.17.56",
        8123,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLICKHOUSE_DB"] = "asura",
            ["CLICKHOUSE_USER"] = "asura",
            ["CLICKHOUSE_PASSWORD"] = Password,
            ["CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT"] = "1",
        },
        [],
        static (host, port) =>
            $"Host={host};Port={port};Protocol=http;Database=asura;Username=asura;Password={Password};Timeout=15;CommandTimeout=30",
        "SELECT 1",
        CreateSeed(),
        new DatabaseProviderExpectations(
            CanEdit: false,
            HasIndexes: true,
            HasIdentity: false,
            HasGeneratedColumn: true,
            RequiredValueKinds: new HashSet<DatabaseValueKind>
            {
                DatabaseValueKind.UnsignedInteger,
                DatabaseValueKind.Text,
                DatabaseValueKind.Decimal,
                DatabaseValueKind.Boolean,
                DatabaseValueKind.Timestamp,
            },
            "ClickHouse browsing and metadata are native; row mutations remain intentionally disabled.",
            ScoreIndex: new DatabaseIndexExpectations(
                FirstColumnDescending: false)));

    private static DatabaseSeed CreateSeed() => new(
        [
            """
            CREATE TABLE `viewer_rows` (
                `id` UInt64,
                `code` String,
                `title` String,
                `score` Decimal(12, 2),
                `enabled` Bool,
                `note` Nullable(String),
                `status` LowCardinality(String) DEFAULT 'draft',
                `created_at` DateTime64(3, 'UTC')
                    DEFAULT toDateTime64('2025-01-01 00:00:00', 3, 'UTC'),
                `payload` String DEFAULT '{}',
                `blob_value` Nullable(String),
                `computed_label` String MATERIALIZED concat(`title`, ':', `code`),
                INDEX `idx_viewer_rows_score` `score` TYPE minmax GRANULARITY 1
            )
            ENGINE = MergeTree
            PRIMARY KEY (`id`)
            ORDER BY (`id`, `code`)
            """,
            """
            INSERT INTO `viewer_rows`
                (`id`, `code`, `title`, `score`, `enabled`, `note`, `status`,
                 `created_at`, `payload`, `blob_value`)
            VALUES
                (1, 'alpha', 'Alpha', -100.00, TRUE, 'one', DEFAULT,
                 '2025-01-01 00:00:00', '{"slot":1}', unhex('0102')),
                (2, 'beta', 'Beta', 0.00, FALSE,
                 'Robert''); DROP TABLE viewer_rows;--', 'published',
                 '2025-01-02 00:00:00', '{"slot":2}', NULL),
                (3, 'literal', 'literal%_!needle', 100.00, TRUE, NULL, DEFAULT,
                 '2025-01-03 00:00:00', '{"slot":3}', NULL),
                (4, 'omega-a', 'Omega', 300.00, TRUE, 'four', DEFAULT,
                 '2025-01-04 00:00:00', '{"slot":4}', NULL),
                (5, 'omega-b', 'unicode-🧪', 300.00, FALSE, 'comma,"quote"', DEFAULT,
                 '2025-01-05 00:00:00', '{"slot":5}', NULL)
            """,
            """
            INSERT INTO `viewer_rows` (`id`, `code`, `title`, `score`, `enabled`, `note`)
            SELECT number + 6,
                   concat('row-', leftPad(toString(number + 6), 3, '0')),
                   concat('Row ', toString(number + 6)),
                   toDecimal64(1006 + number, 2),
                   modulo(number + 6, 2) = 0,
                   if(modulo(number + 6, 3) = 0, NULL, 'filler')
            FROM numbers(200)
            """,
            """
            CREATE TABLE `viewer_keyless` (
                `position` UInt64,
                `label` Nullable(String)
            )
            ENGINE = MergeTree
            ORDER BY tuple()
            """,
            """
            INSERT INTO `viewer_keyless`
            SELECT number + 1, concat('keyless-', toString(number + 1))
            FROM numbers(205)
            """,
            """
            CREATE VIEW `viewer_rows_view` AS
            SELECT `id`, `code`, `title` FROM `viewer_rows`
            """,
            """
            CREATE TABLE `viewer``odd.table` (`select` Int64)
            ENGINE = MergeTree
            ORDER BY tuple()
            """,
            "INSERT INTO `viewer``odd.table` (`select`) VALUES (42)",
        ],
        HostileTable: "viewer`odd.table");
}
