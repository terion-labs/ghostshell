package sh.asura.sql;

import org.apache.calcite.avatica.util.Quoting;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/** Resolves simple FROM/JOIN aliases without requiring provider grammar to parse. */
final class CatalogSqlResolver {
    private static final Set<String> FROM_TERMINATORS = Set.of(
        "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "ROWS",
        "SETTINGS", "FORMAT", "QUALIFY", "WINDOW", "UNION", "EXCEPT", "INTERSECT");
    private static final Set<String> ALIAS_BOUNDARIES = Set.of(
        "AS", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER",
        "ON", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "ROWS",
        "SETTINGS", "FORMAT", "QUALIFY", "WINDOW", "UNION", "EXCEPT", "INTERSECT",
        "FINAL", "SAMPLE", "PREWHERE", "USE", "FORCE", "IGNORE", "WITH");
    private static final Set<String> SET_OPERATORS = Set.of(
        "UNION", "EXCEPT", "INTERSECT");
    private static final Set<String> NON_EXPRESSION_STATEMENT_STARTS = Set.of(
        "ALTER", "CALL", "CREATE", "DELETE", "DROP", "GRANT", "INSERT", "MERGE",
        "REVOKE", "TRUNCATE", "UPDATE", "WITH");

    private final CatalogSnapshot snapshot;
    private final SqlDialectProfile profile;

    CatalogSqlResolver(CatalogSnapshot snapshot, SqlDialectProfile profile) {
        this.snapshot = snapshot;
        this.profile = profile;
    }

    Resolution resolve(String sql) {
        List<Token> tokens = tokenize(sql);
        var relations = new ArrayList<Relation>();
        boolean inFrom = false;
        boolean expectRelation = false;
        boolean unsafe = false;
        boolean hasAnyRelationReference = false;
        int relationCount = 0;
        int topLevelSelectCount = 0;

        for (int index = 0; index < tokens.size(); index++) {
            Token token = tokens.get(index);
            if (token.isKeyword("FROM") || token.isKeyword("JOIN")) {
                hasAnyRelationReference = true;
            }
            if (token.depth() == 0 && token.isKeyword("SELECT")) {
                topLevelSelectCount++;
            }
            if (token.depth() == 0
                && (token.isKeywordIn(SET_OPERATORS) || token.isOther(";"))) {
                unsafe = true;
            }
            if (token.depth() > 0 && token.isKeyword("FROM")) {
                unsafe = true;
            }
            if (token.depth() != 0) {
                continue;
            }
            if (token.isKeyword("FROM")) {
                inFrom = true;
                expectRelation = true;
                continue;
            }
            if (!inFrom) {
                continue;
            }
            if (token.isKeywordIn(FROM_TERMINATORS)) {
                inFrom = false;
                expectRelation = false;
                continue;
            }
            if (token.isKeyword("JOIN")) {
                expectRelation = true;
                continue;
            }
            if (token.kind() == TokenKind.COMMA) {
                expectRelation = true;
                continue;
            }
            if (!expectRelation) {
                continue;
            }
            if (token.isKeyword("LATERAL") || token.isKeyword("ONLY")) {
                continue;
            }

            RelationParse parsed = parseRelation(tokens, index);
            relationCount++;
            expectRelation = false;
            if (parsed == null) {
                unsafe = true;
                continue;
            }
            index = parsed.lastTokenIndex();
            CatalogObject object = matchObject(parsed.path());
            if (object == null) {
                unsafe = true;
                continue;
            }
            Relation relation = new Relation(parsed.qualifier(), object);
            if (findRelation(relations, relation.qualifier()) != null) {
                unsafe = true;
                continue;
            }
            relations.add(relation);
        }

        boolean singleSelectScope = startsWithSelect(tokens) && topLevelSelectCount == 1;
        boolean expressionFragment = startsWithWhere(tokens)
            || startsWithExpression(tokens);
        return new Resolution(
            sql,
            List.copyOf(tokens),
            List.copyOf(relations),
            singleSelectScope
                && relationCount > 0
                && !unsafe
                && relations.size() == relationCount,
            hasAnyRelationReference,
            !hasAnyRelationReference
                && !unsafe
                && topLevelSelectCount <= 1
                && (singleSelectScope || expressionFragment));
    }

