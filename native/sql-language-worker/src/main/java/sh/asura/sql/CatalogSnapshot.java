package sh.asura.sql;

import com.fasterxml.jackson.databind.JsonNode;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/** Detached metadata supplied by Asura. The worker never receives database credentials. */
record CatalogSnapshot(
    String driverId,
    String defaultCatalog,
    String defaultSchema,
    List<CatalogObject> objects,
    List<CatalogRoutine> routines,
    CatalogMetadataCoverage routineCoverage,
    CatalogMetadataCoverage intrinsicCoverage,
    List<CatalogIntrinsicSymbol> intrinsicSymbols) {

    private static final int MAXIMUM_OBJECTS = 50_000;
    private static final int MAXIMUM_COLUMNS = 50_000;
    private static final int MAXIMUM_ROUTINES = 50_000;
    private static final int MAXIMUM_ROUTINE_PARAMETERS = 50_000;
    private static final int MAXIMUM_ROUTINE_ARGUMENTS = 1_024;
    private static final int MAXIMUM_INTRINSIC_SYMBOLS = 50_000;
    private static final Set<String> ROUTINE_KINDS = Set.of(
        "unknown", "scalar", "aggregate", "window", "table");
    private static final Set<String> PARAMETER_MODES = Set.of(
        "unknown", "in", "out", "inout");

    CatalogSnapshot(
        String driverId,
        String defaultCatalog,
        String defaultSchema,
        List<CatalogObject> objects) {
        this(
            driverId,
            defaultCatalog,
            defaultSchema,
            objects,
            List.of(),
            CatalogMetadataCoverage.NONE,
            CatalogMetadataCoverage.NONE,
            List.of());
    }

    CatalogSnapshot(
        String driverId,
        String defaultCatalog,
        String defaultSchema,
        List<CatalogObject> objects,
        List<CatalogRoutine> routines) {
        this(
            driverId,
            defaultCatalog,
            defaultSchema,
            objects,
            routines,
            CatalogMetadataCoverage.USER_DEFINED_ONLY,
            CatalogMetadataCoverage.NONE,
            List.of());
    }

    static CatalogSnapshot parse(JsonNode params) throws ProtocolException {
        JsonNode catalog = params.get("catalog");
        if (catalog == null || !catalog.isObject()) {
            throw new ProtocolException("invalidParams", "Property 'catalog' must be an object.");
        }

        String driverId = ProtocolJson.requiredText(catalog, "driverId");
        String defaultCatalog = ProtocolJson.optionalText(catalog, "defaultCatalog");
        String defaultSchema = ProtocolJson.optionalText(catalog, "defaultSchema");
        JsonNode objectsNode = catalog.get("objects");
        if (objectsNode == null || !objectsNode.isArray()) {
            throw new ProtocolException("invalidParams", "Property 'catalog.objects' must be an array.");
        }
        if (objectsNode.size() > MAXIMUM_OBJECTS) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog contains more than " + MAXIMUM_OBJECTS + " objects.");
        }

        var objects = new ArrayList<CatalogObject>(objectsNode.size());
        for (int index = 0; index < objectsNode.size(); index++) {
            objects.add(parseObject(objectsNode.get(index), index));
        }

        JsonNode routinesNode = catalog.get("routines");
        if (routinesNode != null && !routinesNode.isArray()) {
            throw new ProtocolException(
                "invalidParams",
                "Property 'catalog.routines' must be an array when present.");
        }
        if (routinesNode != null && routinesNode.size() > MAXIMUM_ROUTINES) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog contains more than " + MAXIMUM_ROUTINES + " routines.");
        }
        var routines = new ArrayList<CatalogRoutine>(
            routinesNode == null ? 0 : routinesNode.size());
        int parameterCount = 0;
        if (routinesNode != null) {
            for (int index = 0; index < routinesNode.size(); index++) {
                CatalogRoutine routine = parseRoutine(routinesNode.get(index), index);
                parameterCount += routine.parameters().size();
                if (parameterCount > MAXIMUM_ROUTINE_PARAMETERS) {
                    throw new ProtocolException(
                        "invalidParams",
                        "Catalog contains more than " + MAXIMUM_ROUTINE_PARAMETERS
                            + " routine parameters.");
                }
                routines.add(routine);
            }
        }
        CatalogMetadataCoverage routineCoverage = CatalogMetadataCoverage.parse(
            ProtocolJson.optionalText(catalog, "routineCoverage"),
            "catalog.routineCoverage",
            true);
        CatalogMetadataCoverage intrinsicCoverage = CatalogMetadataCoverage.parse(
            ProtocolJson.optionalText(catalog, "intrinsicCoverage"),
            "catalog.intrinsicCoverage",
            false);
        JsonNode intrinsicSymbolsNode = catalog.get("intrinsicSymbols");
        if (intrinsicSymbolsNode != null && !intrinsicSymbolsNode.isArray()) {
            throw new ProtocolException(
                "invalidParams",
                "Property 'catalog.intrinsicSymbols' must be an array when present.");
        }
        if (intrinsicSymbolsNode != null
            && intrinsicSymbolsNode.size() > MAXIMUM_INTRINSIC_SYMBOLS) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog contains more than " + MAXIMUM_INTRINSIC_SYMBOLS
                    + " intrinsic symbols.");
        }
        var intrinsicSymbols = new ArrayList<CatalogIntrinsicSymbol>(
            intrinsicSymbolsNode == null ? 0 : intrinsicSymbolsNode.size());
        if (intrinsicSymbolsNode != null) {
            for (int index = 0; index < intrinsicSymbolsNode.size(); index++) {
                intrinsicSymbols.add(parseIntrinsicSymbol(
                    intrinsicSymbolsNode.get(index),
                    index));
            }
        }

        return new CatalogSnapshot(
            driverId,
            defaultCatalog,
            defaultSchema,
            List.copyOf(objects),
            List.copyOf(routines),
            routineCoverage,
            intrinsicCoverage,
            List.copyOf(intrinsicSymbols));
    }

    private static CatalogObject parseObject(JsonNode node, int objectIndex)
        throws ProtocolException {
        if (!node.isObject()) {
            throw invalidObject(objectIndex, "must be an object");
        }

        JsonNode idNode = node.get("id");
        if (idNode == null || !idNode.isObject()) {
            throw invalidObject(objectIndex, "must contain an object property 'id'");
        }
        var id = new CatalogObjectId(
            ProtocolJson.optionalText(idNode, "catalog"),
            ProtocolJson.optionalText(idNode, "schema"),
            ProtocolJson.requiredText(idNode, "name"));
        String kind = ProtocolJson.requiredText(node, "kind");

        JsonNode columnsNode = node.get("columns");
        if (columnsNode == null || !columnsNode.isArray()) {
            throw invalidObject(objectIndex, "must contain an array property 'columns'");
        }
        if (columnsNode.size() > MAXIMUM_COLUMNS) {
            throw invalidObject(objectIndex, "contains too many columns");
        }

        var columns = new ArrayList<CatalogColumn>(columnsNode.size());
        for (int columnIndex = 0; columnIndex < columnsNode.size(); columnIndex++) {
            CatalogColumn column = parseColumn(columnsNode.get(columnIndex), objectIndex, columnIndex);
            columns.add(column);
        }

        return new CatalogObject(id, kind, List.copyOf(columns));
    }

    private static CatalogColumn parseColumn(JsonNode node, int objectIndex, int columnIndex)
        throws ProtocolException {
        if (!node.isObject()) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog object " + objectIndex + " column " + columnIndex + " must be an object.");
        }

        return new CatalogColumn(
            ProtocolJson.requiredText(node, "name"),
            ProtocolJson.requiredText(node, "dataTypeName"),
            ProtocolJson.requiredText(node, "valueKind"),
            ProtocolJson.optionalBoolean(node, "isNullable"));
    }

    private static CatalogRoutine parseRoutine(JsonNode node, int routineIndex)
        throws ProtocolException {
        if (!node.isObject()) {
            throw invalidRoutine(routineIndex, "must be an object");
        }
        JsonNode idNode = node.get("id");
        if (idNode == null || !idNode.isObject()) {
            throw invalidRoutine(routineIndex, "must contain an object property 'id'");
        }
        var id = new CatalogObjectId(
            ProtocolJson.optionalText(idNode, "catalog"),
            ProtocolJson.optionalText(idNode, "schema"),
            ProtocolJson.requiredText(idNode, "name"));
        String kind = normalizedEnum(
            ProtocolJson.requiredText(node, "kind"),
            ROUTINE_KINDS,
            "Catalog routine " + routineIndex + " kind");
        String signature = ProtocolJson.requiredText(node, "signature");
        JsonNode parametersNode = node.get("parameters");
        if (parametersNode == null || !parametersNode.isArray()) {
            throw invalidRoutine(routineIndex, "must contain an array property 'parameters'");
        }
        if (parametersNode.size() > MAXIMUM_ROUTINE_ARGUMENTS) {
            throw invalidRoutine(routineIndex, "contains too many parameters");
        }
        var parameters = new ArrayList<CatalogRoutineParameter>(parametersNode.size());
        for (int parameterIndex = 0; parameterIndex < parametersNode.size(); parameterIndex++) {
            parameters.add(parseRoutineParameter(
                parametersNode.get(parameterIndex),
                routineIndex,
                parameterIndex));
        }
        validateRoutineParameters(parameters, routineIndex);
        Integer minimumArgumentCount = optionalNonNegativeInteger(
            node,
            "minimumArgumentCount");
        Integer maximumArgumentCount = optionalNonNegativeInteger(
            node,
            "maximumArgumentCount");
        if (maximumArgumentCount != null && minimumArgumentCount == null) {
            throw invalidRoutine(
                routineIndex,
                "must provide minimumArgumentCount when maximumArgumentCount is present");
        }
        if (minimumArgumentCount != null
            && maximumArgumentCount != null
            && minimumArgumentCount > maximumArgumentCount) {
            throw invalidRoutine(
                routineIndex,
                "has minimumArgumentCount greater than maximumArgumentCount");
        }
        if (minimumArgumentCount != null && !parameters.isEmpty()) {
            validateExplicitArity(
                parameters,
                minimumArgumentCount,
                maximumArgumentCount,
                routineIndex);
        }
        return new CatalogRoutine(
            id,
            kind,
            signature,
            List.copyOf(parameters),
            ProtocolJson.optionalText(node, "returnTypeName"),
            ProtocolJson.optionalText(node, "returnValueKind"),
            minimumArgumentCount,
            maximumArgumentCount);
    }

    private static CatalogIntrinsicSymbol parseIntrinsicSymbol(JsonNode node, int index)
        throws ProtocolException {
        if (!node.isObject()) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog intrinsic symbol " + index + " must be an object.");
        }
        String kind = ProtocolJson.requiredText(node, "kind").toLowerCase(Locale.ROOT);
        if (!kind.equals("keyword")) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog intrinsic symbol " + index + " kind must be keyword.");
        }
        return new CatalogIntrinsicSymbol(
            ProtocolJson.requiredText(node, "name"),
            kind);
    }

    private static CatalogRoutineParameter parseRoutineParameter(
        JsonNode node,
        int routineIndex,
        int parameterIndex) throws ProtocolException {
        if (!node.isObject()) {
            throw new ProtocolException(
                "invalidParams",
                "Catalog routine " + routineIndex + " parameter " + parameterIndex
                    + " must be an object.");
        }
        String mode = normalizedEnum(
            ProtocolJson.requiredText(node, "mode"),
            PARAMETER_MODES,
            "Catalog routine " + routineIndex + " parameter " + parameterIndex + " mode");
        return new CatalogRoutineParameter(
            ProtocolJson.optionalText(node, "name"),
            ProtocolJson.requiredText(node, "dataTypeName"),
            ProtocolJson.optionalText(node, "valueKind"),
            mode,
            Boolean.TRUE.equals(ProtocolJson.optionalBoolean(node, "isOptional")),
            Boolean.TRUE.equals(ProtocolJson.optionalBoolean(node, "isVariadic")));
    }

    private static void validateRoutineParameters(
        List<CatalogRoutineParameter> parameters,
        int routineIndex) throws ProtocolException {
        boolean optionalInputSeen = false;
        boolean variadicInputSeen = false;
        for (CatalogRoutineParameter parameter : parameters) {
            if (parameter.mode().equals("out")) {
                continue;
            }
            if (variadicInputSeen) {
                throw invalidRoutine(
                    routineIndex,
                    "has an input-capable parameter after its variadic parameter");
            }
            if (parameter.isVariadic()) {
                variadicInputSeen = true;
                continue;
            }
            if (optionalInputSeen && !parameter.isOptional()) {
                throw invalidRoutine(
                    routineIndex,
                    "has a required input parameter after an optional parameter");
            }
            optionalInputSeen |= parameter.isOptional();
        }
    }

    private static void validateExplicitArity(
        List<CatalogRoutineParameter> parameters,
        int explicitMinimum,
        Integer explicitMaximum,
        int routineIndex) throws ProtocolException {
        List<CatalogRoutineParameter> inputs = parameters.stream()
            .filter(parameter -> !parameter.mode().equals("out"))
            .toList();
        int derivedMinimum = (int) inputs.stream()
            .filter(parameter -> !parameter.isOptional() && !parameter.isVariadic())
            .count();
        boolean variadic = inputs.stream().anyMatch(CatalogRoutineParameter::isVariadic);
        Integer derivedMaximum = variadic ? null : inputs.size();
        if (explicitMinimum != derivedMinimum
            || !java.util.Objects.equals(explicitMaximum, derivedMaximum)) {
            throw invalidRoutine(
                routineIndex,
                "argument counts contradict its parameter metadata");
        }
    }

    private static Integer optionalNonNegativeInteger(JsonNode node, String property)
        throws ProtocolException {
        JsonNode value = node.get(property);
        if (value == null || value.isNull()) {
            return null;
        }
        if (!value.isIntegralNumber()
            || !value.canConvertToInt()
            || value.intValue() < 0
            || value.intValue() > MAXIMUM_ROUTINE_ARGUMENTS) {
            throw new ProtocolException(
                "invalidParams",
                "Property '" + property + "' must be null or an integer from 0 through "
                    + MAXIMUM_ROUTINE_ARGUMENTS + ".");
        }
        return value.intValue();
    }

    private static String normalizedEnum(
        String value,
        Set<String> allowed,
        String description) throws ProtocolException {
        String normalized = value.toLowerCase(Locale.ROOT);
        if (!allowed.contains(normalized)) {
            throw new ProtocolException(
                "invalidParams",
                description + " must be one of " + String.join(", ", allowed) + ".");
        }
        return normalized;
    }

    private static ProtocolException invalidObject(int index, String message) {
        return new ProtocolException(
            "invalidParams",
            "Catalog object " + index + " " + message + ".");
    }

    private static ProtocolException invalidRoutine(int index, String message) {
        return new ProtocolException(
            "invalidParams",
            "Catalog routine " + index + " " + message + ".");
    }
}

