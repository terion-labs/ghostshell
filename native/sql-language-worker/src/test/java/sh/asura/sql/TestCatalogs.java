package sh.asura.sql;

import java.util.List;

final class TestCatalogs {
    private TestCatalogs() {
    }

    static CatalogSnapshot postgres() {
        return withIntrinsicSymbols(new CatalogSnapshot(
            "postgres",
            "app",
            "public",
            List.of(
                new CatalogObject(
                    new CatalogObjectId("app", "public", "people"),
                    "Table",
                    List.of(
                        column("id", "bigint", "SignedInteger", false),
                        column("name", "text", "Text", false),
                        column("active", "boolean", "Boolean", true))),
                new CatalogObject(
                    new CatalogObjectId("app", "public", "CaseSensitive"),
                    "View",
                    List.of(
                        column("Key", "uuid", "Guid", false),
                        column("DisplayName", "text", "Text", true))))),
            "COALESCE",
            "COUNT");
    }

    static CatalogSnapshot editorPostgres() {
        return withIntrinsicSymbols(new CatalogSnapshot(
            "postgres",
            "app",
            "public",
            List.of(
                new CatalogObject(
                    new CatalogObjectId("app", "public", "articles"),
                    "Table",
                    List.of(
                        column("id", "bigint", "SignedInteger", false),
                        column("title", "text", "Text", false),
                        column("content", "text", "Text", true))),
                new CatalogObject(
                    new CatalogObjectId("app", "public", "authors"),
                    "Table",
                    List.of(
                        column("id", "bigint", "SignedInteger", false),
                        column("name", "text", "Text", false))))),
            "COALESCE",
            "COUNT");
    }

    static CatalogSnapshot forDriver(String driverId) {
        boolean foldsUnquotedNamesToUpper = driverId.equals("oracle")
            || driverId.equals("firebird");
        String tableName = foldsUnquotedNamesToUpper ? "PEOPLE" : "people";
        String idColumn = foldsUnquotedNamesToUpper ? "ID" : "id";
        String nameColumn = foldsUnquotedNamesToUpper ? "NAME" : "name";
        CatalogShape shape = switch (driverId) {
            case "sqlite" -> new CatalogShape(null, "main", null, "main");
            case "postgres", "cockroach", "redshift" ->
                new CatalogShape("app", "public", "app", "public");
            case "mysql", "mariadb", "clickhouse" ->
                new CatalogShape(null, "app", null, "app");
            case "sqlserver" -> new CatalogShape("app", "dbo", "app", "dbo");
            case "duckdb" -> new CatalogShape("app", "main", "app", "main");
            case "oracle" -> new CatalogShape(null, "APP", null, "APP");
            case "firebird" -> new CatalogShape(null, null, null, null);
            default -> throw new IllegalArgumentException("Unknown test driver: " + driverId);
        };
        return withIntrinsicSymbols(new CatalogSnapshot(
            driverId,
            shape.defaultCatalog(),
            shape.defaultSchema(),
            List.of(new CatalogObject(
                new CatalogObjectId(shape.objectCatalog(), shape.objectSchema(), tableName),
                "Table",
                List.of(
                    column(idColumn, "bigint", "SignedInteger", false),
                    column(nameColumn, "text", "Text", false))))),
            "COALESCE",
            "COUNT");
    }

    static String productionPreview(String driverId) {
        return switch (driverId) {
            case "sqlite" ->
                "SELECT * FROM \"main\".\"people\" LIMIT 200 OFFSET 0;";
            case "postgres", "cockroach", "redshift" ->
                "SELECT * FROM \"public\".\"people\" LIMIT 200 OFFSET 0;";
            case "mysql", "mariadb", "clickhouse" ->
                "SELECT * FROM `app`.`people` LIMIT 200 OFFSET 0;";
            case "sqlserver" ->
                "SELECT * FROM [app].[dbo].[people] ORDER BY (SELECT NULL) "
                    + "OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;";
            case "duckdb" ->
                "SELECT * FROM \"app\".\"main\".\"people\" LIMIT 200 OFFSET 0;";
            case "oracle" ->
                "SELECT * FROM \"APP\".\"PEOPLE\" ORDER BY 1 "
                    + "OFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;";
            case "firebird" -> "SELECT * FROM \"PEOPLE\" ROWS 1 TO 200;";
            default -> throw new IllegalArgumentException("Unknown test driver: " + driverId);
        };
    }

