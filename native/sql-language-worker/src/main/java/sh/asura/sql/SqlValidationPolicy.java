package sh.asura.sql;

import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.advise.SqlAdvisor.ValidateErrorInfo;
import org.apache.calcite.sql.util.SqlShuttle;

import java.util.Locale;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Keeps inline diagnostics conservative where Calcite is not the provider parser. */
final class SqlValidationPolicy {
    private static final Pattern UNKNOWN_COLUMN = Pattern.compile(
        "^Column '(.+)' not found (?:in any table|in table '.+')\\.?$");
    private static final Pattern AMBIGUOUS_COLUMN = Pattern.compile(
        "^Column '(.+)' is ambiguous\\.?$");

    private SqlValidationPolicy() {
    }

    static String shadowBindVariables(String sql) {
        char[] shadow = sql.toCharArray();
        int index = 0;
        while (index < shadow.length) {
            char current = shadow[index];
            if (current == '\'' || current == '"' || current == '`') {
                index = skipQuoted(shadow, index, current);
                continue;
            }
            if (current == '[') {
                index = skipBracketIdentifier(shadow, index);
                continue;
            }
            if (current == '-' && hasNext(shadow, index, '-')) {
                index = skipLineComment(shadow, index + 2);
                continue;
            }
            if (current == '/' && hasNext(shadow, index, '*')) {
                index = skipBlockComment(shadow, index + 2);
                continue;
            }
            if (current == '$') {
                String delimiter = dollarQuoteDelimiter(sql, index);
                if (delimiter != null) {
                    int close = sql.indexOf(delimiter, index + delimiter.length());
                    index = close < 0 ? shadow.length : close + delimiter.length();
                    continue;
                }
                int end = bindEnd(shadow, index + 1, true);
                if (end > index + 1) {
                    shadowToken(shadow, index, end);
                    index = end;
                    continue;
                }
            }
            if (current == '@') {
                int nameStart = hasNext(shadow, index, '@') ? index + 2 : index + 1;
                int end = bindEnd(shadow, nameStart, false);
                if (end > nameStart) {
                    shadowToken(shadow, index, end);
                    index = end;
                    continue;
                }
            }
            if (current == ':'
                && (index == 0 || shadow[index - 1] != ':')
                && (index + 1 >= shadow.length || shadow[index + 1] != ':')) {
                int end = bindEnd(shadow, index + 1, false);
                if (end > index + 1) {
                    shadowToken(shadow, index, end);
                    index = end;
                    continue;
                }
            }
            index++;
        }
        return new String(shadow);
    }

    static boolean supportsCatalogDiagnostics(SqlNode statement) {
        if (!statement.getKind().belongsTo(SqlKind.QUERY)) {
            return false;
        }

        var hasFrom = new AtomicBoolean();
        statement.accept(new SqlShuttle() {
            @Override
            public SqlNode visit(SqlCall call) {
                if (call instanceof SqlSelect select && select.getFrom() != null) {
                    hasFrom.set(true);
                }
                return super.visit(call);
            }
        });
        return hasFrom.get();
    }

    static CatalogIssue catalogIssue(
        ValidateErrorInfo error,
        SqlDialectProfile profile) {
        String message = error.getMessage();
        if (message == null) {
            return null;
        }

        Matcher unknown = UNKNOWN_COLUMN.matcher(message);
        if (unknown.matches()) {
            if (isProviderPseudoColumn(profile.driverId(), unknown.group(1))) {
                return null;
            }
            return new CatalogIssue("unknownColumn", message);
        }
        if (AMBIGUOUS_COLUMN.matcher(message).matches()) {
            return new CatalogIssue("ambiguousColumn", message);
        }
        return null;
    }

    private static int skipQuoted(char[] text, int start, char quote) {
        int index = start + 1;
        while (index < text.length) {
            if (text[index] == '\\' && index + 1 < text.length) {
                index += 2;
                continue;
            }
            if (text[index] == quote) {
                if (index + 1 < text.length && text[index + 1] == quote) {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        return text.length;
    }

    private static int skipBracketIdentifier(char[] text, int start) {
        int index = start + 1;
        while (index < text.length) {
            if (text[index] == ']') {
                if (index + 1 < text.length && text[index + 1] == ']') {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        return text.length;
    }

    private static int skipLineComment(char[] text, int start) {
        int index = start;
        while (index < text.length && text[index] != '\n' && text[index] != '\r') {
            index++;
        }
        return index;
    }

    private static int skipBlockComment(char[] text, int start) {
        int depth = 1;
        int index = start;
        while (index < text.length && depth > 0) {
            if (text[index] == '/' && hasNext(text, index, '*')) {
                depth++;
                index += 2;
            } else if (text[index] == '*' && hasNext(text, index, '/')) {
                depth--;
                index += 2;
            } else {
                index++;
            }
        }
        return index;
    }

    private static String dollarQuoteDelimiter(String sql, int start) {
        int index = start + 1;
        if (index < sql.length() && sql.charAt(index) == '$') {
            return "$$";
        }
        if (index >= sql.length() || !isIdentifierStart(sql.charAt(index))) {
            return null;
        }
        index++;
        while (index < sql.length() && isDollarTagPart(sql.charAt(index))) {
            index++;
        }
        return index < sql.length() && sql.charAt(index) == '$'
            ? sql.substring(start, index + 1)
            : null;
    }

    private static int bindEnd(char[] text, int start, boolean allowLeadingDigit) {
        if (start >= text.length) {
            return start;
        }
        char first = text[start];
        if (!(isIdentifierStart(first) || (allowLeadingDigit && Character.isDigit(first)))) {
            return start;
        }
        int index = start + 1;
        while (index < text.length && isIdentifierPart(text[index])) {
            index++;
        }
        return index;
    }

    private static void shadowToken(char[] text, int start, int end) {
        text[start] = '?';
        for (int index = start + 1; index < end; index++) {
            text[index] = ' ';
        }
    }

    private static boolean hasNext(char[] text, int index, char expected) {
        return index + 1 < text.length && text[index + 1] == expected;
    }

    private static boolean isIdentifierStart(char value) {
        return value == '_' || Character.isLetter(value);
    }

    private static boolean isIdentifierPart(char value) {
        return value == '_' || value == '$' || Character.isLetterOrDigit(value);
    }

    private static boolean isDollarTagPart(char value) {
        return value == '_' || Character.isLetterOrDigit(value);
    }

    static boolean isProviderPseudoColumn(String driverId, String columnName) {
        String normalized = columnName.toUpperCase(Locale.ROOT);
        if (driverId.equals("clickhouse") && normalized.startsWith("_")) {
            return true;
        }
        Set<String> names = switch (driverId) {
            case "postgres", "cockroach", "redshift" -> Set.of(
                "CTID", "OID", "TABLEOID", "XMIN", "XMAX", "CMIN", "CMAX");
            case "sqlite", "duckdb" -> Set.of("ROWID", "OID", "_ROWID_");
            case "oracle" -> Set.of(
                "ROWNUM", "ROWID", "ORA_ROWSCN", "LEVEL",
                "CONNECT_BY_ISLEAF", "CONNECT_BY_ISCYCLE");
            case "firebird" -> Set.of("RDB$DB_KEY", "RDB$RECORD_VERSION");
            default -> Set.of();
        };
        return names.contains(normalized);
    }

    record CatalogIssue(String code, String message) {
    }
}
