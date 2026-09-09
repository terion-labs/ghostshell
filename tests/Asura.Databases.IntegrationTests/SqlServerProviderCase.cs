using Asura.Application;

namespace Asura.Databases.IntegrationTests;

internal static class SqlServerProviderCase
{
    private const string Password = "Asura_Test1!";

    public static DatabaseProviderCase Definition { get; } = new ContainerDatabaseProviderCase(
        "sqlserver",
        "SQL Server 2025 CU7",
        "mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-24.04",
        1433,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ACCEPT_EULA"] = "Y",
            ["MSSQL_PID"] = "Developer",
            ["MSSQL_SA_PASSWORD"] = Password,
            ["MSSQL_DB"] = "asura",
        },
        [],
        static (host, port) =>
            $"Server={host},{port};Database=asura;User ID=sa;Password={Password};Encrypt=True;TrustServerCertificate=True;Connect Timeout=15;Command Timeout=30",
        "SELECT 1",
        CreateSeed(),
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
                DatabaseValueKind.TimestampWithZone,
                DatabaseValueKind.Binary,
            },
            ExpectedCodeLength: 80,
            ExpectedScorePrecision: 12,
            ExpectedScoreScale: 2,
            ScoreIndex: new DatabaseIndexExpectations(
                FirstColumnDescending: true,
                IncludedColumn: "note",
                PredicateFragment: "IS NOT NULL")),
        Platform: "linux/amd64");

    private static DatabaseSeed CreateSeed() => new(
        [
            """
            CREATE FUNCTION [dbo].[viewer_identity](@value BIGINT)
            RETURNS BIGINT
            AS
            BEGIN
                RETURN @value
            END
            """,
            """
            CREATE TABLE [viewer_rows] (
                [id] BIGINT IDENTITY(1, 1) NOT NULL
                    CONSTRAINT [pk_viewer_rows] PRIMARY KEY,
                [code] NVARCHAR(80) NOT NULL
                    CONSTRAINT [uq_viewer_rows_code] UNIQUE,
                [title] NVARCHAR(200) NOT NULL,
                [score] DECIMAL(12, 2) NOT NULL,
                [enabled] BIT NOT NULL,
                [note] NVARCHAR(400) NULL,
                [status] NVARCHAR(40) NOT NULL
                    CONSTRAINT [df_viewer_rows_status] DEFAULT N'draft',
                [created_at] DATETIMEOFFSET(7) NOT NULL
                    CONSTRAINT [df_viewer_rows_created_at]
                    DEFAULT CAST('2025-01-01T00:00:00+00:00' AS DATETIMEOFFSET),
                [payload] NVARCHAR(MAX) NOT NULL
                    CONSTRAINT [df_viewer_rows_payload] DEFAULT N'{}',
                [blob_value] VARBINARY(32) NULL,
                [computed_label] AS ([title] + N':' + [code]) PERSISTED
            )
            """,
            """
            CREATE INDEX [idx_viewer_rows_score]
            ON [viewer_rows] ([score] DESC)
            INCLUDE ([note])
            WHERE [note] IS NOT NULL
            """,
            """
            INSERT INTO [viewer_rows]
                ([code], [title], [score], [enabled], [note], [status],
                 [created_at], [payload], [blob_value])
            VALUES
                (N'alpha', N'Alpha', -100.00, 1, N'one', DEFAULT,
                 '2025-01-01T00:00:00+00:00', N'{"slot":1}', 0x0102),
                (N'beta', N'Beta', 0.00, 0,
                 N'Robert''); DROP TABLE viewer_rows;--', N'published',
                 '2025-01-02T00:00:00+00:00', N'{"slot":2}', NULL),
                (N'literal', N'literal%_!needle', 100.00, 1, NULL, DEFAULT,
                 '2025-01-03T00:00:00+00:00', N'{"slot":3}', NULL),
                (N'omega-a', N'Omega', 300.00, 1, N'four', DEFAULT,
                 '2025-01-04T00:00:00+00:00', N'{"slot":4}', NULL),
                (N'omega-b', N'unicode-🧪', 300.00, 0, N'comma,"quote"', DEFAULT,
                 '2025-01-05T00:00:00+00:00', N'{"slot":5}', NULL)
            """,
            """
            ;WITH [values_to_insert] ([value]) AS (
                SELECT 6
                UNION ALL
                SELECT [value] + 1
                FROM [values_to_insert]
                WHERE [value] < 205
            )
            INSERT INTO [viewer_rows] ([code], [title], [score], [enabled], [note])
            SELECT N'row-' + RIGHT(N'000' + CONVERT(NVARCHAR(3), [value]), 3),
                   N'Row ' + CONVERT(NVARCHAR(10), [value]),
                   1000 + [value],
                   CASE WHEN [value] % 2 = 0 THEN 1 ELSE 0 END,
                   CASE WHEN [value] % 3 = 0 THEN NULL ELSE N'filler' END
            FROM [values_to_insert]
            OPTION (MAXRECURSION 0)
            """,
            "CREATE TABLE [viewer_keyless] ([position] BIGINT NOT NULL, [label] NVARCHAR(80) NULL)",
            """
            ;WITH [values_to_insert] ([value]) AS (
                SELECT 1
                UNION ALL
                SELECT [value] + 1
                FROM [values_to_insert]
                WHERE [value] < 205
            )
            INSERT INTO [viewer_keyless] ([position], [label])
            SELECT [value], N'keyless-' + CONVERT(NVARCHAR(10), [value])
            FROM [values_to_insert]
            OPTION (MAXRECURSION 0)
            """,
            "CREATE VIEW [viewer_rows_view] AS SELECT [id], [code], [title] FROM [viewer_rows]",
            "CREATE TABLE [viewer]]odd.table] ([select] BIGINT NOT NULL)",
            "INSERT INTO [viewer]]odd.table] ([select]) VALUES (42)",
        ],
        HostileTable: "viewer]odd.table");
}
