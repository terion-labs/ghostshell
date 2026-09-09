using Asura.Application;

namespace Asura.Databases.IntegrationTests;

internal static class MySqlProviderCases
{
    private const string Password = "Asura_Test1!";

    public static DatabaseProviderCase MySql { get; } = Create(
        "mysql",
        "MySQL 8.4.10 LTS",
        "mysql:8.4.10",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MYSQL_ROOT_PASSWORD"] = Password,
            ["MYSQL_DATABASE"] = "asura",
            ["MYSQL_USER"] = "asura",
            ["MYSQL_PASSWORD"] = Password,
        },
        expectsJson: true);

    public static DatabaseProviderCase MariaDb { get; } = Create(
        "mariadb",
        "MariaDB 11.8.8 LTS",
        "mariadb:11.8.8",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MARIADB_ROOT_PASSWORD"] = Password,
            ["MARIADB_DATABASE"] = "asura",
            ["MARIADB_USER"] = "asura",
            ["MARIADB_PASSWORD"] = Password,
        },
        // MariaDB implements JSON as a LONGTEXT alias and reports it as text.
        expectsJson: false);

    private static DatabaseProviderCase Create(
        string id,
        string displayName,
        string image,
        IReadOnlyDictionary<string, string> environment,
        bool expectsJson)
    {
        var valueKinds = new HashSet<DatabaseValueKind>
        {
            DatabaseValueKind.SignedInteger,
            DatabaseValueKind.Text,
            DatabaseValueKind.Decimal,
            DatabaseValueKind.Boolean,
            DatabaseValueKind.Timestamp,
            DatabaseValueKind.Binary,
        };
        if (expectsJson)
        {
            valueKinds.Add(DatabaseValueKind.Json);
        }

        return new ContainerDatabaseProviderCase(
            id,
            displayName,
            image,
            3306,
            environment,
            ["--log-bin-trust-function-creators=1"],
            static (host, port) =>
                $"Server={host};Port={port};Database=asura;User ID=asura;Password={Password};SslMode=None;AllowPublicKeyRetrieval=True;ConnectionTimeout=15;DefaultCommandTimeout=30",
            "SELECT 1",
            CreateSeed(),
            new DatabaseProviderExpectations(
                CanEdit: true,
                HasIndexes: true,
                HasIdentity: true,
                HasGeneratedColumn: true,
                RequiredValueKinds: valueKinds,
                ExpectedCodeLength: 80,
                ExpectedScorePrecision: 12,
                ExpectedScoreScale: 2,
                ScoreIndex: new DatabaseIndexExpectations(
                    FirstColumnDescending: true)));
    }

    private static DatabaseSeed CreateSeed() => new(
        [
            """
            CREATE FUNCTION `viewer_identity`(`value` BIGINT)
            RETURNS BIGINT DETERMINISTIC
            RETURN `value`
            """,
            """
            CREATE TABLE `viewer_rows` (
                `id` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                `code` VARCHAR(80) NOT NULL UNIQUE,
                `title` VARCHAR(200) NOT NULL,
                `score` DECIMAL(12, 2) NOT NULL,
                `enabled` BOOLEAN NOT NULL,
                `note` TEXT NULL,
                `status` VARCHAR(40) NOT NULL DEFAULT 'draft',
                `created_at` DATETIME(6) NOT NULL DEFAULT '2025-01-01 00:00:00',
                `payload` JSON NOT NULL DEFAULT (JSON_OBJECT()),
                `blob_value` VARBINARY(32) NULL,
                `computed_label` VARCHAR(300)
                    GENERATED ALWAYS AS (CONCAT(`title`, ':', `code`)) STORED
            ) ENGINE=InnoDB
            """,
            "CREATE INDEX `idx_viewer_rows_score` ON `viewer_rows` (`score` DESC, `title`)",
            """
            INSERT INTO `viewer_rows`
                (`code`, `title`, `score`, `enabled`, `note`, `status`, `created_at`, `payload`, `blob_value`)
            VALUES
                ('alpha', 'Alpha', -100.00, TRUE, 'one', DEFAULT, '2025-01-01 00:00:00', '{"slot":1}', X'0102'),
                ('beta', 'Beta', 0.00, FALSE, 'Robert''); DROP TABLE viewer_rows;--', 'published', '2025-01-02 00:00:00', '{"slot":2}', NULL),
                ('literal', 'literal%_!needle', 100.00, TRUE, NULL, DEFAULT, '2025-01-03 00:00:00', '{"slot":3}', NULL),
                ('omega-a', 'Omega', 300.00, TRUE, 'four', DEFAULT, '2025-01-04 00:00:00', '{"slot":4}', NULL),
                ('omega-b', 'unicode-🧪', 300.00, FALSE, 'comma,"quote"', DEFAULT, '2025-01-05 00:00:00', '{"slot":5}', NULL)
            """,
            BuildRowsInsert("viewer_rows", 6, 205),
            "CREATE TABLE `viewer_keyless` (`position` BIGINT NOT NULL, `label` VARCHAR(80) NULL) ENGINE=InnoDB",
            BuildKeylessInsert(1, 205),
            "CREATE VIEW `viewer_rows_view` AS SELECT `id`, `code`, `title` FROM `viewer_rows`",
            "CREATE TABLE `viewer``odd.table` (`select` BIGINT NOT NULL) ENGINE=InnoDB",
            "INSERT INTO `viewer``odd.table` (`select`) VALUES (42)",
            "CREATE TABLE `viewer_nontransactional` (`id` BIGINT PRIMARY KEY, `title` VARCHAR(80)) ENGINE=MyISAM",
            "INSERT INTO `viewer_nontransactional` (`id`, `title`) VALUES (1, 'read only')",
        ],
        HostileTable: "viewer`odd.table");

    private static string BuildRowsInsert(string table, int first, int last)
    {
        var rows = Enumerable.Range(first, last - first + 1)
            .Select(value =>
                $"('row-{value:000}', 'Row {value}', {1000 + value}.00, "
                + $"{(value % 2 == 0 ? "TRUE" : "FALSE")}, "
                + (value % 3 == 0 ? "NULL" : "'filler'")
                + ", '{}')");
        return $"INSERT INTO `{table}` (`code`, `title`, `score`, `enabled`, `note`, `payload`) VALUES "
            + string.Join(", ", rows);
    }

    private static string BuildKeylessInsert(int first, int last)
    {
        var rows = Enumerable.Range(first, last - first + 1)
            .Select(value => $"({value}, 'keyless-{value}')");
        return "INSERT INTO `viewer_keyless` (`position`, `label`) VALUES "
            + string.Join(", ", rows);
    }
}
