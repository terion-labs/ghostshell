package sh.asura.sql;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class SqlLanguageWorkerTest {
    private static final ObjectMapper JSON = new ObjectMapper();

    @Test
    void servesInitializeCompleteDiagnoseAndShutdownOverFramedProtocol() throws Exception {
        String initialize = request(
            1,
            "initialize",
            "{\"catalog\":" + TestCatalogs.postgresJson() + "}");
        String complete = request(
            2,
            "complete",
            "{\"sql\":\"SELECT p. FROM people p\",\"cursorOffset\":9}");
        String diagnose = request(
            3,
            "diagnose",
            "{\"sql\":\"SELECT missing FROM people\"}");
        String shutdown = request(4, "shutdown", "{}");

        WorkerRun run = runWorker(initialize, complete, diagnose, shutdown);

        assertEquals(0, run.exitCode());
        assertEquals(4, run.responses().size());
        assertEquals(1, run.responses().get(0).get("result").get("objectCount").intValue());
        assertTrue(hasCompletion(run.responses().get(1), "name"));
        assertTrue(run.responses().get(2).get("result").get("items").get(0)
            .get("message").textValue().contains("missing"));
        assertTrue(run.responses().get(3).get("result").get("accepted").booleanValue());
    }

    @Test
    void completesStatementAndFromKeywordsOverFramedProtocol() throws Exception {
        WorkerRun run = runWorker(
            request(
                5,
                "initialize",
                "{\"catalog\":" + TestCatalogs.postgresJson() + "}"),
            request(
                6,
                "complete",
                "{\"sql\":\"sel\",\"cursorOffset\":3}"),
            request(
                7,
                "complete",
                "{\"sql\":\"SELECT * fr\",\"cursorOffset\":11}"));

        assertEquals(0, run.exitCode());
        assertTrue(hasCompletion(run.responses().get(1), "SELECT"));
        assertTrue(hasCompletion(run.responses().get(2), "FROM"));
    }

    @Test
    void acceptsRoutineMetadataAndCompletesItOverTheFramedProtocol() throws Exception {
        JsonNode catalog = JSON.readTree(TestCatalogs.postgresJson());
        ((com.fasterxml.jackson.databind.node.ObjectNode) catalog).putArray("routines")
            .addObject()
            .set("id", JSON.readTree(
                "{\"catalog\":\"app\",\"schema\":\"public\",\"name\":\"date_add\"}"));
        com.fasterxml.jackson.databind.node.ObjectNode routine =
            (com.fasterxml.jackson.databind.node.ObjectNode) catalog.get("routines").get(0);
        routine.put("kind", "scalar");
        routine.put("signature", "date_add(timestamp, interval [, text])");
        routine.put("returnTypeName", "timestamp");
        routine.put("returnValueKind", "Timestamp");
        routine.put("minimumArgumentCount", 2);
        routine.put("maximumArgumentCount", 3);
        com.fasterxml.jackson.databind.node.ArrayNode parameters = routine.putArray("parameters");
        parameters.addObject()
            .put("name", "value")
            .put("dataTypeName", "timestamp")
            .put("valueKind", "Timestamp")
            .put("mode", "in")
            .put("isOptional", false)
            .put("isVariadic", false);
        parameters.addObject()
            .put("name", "amount")
            .put("dataTypeName", "interval")
            .put("mode", "in")
            .put("isOptional", false)
            .put("isVariadic", false);
        parameters.addObject()
            .put("name", "timezone")
            .put("dataTypeName", "text")
            .put("valueKind", "Text")
            .put("mode", "in")
            .put("isOptional", true)
            .put("isVariadic", false);

        WorkerRun run = runWorker(
            request(51, "initialize", "{\"catalog\":" + catalog + "}"),
            request(
                52,
                "complete",
                "{\"sql\":\"SELECT date_a\",\"cursorOffset\":13}"),
            request(53, "shutdown", "{}"));

        assertEquals(0, run.exitCode());
        JsonNode item = completion(run.responses().get(1), "date_add");
        assertEquals("function", item.get("kind").textValue());
        assertEquals("date_add(", item.get("insertText").textValue());
        assertTrue(item.get("detail").textValue().contains("date_add"));
    }

    @Test
    void acceptsCoverageAndUsesIntrinsicSymbolsAsCalciteCorroboration() throws Exception {
        com.fasterxml.jackson.databind.node.ObjectNode catalog =
            (com.fasterxml.jackson.databind.node.ObjectNode) JSON.readTree(
                TestCatalogs.postgresJson());
        catalog.put("routineCoverage", "complete");
        catalog.put("intrinsicCoverage", "complete");
        catalog.putArray("intrinsicSymbols")
            .addObject()
            .put("name", "CURRENT_TIMESTAMP")
            .put("kind", "keyword");

        WorkerRun run = runWorker(
            request(54, "initialize", "{\"catalog\":" + catalog + "}"),
            request(
                55,
                "complete",
                "{\"sql\":\"SELECT cur\",\"cursorOffset\":10}"));

        assertEquals(0, run.exitCode());
        JsonNode currentTimestamp = completion(run.responses().get(1), "CURRENT_TIMESTAMP");
        assertEquals("keyword", currentTimestamp.get("kind").textValue());
        assertEquals("CURRENT_TIMESTAMP", currentTimestamp.get("insertText").textValue());
    }

    @Test
    void returnsCastTypeCompletionWithTheDataTypeProtocolKind() throws Exception {
        String sql = "SELECT id::inter FROM people";
        int cursorOffset = sql.indexOf("inter") + "inter".length();
        WorkerRun run = runWorker(
            request(
                59,
                "initialize",
                "{\"catalog\":" + TestCatalogs.postgresJson() + "}"),
            request(
                60,
                "complete",
                "{\"sql\":" + JSON.writeValueAsString(sql)
                    + ",\"cursorOffset\":" + cursorOffset + "}"));

        assertEquals(0, run.exitCode());
        JsonNode result = run.responses().get(1).get("result");
        assertEquals(sql.indexOf("inter"), result.get("replacementStart").intValue());
        assertEquals("inter".length(), result.get("replacementLength").intValue());
        JsonNode interval = completion(run.responses().get(1), "INTERVAL");
        assertEquals("dataType", interval.get("kind").textValue());
        assertEquals("type", interval.get("detail").textValue());
        assertEquals("INTERVAL", interval.get("insertText").textValue());
        for (JsonNode item : result.get("items")) {
            assertEquals("dataType", item.get("kind").textValue(), result.toString());
        }
    }

    @Test
    void rejectsInvalidCoverageAndIntrinsicSymbolKinds() throws Exception {
        WorkerRun run = runWorker(
            request(
                56,
                "initialize",
                "{\"catalog\":{\"driverId\":\"postgres\",\"objects\":[],"
                    + "\"routineCoverage\":\"all\"}}"),
            request(
                57,
                "initialize",
                "{\"catalog\":{\"driverId\":\"postgres\",\"objects\":[],"
                    + "\"intrinsicSymbols\":[{\"name\":\"ABS\",\"kind\":\"function\"}]}}"),
            request(
                58,
                "initialize",
                "{\"catalog\":{\"driverId\":\"postgres\",\"objects\":[],"
                    + "\"intrinsicCoverage\":\"userDefinedOnly\"}}"));

        assertEquals("invalidParams", errorCode(run.responses().get(0)));
        assertEquals("invalidParams", errorCode(run.responses().get(1)));
        assertEquals("invalidParams", errorCode(run.responses().get(2)));
    }

    @Test
    void rejectsUnsafeOrContradictoryRoutineArity() throws Exception {
        String base = "{\"driverId\":\"postgres\",\"objects\":[],\"routines\":[";
        String routine = "{\"id\":{\"name\":\"f\"},\"kind\":\"scalar\","
            + "\"signature\":\"f()\",\"parameters\":[]";
        WorkerRun run = runWorker(
            request(61, "initialize", "{\"catalog\":" + base + routine
                + ",\"minimumArgumentCount\":2147483647}]}}"),
            request(62, "initialize", "{\"catalog\":" + base + routine
                + ",\"minimumArgumentCount\":2,\"maximumArgumentCount\":1}]}}"));

        assertEquals("invalidParams", errorCode(run.responses().get(0)));
        assertEquals("invalidParams", errorCode(run.responses().get(1)));
    }

    @Test
    void returnsStableErrorsForMalformedAndOutOfSequenceRequests() throws Exception {
        String completeBeforeInitialize = request(
            8,
            "complete",
            "{\"sql\":\"SELECT \" ,\"cursorOffset\":7}");
        String unknownMethod = request(9, "wat", "{}");

        WorkerRun run = runWorker("not-json", completeBeforeInitialize, unknownMethod);

        assertEquals("invalidRequest", errorCode(run.responses().get(0)));
        assertEquals(0, run.responses().get(0).get("id").longValue());
        assertEquals("notInitialized", errorCode(run.responses().get(1)));
        assertEquals(8, run.responses().get(1).get("id").longValue());
        assertEquals("methodNotFound", errorCode(run.responses().get(2)));
    }

    @Test
    void failedCatalogUpdateLeavesPreviousCatalogUsable() throws Exception {
        String initialize = request(
            11,
            "initialize",
            "{\"catalog\":" + TestCatalogs.postgresJson() + "}");
        String invalidUpdate = request(
            12,
            "updateCatalog",
            "{\"catalog\":{\"driverId\":\"unknown\",\"objects\":[]}}");
        String complete = request(
            13,
            "complete",
            "{\"sql\":\"SELECT p. FROM people p\",\"cursorOffset\":9}");

        WorkerRun run = runWorker(initialize, invalidUpdate, complete);

        assertEquals("invalidParams", errorCode(run.responses().get(1)));
        assertTrue(hasCompletion(run.responses().get(2), "id"));
    }

    @Test
    void acceptsRequestScopedPreferredObjectWithoutChangingDiagnostics() throws Exception {
        String catalogJson = JSON.writeValueAsString(TestCatalogs.editorPostgres());
        String preferred = "\"preferredObject\":{"
            + "\"catalog\":\"app\",\"schema\":\"public\",\"name\":\"articles\"}";
        String diagnosticSql = "SELECT * FROM public.authors WHERE articles";

        WorkerRun run = runWorker(
            request(31, "initialize", "{\"catalog\":" + catalogJson + "}"),
            request(
                32,
                "complete",
                "{\"sql\":\"SELECT ti\",\"cursorOffset\":9," + preferred + "}"),
            request(
                33,
                "complete",
                "{\"sql\":\"SELECT articles.\",\"cursorOffset\":16,"
                    + preferred + "}"),
            request(
                34,
                "diagnose",
                "{\"sql\":" + JSON.writeValueAsString(diagnosticSql) + "}"),
            request(35, "shutdown", "{}"));

        assertEquals(0, run.exitCode());
        assertTrue(hasCompletion(run.responses().get(1), "title"));
        assertFalse(hasCompletion(run.responses().get(1), "content"));
        assertTrue(hasCompletion(run.responses().get(2), "id"));
        assertTrue(hasCompletion(run.responses().get(2), "title"));
        assertEquals(
            "unknownColumn",
            run.responses().get(3).get("result").get("items").get(0)
                .get("code").textValue());
    }

    @Test
    void rejectsMalformedPreferredObjectWithoutMutatingTheSession() throws Exception {
        String initialize = request(
            41,
            "initialize",
            "{\"catalog\":" + TestCatalogs.postgresJson() + "}");
        String wrongType = request(
            42,
            "complete",
            "{\"sql\":\"SELECT n\",\"cursorOffset\":8,"
                + "\"preferredObject\":\"people\"}");
        String missingName = request(
            43,
            "complete",
            "{\"sql\":\"SELECT n\",\"cursorOffset\":8,"
                + "\"preferredObject\":{\"schema\":\"public\"}}");
        String valid = request(
            44,
            "complete",
            "{\"sql\":\"SELECT n\",\"cursorOffset\":8,"
                + "\"preferredObject\":{\"catalog\":\"app\","
                + "\"schema\":\"public\",\"name\":\"people\"}}");

        WorkerRun run = runWorker(initialize, wrongType, missingName, valid);

        assertEquals("invalidParams", errorCode(run.responses().get(1)));
        assertEquals("invalidParams", errorCode(run.responses().get(2)));
        assertTrue(hasCompletion(run.responses().get(3), "name"));
    }

    @ParameterizedTest(name = "framed {0} provider-extension fallback")
    @ValueSource(strings = {"sqlserver", "firebird", "clickhouse"})
    void preservesCompletionAndDiagnosticsAcrossProviderSyntaxOverFraming(String driverId)
        throws Exception {
        String catalogJson = JSON.writeValueAsString(TestCatalogs.forDriver(driverId));
        String completionSql = TestCatalogs.providerExtensionCompletionQuery(driverId);
        String missingSql = TestCatalogs.providerExtensionMissingColumnQuery(driverId);
        String expectedId = driverId.equals("firebird") ? "ID" : "id";

        WorkerRun run = runWorker(
            request(21, "initialize", "{\"catalog\":" + catalogJson + "}"),
            request(
                22,
                "complete",
                "{\"sql\":" + JSON.writeValueAsString(completionSql)
                    + ",\"cursorOffset\":" + (completionSql.indexOf("p.") + 2) + "}"),
            request(
                23,
                "diagnose",
                "{\"sql\":" + JSON.writeValueAsString(missingSql) + "}"),
            request(24, "shutdown", "{}"));

        assertEquals(0, run.exitCode());
        assertTrue(hasCompletion(run.responses().get(1), expectedId));
        JsonNode diagnostic = run.responses().get(2).path("result").path("items").get(0);
        assertEquals("unknownColumn", diagnostic.path("code").textValue());
        assertTrue(diagnostic.path("message").textValue().contains("definitely_missing"));
    }

    @Test
    void oversizedFrameReturnsOneErrorAndTerminates() throws Exception {
        int length = FrameCodec.MAXIMUM_FRAME_BYTES + 1;
        byte[] input = {
            (byte) (length >>> 24),
            (byte) (length >>> 16),
            (byte) (length >>> 8),
            (byte) length
        };
        var output = new ByteArrayOutputStream();

        int exitCode = new SqlLanguageWorker().run(new ByteArrayInputStream(input), output);
        List<JsonNode> responses = decodeFrames(output.toByteArray());

        assertEquals(2, exitCode);
        assertEquals(1, responses.size());
        assertEquals("invalidFrame", errorCode(responses.getFirst()));
    }

    private static WorkerRun runWorker(String... requests) throws Exception {
        var input = new ByteArrayOutputStream();
        for (String request : requests) {
            FrameCodec.write(input, ProtocolJson.utf8(request));
        }
        var output = new ByteArrayOutputStream();
        int exitCode = new SqlLanguageWorker().run(
            new ByteArrayInputStream(input.toByteArray()),
            output);
        return new WorkerRun(exitCode, decodeFrames(output.toByteArray()));
    }

    private static List<JsonNode> decodeFrames(byte[] bytes) throws Exception {
        var input = new ByteArrayInputStream(bytes);
        var responses = new ArrayList<JsonNode>();
        byte[] payload;
        while ((payload = FrameCodec.read(input)) != null) {
            responses.add(JSON.readTree(payload));
        }
        return List.copyOf(responses);
    }

    private static String request(long id, String method, String params) {
        return "{\"version\":1,\"id\":" + id + ",\"method\":\"" + method
            + "\",\"params\":" + params + "}";
    }

    private static boolean hasCompletion(JsonNode response, String label) {
        return completionOrNull(response, label) != null;
    }

    private static JsonNode completion(JsonNode response, String label) {
        JsonNode item = completionOrNull(response, label);
        if (item == null) {
            throw new AssertionError("Completion item not found: " + label + ": " + response);
        }
        return item;
    }

    private static JsonNode completionOrNull(JsonNode response, String label) {
        for (JsonNode item : response.get("result").get("items")) {
            if (label.equals(item.get("label").textValue())) {
                return item;
            }
        }
        return null;
    }

    private static String errorCode(JsonNode response) {
        return response.get("error").get("code").textValue();
    }

    private record WorkerRun(int exitCode, List<JsonNode> responses) {
    }
}
