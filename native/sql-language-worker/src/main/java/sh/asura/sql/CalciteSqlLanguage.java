package sh.asura.sql;

import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import org.apache.calcite.jdbc.CalciteSchema;
import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.prepare.CalciteCatalogReader;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.schema.Schema;
import org.apache.calcite.schema.impl.AbstractSchema;
import org.apache.calcite.schema.impl.AbstractTable;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.advise.SqlAdvisor;
import org.apache.calcite.sql.advise.SqlAdvisor.ValidateErrorInfo;
import org.apache.calcite.sql.advise.SqlAdvisorValidator;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.validate.SqlMoniker;
import org.apache.calcite.sql.validate.SqlMonikerImpl;
import org.apache.calcite.sql.validate.SqlMonikerType;
import org.apache.calcite.sql.validate.SqlValidator;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** One immutable Calcite catalog and advisor, replaced atomically on catalog update. */
final class CalciteSqlLanguage {
    private static final int MAXIMUM_COMPLETION_ITEMS = 2_000;
    private static final Set<String> PORTABLE_EXPRESSION_KEYWORDS = Set.of(
        "AND", "BETWEEN", "CASE", "CAST", "ELSE", "END", "EXISTS", "IN", "IS",
        "LIKE", "NOT", "NULL", "OR", "THEN", "WHEN");
    private static final Set<String> PORTABLE_STATEMENT_START_KEYWORDS = Set.of(
        "SELECT", "WITH");
    private final SqlAdvisor advisor;
    private final SqlDialectProfile profile;
    private final CatalogSqlResolver catalogResolver;
    private final Map<String, String> objectKinds;
    private final Map<String, String> unambiguousColumnTypes;
    private final List<CompletionItem> rootCompletions;
    private final List<SqlFunctionCatalog.Candidate> functionCompletions;
    private final List<CompletionItem> typeCompletions;

    private CalciteSqlLanguage(
        SqlAdvisor advisor,
        SqlDialectProfile profile,
        CatalogSqlResolver catalogResolver,
        Map<String, String> objectKinds,
        Map<String, String> unambiguousColumnTypes,
        List<CompletionItem> rootCompletions,
        List<SqlFunctionCatalog.Candidate> functionCompletions,
        List<CompletionItem> typeCompletions) {
        this.advisor = advisor;
        this.profile = profile;
        this.catalogResolver = catalogResolver;
        this.objectKinds = objectKinds;
        this.unambiguousColumnTypes = unambiguousColumnTypes;
        this.rootCompletions = rootCompletions;
        this.functionCompletions = functionCompletions;
        this.typeCompletions = typeCompletions;
    }

    static CalciteSqlLanguage create(CatalogSnapshot snapshot) throws ProtocolException {
        SqlDialectProfile profile = SqlDialectProfile.forCatalog(snapshot);
        String rootName = snapshot.defaultCatalog() == null ? "" : snapshot.defaultCatalog();
        CalciteSchema root = CalciteSchema.createRootSchema(false, false, rootName);

        var objectKinds = new HashMap<String, String>();
        var columnTypes = new HashMap<String, Set<String>>();
        for (CatalogObject object : snapshot.objects()) {
            validateColumns(object, profile);
            List<String> parentPath = parentPath(snapshot, object.id(), profile);
            CalciteSchema schema = findOrAddSchema(root, parentPath, profile);
            if (schema.getTable(object.id().name(), profile.objectCaseSensitive()) != null) {
                throw new ProtocolException(
                    "invalidParams",
                    "Catalog contains duplicate object '" + qualifiedName(object.id()) + "'.");
            }

            schema.add(object.id().name(), new CatalogTable(object));
            List<String> fullPath = new ArrayList<>(schema.path(null));
            fullPath.add(object.id().name());
            objectKinds.put(pathKey(fullPath, profile), normalizeKind(object.kind()));
            collectColumnTypes(columnTypes, object, profile);
        }

        var typeFactory = new JavaTypeFactoryImpl();
        var catalogReader = new CalciteCatalogReader(
            root,
            defaultSchemaPath(snapshot),
            typeFactory,
            profile.connectionConfig());
        SqlFunctionCatalog functionCatalog = SqlFunctionCatalog.create(
            snapshot,
            profile,
            typeFactory);
        SqlValidator.Config validatorConfig = SqlValidator.Config.DEFAULT
            .withConformance(profile.conformance())
            .withLenientOperatorLookup(true);
        var validator = new SqlAdvisorValidator(
            functionCatalog.operatorTable(),
            catalogReader,
            typeFactory,
            validatorConfig);
        var advisor = new SqlAdvisor(validator, profile.parserConfig());
        List<CompletionItem> rootCompletions = createRootCompletions(snapshot, advisor);

        return new CalciteSqlLanguage(
            advisor,
            profile,
            new CatalogSqlResolver(snapshot, profile),
            Map.copyOf(objectKinds),
            collapseColumnTypes(columnTypes),
            rootCompletions,
            functionCatalog.candidates(),
            createTypeCompletions(snapshot));
    }