    List<CatalogColumn> columnsForQualifier(Resolution resolution, int cursorOffset) {
        if (!resolution.allRelationsResolved()) {
            return List.of();
        }
        Token qualifier = completionQualifier(resolution, cursorOffset);
        if (qualifier == null) {
            return List.of();
        }
        Relation relation = findRelation(resolution.relations(), qualifier);
        return relation == null ? List.of() : relation.object().columns();
    }

    boolean qualifierMatchesObject(
        Resolution resolution,
        int cursorOffset,
        CatalogObject object) {
        Token qualifier = completionQualifier(resolution, cursorOffset);
        return qualifier != null && profile.matchesObjectIdentifier(
            qualifier.text(),
            qualifier.quoted(),
            object.id().name());
    }

    boolean completionQualifierMatches(
        Resolution resolution,
        int cursorOffset,
        String expected) {
        return completionQualifierPathMatches(
            resolution,
            cursorOffset,
            List.of(expected));
    }

    boolean completionQualifierPathMatches(
        Resolution resolution,
        int cursorOffset,
        List<String> expected) {
        List<Token> qualifierPath = completionQualifierPath(resolution, cursorOffset);
        if (qualifierPath.size() != expected.size()) {
            return false;
        }
        for (int index = 0; index < expected.size(); index++) {
            Token actual = qualifierPath.get(index);
            if (!profile.matchesObjectIdentifier(
                actual.text(),
                actual.quoted(),
                expected.get(index))) {
                return false;
            }
        }
        return true;
    }

    CatalogObject preferredObject(CatalogObjectId requested) {
        List<CatalogObject> exact = snapshot.objects().stream()
            .filter(object -> exactPreferredIdentityMatch(requested, object.id()))
            .toList();
        if (exact.size() == 1) {
            return exact.getFirst();
        }
        if (exact.size() > 1) {
            return null;
        }

        List<CatalogObject> compatible = snapshot.objects().stream()
            .filter(object -> compatiblePreferredIdentityMatch(requested, object.id()))
            .toList();
        return compatible.size() == 1 ? compatible.getFirst() : null;
    }

    CompletionContext completionContext(Resolution resolution, int cursorOffset) {
        int cursorDepth = cursorDepth(resolution.tokens(), cursorOffset);
        CompletionContext[] contexts = new CompletionContext[cursorDepth + 1];
        boolean[] relationLists = new boolean[cursorDepth + 1];
        int[] waitingForBy = new int[cursorDepth + 1];
        contexts[0] = cursorDepth == 0
            ? CompletionContext.UNKNOWN
            : CompletionContext.EXPRESSION;
        for (Token token : resolution.tokens()) {
            if (token.start() >= cursorOffset || token.depth() > cursorDepth) {
                continue;
            }
            int depth = token.depth();
            for (int nested = depth + 1; nested <= cursorDepth; nested++) {
                contexts[nested] = null;
                relationLists[nested] = false;
                waitingForBy[nested] = 0;
            }
            if (contexts[depth] == null) {
                contexts[depth] = inheritedContext(contexts, depth);
            }
            if (token.isKeyword("SELECT")) {
                contexts[depth] = CompletionContext.SELECT_LIST;
                relationLists[depth] = false;
                waitingForBy[depth] = 0;
            } else if (token.isKeyword("FROM") || token.isKeyword("JOIN")) {
                contexts[depth] = CompletionContext.RELATION;
                relationLists[depth] = true;
                waitingForBy[depth] = 0;
            } else if (token.isKeyword("ON")
                || token.isKeyword("WHERE")
                || token.isKeyword("QUALIFY")
                || token.isKeyword("PREWHERE")) {
                contexts[depth] = CompletionContext.PREDICATE;
                relationLists[depth] = false;
                waitingForBy[depth] = 0;
            } else if (token.isKeyword("HAVING")) {
                contexts[depth] = CompletionContext.HAVING;
                relationLists[depth] = false;
                waitingForBy[depth] = 0;
            } else if (token.isKeyword("GROUP")) {
                contexts[depth] = CompletionContext.UNKNOWN;
                relationLists[depth] = false;
                waitingForBy[depth] = 1;
            } else if (token.isKeyword("ORDER")) {
                contexts[depth] = CompletionContext.UNKNOWN;
                relationLists[depth] = false;
                waitingForBy[depth] = 2;
            } else if (waitingForBy[depth] != 0 && token.isKeyword("BY")) {
                contexts[depth] = waitingForBy[depth] == 1
                    ? CompletionContext.GROUP_BY
                    : CompletionContext.ORDER_BY;
                waitingForBy[depth] = 0;
            } else if (token.isKeywordIn(FROM_TERMINATORS)) {
                contexts[depth] = CompletionContext.UNKNOWN;
                relationLists[depth] = false;
                waitingForBy[depth] = 0;
            } else if (token.kind() == TokenKind.COMMA && relationLists[depth]) {
                contexts[depth] = CompletionContext.RELATION;
            }
        }
        CompletionContext context = contexts[cursorDepth] == null
            ? inheritedContext(contexts, cursorDepth)
            : contexts[cursorDepth];
        if (isTypeCompletion(resolution.tokens(), cursorOffset, cursorDepth)) {
            return CompletionContext.TYPE;
        }
        return isExpressionContext(context)
            && completionQualifier(resolution, cursorOffset) != null
            ? CompletionContext.QUALIFIED_MEMBER
            : context;
    }