record CatalogObject(CatalogObjectId id, String kind, List<CatalogColumn> columns) {
}

record CatalogObjectId(String catalog, String schema, String name) {
}

record CatalogColumn(String name, String dataTypeName, String valueKind, Boolean isNullable) {
}

record CatalogRoutine(
    CatalogObjectId id,
    String kind,
    String signature,
    List<CatalogRoutineParameter> parameters,
    String returnTypeName,
    String returnValueKind,
    Integer minimumArgumentCount,
    Integer maximumArgumentCount) {
}

record CatalogRoutineParameter(
    String name,
    String dataTypeName,
    String valueKind,
    String mode,
    boolean isOptional,
    boolean isVariadic) {
}

enum CatalogMetadataCoverage {
    NONE("none"),
    USER_DEFINED_ONLY("userDefinedOnly"),
    COMPLETE("complete"),
    PARTIAL("partial");

    private final String wireName;

    CatalogMetadataCoverage(String wireName) {
        this.wireName = wireName;
    }

    @com.fasterxml.jackson.annotation.JsonValue
    String wireName() {
        return wireName;
    }

    static CatalogMetadataCoverage parse(
        String value,
        String property,
        boolean allowUserDefinedOnly)
        throws ProtocolException {
        if (value == null) {
            return NONE;
        }
        for (CatalogMetadataCoverage coverage : values()) {
            if (coverage.wireName.equals(value)
                && (coverage != USER_DEFINED_ONLY || allowUserDefinedOnly)) {
                return coverage;
            }
        }
        throw new ProtocolException(
            "invalidParams",
            "Property '" + property
                + "' must be none, "
                + (allowUserDefinedOnly ? "userDefinedOnly, " : "")
                + "complete, or partial.");
    }
}

record CatalogIntrinsicSymbol(String name, String kind) {
}
