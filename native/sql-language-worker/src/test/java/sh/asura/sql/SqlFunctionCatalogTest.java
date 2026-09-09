package sh.asura.sql;

import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.sql.SqlFunctionCategory;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.validate.SqlNameMatchers;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class SqlFunctionCatalogTest {
    @Test
    void modelsExactOptionalVariadicAndUnknownRoutineArity() throws Exception {
        CatalogSnapshot snapshot = snapshotWithRoutines(List.of(
            routine("f_zero", List.of(), 0, 0),
            routine("f_optional", List.of(
                parameter("required", false, false),
                parameter("optional_one", true, false),
                parameter("optional_two", true, false)), 1, 3),
            routine("f_variadic", List.of(
                parameter("required", false, false),
                parameter("rest", false, true)), 1, null),
            routine("f_unknown", List.of(), null, null)));
        SqlFunctionCatalog catalog = SqlFunctionCatalog.create(
            snapshot,
            SqlDialectProfile.forCatalog(snapshot),
            new JavaTypeFactoryImpl());

        assertRange(catalog, "f_zero", 0, 0);
        assertRange(catalog, "f_optional", 1, 3);
        assertRange(catalog, "f_variadic", 1, -1);
        assertRange(catalog, "f_unknown", 0, -1);
    }

    @Test
    void excludesNonExpressionAndInternallyNamedOperators() throws Exception {
        CatalogSnapshot snapshot = TestCatalogs.forDriver("oracle");
        SqlFunctionCatalog catalog = SqlFunctionCatalog.create(
            snapshot,
            SqlDialectProfile.forCatalog(snapshot),
            new JavaTypeFactoryImpl());
        List<String> labels = catalog.candidates().stream()
            .map(SqlFunctionCatalog.Candidate::label)
            .toList();

        assertFalse(labels.contains("DESCRIPTOR"));
        assertFalse(labels.contains("TUMBLE"));
        assertFalse(labels.contains("HOP"));
        assertFalse(labels.contains("SESSION"));
        assertFalse(labels.contains("TRANSLATE3"));
        assertTrue(labels.contains("TRANSLATE"));
    }

    @Test
    void resolvesRoutineLookupUsingDriverInvocationRules() throws Exception {
        SqlFunctionCatalog postgres = functionCatalog(new CatalogSnapshot(
            "postgres", "app", "public", List.of(), List.of(
                routine("app", "public", "public_func"),
                routine("app", "analytics", "other_func"))));
        SqlFunctionCatalog sqlServer = functionCatalog(new CatalogSnapshot(
            "sqlserver", "app", "dbo", List.of(), List.of(
                routine("app", "dbo", "server_func"))));
        SqlFunctionCatalog sqlite = functionCatalog(new CatalogSnapshot(
            "sqlite", null, "main", List.of(), List.of(
                routine(null, "main", "sqlite_func"))));

        assertTrue(hasRoutine(postgres, "public_func"));
        assertTrue(hasRoutine(postgres, "public", "public_func"));
        assertFalse(hasRoutine(postgres, "other_func"));
        assertTrue(hasRoutine(postgres, "analytics", "other_func"));
        assertFalse(hasRoutine(sqlServer, "server_func"));
        assertTrue(hasRoutine(sqlServer, "dbo", "server_func"));
        assertTrue(hasRoutine(sqlite, "sqlite_func"));
        assertFalse(hasRoutine(sqlite, "main", "sqlite_func"));
    }

    @Test
    void usesCatalogQualificationForAmbiguousDuckDbRoutines() throws Exception {
        SqlFunctionCatalog duckDb = functionCatalog(new CatalogSnapshot(
            "duckdb", "db1", "main", List.of(), List.of(
                routine("db1", "main", "shared_func"),
                routine("db2", "main", "shared_func"))));

        assertFalse(hasRoutine(duckDb, "shared_func"));
        assertFalse(hasRoutine(duckDb, "main", "shared_func"));
        assertTrue(hasRoutine(duckDb, "db1", "main", "shared_func"));
        assertTrue(hasRoutine(duckDb, "db2", "main", "shared_func"));
        assertEquals(
            List.of(
                List.of("db1", "main", "shared_func"),
                List.of("db2", "main", "shared_func")),
            duckDb.candidates().stream()
                .filter(SqlFunctionCatalog.Candidate::catalogOnly)
                .filter(candidate -> candidate.label().equalsIgnoreCase("shared_func"))
                .map(SqlFunctionCatalog.Candidate::nameParts)
                .toList());
    }

    @Test
    void omitsAmbiguousRoutineIdentityWhenDialectCannotExpressItsCatalog() throws Exception {
        SqlFunctionCatalog postgres = functionCatalog(new CatalogSnapshot(
            "postgres", "db1", "public", List.of(), List.of(
                routine("db1", "public", "shared_func"),
                routine("db2", "public", "shared_func"))));

        assertFalse(hasRoutine(postgres, "shared_func"));
        assertFalse(hasRoutine(postgres, "public", "shared_func"));
        assertTrue(postgres.candidates().stream()
            .noneMatch(candidate -> candidate.catalogOnly()
                && candidate.label().equalsIgnoreCase("shared_func")));
    }

    @Test
    void resolvesIntrinsicCollisionsByDialectOrSemanticEquivalence() throws Exception {
        CatalogSnapshot firebirdBase = TestCatalogs.forDriver("firebird");
        CatalogSnapshot firebirdSnapshot = new CatalogSnapshot(
            firebirdBase.driverId(),
            firebirdBase.defaultCatalog(),
            firebirdBase.defaultSchema(),
            firebirdBase.objects(),
            List.of(),
            CatalogMetadataCoverage.USER_DEFINED_ONLY,
            CatalogMetadataCoverage.COMPLETE,
            List.of(
                new CatalogIntrinsicSymbol("DATEADD", "keyword"),
                new CatalogIntrinsicSymbol("EXTRACT", "keyword")));
        SqlFunctionCatalog firebird = functionCatalog(firebirdSnapshot);
        List<SqlOperator> dateAdd = functionOperators(firebird, "DATEADD");

        assertEquals(1, dateAdd.size());
        assertEquals("SqlTimestampAddFunction", dateAdd.getFirst().getClass().getSimpleName());
        assertEquals(3, dateAdd.getFirst().getOperandCountRange().getMin());
        assertEquals(3, dateAdd.getFirst().getOperandCountRange().getMax());

        CatalogSnapshot oracleBase = TestCatalogs.forDriver("oracle");
        CatalogSnapshot oracleSnapshot = new CatalogSnapshot(
            oracleBase.driverId(),
            oracleBase.defaultCatalog(),
            oracleBase.defaultSchema(),
            oracleBase.objects(),
            List.of(),
            CatalogMetadataCoverage.USER_DEFINED_ONLY,
            CatalogMetadataCoverage.COMPLETE,
            List.of(new CatalogIntrinsicSymbol("CONCAT", "keyword")));
        List<SqlOperator> oracleConcat = functionOperators(
            functionCatalog(oracleSnapshot),
            "CONCAT");
        assertFalse(oracleConcat.isEmpty());
        assertTrue(oracleConcat.stream().allMatch(operator ->
            operator.getOperandCountRange().getMin() == 2
                && operator.getOperandCountRange().getMax() == 2));
    }

    private static void assertRange(
        SqlFunctionCatalog catalog,
        String name,
        int minimum,
        int maximum) {
        var operators = new ArrayList<SqlOperator>();
        catalog.operatorTable().lookupOperatorOverloads(
            new SqlIdentifier(name, SqlParserPos.ZERO),
            SqlFunctionCategory.USER_DEFINED_FUNCTION,
            SqlSyntax.FUNCTION,
            operators,
            SqlNameMatchers.withCaseSensitive(false));
        SqlOperator routine = operators.stream()
            .filter(operator -> operator.getName().equalsIgnoreCase(name))
            .findFirst()
            .orElseThrow();

        assertEquals(minimum, routine.getOperandCountRange().getMin(), name);
        assertEquals(maximum, routine.getOperandCountRange().getMax(), name);
    }

    private static SqlFunctionCatalog functionCatalog(CatalogSnapshot snapshot)
        throws Exception {
        return SqlFunctionCatalog.create(
            snapshot,
            SqlDialectProfile.forCatalog(snapshot),
            new JavaTypeFactoryImpl());
    }

    private static boolean hasRoutine(SqlFunctionCatalog catalog, String... path) {
        return !functionOperators(catalog, path).isEmpty();
    }

    private static List<SqlOperator> functionOperators(
        SqlFunctionCatalog catalog,
        String... path) {
        var operators = new ArrayList<SqlOperator>();
        catalog.operatorTable().lookupOperatorOverloads(
            new SqlIdentifier(List.of(path), SqlParserPos.ZERO),
            SqlFunctionCategory.USER_DEFINED_FUNCTION,
            SqlSyntax.FUNCTION,
            operators,
            SqlNameMatchers.withCaseSensitive(false));
        return operators.stream()
            .filter(operator -> operator.getName().equalsIgnoreCase(path[path.length - 1]))
            .toList();
    }

    private static CatalogSnapshot snapshotWithRoutines(List<CatalogRoutine> routines) {
        CatalogSnapshot base = TestCatalogs.postgres();
        return new CatalogSnapshot(
            base.driverId(),
            base.defaultCatalog(),
            base.defaultSchema(),
            base.objects(),
            routines);
    }

    private static CatalogRoutine routine(
        String name,
        List<CatalogRoutineParameter> parameters,
        Integer minimum,
        Integer maximum) {
        return new CatalogRoutine(
            new CatalogObjectId("app", "public", name),
            "scalar",
            name + "(...) RETURNS text",
            parameters,
            "text",
            "Text",
            minimum,
            maximum);
    }

    private static CatalogRoutine routine(
        String catalog,
        String schema,
        String name) {
        return new CatalogRoutine(
            new CatalogObjectId(catalog, schema, name),
            "scalar",
            name + "() RETURNS text",
            List.of(),
            "text",
            "Text",
            0,
            0);
    }

    private static CatalogRoutineParameter parameter(
        String name,
        boolean optional,
        boolean variadic) {
        return new CatalogRoutineParameter(
            name,
            "text",
            "Text",
            "in",
            optional,
            variadic);
    }
}