    boolean isNonCodeCompletionPosition(Resolution resolution, int cursorOffset) {
        for (Token token : resolution.tokens()) {
            if (token.start() >= cursorOffset || cursorOffset > token.end()) {
                continue;
            }
            if (token.kind() == TokenKind.COMMENT) {
                return cursorOffset < token.end()
                    || !isClosedBlockComment(resolution.sql(), token);
            }
            if (token.kind() == TokenKind.LITERAL) {
                return cursorOffset < token.end()
                    || !isClosedLiteral(resolution.sql(), token);
            }
        }
        return false;
    }

    boolean isQuotedIdentifierCompletion(Resolution resolution, int cursorOffset) {
        return resolution.tokens().stream().anyMatch(token ->
            token.quoted()
                && token.start() < cursorOffset
                && (cursorOffset < token.end()
                    || cursorOffset == token.end()
                        && !isClosedQuotedIdentifier(resolution.sql(), token)));
    }

    boolean hasCompletedSelectProjection(Resolution resolution, int replacementStart) {
        int cursorDepth = cursorDepth(resolution.tokens(), replacementStart);
        boolean inSelectList = false;
        boolean hasProjection = false;
        for (Token token : resolution.tokens()) {
            if (token.start() >= replacementStart || token.depth() != cursorDepth) {
                continue;
            }
            if (token.isKeyword("SELECT")) {
                inSelectList = true;
                hasProjection = false;
                continue;
            }
            if (!inSelectList) {
                continue;
            }
            if (token.isKeyword("FROM") || token.isKeywordIn(FROM_TERMINATORS)) {
                return false;
            }
            if (isSelectProjectionToken(token)) {
                hasProjection = true;
            }
        }
        return inSelectList && hasProjection;
    }

    List<ExpressionColumn> columnsForExpression(Resolution resolution) {
        if (!resolution.allRelationsResolved() || resolution.relations().isEmpty()) {
            return List.of();
        }

        boolean qualify = resolution.relations().size() > 1;
        var columns = new ArrayList<ExpressionColumn>();
        for (Relation relation : resolution.relations()) {
            String qualifier = sourceText(resolution.sql(), relation.qualifier());
            String qualifierName = relation.qualifier().text();
            for (CatalogColumn column : relation.object().columns()) {
                String label = !qualify
                    ? column.name()
                    : qualifier + "." + column.name();
                columns.add(new ExpressionColumn(
                    label,
                    qualifier,
                    qualifierName,
                    qualify,
                    column));
            }
        }
        return List.copyOf(columns);
    }

