package sh.asura.sql;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/** Reads and writes the worker's 4-byte big-endian, length-prefixed frames. */
final class FrameCodec {
    static final int MAXIMUM_FRAME_BYTES = 8 * 1024 * 1024;
    private static final int HEADER_BYTES = 4;

    private FrameCodec() {
    }

    static byte[] read(InputStream input) throws IOException, ProtocolException {
        int first = input.read();
        if (first == -1) {
            return null;
        }

        byte[] header = new byte[HEADER_BYTES];
        header[0] = (byte) first;
        readFully(input, header, 1, HEADER_BYTES - 1, "frame header");

        long length = Integer.toUnsignedLong(
            (Byte.toUnsignedInt(header[0]) << 24)
                | (Byte.toUnsignedInt(header[1]) << 16)
                | (Byte.toUnsignedInt(header[2]) << 8)
                | Byte.toUnsignedInt(header[3]));
        if (length == 0 || length > MAXIMUM_FRAME_BYTES) {
            throw new ProtocolException(
                "invalidFrame",
                "Frame length must be between 1 and " + MAXIMUM_FRAME_BYTES + " bytes.",
                true);
        }

        byte[] payload = new byte[(int) length];
        readFully(input, payload, 0, payload.length, "frame payload");
        return payload;
    }

    static void write(OutputStream output, byte[] payload) throws IOException, ProtocolException {
        if (payload.length == 0 || payload.length > MAXIMUM_FRAME_BYTES) {
            throw new ProtocolException(
                "responseTooLarge",
                "Response exceeds the maximum frame size of " + MAXIMUM_FRAME_BYTES + " bytes.");
        }

        int length = payload.length;
        output.write((length >>> 24) & 0xff);
        output.write((length >>> 16) & 0xff);
        output.write((length >>> 8) & 0xff);
        output.write(length & 0xff);
        output.write(payload);
        output.flush();
    }

    private static void readFully(
        InputStream input,
        byte[] destination,
        int offset,
        int length,
        String description) throws IOException, ProtocolException {
        int total = 0;
        while (total < length) {
            int count = input.read(destination, offset + total, length - total);
            if (count == -1) {
                throw new ProtocolException(
                    "invalidFrame",
                    "Unexpected end of input while reading " + description + ".",
                    true);
            }
            if (count == 0) {
                continue;
            }
            total += count;
        }
    }
}