    ObjectNode complete(String sql, int cursorOffset) throws ProtocolException {
        return complete(sql, cursorOffset, null);
    }

    ObjectNode complete(
        String sql,
        int cursorOffset,
        CatalogObjectId preferredObjectId) throws ProtocolException {
        if (cursorOffset < 0 || cursorOffset > sql.length()) {
            throw new ProtocolException(
                "invalidParams",
                "Property 'cursorOffset' must be a valid UTF-16 offset into 'sql'.");
        }

        CatalogSqlResolver.Resolution catalogResolution = catalogResolver.resolve(sql);
        boolean nonCodeCompletion = catalogResolver.isNonCodeCompletionPosition(
            catalogResolution,
            cursorOffset);
        String[] replaced = new String[1];
        List<SqlMoniker> hints = sql.isBlank() || nonCodeCompletion
            ? List.of()
            : advisor.getCompletionHints(sql, cursorOffset, replaced);
        String prefix = replaced[0] == null ? "" : replaced[0];
        if (prefix.isEmpty()) {
            prefix = identifierPrefix(sql, cursorOffset);
        }
        int replacementStart = cursorOffset - prefix.length();
        int replacementEnd = findReplacementEnd(sql, cursorOffset, prefix);
        if (nonCodeCompletion) {
            return completionResult(
                replacementStart,
                replacementEnd - replacementStart,
                List.of());
        }
        CatalogSqlResolver.CompletionContext completionContext =
            catalogResolver.completionContext(catalogResolution, cursorOffset);
        boolean completedSelectProjection =
            catalogResolver.hasCompletedSelectProjection(
                catalogResolution,
                replacementStart);
        CatalogObject preferredObject = preferredObjectId == null
            ? null
            : catalogResolver.preferredObject(preferredObjectId);
        boolean preferredExpression = preferredObject != null
            && catalogResolution.preferredObjectFallbackSafe()
            && isExpressionContext(completionContext);
        boolean preferredMember = preferredObject != null
            && catalogResolution.preferredObjectFallbackSafe()
            && completionContext == CatalogSqlResolver.CompletionContext.QUALIFIED_MEMBER
            && catalogResolver.qualifierMatchesObject(
                catalogResolution,
                cursorOffset,
                preferredObject);
        boolean preferredOwnsColumns = preferredExpression || preferredMember;
        boolean preferredQualifierIsUnresolved = preferredObject != null
            && catalogResolution.preferredObjectFallbackSafe()
            && completionContext == CatalogSqlResolver.CompletionContext.QUALIFIED_MEMBER
            && !preferredMember;
        boolean suppressAdvisorColumns = preferredOwnsColumns
            || preferredQualifierIsUnresolved;

        var itemsByKey = new LinkedHashMap<String, CompletionItem>();
        if (sql.isBlank()) {
            for (CompletionItem item : rootCompletions) {
                addCompletion(itemsByKey, item);
            }
        }
        for (SqlMoniker hint : hints) {
            CompletionItem item = toCompletionItem(hint, prefix);
            if ((!suppressAdvisorColumns || !item.kind().equals("column"))
                && shouldIncludeAdvisorItem(
                    item,
                    completionContext,
                    prefix,
                    completedSelectProjection)) {
                addCompletion(itemsByKey, item);
            }
        }
        if (isStatementStartCompletion(sql, replacementStart)) {
            for (String keyword : PORTABLE_STATEMENT_START_KEYWORDS) {
                if (matchesPrefix(keyword, prefix, false)) {
                    addCompletion(itemsByKey, keywordCompletion(keyword, prefix));
                }
            }
        }
        if (shouldSupplementRelationCompletions(completionContext, itemsByKey)) {
            for (CompletionItem item : rootCompletions) {
                if (isRelationOnlyKind(item.kind())
                    && matchesPrefix(item.label(), prefix, profile.objectCaseSensitive())) {
                    addCompletion(itemsByKey, item);
                }
            }
        }
        if (completionContext == CatalogSqlResolver.CompletionContext.TYPE) {
            for (CompletionItem type : typeCompletions) {
                if (matchesPrefix(type.label(), prefix, false)) {
                    addCompletion(itemsByKey, type);
                }
            }
        }
        List<CatalogColumn> qualifiedColumns = catalogResolver.columnsForQualifier(
            catalogResolution,
            cursorOffset);
        for (CatalogColumn column : qualifiedColumns) {
            if (!matchesPrefix(column.name(), prefix, profile.columnCaseSensitive())) {
                continue;
            }
            var moniker = new SqlMonikerImpl(List.of(column.name()), SqlMonikerType.COLUMN);
            addCompletion(itemsByKey, toCompletionItem(moniker, prefix));
        }
        if (completionContext == CatalogSqlResolver.CompletionContext.QUALIFIED_MEMBER
            && qualifiedColumns.isEmpty()
            && !prefix.isEmpty()) {
            boolean openingParenthesisFollows = openingParenthesisFollows(
                sql,
                replacementEnd);
            for (SqlFunctionCatalog.Candidate function : functionCompletions) {
                if (!function.catalogOnly()
                    || function.nameParts().size() < 2
                    || function.aggregateOnly()
                    || function.requiresOverOnly()
                    || !matchesPrefix(function.label(), prefix, false)
                    || !catalogResolver.completionQualifierPathMatches(
                        catalogResolution,
                        cursorOffset,
                        function.nameParts().subList(
                            0,
                            function.nameParts().size() - 1))) {
                    continue;
                }
                addCompletion(itemsByKey, functionCompletion(
                    function,
                    prefix,
                    openingParenthesisFollows,
                    true));
            }
        }
        if (isExpressionContext(completionContext)
            && qualifiedColumns.isEmpty()) {
            List<CatalogSqlResolver.ExpressionColumn> expressionColumns =
                catalogResolver.columnsForExpression(catalogResolution);
            for (CatalogSqlResolver.ExpressionColumn candidate : expressionColumns) {
                CatalogColumn column = candidate.column();
                if (matchesPrefix(column.name(), prefix, profile.columnCaseSensitive())) {
                    addExpressionColumnCompletion(
                        itemsByKey,
                        candidate,
                        prefix,
                        candidate.qualifiedByDefault());
                }
                if (!prefix.isEmpty()
                    && matchesPrefix(candidate.qualifierName(), prefix, false)) {
                    addExpressionColumnCompletion(itemsByKey, candidate, "", true);
                }
            }
            if (!prefix.isEmpty()
                && !isOpeningQuote(prefix.charAt(0))
                && !catalogResolver.isQuotedIdentifierCompletion(
                    catalogResolution,
                    cursorOffset)) {
                boolean openingParenthesisFollows = openingParenthesisFollows(
                    sql,
                    replacementEnd);
                for (SqlFunctionCatalog.Candidate function : functionCompletions) {
                    if (matchesPrefix(function.label(), prefix, false)
                        && functionAllowedInContext(function, completionContext)) {
                        addCompletion(itemsByKey, functionCompletion(
                            function,
                            prefix,
                            openingParenthesisFollows,
                            false));
                    }
                }
            }
        }
        if (preferredExpression) {
            addPreferredExpressionCompletions(
                itemsByKey,
                preferredObject,
                prefix);
        } else if (preferredMember) {
            addPreferredMemberCompletions(
                itemsByKey,
                preferredObject,
                prefix);
        }
        for (String column : CteCompletion.projectedColumns(
            sql,
            replacementStart,
            replacementEnd,
            profile)) {
            var moniker = new SqlMonikerImpl(List.of(column), SqlMonikerType.COLUMN);
            addCompletion(itemsByKey, toCompletionItem(moniker, prefix));
        }
        List<CompletionItem> items = new ArrayList<>(itemsByKey.values());
        items.sort(Comparator
            .comparingInt((CompletionItem item) -> completionPriority(item.kind()))
            .thenComparing(CompletionItem::label, String.CASE_INSENSITIVE_ORDER)
            .thenComparing(CompletionItem::label));
        if (items.size() > MAXIMUM_COMPLETION_ITEMS) {
            items = new ArrayList<>(items.subList(0, MAXIMUM_COMPLETION_ITEMS));
        }

        return completionResult(
            replacementStart,
            replacementEnd - replacementStart,
            items);
    }