    static String providerExtensionQuery(String driverId) {
        return switch (driverId) {
            case "sqlite" ->
                "INSERT INTO main.people(id, name) VALUES (1, 'x') "
                    + "ON CONFLICT(id) DO UPDATE SET name = 'x'";
            case "postgres", "cockroach", "redshift" ->
                "SELECT id::text FROM public.people WHERE id = $1";
            case "mysql", "mariadb" ->
                "SELECT id FROM app.people WHERE id = @id";
            case "sqlserver" ->
                "SELECT TOP (10) id FROM [app].[dbo].[people] WHERE id = @id";
            case "duckdb" ->
                "SELECT * EXCLUDE (name) FROM app.main.people WHERE id = $id";
            case "oracle" ->
                "SELECT ID FROM APP.PEOPLE WHERE ID = :id AND ROWNUM <= 10";
            case "firebird" ->
                "SELECT FIRST 10 ID FROM PEOPLE WHERE ID = @id";
            case "clickhouse" ->
                "SELECT id FROM app.people SETTINGS max_threads = 1";
            default -> throw new IllegalArgumentException("Unknown test driver: " + driverId);
        };
    }

    static String providerExtensionCompletionQuery(String driverId) {
        return switch (driverId) {
            case "sqlserver" ->
                "SELECT TOP (10) p. FROM [app].[dbo].[people] p WHERE p.id = @id";
            case "firebird" ->
                "SELECT FIRST 10 p. FROM PEOPLE p WHERE p.ID = @id";
            case "clickhouse" ->
                "SELECT p. FROM app.people p SETTINGS max_threads = 1";
            default -> throw new IllegalArgumentException(
                "No parser-fallback completion fixture for: " + driverId);
        };
    }

    static String providerExtensionMissingColumnQuery(String driverId) {
        return providerExtensionCompletionQuery(driverId).replace(
            "p. FROM",
            "p.definitely_missing FROM");
    }

    static String providerExtensionExpressionQuery(String driverId) {
        return switch (driverId) {
            case "sqlserver" ->
                "SELECT TOP (10) * FROM [app].[dbo].[people] p WHERE p.id > 0";
            case "firebird" ->
                "SELECT FIRST 10 * FROM PEOPLE p WHERE p.ID > 0";
            case "clickhouse" ->
                "SELECT * FROM app.people p WHERE p.id > 0 SETTINGS max_threads = 1";
            default -> throw new IllegalArgumentException(
                "No parser-fallback expression fixture for: " + driverId);
        };
    }

    static CatalogSnapshot quotedLowercase(String driverId) {
        String schema = driverId.equals("oracle") ? "SYSTEM" : null;
        return new CatalogSnapshot(
            driverId,
            null,
            schema,
            List.of(new CatalogObject(
                new CatalogObjectId(null, schema, "viewer_rows"),
                "Table",
                List.of(
                    column("id", "bigint", "SignedInteger", false),
                    column("title", "text", "Text", false)))));
    }

    static String postgresJson() {
        return """
            {
              "driverId":"postgres",
              "defaultCatalog":"app",
              "defaultSchema":"public",
              "objects":[
                {
                  "id":{"catalog":"app","schema":"public","name":"people"},
                  "kind":"Table",
                  "columns":[
                    {"name":"id","dataTypeName":"bigint","valueKind":"SignedInteger","isNullable":false},
                    {"name":"name","dataTypeName":"text","valueKind":"Text","isNullable":false},
                    {"name":"active","dataTypeName":"boolean","valueKind":"Boolean","isNullable":true}
                  ]
                }
              ]
            }
            """;
    }

    static CatalogColumn column(
        String name,
        String dataType,
        String valueKind,
        boolean nullable) {
        return new CatalogColumn(name, dataType, valueKind, nullable);
    }

    static CatalogSnapshot withIntrinsicSymbols(
        CatalogSnapshot snapshot,
        String... names) {
        return new CatalogSnapshot(
            snapshot.driverId(),
            snapshot.defaultCatalog(),
            snapshot.defaultSchema(),
            snapshot.objects(),
            snapshot.routines(),
            snapshot.routineCoverage(),
            CatalogMetadataCoverage.PARTIAL,
            java.util.Arrays.stream(names)
                .map(name -> new CatalogIntrinsicSymbol(name, "keyword"))
                .toList());
    }

    private record CatalogShape(
        String defaultCatalog,
        String defaultSchema,
        String objectCatalog,
        String objectSchema) {
    }
}