    List<UnknownColumn> unknownQualifiedColumns(Resolution resolution) {
        if (!resolution.allRelationsResolved()) {
            return List.of();
        }

        var unknown = new ArrayList<UnknownColumn>();
        var seenStarts = new HashSet<Integer>();
        List<Token> tokens = resolution.tokens();
        for (int index = 0; index + 2 < tokens.size(); index++) {
            Token qualifier = tokens.get(index);
            Token dot = tokens.get(index + 1);
            Token column = tokens.get(index + 2);
            if (!qualifier.isIdentifier()
                || dot.kind() != TokenKind.DOT
                || !column.isIdentifier()) {
                continue;
            }
            Relation relation = findRelation(resolution.relations(), qualifier);
            if (relation == null || containsColumn(relation.object(), column)) {
                continue;
            }
            if (SqlValidationPolicy.isProviderPseudoColumn(profile.driverId(), column.text())) {
                continue;
            }
            if (seenStarts.add(column.start())) {
                unknown.add(new UnknownColumn(
                    column.start(),
                    column.length(),
                    column.text(),
                    qualifier.text()));
            }
        }
        return List.copyOf(unknown);
    }

    private RelationParse parseRelation(List<Token> tokens, int start) {
        if (start >= tokens.size() || !tokens.get(start).isIdentifier()) {
            return null;
        }
        int depth = tokens.get(start).depth();
        var path = new ArrayList<Token>();
        path.add(tokens.get(start));
        int index = start + 1;
        while (index + 1 < tokens.size()
            && tokens.get(index).depth() == depth
            && tokens.get(index).kind() == TokenKind.DOT
            && tokens.get(index + 1).depth() == depth
            && tokens.get(index + 1).isIdentifier()) {
            path.add(tokens.get(index + 1));
            index += 2;
        }
        if (index < tokens.size()
            && tokens.get(index).depth() == depth
            && tokens.get(index).kind() == TokenKind.LEFT_PARENTHESIS) {
            return null;
        }

        Token qualifier = path.getLast();
        int lastTokenIndex = index - 1;
        if (index < tokens.size()
            && tokens.get(index).depth() == depth
            && tokens.get(index).isKeyword("AS")) {
            if (index + 1 >= tokens.size() || !tokens.get(index + 1).isIdentifier()) {
                return null;
            }
            qualifier = tokens.get(index + 1);
            lastTokenIndex = index + 1;
        } else if (index < tokens.size()
            && tokens.get(index).depth() == depth
            && tokens.get(index).isIdentifier()
            && !tokens.get(index).isKeywordIn(ALIAS_BOUNDARIES)) {
            qualifier = tokens.get(index);
            lastTokenIndex = index;
        }
        return new RelationParse(List.copyOf(path), qualifier, lastTokenIndex);
    }

    private CatalogObject matchObject(List<Token> referencePath) {
        var candidates = new ArrayList<CatalogObject>();
        for (CatalogObject object : snapshot.objects()) {
            List<String> actualPath = objectPath(object.id());
            if (referencePath.size() > actualPath.size()) {
                continue;
            }
            int offset = actualPath.size() - referencePath.size();
            boolean matches = true;
            for (int index = 0; index < referencePath.size(); index++) {
                Token reference = referencePath.get(index);
                if (!profile.matchesObjectIdentifier(
                    reference.text(),
                    reference.quoted(),
                    actualPath.get(offset + index))) {
                    matches = false;
                    break;
                }
            }
            if (matches) {
                candidates.add(object);
            }
        }
        if (candidates.size() == 1) {
            return candidates.getFirst();
        }
        if (candidates.isEmpty()) {
            return null;
        }

        List<CatalogObject> preferred = candidates.stream()
            .filter(object -> belongsToDefaultPath(object, referencePath.size()))
            .toList();
        return preferred.size() == 1 ? preferred.getFirst() : null;
    }

    private boolean belongsToDefaultPath(CatalogObject object, int referencePartCount) {
        int omittedPartCount = objectPath(object.id()).size() - referencePartCount;
        if (omittedPartCount <= 0) {
            return true;
        }

        if (object.id().catalog() != null) {
            if (!matchesDefaultPathPart(snapshot.defaultCatalog(), object.id().catalog())) {
                return false;
            }
            omittedPartCount--;
        }
        if (omittedPartCount > 0 && object.id().schema() != null) {
            if (!matchesDefaultPathPart(snapshot.defaultSchema(), object.id().schema())) {
                return false;
            }
            omittedPartCount--;
        }
        return omittedPartCount == 0;
    }

