package sh.asura.sql;

import com.fasterxml.jackson.core.JsonProcessingException;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/** Synchronous protocol loop. A process is deliberately isolated to one editor session. */
final class SqlLanguageWorker {
    int run(InputStream input, OutputStream output) throws IOException {
        var session = new SqlLanguageSession();
        while (true) {
            byte[] frame;
            try {
                frame = FrameCodec.read(input);
            } catch (ProtocolException error) {
                writeError(output, 0, error.code(), error.getMessage());
                return error.fatal() ? 2 : 0;
            }
            if (frame == null) {
                return 0;
            }

            ProtocolJson.Request request;
            try {
                request = ProtocolJson.parseRequest(frame);
            } catch (ProtocolException error) {
                writeError(output, 0, error.code(), error.getMessage());
                continue;
            }

            try {
                SqlLanguageSession.Dispatch dispatch = session.dispatch(request);
                writeResponse(output, ProtocolJson.success(request.id(), dispatch.result()), request.id());
                if (dispatch.shutdown()) {
                    return 0;
                }
            } catch (ProtocolException error) {
                writeError(output, request.id(), error.code(), error.getMessage());
            } catch (RuntimeException error) {
                System.err.println("SQL intelligence request failed: " + safeMessage(error));
                writeError(
                    output,
                    request.id(),
                    "internalError",
                    "SQL intelligence failed: " + safeMessage(error));
            }
        }
    }

    private static void writeResponse(OutputStream output, byte[] response, long id)
        throws IOException {
        try {
            FrameCodec.write(output, response);
        } catch (ProtocolException error) {
            writeError(output, id, error.code(), error.getMessage());
        }
    }

    private static void writeError(OutputStream output, long id, String code, String message)
        throws IOException {
        try {
            FrameCodec.write(output, ProtocolJson.error(id, code, message));
        } catch (ProtocolException | JsonProcessingException error) {
            throw new IOException("Could not encode protocol error response.", error);
        }
    }

    private static String safeMessage(Throwable error) {
        String message = error.getMessage();
        return message == null || message.isBlank()
            ? error.getClass().getSimpleName()
            : message;
    }
}
