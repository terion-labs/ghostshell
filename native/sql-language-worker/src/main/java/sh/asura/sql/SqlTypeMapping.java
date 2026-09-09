package sh.asura.sql;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.sql.type.SqlTypeName;

import java.util.Locale;

/** Maps Asura's provider-neutral value kinds to conservative Calcite SQL types. */
final class SqlTypeMapping {
    private SqlTypeMapping() {
    }

    static RelDataType create(RelDataTypeFactory factory, CatalogColumn column) {
        return create(factory, column.dataTypeName(), column.valueKind());
    }

    static RelDataType create(
        RelDataTypeFactory factory,
        String dataTypeName,
        String valueKind) {
        SqlTypeName typeName = valueKind == null
            ? SqlTypeName.ANY
            : fromValueKind(valueKind);
        if (typeName == SqlTypeName.ANY) {
            typeName = dataTypeName == null
                ? SqlTypeName.ANY
                : fromProviderType(dataTypeName);
        }
        return factory.createSqlType(typeName);
    }

    private static SqlTypeName fromValueKind(String valueKind) {
        return switch (normalize(valueKind)) {
            case "text" -> SqlTypeName.VARCHAR;
            case "boolean" -> SqlTypeName.BOOLEAN;
            case "signedinteger" -> SqlTypeName.BIGINT;
            case "unsignedinteger" -> SqlTypeName.UBIGINT;
            case "decimal" -> SqlTypeName.DECIMAL;
            case "floatingpoint" -> SqlTypeName.DOUBLE;
            case "date" -> SqlTypeName.DATE;
            case "time" -> SqlTypeName.TIME;
            case "timestamp" -> SqlTypeName.TIMESTAMP;
            case "timestampwithzone" -> SqlTypeName.TIMESTAMP_TZ;
            case "guid" -> SqlTypeName.UUID;
            case "binary" -> SqlTypeName.VARBINARY;
            case "network" -> SqlTypeName.VARCHAR;
            default -> SqlTypeName.ANY;
        };
    }

    private static SqlTypeName fromProviderType(String providerType) {
        String type = normalize(providerType);
        if (containsAny(type, "bool", "bit")) {
            return SqlTypeName.BOOLEAN;
        }
        if (containsAny(type, "tinyint", "int1")) {
            return SqlTypeName.TINYINT;
        }
        if (containsAny(type, "smallint", "int2")) {
            return SqlTypeName.SMALLINT;
        }
        if (containsAny(type, "bigint", "int8", "serial8", "bigserial")) {
            return SqlTypeName.BIGINT;
        }
        if (containsAny(type, "integer", "int4", "serial", "mediumint")) {
            return SqlTypeName.INTEGER;
        }
        if (containsAny(type, "decimal", "numeric", "number", "money")) {
            return SqlTypeName.DECIMAL;
        }
        if (containsAny(type, "double", "float", "real")) {
            return SqlTypeName.DOUBLE;
        }
        if (type.contains("timestamp") || type.contains("datetime")) {
            return type.contains("zone") || type.contains("tz")
                ? SqlTypeName.TIMESTAMP_TZ
                : SqlTypeName.TIMESTAMP;
        }
        if (type.equals("date")) {
            return SqlTypeName.DATE;
        }
        if (type.startsWith("time")) {
            return SqlTypeName.TIME;
        }
        if (containsAny(type, "binary", "blob", "bytea", "raw")) {
            return SqlTypeName.VARBINARY;
        }
        if (type.contains("uuid") || type.contains("uniqueidentifier")) {
            return SqlTypeName.UUID;
        }
        if (containsAny(type, "char", "text", "string", "clob", "xml", "enum")) {
            return SqlTypeName.VARCHAR;
        }
        return SqlTypeName.ANY;
    }

    private static boolean containsAny(String value, String... candidates) {
        for (String candidate : candidates) {
            if (value.contains(candidate)) {
                return true;
            }
        }
        return false;
    }

    private static String normalize(String value) {
        return value.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]", "");
    }
}