    private boolean exactPreferredIdentityMatch(
        CatalogObjectId requested,
        CatalogObjectId actual) {
        return requested.name().equals(actual.name())
            && exactPreferredPathPart(requested.catalog(), actual.catalog(), snapshot.defaultCatalog())
            && exactPreferredPathPart(requested.schema(), actual.schema(), snapshot.defaultSchema());
    }

    private boolean compatiblePreferredIdentityMatch(
        CatalogObjectId requested,
        CatalogObjectId actual) {
        return profile.matchesObjectIdentifier(requested.name(), false, actual.name())
            && compatiblePreferredPathPart(
                requested.catalog(), actual.catalog(), snapshot.defaultCatalog())
            && compatiblePreferredPathPart(
                requested.schema(), actual.schema(), snapshot.defaultSchema());
    }

    private static boolean exactPreferredPathPart(
        String requested,
        String actual,
        String defaultValue) {
        if (requested != null) {
            return requested.equals(actual);
        }
        return actual == null || actual.equals(defaultValue);
    }

    private boolean compatiblePreferredPathPart(
        String requested,
        String actual,
        String defaultValue) {
        if (requested != null) {
            return actual != null
                && profile.matchesObjectIdentifier(requested, false, actual);
        }
        return actual == null
            || defaultValue != null
                && profile.matchesObjectIdentifier(defaultValue, false, actual);
    }

    private boolean matchesDefaultPathPart(String expected, String actual) {
        return expected != null
            && actual != null
            && profile.matchesObjectIdentifier(expected, true, actual);
    }

    private boolean containsColumn(CatalogObject object, Token reference) {
        return object.columns().stream().anyMatch(column -> profile.matchesColumnIdentifier(
            reference.text(),
            reference.quoted(),
            column.name()));
    }

    private Relation findRelation(List<Relation> relations, Token qualifier) {
        Relation match = null;
        for (Relation relation : relations) {
            if (!profile.equivalentReferences(
                relation.qualifier().text(),
                relation.qualifier().quoted(),
                qualifier.text(),
                qualifier.quoted())) {
                continue;
            }
            if (match != null) {
                return null;
            }
            match = relation;
        }
        return match;
    }

    private static Token completionQualifier(Resolution resolution, int cursorOffset) {
        List<Token> path = completionQualifierPath(resolution, cursorOffset);
        return path.isEmpty() ? null : path.getLast();
    }

    private static List<Token> completionQualifierPath(
        Resolution resolution,
        int cursorOffset) {
        List<Token> tokens = resolution.tokens();
        int candidateDotIndex = -1;
        for (int index = 1; index < tokens.size(); index++) {
            Token dot = tokens.get(index);
            if (dot.kind() != TokenKind.DOT || dot.end() > cursorOffset) {
                continue;
            }
            Token previous = tokens.get(index - 1);
            if (!previous.isIdentifier()) {
                continue;
            }
            if (dot.end() == cursorOffset) {
                candidateDotIndex = index;
                continue;
            }
            if (index + 1 < tokens.size()) {
                Token after = tokens.get(index + 1);
                if (after.isIdentifier()
                    && after.start() >= dot.end()
                    && after.start() <= cursorOffset
                    && cursorOffset <= after.end()) {
                    candidateDotIndex = index;
                }
            }
        }
        if (candidateDotIndex < 1) {
            return List.of();
        }

        var reversed = new ArrayList<Token>();
        int identifierIndex = candidateDotIndex - 1;
        while (identifierIndex >= 0) {
            Token identifier = tokens.get(identifierIndex);
            if (!identifier.isIdentifier()) {
                break;
            }
            reversed.add(identifier);
            if (identifierIndex < 2
                || tokens.get(identifierIndex - 1).kind() != TokenKind.DOT) {
                break;
            }
            identifierIndex -= 2;
        }
        java.util.Collections.reverse(reversed);
        return List.copyOf(reversed);
    }

