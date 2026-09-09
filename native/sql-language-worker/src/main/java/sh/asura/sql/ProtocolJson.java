package sh.asura.sql;

import com.fasterxml.jackson.core.JsonFactory;
import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.core.StreamReadConstraints;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.JsonNodeFactory;
import com.fasterxml.jackson.databind.node.ObjectNode;

import java.io.IOException;
import java.nio.charset.StandardCharsets;

/** Parses untrusted protocol JSON once and creates reflection-free JSON responses. */
final class ProtocolJson {
    static final int VERSION = 1;

    private static final ObjectMapper MAPPER = new ObjectMapper(
        JsonFactory.builder()
            .streamReadConstraints(StreamReadConstraints.builder()
                .maxNestingDepth(64)
                .maxStringLength(FrameCodec.MAXIMUM_FRAME_BYTES)
                .build())
            .build());

    private ProtocolJson() {
    }

    static Request parseRequest(byte[] payload) throws ProtocolException {
        JsonNode root;
        try {
            root = MAPPER.readTree(payload);
        } catch (IOException error) {
            throw new ProtocolException("invalidRequest", "Request is not valid JSON.");
        }

        if (root == null || !root.isObject()) {
            throw new ProtocolException("invalidRequest", "Request must be a JSON object.");
        }

        int version = requiredInteger(root, "version");
        if (version != VERSION) {
            throw new ProtocolException(
                "unsupportedVersion",
                "Unsupported protocol version " + version + "; expected " + VERSION + ".");
        }

        JsonNode idNode = root.get("id");
        if (idNode == null || !idNode.isIntegralNumber() || !idNode.canConvertToLong()) {
            throw new ProtocolException("invalidRequest", "Property 'id' must be a 64-bit integer.");
        }

        String method = requiredText(root, "method");
        JsonNode params = root.get("params");
        if (params == null || params.isNull()) {
            params = JsonNodeFactory.instance.objectNode();
        }
        if (!params.isObject()) {
            throw new ProtocolException("invalidRequest", "Property 'params' must be an object.");
        }

        return new Request(idNode.longValue(), method, params);
    }

    static byte[] success(long id, JsonNode result) throws JsonProcessingException {
        ObjectNode response = JsonNodeFactory.instance.objectNode();
        response.put("version", VERSION);
        response.put("id", id);
        response.set("result", result);
        return MAPPER.writeValueAsBytes(response);
    }

    static byte[] error(long id, String code, String message) throws JsonProcessingException {
        ObjectNode detail = JsonNodeFactory.instance.objectNode();
        detail.put("code", code);
        detail.put("message", message);

        ObjectNode response = JsonNodeFactory.instance.objectNode();
        response.put("version", VERSION);
        response.put("id", id);
        response.set("error", detail);
        return MAPPER.writeValueAsBytes(response);
    }

    static String requiredText(JsonNode object, String property) throws ProtocolException {
        String value = requiredString(object, property);
        if (value.isBlank()) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be a non-empty string.");
        }
        return value;
    }

    static String requiredString(JsonNode object, String property) throws ProtocolException {
        JsonNode node = object.get(property);
        if (node == null || !node.isTextual()) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be a string.");
        }
        return node.textValue();
    }

    static String optionalText(JsonNode object, String property) throws ProtocolException {
        JsonNode node = object.get(property);
        if (node == null || node.isNull()) {
            return null;
        }
        if (!node.isTextual() || node.textValue().isBlank()) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be null or a non-empty string.");
        }
        return node.textValue();
    }

    static int requiredInteger(JsonNode object, String property) throws ProtocolException {
        JsonNode node = object.get(property);
        if (node == null || !node.isIntegralNumber() || !node.canConvertToInt()) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be a 32-bit integer.");
        }
        return node.intValue();
    }

    static Boolean optionalBoolean(JsonNode object, String property) throws ProtocolException {
        JsonNode node = object.get(property);
        if (node == null || node.isNull()) {
            return null;
        }
        if (!node.isBoolean()) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be null or a boolean.");
        }
        return node.booleanValue();
    }

    static ObjectNode object() {
        return JsonNodeFactory.instance.objectNode();
    }

    static byte[] utf8(String json) {
        return json.getBytes(StandardCharsets.UTF_8);
    }

    record Request(long id, String method, JsonNode params) {
    }
}