    private static ObjectNode completionResult(
        int replacementStart,
        int replacementLength,
        List<CompletionItem> items) {
        ObjectNode result = ProtocolJson.object();
        result.put("replacementStart", replacementStart);
        result.put("replacementLength", replacementLength);
        ArrayNode resultItems = result.putArray("items");
        for (CompletionItem item : items) {
            ObjectNode itemNode = resultItems.addObject();
            itemNode.put("label", item.label());
            itemNode.put("kind", item.kind());
            if (item.detail() != null) {
                itemNode.put("detail", item.detail());
            }
            itemNode.put("insertText", item.insertText());
        }
        return result;
    }

    private void addExpressionColumnCompletion(
        Map<String, CompletionItem> items,
        CatalogSqlResolver.ExpressionColumn candidate,
        String replacementPrefix,
        boolean qualify) {
        CatalogColumn column = candidate.column();
        var moniker = new SqlMonikerImpl(List.of(column.name()), SqlMonikerType.COLUMN);
        String replacement = advisor.getReplacement(moniker, replacementPrefix);
        String insertText = qualify
            ? candidate.qualifier() + "." + replacement
            : replacement;
        String label = qualify
            ? candidate.qualifier() + "." + column.name()
            : candidate.label();
        addCompletion(items, new CompletionItem(
            label,
            "column",
            column.dataTypeName(),
            insertText));
    }