    private List<Token> tokenize(String sql) {
        var tokens = new ArrayList<Token>();
        int index = 0;
        int depth = 0;
        while (index < sql.length()) {
            char current = sql.charAt(index);
            if (Character.isWhitespace(current)) {
                index++;
                continue;
            }
            if (current == '-' && index + 1 < sql.length() && sql.charAt(index + 1) == '-') {
                int end = skipLineComment(sql, index + 2);
                tokens.add(new Token("", index, end - index, false, false,
                    TokenKind.COMMENT, depth));
                index = end;
                continue;
            }
            if (current == '/' && index + 1 < sql.length() && sql.charAt(index + 1) == '*') {
                int end = skipBlockComment(sql, index + 2);
                tokens.add(new Token("", index, end - index, false, false,
                    TokenKind.COMMENT, depth));
                index = end;
                continue;
            }
            if (current == '\'') {
                int end = skipQuoted(sql, index, '\'', '\'');
                tokens.add(new Token("", index, end - index, false, false,
                    TokenKind.LITERAL, depth));
                index = end;
                continue;
            }
            if (current == '$') {
                String delimiter = dollarQuoteDelimiter(sql, index);
                if (delimiter != null) {
                    int close = sql.indexOf(delimiter, index + delimiter.length());
                    int end = close < 0 ? sql.length() : close + delimiter.length();
                    tokens.add(new Token("", index, end - index, false, false,
                        TokenKind.LITERAL, depth));
                    index = end;
                    continue;
                }
            }
            if (current == '"'
                && (profile.quoting() == Quoting.DOUBLE_QUOTE
                    || profile.quoting() == Quoting.BRACKET)) {
                int end = skipQuoted(sql, index, '"', '"');
                tokens.add(Token.identifier(
                    unescapeQuoted(sql.substring(index + 1, Math.max(index + 1, end - 1)), '"'),
                    index,
                    end - index,
                    true,
                    depth));
                index = end;
                continue;
            }
            if (current == '`' && profile.quoting() == Quoting.BACK_TICK) {
                int end = skipQuoted(sql, index, '`', '`');
                tokens.add(Token.identifier(
                    unescapeQuoted(sql.substring(index + 1, Math.max(index + 1, end - 1)), '`'),
                    index,
                    end - index,
                    true,
                    depth));
                index = end;
                continue;
            }
            if (current == '[' && profile.quoting() == Quoting.BRACKET) {
                int end = skipQuoted(sql, index, ']', ']');
                tokens.add(Token.identifier(
                    unescapeQuoted(sql.substring(index + 1, Math.max(index + 1, end - 1)), ']'),
                    index,
                    end - index,
                    true,
                    depth));
                index = end;
                continue;
            }
            if (isIdentifierStart(current)) {
                int end = index + 1;
                while (end < sql.length() && isIdentifierPart(sql.charAt(end))) {
                    end++;
                }
                tokens.add(Token.identifier(sql.substring(index, end), index, end - index, false, depth));
                index = end;
                continue;
            }
            if (current == '(') {
                tokens.add(new Token("(", index, 1, false, false,
                    TokenKind.LEFT_PARENTHESIS, depth));
                depth++;
                index++;
                continue;
            }
            if (current == ')') {
                depth = Math.max(0, depth - 1);
                tokens.add(new Token(")", index, 1, false, false,
                    TokenKind.RIGHT_PARENTHESIS, depth));
                index++;
                continue;
            }
            TokenKind kind = switch (current) {
                case '.' -> TokenKind.DOT;
                case ',' -> TokenKind.COMMA;
                default -> TokenKind.OTHER;
            };
            tokens.add(new Token(Character.toString(current), index, 1, false, false, kind, depth));
            index++;
        }
        return List.copyOf(tokens);
    }

