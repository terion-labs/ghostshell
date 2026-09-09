package sh.asura.sql;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.List;
import java.util.Properties;
import java.util.Set;
import org.junit.jupiter.api.Test;

final class LegalClosurePolicyTest {
    private static final int RUNTIME_DEPENDENCY_COUNT = 45;
    private static final String GENERATED_MANIFEST_PROPERTY = "legal.runtimeDependencies";
    private static final String GENERATED_NOTICES_PROPERTY = "legal.thirdPartyNotices";
    private static final String GENERATED_METADATA_PROPERTY = "legal.metadata";

    @Test
    void vendoredLegalSourcesAndRuntimePolicyAreCompleteAndDeterministic() throws Exception {
        Path legalDirectory = projectDirectory().resolve("src/legal");
        List<String> sourceRows = dataRows(legalDirectory.resolve("sources.tsv"));
        List<String> policyRows = dataRows(legalDirectory.resolve("runtime-license-map.tsv"));
        List<String> legalReviewRows = dataRows(legalDirectory.resolve("legal-review.tsv"));

        assertSortedAndUnique(sourceRows, "legal source manifest");
        assertSortedAndUnique(policyRows, "runtime license policy");
        assertSortedAndUnique(legalReviewRows, "legal review manifest");
        assertEquals(
                RUNTIME_DEPENDENCY_COUNT,
                policyRows.size(),
                "the pinned runtime graph must be reviewed coordinate by coordinate");

        Set<String> declaredPaths = new HashSet<>();
        for (String row : sourceRows) {
            String[] fields = row.split("\\t", -1);
            assertEquals(3, fields.length, "malformed legal source row: " + row);
            Path relativePath = Path.of(fields[0]);
            assertFalse(relativePath.isAbsolute(), "legal source path must be relative");
            assertFalse(fields[0].contains(".."), "legal source path must not traverse directories");
            assertTrue(declaredPaths.add(fields[0]), "duplicate legal source: " + fields[0]);
            Path legalFile = legalDirectory.resolve(relativePath);
            assertTrue(Files.size(legalFile) > 0, "vendored legal source must not be empty: " + fields[0]);
            assertEquals(fields[1], sha256(legalFile), "vendored legal source changed: " + fields[0]);
            assertTrue(
                    fields[2].startsWith("https://") || fields[2].startsWith("maven:"),
                    "legal source must identify its immutable upstream origin: " + fields[0]);
        }

        Set<String> actualPaths = new HashSet<>();
        try (var files = Files.walk(legalDirectory.resolve("licenses"))) {
            files.filter(Files::isRegularFile)
                    .map(legalDirectory::relativize)
                    .map(Path::toString)
                    .forEach(actualPaths::add);
        }
        assertEquals(declaredPaths, actualPaths, "every vendored legal file must be hash-pinned and sourced");

        Set<String> coordinates = new HashSet<>();
        for (String row : policyRows) {
            String[] fields = row.split("\\t", -1);
            assertEquals(4, fields.length, "malformed runtime license policy row: " + row);
            assertTrue(fields[0].split(":", -1).length == 3, "policy key must be group:artifact:version");
            assertTrue(coordinates.add(fields[0]), "duplicate policy coordinate: " + fields[0]);
            assertFalse(fields[1].isBlank(), "distribution license must be explicit: " + fields[0]);
            assertReferencedFilesAreDeclared(fields[2], declaredPaths, fields[0]);
            assertReferencedFilesAreDeclared(fields[3], declaredPaths, fields[0]);
        }

        assertTrue(coordinates.contains("org.apache.calcite:calcite-core:1.42.0"));
        assertTrue(coordinates.contains("org.apache.calcite:calcite-babel:1.42.0"));
        assertTrue(coordinates.contains("com.google.guava:listenablefuture:9999.0-empty-to-avoid-conflict-with-guava"));
        assertTrue(coordinates.contains("com.fasterxml.jackson.core:jackson-databind:2.18.9"));
        assertTrue(coordinates.contains("org.apache.commons:commons-lang3:3.18.0"));
        assertFalse(coordinates.stream().anyMatch(coordinate -> coordinate.startsWith("org.apache.httpcomponents:")));
        assertEquals(1, legalReviewRows.size());
        for (String row : legalReviewRows) {
            String[] fields = row.split("\\t", -1);
            assertEquals(2, fields.length, "malformed legal review row: " + row);
            assertTrue(coordinates.contains(fields[0]), "legal review must reference a runtime coordinate");
            assertFalse(fields[1].isBlank(), "legal review reason must be explicit");
        }
    }

