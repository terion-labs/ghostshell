package sh.asura.sql;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.node.ObjectNode;

/** Routes version-one requests and owns the process's single active catalog. */
final class SqlLanguageSession {
    private CalciteSqlLanguage language;

    Dispatch dispatch(ProtocolJson.Request request) throws ProtocolException {
        return switch (request.method()) {
            case "initialize", "updateCatalog" -> updateCatalog(request.params());
            case "complete" -> new Dispatch(complete(request.params()), false);
            case "diagnose" -> new Dispatch(diagnose(request.params()), false);
            case "shutdown" -> new Dispatch(shutdownResult(), true);
            default -> throw new ProtocolException(
                "methodNotFound",
                "Unknown method '" + request.method() + "'.");
        };
    }

    private Dispatch updateCatalog(JsonNode params) throws ProtocolException {
        CatalogSnapshot snapshot = CatalogSnapshot.parse(params);
        CalciteSqlLanguage replacement = CalciteSqlLanguage.create(snapshot);
        language = replacement;

        ObjectNode result = ProtocolJson.object();
        result.put("objectCount", snapshot.objects().size());
        return new Dispatch(result, false);
    }

    private ObjectNode complete(JsonNode params) throws ProtocolException {
        CalciteSqlLanguage active = requireCatalog();
        String sql = ProtocolJson.requiredString(params, "sql");
        int cursorOffset = ProtocolJson.requiredInteger(params, "cursorOffset");
        return active.complete(sql, cursorOffset, preferredObject(params));
    }

    private static CatalogObjectId preferredObject(JsonNode params) throws ProtocolException {
        JsonNode preferred = params.get("preferredObject");
        if (preferred == null || preferred.isNull()) {
            return null;
        }
        if (!preferred.isObject()) {
            throw new ProtocolException(
                "invalidParams",
                "Property 'preferredObject' must be null or an object.");
        }
        return new CatalogObjectId(
            ProtocolJson.optionalText(preferred, "catalog"),
            ProtocolJson.optionalText(preferred, "schema"),
            ProtocolJson.requiredText(preferred, "name"));
    }

    private ObjectNode diagnose(JsonNode params) throws ProtocolException {
        CalciteSqlLanguage active = requireCatalog();
        return active.diagnose(ProtocolJson.requiredString(params, "sql"));
    }

    private CalciteSqlLanguage requireCatalog() throws ProtocolException {
        if (language == null) {
            throw new ProtocolException(
                "notInitialized",
                "Initialize a catalog before requesting SQL intelligence.");
        }
        return language;
    }

    private static ObjectNode shutdownResult() {
        ObjectNode result = ProtocolJson.object();
        result.put("accepted", true);
        return result;
    }

    record Dispatch(JsonNode result, boolean shutdown) {
    }
}