    private static int skipQuoted(String sql, int start, char closing, char escaped) {
        int index = start + 1;
        while (index < sql.length()) {
            if (sql.charAt(index) == '\\' && index + 1 < sql.length()) {
                index += 2;
                continue;
            }
            if (sql.charAt(index) == closing) {
                if (index + 1 < sql.length() && sql.charAt(index + 1) == escaped) {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        return sql.length();
    }

    private static int skipLineComment(String sql, int start) {
        int index = start;
        while (index < sql.length() && sql.charAt(index) != '\n' && sql.charAt(index) != '\r') {
            index++;
        }
        return index;
    }

    private static int skipBlockComment(String sql, int start) {
        int depth = 1;
        int index = start;
        while (index < sql.length() && depth > 0) {
            if (index + 1 < sql.length()
                && sql.charAt(index) == '/'
                && sql.charAt(index + 1) == '*') {
                depth++;
                index += 2;
            } else if (index + 1 < sql.length()
                && sql.charAt(index) == '*'
                && sql.charAt(index + 1) == '/') {
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
        while (index < sql.length()
            && (sql.charAt(index) == '_' || Character.isLetterOrDigit(sql.charAt(index)))) {
            index++;
        }
        return index < sql.length() && sql.charAt(index) == '$'
            ? sql.substring(start, index + 1)
            : null;
    }

    private static String unescapeQuoted(String value, char quote) {
        String doubled = Character.toString(quote) + quote;
        return value.replace(doubled, Character.toString(quote));
    }

    private static boolean isIdentifierStart(char value) {
        return value == '_' || Character.isLetter(value);
    }

    private static boolean isIdentifierPart(char value) {
        return value == '_' || value == '$' || Character.isLetterOrDigit(value);
    }

    private static List<String> objectPath(CatalogObjectId id) {
        var path = new ArrayList<String>(3);
        if (id.catalog() != null) {
            path.add(id.catalog());
        }
        if (id.schema() != null) {
            path.add(id.schema());
        }
        path.add(id.name());
        return List.copyOf(path);
    }

    private static boolean startsWithSelect(List<Token> tokens) {
        return !tokens.isEmpty() && tokens.getFirst().isKeyword("SELECT");
    }

    private static boolean startsWithWhere(List<Token> tokens) {
        return !tokens.isEmpty() && tokens.getFirst().isKeyword("WHERE");
    }

    private static boolean startsWithExpression(List<Token> tokens) {
        return !tokens.isEmpty()
            && !tokens.getFirst().isKeywordIn(NON_EXPRESSION_STATEMENT_STARTS);
    }

    private static boolean isSelectProjectionToken(Token token) {
        if (token.isIdentifier()) {
            return !token.isKeyword("ALL")
                && !token.isKeyword("AS")
                && !token.isKeyword("DISTINCT");
        }
        return token.kind() == TokenKind.RIGHT_PARENTHESIS
            || token.kind() == TokenKind.OTHER
                && (token.text().isEmpty()
                    || token.text().equals("*")
                    || token.text().chars().allMatch(Character::isDigit));
    }

    private static CompletionContext inheritedContext(
        CompletionContext[] contexts,
        int depth) {
        for (int parent = depth - 1; parent >= 0; parent--) {
            if (contexts[parent] != null) {
                return contexts[parent];
            }
        }
        return CompletionContext.UNKNOWN;
    }

    private static boolean isTypeCompletion(
        List<Token> tokens,
        int cursorOffset,
        int cursorDepth) {
        int previousIndex = -1;
        for (int index = 0; index < tokens.size(); index++) {
            Token token = tokens.get(index);
            if (token.start() >= cursorOffset || token.depth() != cursorDepth) {
                continue;
            }
            if (token.isIdentifier() && cursorOffset <= token.end()) {
                break;
            }
            previousIndex = index;
        }
        if (previousIndex >= 1
            && tokens.get(previousIndex - 1).depth() == cursorDepth
            && tokens.get(previousIndex - 1).isOther(":" )
            && tokens.get(previousIndex).isOther(":")) {
            return true;
        }

        int openingParenthesis = -1;
        for (int index = 0; index < tokens.size(); index++) {
            Token token = tokens.get(index);
            if (token.start() >= cursorOffset) {
                break;
            }
            if (token.kind() == TokenKind.LEFT_PARENTHESIS
                && token.depth() == cursorDepth - 1) {
                openingParenthesis = index;
            }
        }
        if (openingParenthesis <= 0
            || !tokens.get(openingParenthesis - 1).isKeyword("CAST")) {
            return false;
        }
        for (int index = openingParenthesis + 1; index < tokens.size(); index++) {
            Token token = tokens.get(index);
            if (token.start() >= cursorOffset) {
                break;
            }
            if (token.depth() == cursorDepth && token.isKeyword("AS")) {
                return true;
            }
        }
        return false;
    }

    private static boolean isClosedBlockComment(String sql, Token token) {
        return token.length() >= 4
            && sql.charAt(token.end() - 2) == '*'
            && sql.charAt(token.end() - 1) == '/';
    }

    private static boolean isClosedLiteral(String sql, Token token) {
        char opening = sql.charAt(token.start());
        if (opening == '\'') {
            return token.length() >= 2 && sql.charAt(token.end() - 1) == '\'';
        }
        String delimiter = dollarQuoteDelimiter(sql, token.start());
        return delimiter != null
            && token.length() >= delimiter.length() * 2
            && sql.regionMatches(
                token.end() - delimiter.length(),
                delimiter,
                0,
                delimiter.length());
    }

    private static boolean isClosedQuotedIdentifier(String sql, Token token) {
        char opening = sql.charAt(token.start());
        char closing = opening == '[' ? ']' : opening;
        return token.length() >= 2 && sql.charAt(token.end() - 1) == closing;
    }

    private static boolean isExpressionContext(CompletionContext context) {
        return switch (context) {
            case EXPRESSION, SELECT_LIST, PREDICATE, HAVING, GROUP_BY, ORDER_BY -> true;
            default -> false;
        };
    }

    private static int cursorDepth(List<Token> tokens, int cursorOffset) {
        int depth = 0;
        for (Token token : tokens) {
            if (token.start() >= cursorOffset) {
                break;
            }
            depth = token.kind() == TokenKind.LEFT_PARENTHESIS
                && token.end() <= cursorOffset
                ? token.depth() + 1
                : token.depth();
        }
        return depth;
    }

    private static String sourceText(String sql, Token token) {
        return sql.substring(token.start(), token.end());
    }

    enum CompletionContext {
        EXPRESSION,
        SELECT_LIST,
        PREDICATE,
        HAVING,
        GROUP_BY,
        ORDER_BY,
        TYPE,
        QUALIFIED_MEMBER,
        RELATION,
        UNKNOWN
    }

    record ExpressionColumn(
        String label,
        String qualifier,
        String qualifierName,
        boolean qualifiedByDefault,
        CatalogColumn column) {
    }

    record Resolution(
        String sql,
        List<Token> tokens,
        List<Relation> relations,
        boolean allRelationsResolved,
        boolean hasAnyRelationReference,
        boolean preferredObjectFallbackSafe) {
    }

    record UnknownColumn(int start, int length, String name, String qualifier) {
    }

    private record Relation(Token qualifier, CatalogObject object) {
    }

    private record RelationParse(List<Token> path, Token qualifier, int lastTokenIndex) {
    }

    private enum TokenKind {
        IDENTIFIER,
        LITERAL,
        COMMENT,
        DOT,
        COMMA,
        LEFT_PARENTHESIS,
        RIGHT_PARENTHESIS,
        OTHER
    }

    private record Token(
        String text,
        int start,
        int length,
        boolean identifier,
        boolean quoted,
        TokenKind kind,
        int depth) {

        static Token identifier(
            String text,
            int start,
            int length,
            boolean quoted,
            int depth) {
            return new Token(text, start, length, true, quoted, TokenKind.IDENTIFIER, depth);
        }

        static Token other(int start, int length, int depth) {
            return new Token("", start, length, false, false, TokenKind.OTHER, depth);
        }

        int end() {
            return start + length;
        }

        boolean isIdentifier() {
            return identifier;
        }

        boolean isKeyword(String keyword) {
            return identifier && !quoted && text.equalsIgnoreCase(keyword);
        }

        boolean isKeywordIn(Set<String> keywords) {
            return identifier
                && !quoted
                && keywords.contains(text.toUpperCase(Locale.ROOT));
        }

        boolean isOther(String value) {
            return kind == TokenKind.OTHER && text.equals(value);
        }
    }
}