    private void addPreferredExpressionCompletions(
        Map<String, CompletionItem> items,
        CatalogObject preferredObject,
        String prefix) {
        for (CatalogColumn column : preferredObject.columns()) {
            if (!matchesPrefix(column.name(), prefix, profile.columnCaseSensitive())) {
                continue;
            }
            addCompletion(items, preferredColumn(column, prefix));
        }

        if (prefix.isEmpty()
            || !matchesPrefix(
                preferredObject.id().name(),
                prefix,
                profile.objectCaseSensitive())) {
            return;
        }

        String qualifier = advisor.getReplacement(
            new SqlMonikerImpl(
                List.of(preferredObject.id().name()),
                SqlMonikerType.TABLE),
            prefix);
        for (CatalogColumn column : preferredObject.columns()) {
            CompletionItem member = preferredColumn(column, "");
            addCompletion(items, new CompletionItem(
                preferredObject.id().name() + "." + column.name(),
                "column",
                column.dataTypeName(),
                qualifier + "." + member.insertText()));
        }
    }

    private void addPreferredMemberCompletions(
        Map<String, CompletionItem> items,
        CatalogObject preferredObject,
        String prefix) {
        for (CatalogColumn column : preferredObject.columns()) {
            if (matchesPrefix(column.name(), prefix, profile.columnCaseSensitive())) {
                addCompletion(items, preferredColumn(column, prefix));
            }
        }
    }

    private CompletionItem preferredColumn(CatalogColumn column, String prefix) {
        var moniker = new SqlMonikerImpl(List.of(column.name()), SqlMonikerType.COLUMN);
        return new CompletionItem(
            column.name(),
            "column",
            column.dataTypeName(),
            advisor.getReplacement(moniker, prefix));
    }

    private CompletionItem keywordCompletion(String keyword, String prefix) {
        var moniker = new SqlMonikerImpl(List.of(keyword), SqlMonikerType.KEYWORD);
        return new CompletionItem(
            keyword,
            "keyword",
            null,
            advisor.getReplacement(moniker, prefix));
    }

    ObjectNode diagnose(String sql) {
        ObjectNode result = ProtocolJson.object();
        ArrayNode items = result.putArray("items");
        if (sql.isBlank()) {
            return result;
        }

        CatalogSqlResolver.Resolution catalogResolution = catalogResolver.resolve(sql);
        if (!catalogResolution.allRelationsResolved()) {
            return result;
        }

        var emitted = new HashSet<String>();
        String validationSql = SqlValidationPolicy.shadowBindVariables(sql);
        try {
            SqlNode statement = SqlParser.create(validationSql, profile.parserConfig()).parseStmt();
            if (profile.canUseCalciteColumnDiagnostics()
                && SqlValidationPolicy.supportsCatalogDiagnostics(statement)) {
                appendCalciteCatalogDiagnostics(sql, validationSql, items, emitted);
            }
        } catch (Exception unsupportedOrInvalidSyntax) {
            // Calcite is not the provider's parser. Painting dialect syntax red
            // is worse than omitting a syntax diagnostic that the server will
            // authoritatively report when the user runs the statement.
            // Alias-qualified membership can still be proven from the detached
            // catalog below without accepting or rejecting provider grammar.
        }

        for (CatalogSqlResolver.UnknownColumn unknown
            : catalogResolver.unknownQualifiedColumns(catalogResolution)) {
            String key = diagnosticKey(unknown.start(), unknown.length(), "unknownColumn");
            if (!emitted.add(key)) {
                continue;
            }
            ObjectNode item = items.addObject();
            item.put("start", unknown.start());
            item.put("length", unknown.length());
            item.put("severity", "error");
            item.put(
                "message",
                "Column '" + unknown.name() + "' not found in table '"
                    + unknown.qualifier() + "'");
            item.put("code", "unknownColumn");
        }
        return result;
    }

