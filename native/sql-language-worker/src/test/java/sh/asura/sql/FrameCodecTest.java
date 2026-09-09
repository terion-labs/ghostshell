package sh.asura.sql;

import org.junit.jupiter.api.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

final class FrameCodecTest {
    @Test
    void roundTripsUtf8Payload() throws Exception {
        byte[] payload = ProtocolJson.utf8("{\"sql\":\"SELECT 'Привіт 😀'\"}");
        var output = new ByteArrayOutputStream();

        FrameCodec.write(output, payload);

        assertArrayEquals(payload, FrameCodec.read(new ByteArrayInputStream(output.toByteArray())));
    }

    @Test
    void returnsNullOnlyForCleanEndOfStream() throws Exception {
        assertNull(FrameCodec.read(new ByteArrayInputStream(new byte[0])));
    }

    @Test
    void rejectsTruncatedPayloadAsFatal() {
        byte[] truncated = {0, 0, 0, 4, '{', '}'};

        ProtocolException error = assertThrows(
            ProtocolException.class,
            () -> FrameCodec.read(new ByteArrayInputStream(truncated)));

        assertEquals("invalidFrame", error.code());
        assertTrue(error.fatal());
    }

    @Test
    void rejectsFramesOverEightMibAsFatalWithoutAllocatingThem() {
        int length = FrameCodec.MAXIMUM_FRAME_BYTES + 1;
        byte[] header = {
            (byte) (length >>> 24),
            (byte) (length >>> 16),
            (byte) (length >>> 8),
            (byte) length
        };

        ProtocolException error = assertThrows(
            ProtocolException.class,
            () -> FrameCodec.read(new ByteArrayInputStream(header)));

        assertEquals("invalidFrame", error.code());
        assertTrue(error.fatal());
    }
}
