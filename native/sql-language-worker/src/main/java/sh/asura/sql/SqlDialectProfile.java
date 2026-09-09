package sh.asura.sql;

import org.apache.calcite.avatica.util.Casing;
import org.apache.calcite.avatica.util.Quoting;
import org.apache.calcite.config.CalciteConnectionConfigImpl;
import org.apache.calcite.config.CalciteConnectionProperty;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.babel.SqlBabelParserImpl;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.validate.SqlConformance;
import org.apache.calcite.sql.validate.SqlConformanceEnum;

import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;

/** The identifier rules Calcite needs for Asura's built-in database drivers. */
record SqlDialectProfile(
    String driverId,
    Quoting quoting,
    Casing unquotedCasing,
    boolean caseSensitive,
    boolean objectCaseSensitive,
    boolean columnCaseSensitive,
    SqlConformance conformance) {

    static SqlDialectProfile forDriver(String driverId) throws ProtocolException {
        String normalizedDriverId = driverId.toLowerCase(Locale.ROOT);
        return switch (normalizedDriverId) {
            case "postgres", "cockroach" ->
                profile(normalizedDriverId, Quoting.DOUBLE_QUOTE, Casing.TO_LOWER, true,
                    SqlConformanceEnum.BABEL);
            case "redshift" ->
                profile(normalizedDriverId, Quoting.DOUBLE_QUOTE, Casing.TO_LOWER, false,
                    SqlConformanceEnum.BABEL);
            case "mysql", "mariadb" ->
                profile(normalizedDriverId, Quoting.BACK_TICK, Casing.UNCHANGED, false,
                    SqlConformanceEnum.MYSQL_5);
            case "sqlserver" ->
                profile(normalizedDriverId, Quoting.BRACKET, Casing.UNCHANGED, false,
                    SqlConformanceEnum.BABEL);
            case "oracle" ->
                profile(normalizedDriverId, Quoting.DOUBLE_QUOTE, Casing.TO_UPPER, true,
                    SqlConformanceEnum.ORACLE_12);
            case "firebird" ->
                profile(normalizedDriverId, Quoting.DOUBLE_QUOTE, Casing.TO_UPPER, true,
                    SqlConformanceEnum.BABEL);
            case "clickhouse" ->
                profile(normalizedDriverId, Quoting.BACK_TICK, Casing.UNCHANGED, true,
                    SqlConformanceEnum.BABEL);
            case "sqlite", "duckdb" ->
                profile(normalizedDriverId, Quoting.DOUBLE_QUOTE, Casing.UNCHANGED, false,
                    SqlConformanceEnum.BABEL);
            default -> throw new ProtocolException(
                "invalidParams",
                "Unsupported database driver '" + driverId + "'.");
        };
    }

    static SqlDialectProfile forCatalog(CatalogSnapshot snapshot) throws ProtocolException {
        SqlDialectProfile base = forDriver(snapshot.driverId());
        CaseDistinctIdentifiers distinct = caseDistinctIdentifiers(snapshot);
        if (!distinct.objects() && !distinct.columns()) {
            return base;
        }
        boolean separatesTableAndColumnCase = base.driverId().equals("mysql")
            || base.driverId().equals("mariadb");
        boolean objectCaseSensitive = base.objectCaseSensitive()
            || distinct.objects()
            || (!separatesTableAndColumnCase && distinct.columns());
        boolean columnCaseSensitive = base.columnCaseSensitive()
            || distinct.columns()
            || (!separatesTableAndColumnCase && distinct.objects());
        return new SqlDialectProfile(
            base.driverId(),
            base.quoting(),
            base.unquotedCasing(),
            objectCaseSensitive || columnCaseSensitive,
            objectCaseSensitive,
            columnCaseSensitive,
            base.conformance());
    }

    SqlParser.Config parserConfig() {
        return SqlParser.config()
            .withParserFactory(SqlBabelParserImpl.FACTORY)
            .withQuoting(quoting)
            .withQuotedCasing(Casing.UNCHANGED)
            .withUnquotedCasing(unquotedCasing)
            .withCaseSensitive(caseSensitive)
            .withConformance(conformance);
    }

    CalciteConnectionConfigImpl connectionConfig() {
        return new CalciteConnectionConfigImpl(new Properties())
            .set(CalciteConnectionProperty.CASE_SENSITIVE, Boolean.toString(caseSensitive))
            .set(CalciteConnectionProperty.CONFORMANCE, conformance.toString());
    }

    String normalizeIdentifier(String value) {
        return caseSensitive ? value : value.toLowerCase(Locale.ROOT);
    }

    String normalizeObjectIdentifier(String value) {
        return objectCaseSensitive ? value : value.toLowerCase(Locale.ROOT);
    }

    String normalizeColumnIdentifier(String value) {
        return columnCaseSensitive ? value : value.toLowerCase(Locale.ROOT);
    }

    boolean matchesObjectIdentifier(String reference, boolean quoted, String catalogName) {
        String normalizedReference = quoted ? reference : foldUnquoted(reference);
        return objectCaseSensitive
            ? normalizedReference.equals(catalogName)
            : normalizedReference.equalsIgnoreCase(catalogName);
    }

    boolean matchesColumnIdentifier(String reference, boolean quoted, String catalogName) {
        String normalizedReference = quoted ? reference : foldUnquoted(reference);
        return columnCaseSensitive
            ? normalizedReference.equals(catalogName)
            : normalizedReference.equalsIgnoreCase(catalogName);
    }

    boolean equivalentReferences(
        String first,
        boolean firstQuoted,
        String second,
        boolean secondQuoted) {
        String normalizedFirst = firstQuoted ? first : foldUnquoted(first);
        String normalizedSecond = secondQuoted ? second : foldUnquoted(second);
        return columnCaseSensitive
            ? normalizedFirst.equals(normalizedSecond)
            : normalizedFirst.equalsIgnoreCase(normalizedSecond);
    }

    boolean canUseCalciteColumnDiagnostics() {
        return caseSensitive == columnCaseSensitive;
    }

    List<SqlLibrary> operatorLibraries() {
        SqlLibrary dialect = dialectOperatorLibrary();
        return dialect == null
            ? List.of(SqlLibrary.STANDARD)
            : List.of(SqlLibrary.STANDARD, dialect);
    }

    SqlLibrary dialectOperatorLibrary() {
        return switch (driverId) {
            case "postgres", "cockroach" -> SqlLibrary.POSTGRESQL;
            case "redshift" -> SqlLibrary.REDSHIFT;
            case "mysql", "mariadb" -> SqlLibrary.MYSQL;
            case "sqlserver" -> SqlLibrary.MSSQL;
            case "oracle" -> SqlLibrary.ORACLE;
            case "clickhouse" -> SqlLibrary.CLICKHOUSE;
            default -> null;
        };
    }

    private String foldUnquoted(String value) {
        return switch (unquotedCasing) {
            case TO_LOWER -> value.toLowerCase(Locale.ROOT);
            case TO_UPPER -> value.toUpperCase(Locale.ROOT);
            default -> value;
        };
    }

    private static CaseDistinctIdentifiers caseDistinctIdentifiers(CatalogSnapshot snapshot) {
        Map<String, String> objectNames = new HashMap<>();
        boolean distinctObjects = false;
        boolean distinctColumns = false;
        for (CatalogObject object : snapshot.objects()) {
            String exactObjectName = String.join(
                "\u0000",
                object.id().catalog() == null ? "" : object.id().catalog(),
                object.id().schema() == null ? "" : object.id().schema(),
                object.id().name());
            if (hasCaseDistinctValue(objectNames, exactObjectName)) {
                distinctObjects = true;
            }

            Map<String, String> columnNames = new HashMap<>();
            for (CatalogColumn column : object.columns()) {
                if (hasCaseDistinctValue(columnNames, column.name())) {
                    distinctColumns = true;
                }
            }
        }
        return new CaseDistinctIdentifiers(distinctObjects, distinctColumns);
    }

    private static boolean hasCaseDistinctValue(Map<String, String> values, String exact) {
        String previous = values.putIfAbsent(exact.toLowerCase(Locale.ROOT), exact);
        return previous != null && !previous.equals(exact);
    }

    private record CaseDistinctIdentifiers(boolean objects, boolean columns) {
    }

    private static SqlDialectProfile profile(
        String driverId,
        Quoting quoting,
        Casing unquotedCasing,
        boolean caseSensitive,
        SqlConformance conformance) {
        return new SqlDialectProfile(
            driverId,
            quoting,
            unquotedCasing,
            caseSensitive,
            caseSensitive,
            caseSensitive,
            conformance);
    }
}