    private void appendCalciteCatalogDiagnostics(
        String sql,
        String validationSql,
        ArrayNode items,
        Set<String> emitted) {
        List<ValidateErrorInfo> errors;
        try {
            errors = advisor.validate(validationSql);
        } catch (RuntimeException unsupportedValidation) {
            return;
        }
        if (errors == null) {
            return;
        }
        for (ValidateErrorInfo error : errors) {
            SqlValidationPolicy.CatalogIssue issue =
                SqlValidationPolicy.catalogIssue(error, profile);
            if (issue == null) {
                continue;
            }
            int start = TextOffsets.fromLineColumn(
                sql,
                error.getStartLineNum(),
                error.getStartColumnNum());
            int length = TextOffsets.inclusiveRangeLength(
                sql,
                error.getStartLineNum(),
                error.getStartColumnNum(),
                error.getEndLineNum(),
                error.getEndColumnNum());
            if (!emitted.add(diagnosticKey(start, length, issue.code()))) {
                continue;
            }
            ObjectNode item = items.addObject();
            item.put("start", start);
            item.put("length", length);
            item.put("severity", "error");
            item.put("message", issue.message());
            item.put("code", issue.code());
        }
    }

    private CompletionItem toCompletionItem(SqlMoniker hint, String replaced) {
        List<String> names = hint.getFullyQualifiedNames();
        String label = names.isEmpty() ? hint.toString() : names.getLast();
        String kind = completionKind(hint, names);
        String detail = completionDetail(kind, names, label);
        String insertText = advisor.getReplacement(hint, replaced);
        return new CompletionItem(label, kind, detail, insertText);
    }

    private static void addCompletion(
        Map<String, CompletionItem> items,
        CompletionItem item) {
        String key = item.kind() + '\u0000' + item.insertText() + '\u0000' + item.detail();
        items.putIfAbsent(key, item);
    }

    private String completionKind(SqlMoniker hint, List<String> names) {
        if (hint.getType() == SqlMonikerType.TABLE) {
            return objectKinds.getOrDefault(pathKey(names, profile), "table");
        }
        return hint.getType().name().toLowerCase(Locale.ROOT);
    }

    private String completionDetail(String kind, List<String> names, String label) {
        if ("column".equals(kind)) {
            return unambiguousColumnTypes.get(profile.normalizeColumnIdentifier(label));
        }
        if (names.size() > 1) {
            return String.join(".", names.subList(0, names.size() - 1));
        }
        return null;
    }

    private static int findReplacementEnd(String sql, int cursorOffset, String prefix) {
        int end = cursorOffset;
        while (end < sql.length() && Character.isJavaIdentifierPart(sql.charAt(end))) {
            end++;
        }
        if (!prefix.isEmpty() && isOpeningQuote(prefix.charAt(0)) && end < sql.length()) {
            char expected = prefix.charAt(0) == '[' ? ']' : prefix.charAt(0);
            if (sql.charAt(end) == expected) {
                end++;
            }
        }
        return end;
    }

    private static String identifierPrefix(String sql, int cursorOffset) {
        int start = cursorOffset;
        while (start > 0 && Character.isJavaIdentifierPart(sql.charAt(start - 1))) {
            start--;
        }
        return sql.substring(start, cursorOffset);
    }

    private static String diagnosticKey(int start, int length, String code) {
        return start + ":" + length + ":" + code;
    }

    private static boolean isOpeningQuote(char value) {
        return value == '"' || value == '`' || value == '[';
    }

