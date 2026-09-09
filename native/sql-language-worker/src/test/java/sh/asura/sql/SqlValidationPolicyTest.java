package sh.asura.sql;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class SqlValidationPolicyTest {
    @Test
    void shadowsBindVariablesWithoutChangingUtf16OffsetsOrQuotedText() {
        String sql = "SELECT $1, @id, :name, id::text, '$2 @x :y', \"@quoted\", "
            + "$$ $3 @z :q $$ -- @comment\nFROM people /* :block */";

        String shadow = SqlValidationPolicy.shadowBindVariables(sql);

        assertEquals(sql.length(), shadow.length());
        assertEquals(sql.lines().count(), shadow.lines().count());
        assertEquals('?', shadow.charAt(sql.indexOf("$1")));
        assertEquals('?', shadow.charAt(sql.indexOf("@id")));
        assertEquals('?', shadow.charAt(sql.indexOf(":name")));
        assertTrue(shadow.contains("id::text"));
        assertTrue(shadow.contains("'$2 @x :y'"));
        assertTrue(shadow.contains("\"@quoted\""));
        assertTrue(shadow.contains("$$ $3 @z :q $$"));
        assertTrue(shadow.contains("-- @comment"));
        assertTrue(shadow.contains("/* :block */"));
    }
}