    @Test
    void generatedClosureIndexesEveryRuntimeDependencyAndRequiredCalciteNotices() throws Exception {
        String manifestProperty = System.getProperty(GENERATED_MANIFEST_PROPERTY);
        String noticesProperty = System.getProperty(GENERATED_NOTICES_PROPERTY);
        String metadataProperty = System.getProperty(GENERATED_METADATA_PROPERTY);
        if (manifestProperty == null && noticesProperty == null && metadataProperty == null) {
            return;
        }
        assertTrue(manifestProperty != null && noticesProperty != null && metadataProperty != null,
                "all generated legal closure paths must be supplied together");

        Path manifest = Path.of(manifestProperty);
        Path notices = Path.of(noticesProperty);
        Path metadata = Path.of(metadataProperty);
        List<String> dependencies = Files.readAllLines(manifest);
        assertEquals(RUNTIME_DEPENDENCY_COUNT, dependencies.size());
        assertSortedAndUnique(dependencies, "generated runtime dependency manifest");

        List<String> expectedPolicyKeys = dependencies.stream().map(LegalClosurePolicyTest::policyKey).toList();
        List<String> actualPolicyKeys = dataRows(projectDirectory().resolve("src/legal/runtime-license-map.tsv"))
                .stream()
                .map(row -> row.substring(0, row.indexOf('\t')))
                .toList();
        assertEquals(actualPolicyKeys, expectedPolicyKeys, "generated graph must exactly match reviewed policy");

        String noticeText = Files.readString(notices);
        assertTrue(noticeText.contains("Legal closure format: 1"));
        assertTrue(noticeText.contains("Runtime dependency count: " + RUNTIME_DEPENDENCY_COUNT));
        assertTrue(noticeText.contains("Dependencies requiring legal review: 1"));
        for (String coordinate : expectedPolicyKeys) {
            assertTrue(noticeText.contains("### `" + coordinate + "`"), "missing notice index row: " + coordinate);
        }
        int calciteStart = noticeText.indexOf("### `org.apache.calcite:calcite-core:1.42.0`");
        int calciteEnd = noticeText.indexOf("\n### `", calciteStart + 5);
        String calciteSection = noticeText.substring(calciteStart, calciteEnd);
        assertTrue(calciteSection.contains("`META-INF/LICENSE` (embedded in JAR)"));
        assertTrue(calciteSection.contains("`META-INF/NOTICE` (embedded in JAR)"));
        String jacksonCoreSection = dependencySection(
                noticeText, "com.fasterxml.jackson.core:jackson-core:2.18.9");
        assertTrue(jacksonCoreSection.contains("`META-INF/FastDoubleParser-LICENSE` (embedded in JAR)"));
        assertTrue(jacksonCoreSection.contains("`META-INF/FastDoubleParser-ThirdParty-LICENSE` (embedded in JAR)"));
        assertTrue(jacksonCoreSection.contains("`META-INF/Schubfach-LICENSE` (embedded in JAR)"));
        assertTrue(jacksonCoreSection.contains("`META-INF/NOTICE` (embedded in JAR)"));
        assertTrue(jacksonCoreSection.contains("licenses/fast-double-parser-07d9189-bsl-1.0.txt"));
        assertTrue(jacksonCoreSection.contains("licenses/fastdoubleparser-522be16-mit.txt"));
        String commonsMathSection = dependencySection(noticeText, "org.apache.commons:commons-math3:3.6.1");
        assertTrue(commonsMathSection.contains(
                "Apache-2.0 AND Minpack AND BSD-2-Clause AND BSD-3-Clause"));
        assertTrue(commonsMathSection.contains("`META-INF/LICENSE.txt` (embedded in JAR)"));
        assertTrue(commonsMathSection.contains("Legal review required: **yes**"));
        assertTrue(noticeText.contains("licenses/jakarta-transaction-1.3.3-notice.txt"));

        Properties properties = new Properties();
        try (var input = Files.newInputStream(metadata)) {
            properties.load(input);
        }
        assertEquals("1", properties.getProperty("formatVersion"));
        assertEquals(Integer.toString(RUNTIME_DEPENDENCY_COUNT), properties.getProperty("runtimeDependencyCount"));
        assertEquals("1", properties.getProperty("legalReviewRequiredCount"));
        assertTrue(Integer.parseInt(properties.getProperty("legalDocumentCount")) >= 30);
        assertEquals(sha256(manifest), properties.getProperty("runtimeDependenciesSha256"));
        assertEquals(sha256(notices), properties.getProperty("thirdPartyNoticesSha256"));
    }

    private static Path projectDirectory() {
        return Path.of(System.getProperty("basedir", ".")).toAbsolutePath().normalize();
    }

    private static List<String> dataRows(Path path) throws IOException {
        List<String> rows = new ArrayList<>();
        for (String line : Files.readAllLines(path)) {
            if (!line.isBlank() && !line.stripLeading().startsWith("#")) {
                rows.add(line);
            }
        }
        return rows;
    }

    private static void assertSortedAndUnique(List<String> rows, String label) {
        List<String> sorted = rows.stream().sorted().distinct().toList();
        assertEquals(sorted, rows, label + " must be sorted and unique");
    }

    private static void assertReferencedFilesAreDeclared(String field, Set<String> declaredPaths, String coordinate) {
        if (field.equals("-")) {
            return;
        }
        for (String path : field.split(",")) {
            assertTrue(declaredPaths.contains(path), "undeclared legal file for " + coordinate + ": " + path);
        }
    }

    private static String policyKey(String dependency) {
        String[] fields = dependency.split(":", -1);
        assertTrue(fields.length == 5 || fields.length == 6, "malformed runtime coordinate: " + dependency);
        String version = fields.length == 5 ? fields[3] : fields[4];
        return fields[0] + ":" + fields[1] + ":" + version;
    }

    private static String dependencySection(String notices, String coordinate) {
        int start = notices.indexOf("### `" + coordinate + "`");
        assertTrue(start >= 0, "missing notice section: " + coordinate);
        int end = notices.indexOf("\n### `", start + 5);
        return notices.substring(start, end < 0 ? notices.length() : end);
    }

    private static String sha256(Path path) throws IOException, NoSuchAlgorithmException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (var input = Files.newInputStream(path)) {
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                digest.update(buffer, 0, read);
            }
        }
        return HexFormat.of().formatHex(digest.digest());
    }
}
