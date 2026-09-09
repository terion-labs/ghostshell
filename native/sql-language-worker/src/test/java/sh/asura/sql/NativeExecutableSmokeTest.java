package sh.asura.sql;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Assumptions;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Exercises the linked Native Image through the same framed protocol as Asura. */
final class NativeExecutableSmokeTest {
    private static final ObjectMapper JSON = new ObjectMapper();

    @Test
    @Timeout(value = 30, unit = TimeUnit.SECONDS)
    void linkedExecutableCompletesAndDiagnosesAgainstTheInitializedCatalog() throws Exception {
        String configuredPath = System.getProperty("native.executable");
        Assumptions.assumeTrue(
            configuredPath != null && !configuredPath.isBlank(),
            "Set -Dnative.executable to run the linked-image smoke test.");

        Path executable = Path.of(configuredPath).toAbsolutePath().normalize();
        assertTrue(Files.isRegularFile(executable), "Native executable is missing: " + executable);

        Process process = new ProcessBuilder(executable.toString())
            .redirectError(ProcessBuilder.Redirect.INHERIT)
            .start();
        CompletableFuture<List<JsonNode>> responses = CompletableFuture.supplyAsync(() -> {
            try {
                return decodeFrames(process.getInputStream());
            } catch (Exception error) {
                throw new IllegalStateException("Cannot read native worker responses.", error);
            }
        });

        try {
            writeRequest(
                process,
                request(1, "initialize", "{\"catalog\":" + TestCatalogs.postgresJson() + "}"));
            writeRequest(
                process,
                request(
                    2,
                    "complete",
                    "{\"sql\":\"SELECT p. FROM people p\",\"cursorOffset\":9}"));
            writeRequest(
                process,
                request(
                    3,
                    "diagnose",
                    "{\"sql\":\"SELECT missing FROM people\"}"));
            int nextId = 4;
            for (String driverId : List.of("sqlserver", "firebird", "clickhouse")) {
                String catalog = JSON.writeValueAsString(TestCatalogs.forDriver(driverId));
                String completionSql = TestCatalogs.providerExtensionCompletionQuery(driverId);
                String expectedId = driverId.equals("firebird") ? "ID" : "id";
                String validSql = completionSql.replace(
                    "p. FROM",
                    "p." + expectedId + " FROM");
                String invalidSql = TestCatalogs.providerExtensionMissingColumnQuery(driverId);
                writeRequest(
                    process,
                    request(nextId++, "updateCatalog", "{\"catalog\":" + catalog + "}"));
                writeRequest(
                    process,
                    request(
                        nextId++,
                        "complete",
                        "{\"sql\":" + JSON.writeValueAsString(completionSql)
                            + ",\"cursorOffset\":" + (completionSql.indexOf("p.") + 2) + "}"));
                writeRequest(
                    process,
                    request(
                        nextId++,
                        "diagnose",
                        "{\"sql\":" + JSON.writeValueAsString(validSql) + "}"));
                writeRequest(
                    process,
                    request(
                        nextId++,
                        "diagnose",
                        "{\"sql\":" + JSON.writeValueAsString(invalidSql) + "}"));
            }
            String redshiftCatalog = JSON.writeValueAsString(TestCatalogs.forDriver("redshift"));
            writeRequest(
                process,
                request(nextId++, "updateCatalog", "{\"catalog\":" + redshiftCatalog + "}"));
            writeRequest(
                process,
                request(
                    nextId++,
                    "diagnose",
                    "{\"sql\":\"SELECT p.\\\"ID\\\" FROM public.people p\"}"));
            writeRequest(
                process,
                request(
                    nextId++,
                    "diagnose",
                    "{\"sql\":\"SELECT p.\\\"definitely_missing\\\" "
                        + "FROM public.people p\"}"));
            writeRequest(process, request(nextId, "shutdown", "{}"));
            process.getOutputStream().close();

            assertTrue(process.waitFor(20, TimeUnit.SECONDS), "Native worker did not exit.");
            assertEquals(0, process.exitValue());

            List<JsonNode> frames = responses.get(5, TimeUnit.SECONDS);
            assertEquals(19, frames.size());
            for (JsonNode frame : frames) {
                assertFalse(frame.has("error"), frame.toString());
            }
            assertEquals(1, frames.get(0).path("result").path("objectCount").intValue());
            assertTrue(hasCompletion(frames.get(1), "id"));
            assertTrue(hasCompletion(frames.get(1), "name"));
            assertTrue(frames.get(2).path("result").path("items").get(0)
                .path("message").textValue().contains("missing"));
            int frameIndex = 3;
            for (String driverId : List.of("sqlserver", "firebird", "clickhouse")) {
                String expectedId = driverId.equals("firebird") ? "ID" : "id";
                assertEquals(1, frames.get(frameIndex++).path("result")
                    .path("objectCount").intValue());
                assertTrue(hasCompletion(frames.get(frameIndex++), expectedId), driverId);
                assertTrue(frames.get(frameIndex++).path("result").path("items").isEmpty(),
                    driverId);
                assertEquals("unknownColumn", frames.get(frameIndex++).path("result")
                    .path("items").get(0).path("code").textValue(), driverId);
            }
            assertEquals(1, frames.get(frameIndex++).path("result")
                .path("objectCount").intValue());
            assertTrue(frames.get(frameIndex++).path("result").path("items").isEmpty());
            assertEquals("unknownColumn", frames.get(frameIndex++).path("result")
                .path("items").get(0).path("code").textValue());
            assertTrue(frames.get(frameIndex).path("result").path("accepted").booleanValue());
        } finally {
            if (process.isAlive()) {
                process.destroyForcibly();
                process.waitFor(5, TimeUnit.SECONDS);
            }
        }
    }

    private static void writeRequest(Process process, String json) throws Exception {
        FrameCodec.write(process.getOutputStream(), ProtocolJson.utf8(json));
    }

    private static List<JsonNode> decodeFrames(InputStream input) throws Exception {
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
        for (JsonNode item : response.path("result").path("items")) {
            if (label.equals(item.path("label").textValue())) {
                return true;
            }
        }
        return false;
    }
}
