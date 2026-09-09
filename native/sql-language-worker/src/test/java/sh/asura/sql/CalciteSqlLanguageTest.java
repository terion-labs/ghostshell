package sh.asura.sql;

import com.fasterxml.jackson.databind.JsonNode;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class CalciteSqlLanguageTest {
    private static final List<String> DRIVER_IDS = List.of(
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse");

    @Test
    void completesColumnsThroughATableAlias() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "SELECT p. FROM people p";

        JsonNode result = language.complete(sql, sql.indexOf("p.") + 2);

        assertTrue(labels(result).containsAll(Set.of("id", "name", "active", "*")));
        assertEquals(sql.indexOf(" FROM"), result.get("replacementStart").intValue());
        assertEquals(0, result.get("replacementLength").intValue());
    }

    @Test
    void completesClauseKeywordsAfterAQualifiedRelation() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM \"public\".\"articles\" W";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(labels(result).containsAll(Set.of("WHERE", "WINDOW")), result.toString());
        assertEquals(sql.length() - 1, result.get("replacementStart").intValue());
        assertEquals(1, result.get("replacementLength").intValue());
    }

    @ParameterizedTest(name = "{0} completes portable statement and clause keywords")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void completesPortableKeywordsForEveryBuiltInDriver(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        String table = driverId.equals("oracle") || driverId.equals("firebird")
            ? "PEOPLE"
            : "people";
        Map<String, String> cases = Map.ofEntries(
            Map.entry("sel", "SELECT"),
            Map.entry("SELECT * fr", "FROM"),
            Map.entry("SELECT id fr", "FROM"),
            Map.entry("SELECT * FROM " + table + " wh", "WHERE"),
            Map.entry("SELECT * FROM " + table + " p jo", "JOIN"),
            Map.entry("SELECT * FROM " + table + " gro", "GROUP"),
            Map.entry("SELECT * FROM " + table + " ord", "ORDER"),
            Map.entry("SELECT * FROM " + table + " hav", "HAVING"),
            Map.entry("SELECT id FROM " + table + " ORDER b", "BY"),
            Map.entry("SELECT id FROM " + table + " un", "UNION"),
            Map.entry("SELECT id FROM " + table + " ex", "EXCEPT"),
            Map.entry("SELECT id FROM " + table + " int", "INTERSECT"));

        for (var testCase : cases.entrySet()) {
            JsonNode result = completeAtEnd(language, testCase.getKey());
            JsonNode keyword = findItem(result, testCase.getValue());

            assertEquals("keyword", keyword.get("kind").textValue(),
                driverId + ": " + testCase.getKey() + ": " + result);
            assertEquals(
                testCase.getKey().lastIndexOf(' ') + 1,
                result.get("replacementStart").intValue(),
                driverId + ": " + testCase.getKey() + ": " + result);
        }

        Map<String, String> invalidExpressionHints = Map.of(
            "SELECT * WHERE fr", "FROM",
            "SELECT * WHERE ex", "EXCEPT",
            "SELECT * WHERE ord", "ORDER",
            "SELECT fr", "FROM",
            "SELECT ex", "EXCEPT",
            "SELECT int", "INTERSECT");
        for (var testCase : invalidExpressionHints.entrySet()) {
            JsonNode result = completeAtEnd(language, testCase.getKey());

            assertFalse(labels(result).contains(testCase.getValue()),
                driverId + ": " + testCase.getKey() + ": " + result);
        }
    }

    @Test
    void doesNotInjectClauseKeywordsIntoPredicateExpressions() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        JsonNode result = completeAtEnd(
            language,
            "SELECT * FROM people WHERE fr");

        assertFalse(labels(result).contains("FROM"), result.toString());
    }

    @Test
    void completesColumnsWithoutSuggestingRelationsInExpressionClauses() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());

        for (String sql : List.of(
            "SELECT * FROM public.articles WHERE ",
            "SELECT * FROM public.articles ORDER BY ",
            "SELECT * FROM public.articles GROUP BY ")) {
            JsonNode result = language.complete(sql, sql.length());

            assertTrue(
                labels(result).containsAll(Set.of("id", "title", "content")),
                sql + ": " + result);
            assertNoRelationOnlyItems(sql, result);
        }
    }

    @Test
    void filtersQualifiedColumnFallbackByTheTypedPrefix() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM public.articles a WHERE a.ti";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(labels(result).contains("title"), result.toString());
        assertFalse(labels(result).contains("id"), result.toString());
        assertFalse(labels(result).contains("content"), result.toString());
        assertEquals(sql.length() - 2, result.get("replacementStart").intValue());
        assertEquals(2, result.get("replacementLength").intValue());
    }

    @Test
    void completesAnAliasQualifierInsideAWhereExpression() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM public.articles a WHERE a.";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(labels(result).containsAll(Set.of("id", "title", "content")),
            result.toString());
        assertNoRelationOnlyItems(sql, result);
        assertQualifiedMemberItems(sql, result);
    }

    @Test
    void completesPreferredObjectColumnsOnlyWhenSqlHasNoRelationScope() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        CatalogObjectId articles = new CatalogObjectId("app", "public", "articles");

        for (String sql : List.of(
            "SELECT ti",
            "SELECT * WHERE ti",
            "SELECT 1 WHERE ti",
            "WHERE ti",
            "COALESCE(ti")) {
            JsonNode result = language.complete(sql, sql.length(), articles);

            assertTrue(labels(result).contains("title"), sql + ": " + result);
            assertFalse(labels(result).contains("id"), sql + ": " + result);
            assertFalse(labels(result).contains("content"), sql + ": " + result);
        }

        JsonNode emptyExpression = language.complete("SELECT ", 7, articles);
        assertTrue(
            labels(emptyExpression).containsAll(Set.of("id", "title", "content")),
            emptyExpression.toString());
    }

    @Test
    void completesPreferredObjectNameAndMemberPrefixesWithoutOfferingATableValue()
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        CatalogObjectId articles = new CatalogObjectId("app", "public", "articles");

        JsonNode namePrefix = completeAtEnd(language, "SELECT arti", articles);
        JsonNode member = completeAtEnd(language, "SELECT articles.", articles);
        JsonNode memberPrefix = completeAtEnd(language, "SELECT articles.ti", articles);

        assertTrue(insertTexts(namePrefix).containsAll(Set.of(
            "articles.id", "articles.title", "articles.content")),
            namePrefix.toString());
        assertFalse(labels(namePrefix).contains("articles"), namePrefix.toString());
        assertNoRelationOnlyItems("preferred name prefix", namePrefix);
        assertTrue(labels(member).containsAll(Set.of("id", "title", "content")),
            member.toString());
        assertEquals(Set.of("title"), columnLabels(memberPrefix), memberPrefix.toString());
        assertQualifiedMemberItems("preferred member prefix", memberPrefix);
    }

    @Test
    void explicitResolvedOrUnresolvedRelationsAlwaysOverridePreferredObject() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        CatalogObjectId articles = new CatalogObjectId("app", "public", "articles");

        JsonNode explicitOther = completeAtEnd(
            language,
            "SELECT * FROM public.authors WHERE ti",
            articles);
        JsonNode explicitOtherColumn = completeAtEnd(
            language,
            "SELECT * FROM public.authors WHERE na",
            articles);
        JsonNode unresolved = completeAtEnd(
            language,
            "SELECT * FROM missing_relation WHERE ti",
            articles);
        JsonNode unrelatedQualifier = completeAtEnd(
            language,
            "SELECT other.",
            articles);

        assertFalse(labels(explicitOther).contains("title"), explicitOther.toString());
        assertTrue(labels(explicitOtherColumn).contains("name"), explicitOtherColumn.toString());
        assertFalse(labels(unresolved).contains("title"), unresolved.toString());
        assertTrue(columnLabels(unrelatedQualifier).isEmpty(), unrelatedQualifier.toString());
    }

    @Test
    void expandsExplicitRelationPrefixesWithoutHidingMatchingColumnPrefixes()
        throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "postgres",
            "app",
            "public",
            List.of(new CatalogObject(
                new CatalogObjectId("app", "public", "articles"),
                "Table",
                List.of(
                    TestCatalogs.column("id", "bigint", "SignedInteger", false),
                    TestCatalogs.column("author", "text", "Text", false)))));
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        String sql = "SELECT * FROM public.articles WHERE a";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(insertTexts(result).contains("author"), result.toString());
        assertTrue(insertTexts(result).containsAll(Set.of(
            "articles.id", "articles.author")), result.toString());
    }

    @Test
    void completesPartiallyTypedStandardFunctionsInExpressions() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT CO FROM public.articles";
        int cursor = sql.indexOf("CO") + 2;

        JsonNode result = language.complete(sql, cursor);
        JsonNode coalesce = findItem(result, "COALESCE");

        assertEquals("function", coalesce.get("kind").textValue());
        assertEquals("COALESCE(", coalesce.get("insertText").textValue());
        assertTrue(coalesce.get("detail").textValue().contains("COALESCE"));
        assertNoRelationOnlyItems(sql, result);
    }

    @Test
    void getsFunctionNamesFromTheConfiguredCalciteDialectLibrary()
        throws Exception {
        CalciteSqlLanguage postgres = CalciteSqlLanguage.create(TestCatalogs.postgres());
        CalciteSqlLanguage sqlServer = CalciteSqlLanguage.create(
            TestCatalogs.forDriver("sqlserver"));
        CalciteSqlLanguage oracle = CalciteSqlLanguage.create(
            TestCatalogs.forDriver("oracle"));

        assertEquals("function", findItem(completeAtEnd(
            postgres,
            "SELECT * FROM people WHERE date_p"), "DATE_PART").get("kind").textValue());
        assertFalse(labels(completeAtEnd(
            postgres,
            "SELECT * FROM people WHERE datea")).contains("DATEADD"));
        assertEquals("function", findItem(completeAtEnd(
            sqlServer,
            "SELECT * FROM people WHERE datea"), "DATEADD").get("kind").textValue());
        assertEquals("function", findItem(completeAtEnd(
            oracle,
            "SELECT * FROM PEOPLE WHERE nv"), "NVL").get("kind").textValue());
    }

    @Test
    void requiresPositiveProvenanceForStandardAndGrammarFunctions() throws Exception {
        for (String driverId : DRIVER_IDS) {
            CatalogSnapshot base = TestCatalogs.forDriver(driverId);
            CalciteSqlLanguage language = CalciteSqlLanguage.create(withoutFunctionEvidence(base));
            String table = driverId.equals("oracle") || driverId.equals("firebird")
                ? "PEOPLE"
                : "people";
            for (String candidate : List.of(
                "COALESCE", "CURRENT_TIMESTAMP", "DAYOFMONTH", "GROUP_ID", "WEEK", "YEAR")) {
                String prefix = candidate.substring(0, Math.min(5, candidate.length()));
                JsonNode result = completeAtEnd(
                    language,
                    "SELECT * FROM " + table + " WHERE " + prefix);
                assertFalse(labels(result).contains(candidate),
                    driverId + " exposed uncorroborated " + candidate + ": " + result);
            }
        }
    }

    @Test
    void omitsKnownInvalidStandardCandidatesWithoutServerEvidence() throws Exception {
        Map<String, List<String>> invalidByDriver = Map.of(
            "mysql", List.of("CBRT"),
            "mariadb", List.of("CBRT"),
            "firebird", List.of("CBRT", "CONCAT"),
            "oracle", List.of("CBRT", "PI", "RAND"));

        for (var entry : invalidByDriver.entrySet()) {
            CatalogSnapshot base = TestCatalogs.forDriver(entry.getKey());
            CalciteSqlLanguage language = CalciteSqlLanguage.create(withoutFunctionEvidence(base));
            String table = entry.getKey().equals("oracle") || entry.getKey().equals("firebird")
                ? "PEOPLE"
                : "people";
            for (String candidate : entry.getValue()) {
                JsonNode result = completeAtEnd(
                    language,
                    "SELECT * FROM " + table + " WHERE "
                        + candidate.substring(0, Math.min(3, candidate.length())));
                assertFalse(labels(result).contains(candidate),
                    entry.getKey() + " exposed invalid " + candidate + ": " + result);
            }
        }
    }

    @Test
    void intrinsicSymbolsOnlyCorroborateExistingCalciteOperators() throws Exception {
        CatalogSnapshot base = withoutFunctionEvidence(TestCatalogs.forDriver("firebird"));
        CatalogSnapshot snapshot = new CatalogSnapshot(
            base.driverId(),
            base.defaultCatalog(),
            base.defaultSchema(),
            base.objects(),
            base.routines(),
            CatalogMetadataCoverage.USER_DEFINED_ONLY,
            CatalogMetadataCoverage.COMPLETE,
            List.of(
                new CatalogIntrinsicSymbol("DATEADD", "keyword"),
                new CatalogIntrinsicSymbol("EXTRACT", "keyword"),
                new CatalogIntrinsicSymbol("REGEXP_REPLACE", "keyword"),
                new CatalogIntrinsicSymbol("TRANSLATE3", "keyword"),
                new CatalogIntrinsicSymbol("NOT_A_CALCITE_FUNCTION", "keyword")));
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);

        JsonNode dateAdd = completeAtEnd(
            language,
            "SELECT * FROM PEOPLE WHERE datea");
        assertEquals(
            "DATEADD(",
            findItem(dateAdd, "DATEADD").get("insertText").textValue(),
            dateAdd.toString());
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM PEOPLE WHERE not_a")).contains("NOT_A_CALCITE_FUNCTION"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM PEOPLE WHERE extr")).contains("EXTRACT"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM PEOPLE WHERE regexp_r")).contains("REGEXP_REPLACE"));
        JsonNode internalName = completeAtEnd(
            language,
            "SELECT * FROM PEOPLE WHERE translate3");
        assertFalse(labels(internalName).contains("TRANSLATE"), internalName.toString());
        assertFalse(labels(internalName).contains("TRANSLATE3"), internalName.toString());
        assertTrue(language.diagnose(
            "SELECT DATEADD(DAY, 1, CURRENT_TIMESTAMP) FROM PEOPLE")
            .get("items").isEmpty());
    }

    @Test
    void completeRoutineCoverageRequiresCorroborationForDialectCallables() throws Exception {
        CatalogSnapshot base = TestCatalogs.forDriver("sqlserver");
        CalciteSqlLanguage incomplete = CalciteSqlLanguage.create(base);
        CatalogSnapshot completeSnapshot = new CatalogSnapshot(
            base.driverId(),
            base.defaultCatalog(),
            base.defaultSchema(),
            base.objects(),
            base.routines(),
            CatalogMetadataCoverage.COMPLETE,
            base.intrinsicCoverage(),
            base.intrinsicSymbols());
        CalciteSqlLanguage complete = CalciteSqlLanguage.create(completeSnapshot);

        String sql = "SELECT * FROM people WHERE datea";
        assertTrue(labels(completeAtEnd(incomplete, sql)).contains("DATEADD"));
        assertFalse(labels(completeAtEnd(complete, sql)).contains("DATEADD"));
    }

    @Test
    void usesIndependentCoverageAuthorityForCallablesAndBareValues() throws Exception {
        CatalogSnapshot sqlServerBase = TestCatalogs.forDriver("sqlserver");
        CalciteSqlLanguage sqlServer = CalciteSqlLanguage.create(new CatalogSnapshot(
            sqlServerBase.driverId(),
            sqlServerBase.defaultCatalog(),
            sqlServerBase.defaultSchema(),
            sqlServerBase.objects(),
            List.of(),
            CatalogMetadataCoverage.USER_DEFINED_ONLY,
            CatalogMetadataCoverage.PARTIAL,
            List.of()));
        JsonNode dateAdd = completeAtEnd(
            sqlServer,
            "SELECT * FROM people WHERE datea");
        assertTrue(labels(dateAdd).contains("DATEADD"), dateAdd.toString());

        CalciteSqlLanguage sqlServerWithCompleteIntrinsics = CalciteSqlLanguage.create(
            new CatalogSnapshot(
                sqlServerBase.driverId(),
                sqlServerBase.defaultCatalog(),
                sqlServerBase.defaultSchema(),
                sqlServerBase.objects(),
                List.of(),
                CatalogMetadataCoverage.USER_DEFINED_ONLY,
                CatalogMetadataCoverage.COMPLETE,
                List.of()));
        JsonNode authoritativeCallableAbsence = completeAtEnd(
            sqlServerWithCompleteIntrinsics,
            "SELECT * FROM people WHERE datea");
        assertFalse(labels(authoritativeCallableAbsence).contains("DATEADD"),
            authoritativeCallableAbsence.toString());

        CatalogSnapshot oracleBase = TestCatalogs.forDriver("oracle");
        CalciteSqlLanguage oracle = CalciteSqlLanguage.create(new CatalogSnapshot(
            oracleBase.driverId(),
            oracleBase.defaultCatalog(),
            oracleBase.defaultSchema(),
            oracleBase.objects(),
            List.of(),
            CatalogMetadataCoverage.COMPLETE,
            CatalogMetadataCoverage.PARTIAL,
            List.of(new CatalogIntrinsicSymbol("SYSDATE", "keyword"))));
        JsonNode sysdate = completeAtEnd(
            oracle,
            "SELECT * FROM PEOPLE WHERE sysd");
        assertTrue(labels(sysdate).contains("SYSDATE"), sysdate.toString());

        CalciteSqlLanguage oracleWithCompleteIntrinsics = CalciteSqlLanguage.create(
            new CatalogSnapshot(
                oracleBase.driverId(),
                oracleBase.defaultCatalog(),
                oracleBase.defaultSchema(),
                oracleBase.objects(),
                List.of(),
                CatalogMetadataCoverage.USER_DEFINED_ONLY,
                CatalogMetadataCoverage.COMPLETE,
                List.of()));
        JsonNode authoritativeAbsence = completeAtEnd(
            oracleWithCompleteIntrinsics,
            "SELECT * FROM PEOPLE WHERE sysd");
        assertFalse(labels(authoritativeAbsence).contains("SYSDATE"),
            authoritativeAbsence.toString());
    }

    @ParameterizedTest(name = "{0} fails closed for uncorroborated standard functions")
    @ValueSource(strings = {"sqlserver", "redshift"})
    void productionCoverageOmitsUnprovenStandardFunctionsButKeepsDialectLibrary(
        String driverId) throws Exception {
        CatalogSnapshot base = TestCatalogs.forDriver(driverId);
        CatalogMetadataCoverage routineCoverage = driverId.equals("sqlserver")
            ? CatalogMetadataCoverage.USER_DEFINED_ONLY
            : CatalogMetadataCoverage.NONE;
        CalciteSqlLanguage language = CalciteSqlLanguage.create(new CatalogSnapshot(
            base.driverId(),
            base.defaultCatalog(),
            base.defaultSchema(),
            base.objects(),
            List.of(),
            routineCoverage,
            CatalogMetadataCoverage.NONE,
            List.of()));
        String table = "people";

        JsonNode dialect = completeAtEnd(
            language,
            "SELECT * FROM " + table + " WHERE datea");
        assertTrue(labels(dialect).contains("DATEADD"), driverId + ": " + dialect);

        for (String unproven : List.of(
            "COALESCE", "COUNT", "CEIL", "CHAR_LENGTH", "MOD", "EVERY", "SOME",
            "REGR_COUNT", "GROUP_ID", "DAYOFMONTH")) {
            String prefix = unproven.substring(0, Math.min(5, unproven.length()));
            JsonNode result = completeAtEnd(
                language,
                "SELECT * FROM " + table + " WHERE " + prefix);
            assertFalse(labels(result).contains(unproven),
                driverId + " exposed unproven " + unproven + ": " + result);
        }
    }

    @ParameterizedTest(name = "{0} derives expression functions from Calcite metadata")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void derivesFunctionCompletionForEveryBuiltInDriver(String driverId)
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        String table = driverId.equals("oracle") || driverId.equals("firebird")
            ? "PEOPLE"
            : "people";
        JsonNode result = completeAtEnd(
            language,
            "SELECT * FROM " + table + " WHERE coa");
        JsonNode coalesce = findItem(result, "COALESCE");

        assertEquals("function", coalesce.get("kind").textValue(), driverId + ": " + result);
        assertEquals("COALESCE(", coalesce.get("insertText").textValue(),
            driverId + ": " + result);
        assertTrue(coalesce.hasNonNull("detail"), driverId + ": " + result);
        assertNoRelationOnlyItems(driverId, result);
    }

    @ParameterizedTest(name = "{0} gates aggregate functions by SQL clause")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void gatesAggregateFunctionsForEveryBuiltInDriver(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        String table = driverId.equals("oracle") || driverId.equals("firebird")
            ? "PEOPLE"
            : "people";

        String selectSql = "SELECT cou FROM " + table;
        JsonNode selectResult = language.complete(selectSql, "SELECT cou".length());
        JsonNode selectCount = findItem(selectResult, "COUNT");
        assertEquals("function", selectCount.get("kind").textValue(),
            driverId + ": " + selectResult);
        assertEquals("COUNT(", selectCount.get("insertText").textValue(),
            driverId + ": " + selectResult);

        for (String sql : List.of(
            "SELECT id FROM " + table + " HAVING cou",
            "SELECT id FROM " + table + " ORDER BY cou")) {
            JsonNode result = completeAtEnd(language, sql);
            JsonNode count = findItem(result, "COUNT");
            assertEquals("function", count.get("kind").textValue(), driverId + ": " + result);
            assertEquals("COUNT(", count.get("insertText").textValue(),
                driverId + ": " + result);
        }

        for (String sql : List.of(
            "SELECT * FROM " + table + " WHERE cou",
            "SELECT * FROM " + table + " p JOIN " + table + " q ON cou",
            "SELECT id FROM " + table + " GROUP BY cou")) {
            JsonNode result = completeAtEnd(language, sql);
            assertFalse(labels(result).contains("COUNT"), driverId + ": " + result);
        }
    }

    @Test
    void mergesScalarAggregateAndWindowRoutineSemanticsWithoutMakingCountScalar()
        throws Exception {
        CatalogSnapshot base = TestCatalogs.forDriver("sqlite");
        CatalogSnapshot snapshot = new CatalogSnapshot(
            base.driverId(),
            base.defaultCatalog(),
            base.defaultSchema(),
            base.objects(),
            List.of(
                routineWithKind(null, "main", "count", "window", "count(value)"),
                routineWithKind(null, "main", "aggregate_only", "aggregate",
                    "aggregate_only(value)"),
                routineWithKind(null, "main", "window_only", "window",
                    "window_only() OVER (...)"),
                routineWithKind(null, "main", "row_number", "window",
                    "row_number() OVER (...)"),
                routineWithKind(null, "main", "dual_mode", "aggregate",
                    "dual_mode(value)"),
                routineWithKind(null, "main", "dual_mode", "scalar",
                    "dual_mode(value, fallback)")),
            CatalogMetadataCoverage.COMPLETE,
            CatalogMetadataCoverage.COMPLETE,
            List.of(
                new CatalogIntrinsicSymbol("COUNT", "keyword"),
                new CatalogIntrinsicSymbol("ROW_NUMBER", "keyword")));
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);

        for (String sql : List.of(
            "SELECT cou FROM people",
            "SELECT id FROM people HAVING cou",
            "SELECT id FROM people ORDER BY cou")) {
            JsonNode result = sql.startsWith("SELECT cou")
                ? language.complete(sql, "SELECT cou".length())
                : completeAtEnd(language, sql);
            assertTrue(labels(result).contains("COUNT"), sql + ": " + result);
        }
        for (String sql : List.of(
            "SELECT * FROM people WHERE cou",
            "SELECT * FROM people p JOIN people q ON cou",
            "SELECT id FROM people GROUP BY cou")) {
            JsonNode result = completeAtEnd(language, sql);
            assertFalse(labels(result).contains("COUNT"), sql + ": " + result);
        }

        assertTrue(labels(completeAtEnd(
            language,
            "SELECT id FROM people HAVING aggregate_o")).contains("aggregate_only"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM people WHERE aggregate_o")).contains("aggregate_only"));
        assertTrue(labels(completeAtEnd(
            language,
            "SELECT id FROM people ORDER BY window_o")).contains("window_only"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT id FROM people HAVING window_o")).contains("window_only"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM people WHERE window_o")).contains("window_only"));
        assertTrue(labels(completeAtEnd(
            language,
            "SELECT id FROM people ORDER BY row_n")).contains("ROW_NUMBER"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT id FROM people HAVING row_n")).contains("ROW_NUMBER"));
        assertFalse(labels(completeAtEnd(
            language,
            "SELECT * FROM people WHERE row_n")).contains("ROW_NUMBER"));
        assertTrue(labels(completeAtEnd(
            language,
            "SELECT * FROM people WHERE dual_m")).contains("dual_mode"));
    }

    @Test
    void completesCalciteValueFunctionsAndPostgresTypesFromMetadata() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(postgresFunctionCatalog());
        String currentSql = "SELECT * FROM public.articles WHERE \"timestamp\" < cur";
        String typeSql = "SELECT \"timestamp\"::inter FROM public.articles";

        JsonNode current = completeAtEnd(language, currentSql);
        JsonNode currentTimestamp = findItem(current, "CURRENT_TIMESTAMP");
        JsonNode interval = language.complete(typeSql, typeSql.indexOf("inter") + 5);

        assertEquals("keyword", currentTimestamp.get("kind").textValue());
        assertEquals("CURRENT_TIMESTAMP", currentTimestamp.get("insertText").textValue());
        assertEquals(currentSql.length() - 3, current.get("replacementStart").intValue());
        assertEquals(3, current.get("replacementLength").intValue());
        JsonNode intervalType = findItem(interval, "INTERVAL");
        assertEquals("dataType", intervalType.get("kind").textValue());
        assertEquals("type", intervalType.get("detail").textValue());
        assertEquals("INTERVAL", intervalType.get("insertText").textValue());
        for (JsonNode item : interval.get("items")) {
            assertEquals("dataType", item.get("kind").textValue(), interval.toString());
        }
    }

    @Test
    void completesServerDiscoveredPostgresRoutinesWithoutDatabaseQualification()
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(postgresFunctionCatalog());
        String sql = "SELECT * FROM public.articles WHERE date_a";

        JsonNode result = completeAtEnd(language, sql);
        JsonNode dateAdd = findItem(result, "date_add");

        assertEquals("function", dateAdd.get("kind").textValue());
        assertEquals("date_add(", dateAdd.get("insertText").textValue());
        assertTrue(dateAdd.get("detail").textValue().contains("date_add"));
        assertTrue(language.diagnose(
            "SELECT date_add(timestamp, INTERVAL '1 day') FROM public.articles")
            .get("items").isEmpty());

        CalciteSqlLanguage withoutRoutine = CalciteSqlLanguage.create(
            TestCatalogs.editorPostgres());
        assertFalse(labels(completeAtEnd(
            withoutRoutine,
            "SELECT * FROM public.articles WHERE date_a")).contains("date_add"));
    }

    @Test
    void qualifiesRoutineInsertionsAccordingToProviderInvocationRules() throws Exception {
        CalciteSqlLanguage postgres = CalciteSqlLanguage.create(routineCatalog(
            "postgres",
            "app",
            "public",
            List.of(
                routine("app", "public", "public_func"),
                routine("app", "pg_catalog", "system_func"),
                routine("app", "analytics", "other_func"))));
        CalciteSqlLanguage sqlServer = CalciteSqlLanguage.create(routineCatalog(
            "sqlserver",
            "app",
            "dbo",
            List.of(routine("app", "dbo", "server_func"))));
        CalciteSqlLanguage sqlite = CalciteSqlLanguage.create(routineCatalog(
            "sqlite",
            null,
            "main",
            List.of(routine(null, "main", "sqlite_func"))));

        assertEquals("public_func(", findItem(
            completeAtEnd(postgres, "SELECT public_f"),
            "public_func").get("insertText").textValue());
        assertEquals("system_func(", findItem(
            completeAtEnd(postgres, "SELECT system_f"),
            "system_func").get("insertText").textValue());
        assertEquals("analytics.other_func(", findItem(
            completeAtEnd(postgres, "SELECT other_f"),
            "other_func").get("insertText").textValue());
        String qualifiedPostgresSql = "SELECT analytics.other_f";
        JsonNode qualifiedPostgres = completeAtEnd(postgres, qualifiedPostgresSql);
        assertEquals("other_func(", findItem(
            qualifiedPostgres,
            "other_func").get("insertText").textValue());
        assertEquals(qualifiedPostgresSql.indexOf("other_f"),
            qualifiedPostgres.get("replacementStart").intValue());
        assertEquals("other_f".length(),
            qualifiedPostgres.get("replacementLength").intValue());
        assertEquals("dbo.server_func(", findItem(
            completeAtEnd(sqlServer, "SELECT server_f"),
            "server_func").get("insertText").textValue());
        String qualifiedSqlServerSql = "SELECT dbo.server_f";
        JsonNode qualifiedSqlServer = completeAtEnd(sqlServer, qualifiedSqlServerSql);
        assertEquals("server_func(", findItem(
            qualifiedSqlServer,
            "server_func").get("insertText").textValue());
        assertEquals(qualifiedSqlServerSql.indexOf("server_f"),
            qualifiedSqlServer.get("replacementStart").intValue());
        assertEquals("sqlite_func(", findItem(
            completeAtEnd(sqlite, "SELECT sqlite_f"),
            "sqlite_func").get("insertText").textValue());
    }

    @Test
    void completesAmbiguousDuckDbRoutinesWithCatalogQualification() throws Exception {
        CalciteSqlLanguage duckDb = CalciteSqlLanguage.create(routineCatalog(
            "duckdb",
            "db1",
            "main",
            List.of(
                routine("db1", "main", "shared_func"),
                routine("db2", "main", "shared_func"))));

        JsonNode unqualified = completeAtEnd(duckDb, "SELECT shared_f");
        assertEquals(
            Set.of("db1.main.shared_func(", "db2.main.shared_func("),
            insertTextsForLabel(unqualified, "shared_func"),
            unqualified.toString());

        String qualifiedSql = "SELECT db2.main.shared_f";
        JsonNode qualified = completeAtEnd(duckDb, qualifiedSql);
        JsonNode sharedFunction = findItem(qualified, "shared_func");
        assertEquals("shared_func(", sharedFunction.get("insertText").textValue());
        assertEquals(
            qualifiedSql.indexOf("shared_f"),
            qualified.get("replacementStart").intValue());
        assertEquals("shared_f".length(), qualified.get("replacementLength").intValue());
    }

    @Test
    void resolvesBareAndCallableMetadataConflictsWithoutNameExceptions() throws Exception {
        CalciteSqlLanguage sqlite = CalciteSqlLanguage.create(
            TestCatalogs.withIntrinsicSymbols(routineCatalog(
                "sqlite",
                null,
                "main",
                List.of(
                    routine(null, "main", "current_timestamp"),
                    routine(null, "main", "pi"))),
                "CURRENT_TIMESTAMP"));
        JsonNode current = completeAtEnd(sqlite, "SELECT cur");
        JsonNode pi = completeAtEnd(sqlite, "SELECT pi");

        assertEquals(
            Set.of("CURRENT_TIMESTAMP"),
            insertTextsForLabel(current, "CURRENT_TIMESTAMP"),
            current.toString());
        assertEquals(Set.of("PI("), insertTextsForLabel(pi, "PI"), pi.toString());

        CalciteSqlLanguage postgres = CalciteSqlLanguage.create(
            TestCatalogs.withIntrinsicSymbols(
                routineCatalog(
                    "postgres",
                    "app",
                    "public",
                    List.of(routine("app", "custom", "current_timestamp"))),
                "CURRENT_TIMESTAMP"));
        JsonNode qualified = completeAtEnd(postgres, "SELECT current_t");
        assertTrue(insertTextsForLabel(qualified, "CURRENT_TIMESTAMP").contains(
            "CURRENT_TIMESTAMP"), qualified.toString());
        assertTrue(insertTextsForLabel(qualified, "current_timestamp").contains(
            "custom.\"current_timestamp\"("), qualified.toString());
    }

    @Test
    void callableInsertionDoesNotDuplicateAnExistingParenthesisAcrossWhitespace()
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "SELECT CO   (name) FROM people";
        int cursor = sql.indexOf("CO") + 2;

        JsonNode result = language.complete(sql, cursor);

        assertEquals("COALESCE", findItem(result, "COALESCE").get("insertText").textValue());
    }

    @Test
    void doesNotOfferFunctionsInRelationsMembersStringsOrComments() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        Map<String, Integer> cases = Map.of(
            "SELECT * FROM coa", "SELECT * FROM coa".length(),
            "SELECT * FROM people WHERE missing.coa", "SELECT * FROM people WHERE missing.coa".length(),
            "SELECT * FROM people WHERE 'coa'", "SELECT * FROM people WHERE 'coa'".indexOf("coa") + 3,
            "SELECT * FROM people -- coa", "SELECT * FROM people -- coa".length(),
            "SELECT * FROM people /* coa */", "SELECT * FROM people /* coa */".indexOf("coa") + 3,
            "SELECT \"coa\" FROM people", "SELECT \"coa\" FROM people".indexOf("coa") + 3);

        for (var testCase : cases.entrySet()) {
            JsonNode result = language.complete(testCase.getKey(), testCase.getValue());
            assertFalse(labels(result).contains("COALESCE"), testCase.getKey() + ": " + result);
        }
    }

    @Test
    void keepsTableAndSchemaCandidatesInRelationPositions() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String fromSql = "SELECT * FROM \"public\".";
        String joinSql = "SELECT * FROM public.articles a JOIN ";

        JsonNode fromResult = language.complete(fromSql, fromSql.length());
        JsonNode joinResult = language.complete(joinSql, joinSql.length());

        assertTrue(labels(fromResult).containsAll(Set.of("articles", "authors")),
            fromResult.toString());
        assertTrue(labels(joinResult).containsAll(Set.of("articles", "authors")),
            joinResult.toString());
        assertTrue(hasRelationOnlyItem(joinResult), joinResult.toString());
    }

    @Test
    void completesQualifiedColumnsInsteadOfTableAliasesInJoinConditions() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM public.articles a "
            + "JOIN public.authors u ON ";

        JsonNode result = language.complete(sql, sql.length());
        Set<String> insertTexts = insertTexts(result);

        assertTrue(insertTexts.containsAll(Set.of("a.id", "a.title", "u.id", "u.name")),
            result.toString());
        assertNoRelationOnlyItems(sql, result);
    }

    @Test
    void usesTheTypedAliasPrefixForJoinExpressionFallback() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM public.articles p "
            + "JOIN public.authors o ON p";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(insertTexts(result).containsAll(Set.of("p.id", "p.title")),
            result.toString());
        assertFalse(insertTexts(result).contains("o.name"), result.toString());
        assertFalse(labels(result).contains("PREV"), result.toString());
    }

    @Test
    void classifiesRelationCompletionInsideANestedSubquery() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String sql = "SELECT * FROM public.articles a "
            + "WHERE EXISTS (SELECT 1 FROM ";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(labels(result).containsAll(Set.of("articles", "authors")),
            result.toString());
        assertTrue(hasRelationOnlyItem(result), result.toString());
    }

    @Test
    void preservesExpressionCompletionInsideParenthesesAndFunctionArguments() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        for (String sql : List.of(
            "SELECT * FROM public.articles a WHERE (",
            "SELECT * FROM public.articles a WHERE COALESCE(")) {
            JsonNode result = language.complete(sql, sql.length());

            assertTrue(labels(result).containsAll(Set.of("id", "title", "content")),
                sql + ": " + result);
            assertNoRelationOnlyItems(sql, result);
        }

        String qualifiedSql =
            "SELECT * FROM public.articles a WHERE COALESCE(a.";
        JsonNode qualified = language.complete(qualifiedSql, qualifiedSql.length());
        assertTrue(labels(qualified).containsAll(Set.of("id", "title", "content")),
            qualified.toString());
        assertQualifiedMemberItems(qualifiedSql, qualified);
    }

    @Test
    void doesNotLeakFallbackColumnsAcrossSetOperandsOrStatements() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        String setSql = "SELECT title FROM public.articles a UNION ALL "
            + "SELECT na FROM public.authors u";
        String statementSql = "SELECT title FROM public.articles a; "
            + "SELECT na FROM public.authors u";
        for (String sql : List.of(setSql, statementSql)) {
            int cursor = sql.indexOf("na FROM") + 2;

            JsonNode result = language.complete(sql, cursor);

            assertFalse(insertTexts(result).contains("a.title"), sql + ": " + result);
            assertFalse(labels(result).contains("title"), sql + ": " + result);
            if (sql.equals(setSql)) {
                assertTrue(labels(result).contains("name"), sql + ": " + result);
            }
        }
    }

    @ParameterizedTest(name = "{0} completes unqualified expression columns")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void completesExpressionColumnsForEveryBuiltInDriver(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        boolean upper = driverId.equals("oracle") || driverId.equals("firebird");
        String table = upper ? "PEOPLE" : "people";
        String id = upper ? "ID" : "id";
        String name = upper ? "NAME" : "name";
        String sql = "SELECT * FROM " + table + " p WHERE ";

        JsonNode result = language.complete(sql, sql.length());

        assertTrue(labels(result).containsAll(Set.of(id, name)), driverId + ": " + result);
        assertNoRelationOnlyItems(driverId, result);
    }

    @ParameterizedTest(name = "{0} honors request-scoped preferred object completion")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void completesPreferredObjectForEveryBuiltInDriver(String driverId) throws Exception {
        CatalogSnapshot snapshot = TestCatalogs.forDriver(driverId);
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        CatalogObject selected = snapshot.objects().getFirst();
        CatalogObjectId requestId = driverId.equals("sqlite")
            ? new CatalogObjectId(null, null, selected.id().name())
            : selected.id();
        boolean upper = driverId.equals("oracle") || driverId.equals("firebird");
        String name = upper ? "NAME" : "name";
        String tablePrefix = upper ? "PE" : "pe";

        JsonNode expression = completeAtEnd(language, "SELECT na", requestId);
        JsonNode selectedPrefix = completeAtEnd(
            language,
            "SELECT " + tablePrefix,
            requestId);
        JsonNode selectedMember = completeAtEnd(
            language,
            "SELECT " + selected.id().name() + ".",
            requestId);
        JsonNode unresolved = completeAtEnd(
            language,
            "SELECT * FROM definitely_missing WHERE na",
            requestId);
        JsonNode explicit = completeAtEnd(
            language,
            "SELECT * FROM " + selected.id().name() + " WHERE " + tablePrefix,
            requestId);

        assertTrue(labels(expression).contains(name), driverId + ": " + expression);
        assertTrue(
            columnLabels(selectedPrefix).stream().allMatch(
                label -> label.startsWith(selected.id().name() + ".")),
            driverId + ": " + selectedPrefix);
        assertEquals(2, columnLabels(selectedPrefix).size(),
            driverId + ": " + selectedPrefix);
        assertTrue(columnLabels(selectedMember).containsAll(Set.of(
            upper ? "ID" : "id",
            name)), driverId + ": " + selectedMember);
        assertFalse(labels(unresolved).contains(name), driverId + ": " + unresolved);
        assertEquals(2, columnLabels(explicit).size(), driverId + ": " + explicit);
        assertTrue(
            columnLabels(explicit).stream().allMatch(
                label -> label.startsWith(selected.id().name() + ".")),
            driverId + ": " + explicit);
    }

    @ParameterizedTest(name = "{0} quotes preferred names for its provider")
    @ValueSource(strings = {"postgres", "mysql", "sqlserver"})
    void quotesPreferredObjectAndColumnInsertions(String driverId) throws Exception {
        String catalog = driverId.equals("mysql") ? null : "app";
        String schema = driverId.equals("mysql") ? "app" : "public";
        CatalogObjectId id = new CatalogObjectId(catalog, schema, "order details");
        CatalogSnapshot snapshot = new CatalogSnapshot(
            driverId,
            catalog,
            schema,
            List.of(new CatalogObject(
                id,
                "Table",
                List.of(TestCatalogs.column(
                    "display name", "text", "Text", false)))));
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        String expectedQualifier = switch (driverId) {
            case "mysql" -> "`order details`";
            case "sqlserver" -> "[order details]";
            default -> "\"order details\"";
        };
        String expectedColumn = switch (driverId) {
            case "mysql" -> "`display name`";
            case "sqlserver" -> "[display name]";
            default -> "\"display name\"";
        };
        String quotedPrefix = switch (driverId) {
            case "mysql" -> "`order";
            case "sqlserver" -> "[order";
            default -> "\"order";
        };

        JsonNode prefix = completeAtEnd(language, "SELECT " + quotedPrefix, id);
        JsonNode member = completeAtEnd(
            language,
            "SELECT " + expectedQualifier + ".",
            id);

        assertTrue(insertTexts(prefix).contains(expectedQualifier + "." + expectedColumn),
            driverId + ": " + prefix);
        assertTrue(insertTexts(member).contains(expectedColumn), driverId + ": " + member);
    }

    @ParameterizedTest(name = "{0} preserves quoted lowercase preferred identities")
    @ValueSource(strings = {"oracle", "firebird"})
    void quotesLowercasePreferredNamesForUpperFoldingDrivers(String driverId)
        throws Exception {
        CatalogSnapshot snapshot = TestCatalogs.quotedLowercase(driverId);
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        CatalogObjectId preferred = snapshot.objects().getFirst().id();

        JsonNode prefix = completeAtEnd(language, "SELECT viewer_r", preferred);
        JsonNode member = completeAtEnd(
            language,
            "SELECT \"viewer_rows\".",
            preferred);

        assertTrue(columnLabels(prefix).containsAll(Set.of(
            "viewer_rows.id", "viewer_rows.title")), driverId + ": " + prefix);
        assertTrue(insertTexts(prefix).containsAll(Set.of(
            "\"viewer_rows\".\"id\"",
            "\"viewer_rows\".\"title\"")), driverId + ": " + prefix);
        assertTrue(columnLabels(member).containsAll(Set.of("id", "title")),
            driverId + ": " + member);
        assertTrue(insertTexts(member).containsAll(Set.of("\"id\"", "\"title\"")),
            driverId + ": " + member);
    }

    @Test
    void ignoresMissingOrAmbiguousPreferredObjectIdentities() throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "postgres",
            null,
            null,
            List.of(
                new CatalogObject(
                    new CatalogObjectId(null, "one", "articles"),
                    "Table",
                    List.of(TestCatalogs.column("title", "text", "Text", false))),
                new CatalogObject(
                    new CatalogObjectId(null, "two", "articles"),
                    "Table",
                    List.of(TestCatalogs.column("title", "text", "Text", false)))));
        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);

        JsonNode ambiguous = completeAtEnd(
            language,
            "SELECT ti",
            new CatalogObjectId(null, null, "articles"));
        JsonNode missing = completeAtEnd(
            language,
            "SELECT ti",
            new CatalogObjectId(null, "one", "missing"));

        assertFalse(labels(ambiguous).contains("title"), ambiguous.toString());
        assertFalse(labels(missing).contains("title"), missing.toString());
    }

    @ParameterizedTest(name = "{0} provider extensions retain expression completion")
    @ValueSource(strings = {"sqlserver", "firebird", "clickhouse"})
    void completesExpressionsWhenCalciteCannotParseProviderExtensions(String driverId)
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        String sql = TestCatalogs.providerExtensionExpressionQuery(driverId);
        int cursor = sql.indexOf("WHERE ") + "WHERE ".length();
        String id = driverId.equals("firebird") ? "ID" : "id";
        String name = driverId.equals("firebird") ? "NAME" : "name";

        JsonNode result = language.complete(sql, cursor);

        assertTrue(labels(result).containsAll(Set.of(id, name)), driverId + ": " + result);
        assertNoRelationOnlyItems(driverId, result);
    }

    @Test
    void resolvesSchemaAndCatalogQualifiedTables() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        assertTrue(language.diagnose("SELECT p.id FROM public.people p")
            .get("items").isEmpty());
        assertTrue(language.diagnose("SELECT p.id FROM app.public.people p")
            .get("items").isEmpty());
    }

    @Test
    void returnsUnknownColumnDiagnosticAtUtf16Range() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "SELECT id,\nmissing\nFROM people";

        JsonNode diagnostic = language.diagnose(sql).get("items").get(0);

        assertEquals(sql.indexOf("missing"), diagnostic.get("start").intValue());
        assertEquals("missing".length(), diagnostic.get("length").intValue());
        assertEquals("error", diagnostic.get("severity").textValue());
        assertEquals("unknownColumn", diagnostic.get("code").textValue());
        assertTrue(diagnostic.get("message").textValue().contains("missing"));
    }

    @Test
    void preferredCompletionDoesNotAffectDiagnostics() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.editorPostgres());
        CatalogObjectId articles = new CatalogObjectId("app", "public", "articles");
        String explicitSql = "SELECT * FROM public.authors WHERE articles";
        JsonNode beforeExplicit = language.diagnose(explicitSql);
        JsonNode beforeNoFrom = language.diagnose("SELECT * WHERE articles");

        language.complete("SELECT ti", "SELECT ti".length(), articles);

        assertEquals(beforeExplicit, language.diagnose(explicitSql));
        assertEquals(beforeNoFrom, language.diagnose("SELECT * WHERE articles"));
        assertEquals("unknownColumn",
            beforeExplicit.get("items").get(0).get("code").textValue());
        assertTrue(beforeNoFrom.get("items").isEmpty(), beforeNoFrom.toString());
    }

    @Test
    void preservesUnknownColumnValidationThroughPostgresCastAndBindSyntax() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        JsonNode diagnostics = language.diagnose(
            "SELECT missing::text FROM people WHERE id = $1").get("items");

        assertFalse(diagnostics.isEmpty(), diagnostics.toString());
        assertEquals("unknownColumn", diagnostics.get(0).get("code").textValue());
        assertTrue(diagnostics.get(0).get("message").textValue().contains("missing"));
    }

    @Test
    void completesAndValidatesQuotedCaseSensitiveNames() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "SELECT c.\"Dis\" FROM \"CaseSensitive\" c";
        int cursor = sql.indexOf("Dis") + 3;

        JsonNode completion = language.complete(sql, cursor);
        JsonNode displayName = findItem(completion, "DisplayName");

        assertEquals("\"DisplayName\"", displayName.get("insertText").textValue());
        assertEquals(5, completion.get("replacementLength").intValue());
        assertTrue(language.diagnose(
            "SELECT c.\"DisplayName\" FROM \"CaseSensitive\" c").get("items").isEmpty());
    }

    @Test
    void completesColumnsProjectedByACte() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "WITH recent AS (SELECT id, name FROM people) SELECT r. FROM recent r";
        int cursor = sql.indexOf("r. FROM") + 2;

        Set<String> labels = labels(language.complete(sql, cursor));

        assertTrue(labels.containsAll(Set.of("id", "name")), labels.toString());
    }

    @Test
    void replacementRangeIncludesTheSuffixAfterTheCursor() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());
        String sql = "SELECT naZZ FROM people";
        int cursor = sql.indexOf("na") + 2;

        JsonNode result = language.complete(sql, cursor);

        assertEquals(sql.indexOf("naZZ"), result.get("replacementStart").intValue());
        assertEquals(4, result.get("replacementLength").intValue());
    }

    @Test
    void acceptsCompletionForAnEmptyEditor() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        JsonNode result = language.complete("", 0);

        assertEquals(0, result.get("replacementStart").intValue());
        assertEquals(0, result.get("replacementLength").intValue());
        assertFalse(result.get("items").isEmpty());
        assertTrue(labels(result).contains("people"));
    }

    @Test
    void preservesCaseDistinctColumnsForCaseSensitiveDrivers() throws Exception {
        CatalogObject table = new CatalogObject(
            new CatalogObjectId("app", "public", "case_columns"),
            "Table",
            List.of(
                TestCatalogs.column("foo", "text", "Text", true),
                TestCatalogs.column("Foo", "text", "Text", true)));
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "postgres", "app", "public", List.of(table));

        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);

        JsonNode diagnostics = language.diagnose(
            "SELECT foo, \"Foo\" FROM case_columns").get("items");
        assertTrue(diagnostics.isEmpty(), diagnostics.toString());
    }

    @Test
    void treatsQuotedRedshiftIdentifiersAsCaseInsensitiveByDefault() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(
            TestCatalogs.forDriver("redshift"));

        JsonNode valid = language.diagnose(
            "SELECT p.\"ID\" FROM public.people p").get("items");
        JsonNode invalid = language.diagnose(
            "SELECT p.\"definitely_missing\" FROM public.people p").get("items");

        assertTrue(valid.isEmpty(), valid.toString());
        assertFalse(invalid.isEmpty());
        assertEquals("unknownColumn", invalid.get(0).get("code").textValue());
    }

    @Test
    void upgradesRedshiftMatchingWhenTheCatalogContainsCaseDistinctColumns() throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "redshift",
            "app",
            "public",
            List.of(new CatalogObject(
                new CatalogObjectId("app", "public", "case_columns"),
                "Table",
                List.of(
                    TestCatalogs.column("id", "bigint", "SignedInteger", false),
                    TestCatalogs.column("ID", "bigint", "SignedInteger", false)))));

        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        JsonNode diagnostics = language.diagnose(
            "SELECT c.id, c.\"ID\" FROM public.case_columns c").get("items");

        assertTrue(diagnostics.isEmpty(), diagnostics.toString());
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void definesIdentifierRulesForEveryBuiltInDriver(String driverId) throws Exception {
        SqlDialectProfile profile = SqlDialectProfile.forDriver(driverId);

        assertEquals(profile.caseSensitive(), profile.parserConfig().caseSensitive());
    }

    @ParameterizedTest(name = "{0} resolves valid columns and rejects unknown columns")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void validatesSemanticsForEveryBuiltInDriver(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        boolean foldsUnquotedNamesToUpper = driverId.equals("oracle")
            || driverId.equals("firebird");
        String tableName = foldsUnquotedNamesToUpper ? "PEOPLE" : "people";
        String idColumn = foldsUnquotedNamesToUpper ? "ID" : "id";
        String sql = "SELECT p. FROM " + tableName + " p";

        JsonNode valid = language.diagnose("SELECT " + idColumn + " FROM " + tableName)
            .get("items");
        JsonNode invalid = language.diagnose("SELECT definitely_missing FROM " + tableName)
            .get("items");
        Set<String> completions = labels(language.complete(sql, sql.indexOf("p.") + 2));

        assertTrue(valid.isEmpty(), driverId + ": " + valid);
        assertTrue(completions.contains(idColumn), driverId + ": " + completions);
        assertFalse(invalid.isEmpty(), driverId + " accepted an unknown column");
        assertTrue(
            invalid.get(0).get("message").textValue().toLowerCase(Locale.ROOT)
                .contains("definitely_missing"),
            driverId + ": " + invalid);
    }

    @ParameterizedTest(name = "{0} production preview has no false diagnostics")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void acceptsProductionPreviewForEveryBuiltInDriver(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));

        JsonNode diagnostics = language.diagnose(TestCatalogs.productionPreview(driverId))
            .get("items");

        assertTrue(diagnostics.isEmpty(), driverId + ": " + diagnostics);
    }

    @ParameterizedTest(name = "{0} provider syntax has no false diagnostics")
    @ValueSource(strings = {
        "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
        "redshift", "duckdb", "oracle", "firebird", "clickhouse"
    })
    void acceptsCommonProviderExtensionsWithoutPaintingThemRed(String driverId) throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));

        JsonNode diagnostics = language.diagnose(TestCatalogs.providerExtensionQuery(driverId))
            .get("items");

        assertTrue(diagnostics.isEmpty(), driverId + ": " + diagnostics);
    }

    @Test
    void suppressesParserAndDmlDiagnosticsThatTheDetachedCatalogCannotProve() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        assertTrue(language.diagnose("SELECT FROM").get("items").isEmpty());
        assertTrue(language.diagnose("INSERT INTO people(id) VALUES (1)")
            .get("items").isEmpty());
    }

    @ParameterizedTest(name = "{0} fallback preserves completion and qualified diagnostics")
    @ValueSource(strings = {"sqlserver", "firebird", "clickhouse"})
    void resolvesAliasesWhenProviderExtensionsDoNotParseInCalcite(String driverId)
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.forDriver(driverId));
        String completionSql = TestCatalogs.providerExtensionCompletionQuery(driverId);
        String expectedId = driverId.equals("firebird") ? "ID" : "id";

        JsonNode completion = language.complete(
            completionSql,
            completionSql.indexOf("p.") + 2);
        Set<String> completions = labels(completion);
        JsonNode valid = language.diagnose(
            completionSql.replace("p. FROM", "p." + expectedId + " FROM"))
            .get("items");
        JsonNode invalid = language.diagnose(
            TestCatalogs.providerExtensionMissingColumnQuery(driverId)).get("items");

        assertTrue(completions.contains(expectedId), driverId + ": " + completions);
        assertQualifiedMemberItems(driverId, completion);
        assertTrue(valid.isEmpty(), driverId + ": " + valid);
        assertFalse(invalid.isEmpty(), driverId + " lost qualified unknown-column diagnostics");
        assertEquals("unknownColumn", invalid.get(0).get("code").textValue());
    }

    @Test
    void suppressesCatalogErrorsWhenAnyFromRelationIsUnresolved() throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(TestCatalogs.postgres());

        assertTrue(language.diagnose("SELECT missing FROM no_such_table")
            .get("items").isEmpty());
        assertTrue(language.diagnose(
            "SELECT p.missing FROM people p JOIN no_such_table x ON true")
            .get("items").isEmpty());
        assertTrue(language.diagnose(
            "SELECT x.missing FROM people p JOIN no_such_table x ON true")
            .get("items").isEmpty());
        assertTrue(language.diagnose(
            "SELECT missing FROM people p JOIN no_such_table x ON true")
            .get("items").isEmpty());
    }

    @ParameterizedTest(name = "{0} quoted lowercase catalog identifiers")
    @ValueSource(strings = {"oracle", "firebird"})
    void supportsQuotedLowercaseObjectsAndColumnsForUpperFoldingDrivers(String driverId)
        throws Exception {
        CalciteSqlLanguage language = CalciteSqlLanguage.create(
            TestCatalogs.quotedLowercase(driverId));
        String relation = driverId.equals("oracle")
            ? "\"SYSTEM\".\"viewer_rows\""
            : "\"viewer_rows\"";
        String completionSql = "SELECT v. FROM " + relation + " v";

        Set<String> completions = labels(language.complete(
            completionSql,
            completionSql.indexOf("v.") + 2));
        JsonNode valid = language.diagnose(
            "SELECT v.\"id\", v.\"title\" FROM " + relation + " v").get("items");
        JsonNode invalid = language.diagnose(
            "SELECT v.\"missing\" FROM " + relation + " v").get("items");

        assertTrue(completions.containsAll(Set.of("id", "title")), completions.toString());
        assertTrue(valid.isEmpty(), valid.toString());
        assertFalse(invalid.isEmpty(), driverId + " lost quoted unknown-column diagnostics");
    }

    @Test
    void upgradesCaseInsensitiveProfilesForCaseDistinctCatalogColumns() throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "sqlserver",
            "app",
            "dbo",
            List.of(new CatalogObject(
                new CatalogObjectId("app", "dbo", "case_columns"),
                "Table",
                List.of(
                    TestCatalogs.column("foo", "text", "Text", true),
                    TestCatalogs.column("Foo", "text", "Text", true)))));

        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        JsonNode diagnostics = language.diagnose(
            "SELECT [foo], [Foo] FROM [app].[dbo].[case_columns]").get("items");

        assertTrue(diagnostics.isEmpty(), diagnostics.toString());
    }

    @Test
    void upgradesCaseInsensitiveProfilesForCaseDistinctCatalogObjects() throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "mysql",
            null,
            "app",
            List.of(
                new CatalogObject(
                    new CatalogObjectId(null, "app", "people"),
                    "Table",
                    List.of(TestCatalogs.column("Name", "text", "Text", false))),
                new CatalogObject(
                    new CatalogObjectId(null, "app", "People"),
                    "Table",
                    List.of(TestCatalogs.column("Name", "text", "Text", false)))));

        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);
        String completionSql = "SELECT p. FROM app.`People` p";

        assertTrue(language.diagnose("SELECT p.name FROM app.`people` p")
            .get("items").isEmpty());
        assertTrue(language.diagnose("SELECT p.name FROM app.`People` p")
            .get("items").isEmpty());
        assertTrue(labels(language.complete(
            completionSql,
            completionSql.indexOf("p.") + 2)).contains("Name"));
        assertFalse(language.diagnose("SELECT p.definitely_missing FROM app.`People` p")
            .get("items").isEmpty());
    }

    @Test
    void resolvesPartiallyQualifiedObjectsThroughTheDefaultCatalog() throws Exception {
        CatalogSnapshot snapshot = new CatalogSnapshot(
            "duckdb",
            "db1",
            "main",
            List.of(
                table("db1", "main", "people"),
                table("db2", "main", "people"),
                table("db1", "other", "people")));

        CalciteSqlLanguage language = CalciteSqlLanguage.create(snapshot);

        assertUnknownColumn(language, "SELECT missing FROM people");
        assertUnknownColumn(language, "SELECT missing FROM main.people");
        assertUnknownColumn(language, "SELECT missing FROM db2.main.people");
        assertTrue(language.diagnose("SELECT id FROM main.people").get("items").isEmpty());
    }

    @Test
    void suppressesProviderPseudoColumnsMissingFromDetachedMetadata() throws Exception {
        CalciteSqlLanguage duckDb = CalciteSqlLanguage.create(TestCatalogs.forDriver("duckdb"));
        CalciteSqlLanguage oracle = CalciteSqlLanguage.create(TestCatalogs.forDriver("oracle"));
        CalciteSqlLanguage clickHouse = CalciteSqlLanguage.create(
            TestCatalogs.forDriver("clickhouse"));

        assertTrue(duckDb.diagnose("SELECT rowid, p.rowid FROM people p")
            .get("items").isEmpty());
        assertTrue(oracle.diagnose("SELECT ORA_ROWSCN FROM PEOPLE")
            .get("items").isEmpty());
        assertTrue(clickHouse.diagnose("SELECT _partition_value FROM app.people")
            .get("items").isEmpty());
    }

    private static Set<String> labels(JsonNode result) {
        var labels = new HashSet<String>();
        for (JsonNode item : result.get("items")) {
            labels.add(item.get("label").textValue());
        }
        return labels;
    }

    private static JsonNode completeAtEnd(CalciteSqlLanguage language, String sql)
        throws Exception {
        return language.complete(sql, sql.length());
    }

    private static JsonNode completeAtEnd(
        CalciteSqlLanguage language,
        String sql,
        CatalogObjectId preferredObject) throws Exception {
        return language.complete(sql, sql.length(), preferredObject);
    }

    private static Set<String> columnLabels(JsonNode result) {
        var labels = new HashSet<String>();
        for (JsonNode item : result.get("items")) {
            if (item.get("kind").textValue().equals("column")) {
                labels.add(item.get("label").textValue());
            }
        }
        return labels;
    }

    private static Set<String> insertTexts(JsonNode result) {
        var insertTexts = new HashSet<String>();
        for (JsonNode item : result.get("items")) {
            insertTexts.add(item.get("insertText").textValue());
        }
        return insertTexts;
    }

    private static Set<String> insertTextsForLabel(JsonNode result, String label) {
        var insertTexts = new HashSet<String>();
        for (JsonNode item : result.get("items")) {
            if (item.get("label").textValue().equalsIgnoreCase(label)) {
                insertTexts.add(item.get("insertText").textValue());
            }
        }
        return insertTexts;
    }

    private static void assertNoRelationOnlyItems(String context, JsonNode result) {
        assertFalse(hasRelationOnlyItem(result), context + ": " + result);
    }

    private static boolean hasRelationOnlyItem(JsonNode result) {
        for (JsonNode item : result.get("items")) {
            if (Set.of("table", "view", "materializedView", "schema", "catalog")
                .contains(item.get("kind").textValue())) {
                return true;
            }
        }
        return false;
    }

    private static void assertQualifiedMemberItems(String context, JsonNode result) {
        for (JsonNode item : result.get("items")) {
            assertTrue(
                item.get("kind").textValue().equals("column")
                    || item.get("label").textValue().equals("*"),
                context + ": " + result);
        }
    }

    private static CatalogObject table(String catalog, String schema, String name) {
        return new CatalogObject(
            new CatalogObjectId(catalog, schema, name),
            "Table",
            List.of(TestCatalogs.column("id", "bigint", "SignedInteger", false)));
    }

    private static CatalogSnapshot postgresFunctionCatalog() {
        CatalogRoutine dateAdd = new CatalogRoutine(
            new CatalogObjectId("app", "public", "date_add"),
            "scalar",
            "date_add(timestamp with time zone, interval [, text])",
            List.of(
                new CatalogRoutineParameter(
                    "value", "timestamp with time zone", "TimestampWithZone", "in",
                    false, false),
                new CatalogRoutineParameter(
                    "amount", "interval", "Unknown", "in", false, false),
                new CatalogRoutineParameter(
                    "timezone", "text", "Text", "in", true, false)),
            "timestamp with time zone",
            "TimestampWithZone",
            2,
            3);
        return TestCatalogs.withIntrinsicSymbols(new CatalogSnapshot(
            "postgres",
            "app",
            "public",
            List.of(new CatalogObject(
                new CatalogObjectId("app", "public", "articles"),
                "Table",
                List.of(
                    TestCatalogs.column("id", "bigint", "SignedInteger", false),
                    TestCatalogs.column(
                        "timestamp", "timestamp with time zone", "TimestampWithZone", false)))),
            List.of(dateAdd)),
            "CURRENT_TIMESTAMP");
    }

    private static CatalogSnapshot routineCatalog(
        String driverId,
        String defaultCatalog,
        String defaultSchema,
        List<CatalogRoutine> routines) {
        return new CatalogSnapshot(
            driverId,
            defaultCatalog,
            defaultSchema,
            List.of(),
            routines);
    }

    private static CatalogSnapshot withoutFunctionEvidence(CatalogSnapshot snapshot) {
        return new CatalogSnapshot(
            snapshot.driverId(),
            snapshot.defaultCatalog(),
            snapshot.defaultSchema(),
            snapshot.objects(),
            snapshot.routines(),
            snapshot.routineCoverage(),
            CatalogMetadataCoverage.COMPLETE,
            List.of());
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

    private static CatalogRoutine routineWithKind(
        String catalog,
        String schema,
        String name,
        String kind,
        String signature) {
        return new CatalogRoutine(
            new CatalogObjectId(catalog, schema, name),
            kind,
            signature,
            List.of(),
            "bigint",
            "SignedInteger",
            0,
            null);
    }

    private static void assertUnknownColumn(CalciteSqlLanguage language, String sql) {
        JsonNode diagnostics = language.diagnose(sql).get("items");
        assertFalse(diagnostics.isEmpty(), sql + " lost its unknown-column diagnostic");
        assertEquals("unknownColumn", diagnostics.get(0).get("code").textValue());
    }

    private static JsonNode findItem(JsonNode result, String label) {
        for (JsonNode item : result.get("items")) {
            if (label.equals(item.get("label").textValue())) {
                return item;
            }
        }
        throw new AssertionError("Completion item not found: " + label);
    }
}