    private CompletionItem functionCompletion(
        SqlFunctionCatalog.Candidate function,
        String prefix,
        boolean openingParenthesisFollows,
        boolean memberOnly) {
        String replacement;
        if (function.catalogOnly()) {
            var parts = new ArrayList<String>(function.nameParts().size());
            int firstPart = memberOnly ? function.nameParts().size() - 1 : 0;
            for (int index = firstPart; index < function.nameParts().size(); index++) {
                boolean functionName = index == function.nameParts().size() - 1;
                var moniker = new SqlMonikerImpl(
                    List.of(function.nameParts().get(index)),
                    functionName ? SqlMonikerType.FUNCTION : SqlMonikerType.SCHEMA);
                parts.add(advisor.getReplacement(
                    moniker,
                    functionName ? prefix : ""));
            }
            replacement = String.join(".", parts);
        } else {
            replacement = function.label();
        }
        if (function.insertion() == SqlFunctionCatalog.Insertion.CALL
            && !openingParenthesisFollows) {
            replacement += "(";
        }
        return new CompletionItem(
            function.label(),
            function.insertion() == SqlFunctionCatalog.Insertion.CALL
                ? "function"
                : "keyword",
            function.detail(),
            replacement);
    }

    private static boolean functionAllowedInContext(
        SqlFunctionCatalog.Candidate function,
        CatalogSqlResolver.CompletionContext context) {
        if (function.requiresOverOnly()) {
            return context == CatalogSqlResolver.CompletionContext.SELECT_LIST
                || context == CatalogSqlResolver.CompletionContext.ORDER_BY;
        }
        if (function.aggregateOnly()) {
            return context == CatalogSqlResolver.CompletionContext.SELECT_LIST
                || context == CatalogSqlResolver.CompletionContext.HAVING
                || context == CatalogSqlResolver.CompletionContext.ORDER_BY;
        }
        return isExpressionContext(context);
    }

    private static boolean openingParenthesisFollows(String sql, int replacementEnd) {
        int index = replacementEnd;
        while (index < sql.length() && Character.isWhitespace(sql.charAt(index))) {
            index++;
        }
        return index < sql.length() && sql.charAt(index) == '(';
    }

    private static boolean matchesPrefix(
        String candidate,
        String prefix,
        boolean quotedCaseSensitive) {
        if (prefix.isEmpty()) {
            return true;
        }
        boolean quoted = isOpeningQuote(prefix.charAt(0));
        String value = quoted ? prefix.substring(1) : prefix;
        if (!value.isEmpty() && isClosingQuote(value.charAt(value.length() - 1))) {
            value = value.substring(0, value.length() - 1);
        }
        return quoted && quotedCaseSensitive
            ? candidate.startsWith(value)
            : candidate.regionMatches(true, 0, value, 0, value.length());
    }

    private static boolean isClosingQuote(char value) {
        return value == '"' || value == '`' || value == ']';
    }

    private static boolean isRelationOnlyKind(String kind) {
        return kind.equals("table")
            || kind.equals("view")
            || kind.equals("materializedView")
            || kind.equals("schema")
            || kind.equals("catalog");
    }

    private static boolean shouldIncludeAdvisorItem(
        CompletionItem item,
        CatalogSqlResolver.CompletionContext context,
        String prefix,
        boolean completedSelectProjection) {
        if (item.kind().equals("function")) {
            return false;
        }
        if (context == CatalogSqlResolver.CompletionContext.QUALIFIED_MEMBER) {
            return item.kind().equals("column") || item.label().equals("*");
        }
        if (context == CatalogSqlResolver.CompletionContext.TYPE) {
            return item.kind().equals("dataType");
        }
        if (!isExpressionContext(context)) {
            return true;
        }
        if (isRelationOnlyKind(item.kind())) {
            return false;
        }
        if (prefix.isEmpty()) {
            return true;
        }
        return item.kind().equals("column")
            || item.kind().equals("keyword")
                && (PORTABLE_EXPRESSION_KEYWORDS.contains(
                        item.label().toUpperCase(Locale.ROOT))
                    || context == CatalogSqlResolver.CompletionContext.SELECT_LIST
                        && completedSelectProjection
                        && item.label().equalsIgnoreCase("FROM"));
    }

