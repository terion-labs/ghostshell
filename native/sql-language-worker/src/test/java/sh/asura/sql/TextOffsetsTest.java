package sh.asura.sql;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

final class TextOffsetsTest {
    @Test
    void offsetsAreUtf16CodeUnitsLikeDotNetStringOffsets() {
        String sql = "SELECT '😀';\nmissing";

        assertEquals(sql.indexOf("missing"), TextOffsets.fromLineColumn(sql, 2, 1));
    }

    @Test
    void inclusiveRangeLengthHandlesMultilineRanges() {
        String sql = "abc\ndef";

        assertEquals(5, TextOffsets.inclusiveRangeLength(sql, 1, 3, 2, 3));
    }
}