    private static boolean isExpressionContext(
        CatalogSqlResolver.CompletionContext context) {
        return context == CatalogSqlResolver.CompletionContext.EXPRESSION
            || context == CatalogSqlResolver.CompletionContext.SELECT_LIST
            || context == CatalogSqlResolver.CompletionContext.PREDICATE
            || context == CatalogSqlResolver.CompletionContext.HAVING
            || context == CatalogSqlResolver.CompletionContext.GROUP_BY
            || context == CatalogSqlResolver.CompletionContext.ORDER_BY;
    }

    private static boolean isStatementStartCompletion(String sql, int replacementStart) {
        return sql.substring(0, replacementStart).isBlank();
    }

    private static boolean shouldSupplementRelationCompletions(
        CatalogSqlResolver.CompletionContext context,
        Map<String, CompletionItem> items) {
        if (context != CatalogSqlResolver.CompletionContext.RELATION
            || items.values().stream().anyMatch(item -> isRelationOnlyKind(item.kind()))) {
            return false;
        }
        return items.isEmpty() || items.values().stream().anyMatch(item ->
            item.label().equals("TABLE")
                || item.label().equals("LATERAL")
                || item.label().equals("UNNEST"));
    }

    private static List<String> parentPath(
        CatalogSnapshot snapshot,
        CatalogObjectId id,
        SqlDialectProfile profile) {
        var path = new ArrayList<String>(2);
        String catalog = id.catalog();
        if (catalog != null && !sameIdentifier(catalog, snapshot.defaultCatalog(), profile)) {
            path.add(catalog);
        }
        if (id.schema() != null) {
            path.add(id.schema());
        }
        return path;
    }

    private static CalciteSchema findOrAddSchema(
        CalciteSchema root,
        List<String> path,
        SqlDialectProfile profile) {
        CalciteSchema current = root;
        for (String name : path) {
            CalciteSchema child = current.getSubSchema(name, profile.objectCaseSensitive());
            current = child == null ? current.add(name, new AbstractSchema()) : child;
        }
        return current;
    }

    private static List<String> defaultSchemaPath(CatalogSnapshot snapshot) {
        return snapshot.defaultSchema() == null
            ? List.of()
            : List.of(snapshot.defaultSchema());
    }

    private static boolean sameIdentifier(
        String first,
        String second,
        SqlDialectProfile profile) {
        if (second == null) {
            return false;
        }
        return profile.objectCaseSensitive()
            ? first.equals(second)
            : first.equalsIgnoreCase(second);
    }

    private static void collectColumnTypes(
        Map<String, Set<String>> columnTypes,
        CatalogObject object,
        SqlDialectProfile profile) {
        for (CatalogColumn column : object.columns()) {
            String name = profile.normalizeColumnIdentifier(column.name());
            columnTypes.computeIfAbsent(name, ignored -> new HashSet<>())
                .add(column.dataTypeName());
        }
    }

    private static void validateColumns(
        CatalogObject object,
        SqlDialectProfile profile) throws ProtocolException {
        var names = new HashSet<String>();
        for (CatalogColumn column : object.columns()) {
            String normalized = profile.normalizeColumnIdentifier(column.name());
            if (!names.add(normalized)) {
                throw new ProtocolException(
                    "invalidParams",
                    "Catalog object '" + qualifiedName(object.id())
                        + "' contains duplicate column '" + column.name() + "'.");
            }
        }
    }

    private static List<CompletionItem> createRootCompletions(
        CatalogSnapshot snapshot,
        SqlAdvisor advisor) {
        var result = new ArrayList<CompletionItem>();
        var schemaNames = new HashSet<String>();
        if (snapshot.defaultCatalog() != null) {
            addRootMoniker(
                result,
                advisor,
                new SqlMonikerImpl(List.of(snapshot.defaultCatalog()), SqlMonikerType.CATALOG),
                "catalog");
        }
        for (CatalogObject object : snapshot.objects()) {
            List<String> names = qualifiedParts(object.id());
            String kind = normalizeKind(object.kind());
            addRootMoniker(result, advisor, new SqlMonikerImpl(names, SqlMonikerType.TABLE), kind);
            if (object.id().schema() != null && schemaNames.add(object.id().schema())) {
                addRootMoniker(
                    result,
                    advisor,
                    new SqlMonikerImpl(List.of(object.id().schema()), SqlMonikerType.SCHEMA),
                    "schema");
            }
        }
        for (String keyword : advisor.getReservedAndKeyWords()) {
            if (!keyword.matches("[A-Za-z_]+")) {
                continue;
            }
            addRootMoniker(
                result,
                advisor,
                new SqlMonikerImpl(List.of(keyword), SqlMonikerType.KEYWORD),
                "keyword");
        }
        return List.copyOf(result);
    }

    private static List<CompletionItem> createTypeCompletions(CatalogSnapshot snapshot) {
        var names = new LinkedHashMap<String, String>();
        for (SqlTypeName type : SqlTypeName.ALL_TYPES) {
            if (type.isSpecial() || type.getFamily() == null) {
                continue;
            }
            addTypeName(names, type.getSpaceName());
        }
        for (CatalogObject object : snapshot.objects()) {
            for (CatalogColumn column : object.columns()) {
                addTypeName(names, column.dataTypeName());
            }
        }
        for (CatalogRoutine routine : snapshot.routines()) {
            addTypeName(names, routine.returnTypeName());
            for (CatalogRoutineParameter parameter : routine.parameters()) {
                addTypeName(names, parameter.dataTypeName());
            }
        }
        return names.values().stream()
            .map(name -> new CompletionItem(name, "dataType", "type", name))
            .toList();
    }

    private static void addTypeName(Map<String, String> names, String rawName) {
        if (rawName == null) {
            return;
        }
        String name = rawName.strip().replaceAll("\\s+", " ");
        if (!name.matches("[A-Za-z_][A-Za-z0-9_ ]*")) {
            return;
        }
        names.putIfAbsent(name.toUpperCase(Locale.ROOT), name);
        int space = name.indexOf(' ');
        if (space > 0) {
            String root = name.substring(0, space);
            names.putIfAbsent(root.toUpperCase(Locale.ROOT), root);
        }
    }

    private static void addRootMoniker(
        List<CompletionItem> result,
        SqlAdvisor advisor,
        SqlMoniker moniker,
        String kind) {
        List<String> names = moniker.getFullyQualifiedNames();
        String label = names.getLast();
        String detail = names.size() > 1
            ? String.join(".", names.subList(0, names.size() - 1))
            : null;
        result.add(new CompletionItem(label, kind, detail, advisor.getReplacement(moniker, "")));
    }

    private static Map<String, String> collapseColumnTypes(Map<String, Set<String>> columnTypes) {
        var result = new HashMap<String, String>();
        for (var entry : columnTypes.entrySet()) {
            if (entry.getValue().size() == 1) {
                result.put(entry.getKey(), entry.getValue().iterator().next());
            }
        }
        return Map.copyOf(result);
    }

    private static String pathKey(List<String> path, SqlDialectProfile profile) {
        return path.stream()
            .map(profile::normalizeObjectIdentifier)
            .reduce((left, right) -> left + '\u0000' + right)
            .orElse("");
    }

    private static String qualifiedName(CatalogObjectId id) {
        return String.join(".", qualifiedParts(id));
    }

    private static List<String> qualifiedParts(CatalogObjectId id) {
        var parts = new ArrayList<String>(3);
        if (id.catalog() != null) {
            parts.add(id.catalog());
        }
        if (id.schema() != null) {
            parts.add(id.schema());
        }
        parts.add(id.name());
        return List.copyOf(parts);
    }

    private static String normalizeKind(String kind) {
        String normalized = kind.toLowerCase(Locale.ROOT).replace("_", "").replace(" ", "");
        return switch (normalized) {
            case "view" -> "view";
            case "materializedview" -> "materializedView";
            default -> "table";
        };
    }

    private static int completionPriority(String kind) {
        return switch (kind) {
            case "column" -> 0;
            case "table", "view", "materializedView" -> 1;
            case "schema", "catalog" -> 2;
            case "dataType", "function", "procedure" -> 3;
            case "keyword" -> 4;
            default -> 5;
        };
    }

    private record CompletionItem(String label, String kind, String detail, String insertText) {
    }

    private static final class CatalogTable extends AbstractTable {
        private final CatalogObject object;

        private CatalogTable(CatalogObject object) {
            this.object = object;
        }

        @Override
        public RelDataType getRowType(RelDataTypeFactory factory) {
            RelDataTypeFactory.Builder builder = factory.builder();
            for (CatalogColumn column : object.columns()) {
                builder.add(column.name(), SqlTypeMapping.create(factory, column))
                    .nullable(column.isNullable() == null || column.isNullable());
            }
            return builder.build();
        }

        @Override
        public Schema.TableType getJdbcTableType() {
            return normalizeKind(object.kind()).equals("table")
                ? Schema.TableType.TABLE
                : Schema.TableType.VIEW;
        }
    }
}
