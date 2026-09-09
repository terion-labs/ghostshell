package sh.asura.sql;

import org.apache.calcite.jdbc.JavaTypeFactoryImpl;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.sql.SqlFunction;
import org.apache.calcite.sql.SqlFunctionCategory;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOperator;
import org.apache.calcite.sql.SqlOperatorTable;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.SqlTableFunction;
import org.apache.calcite.sql.dialect.AnsiSqlDialect;
import org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.OperandTypes;
import org.apache.calcite.sql.type.ReturnTypes;
import org.apache.calcite.sql.type.SqlOperandCountRanges;
import org.apache.calcite.sql.validate.SqlNameMatcher;
import org.apache.calcite.sql.util.SqlOperatorTables;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** Calcite-owned built-in and detached-catalog routine knowledge for one driver profile. */
final class SqlFunctionCatalog {
    private static final int MAXIMUM_SIGNATURE_DETAIL_LENGTH = 4_096;
    private static final int MAXIMUM_RENDER_OPERANDS = 32;
    private static final SqlOperatorTable STANDARD_OPERATOR_TABLE =
        SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
            List.of(SqlLibrary.STANDARD),
            false);
    private static final Set<SqlOperator> STANDARD_OPERATORS = identitySet(
        STANDARD_OPERATOR_TABLE.getOperatorList());
    private static final SqlOperatorTable ALL_LIBRARY_OPERATOR_TABLE =
        SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
            allOperatorLibraries(),
            false);

    private final SqlOperatorTable operatorTable;
    private final List<Candidate> candidates;

    private SqlFunctionCatalog(
        SqlOperatorTable operatorTable,
        List<Candidate> candidates) {
        this.operatorTable = operatorTable;
        this.candidates = candidates;
    }

    static SqlFunctionCatalog create(
        CatalogSnapshot snapshot,
        SqlDialectProfile profile,
        JavaTypeFactoryImpl typeFactory) {
        SqlOperatorTable libraryTable =
            SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                profile.operatorLibraries());
        Set<SqlOperator> dialectOperators = dialectOperators(profile);
        OperatorEvidence intrinsicEvidence = intrinsicOperators(
            snapshot,
            dialectOperators,
            profile.dialectOperatorLibrary() != null);
        List<CatalogRoutineFunction> routineOperators = createRoutineOperators(
            snapshot,
            profile,
            typeFactory);
        Set<SqlOperator> libraryOperators = identitySet(libraryTable.getOperatorList());
        List<SqlOperator> additionalIntrinsicOperators = intrinsicEvidence.operators().stream()
            .filter(operator -> !libraryOperators.contains(operator))
            .toList();
        SqlOperatorTable calciteTable = additionalIntrinsicOperators.isEmpty()
            ? libraryTable
            : SqlOperatorTables.chain(
                libraryTable,
                SqlOperatorTables.of(additionalIntrinsicOperators));
        SqlOperatorTable operatorTable = routineOperators.isEmpty()
            ? calciteTable
            : SqlOperatorTables.chain(
                calciteTable,
                new CatalogRoutineOperatorTable(
                    snapshot,
                    profile,
                    routineOperators));
        return new SqlFunctionCatalog(
            operatorTable,
            createCandidates(
                operatorTable,
                dialectOperators,
                intrinsicEvidence.identities(),
                routineOperators,
                snapshot.routineCoverage(),
                snapshot.intrinsicCoverage()));
    }

    SqlOperatorTable operatorTable() {
        return operatorTable;
    }

    List<Candidate> candidates() {
        return candidates;
    }

    private static List<CatalogRoutineFunction> createRoutineOperators(
        CatalogSnapshot snapshot,
        SqlDialectProfile profile,
        JavaTypeFactoryImpl typeFactory) {
        var operators = new ArrayList<CatalogRoutineFunction>();
        Map<CatalogObjectId, List<String>> invocationParts = routineInvocationParts(
            snapshot,
            profile);
        for (CatalogRoutine routine : snapshot.routines()) {
            List<String> routineInvocation = invocationParts.get(routine.id());
            if (routine.kind().equals("table") || routineInvocation == null) {
                continue;
            }
            operators.add(new CatalogRoutineFunction(
                routine,
                routineInvocation,
                typeFactory));
        }
        return List.copyOf(operators);
    }

    private static Map<CatalogObjectId, List<String>> routineInvocationParts(
        CatalogSnapshot snapshot,
        SqlDialectProfile profile) {
        var identitiesByName = new LinkedHashMap<String, Set<CatalogObjectId>>();
        for (CatalogRoutine routine : snapshot.routines()) {
            String name = profile.normalizeObjectIdentifier(routine.id().name());
            identitiesByName.computeIfAbsent(name, ignored -> new java.util.LinkedHashSet<>())
                .add(routine.id());
        }

        var result = new LinkedHashMap<CatalogObjectId, List<String>>();
        for (CatalogRoutine routine : snapshot.routines()) {
            CatalogObjectId id = routine.id();
            String name = profile.normalizeObjectIdentifier(id.name());
            boolean ambiguousName = identitiesByName.get(name).size() > 1;
            result.put(id, invocationParts(
                snapshot,
                profile,
                id,
                ambiguousName));
        }
        var identitiesByInvocation = new LinkedHashMap<String, List<CatalogObjectId>>();
        for (var entry : result.entrySet()) {
            String key = entry.getValue().stream()
                .map(profile::normalizeObjectIdentifier)
                .reduce((left, right) -> left + '\u0000' + right)
                .orElse("");
            identitiesByInvocation.computeIfAbsent(key, ignored -> new ArrayList<>())
                .add(entry.getKey());
        }
        for (List<CatalogObjectId> ambiguous : identitiesByInvocation.values()) {
            if (ambiguous.size() < 2) {
                continue;
            }
            if (profile.driverId().equals("duckdb")
                || profile.driverId().equals("sqlserver")) {
                for (CatalogObjectId id : ambiguous) {
                    if (id.catalog() == null || id.schema() == null) {
                        result.remove(id);
                    } else {
                        result.put(id, List.of(id.catalog(), id.schema(), id.name()));
                    }
                }
            } else {
                for (CatalogObjectId id : ambiguous) {
                    result.remove(id);
                }
            }
        }
        return Map.copyOf(result);
    }

    private static List<String> invocationParts(
        CatalogSnapshot snapshot,
        SqlDialectProfile profile,
        CatalogObjectId id,
        boolean ambiguousName) {
        if (id.schema() == null || profile.driverId().equals("sqlite")) {
            return List.of(id.name());
        }
        boolean defaultSchema = snapshot.defaultSchema() != null
            && profile.matchesObjectIdentifier(
                snapshot.defaultSchema(),
                true,
                id.schema());
        boolean systemSearchSchema = (profile.driverId().equals("postgres")
            || profile.driverId().equals("cockroach")
            || profile.driverId().equals("redshift"))
            && id.schema().equalsIgnoreCase("pg_catalog");
        boolean schemaRequired = switch (profile.driverId()) {
            case "sqlserver" -> true;
            case "postgres", "cockroach", "redshift" ->
                !defaultSchema && !systemSearchSchema;
            default -> !defaultSchema;
        };
        return schemaRequired || ambiguousName
            ? List.of(id.schema(), id.name())
            : List.of(id.name());
    }

    private static Set<SqlOperator> dialectOperators(SqlDialectProfile profile) {
        if (profile.dialectOperatorLibrary() == null) {
            return Set.of();
        }
        return identitySet(SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
            List.of(profile.dialectOperatorLibrary()),
            false).getOperatorList());
    }

    private static OperatorEvidence intrinsicOperators(
        CatalogSnapshot snapshot,
        Set<SqlOperator> dialectOperators,
        boolean hasDialectOperatorLibrary) {
        if (snapshot.intrinsicSymbols().isEmpty()) {
            return new OperatorEvidence(List.of(), Set.of());
        }
        Set<String> names = snapshot.intrinsicSymbols().stream()
            .map(CatalogIntrinsicSymbol::name)
            .map(name -> name.toUpperCase(Locale.ROOT))
            .collect(java.util.stream.Collectors.toUnmodifiableSet());
        var matchesByName = new LinkedHashMap<String, List<SqlOperator>>();
        for (SqlOperator operator : ALL_LIBRARY_OPERATOR_TABLE.getOperatorList()) {
            if (insertion(operator.getSyntax()) == null
                || !isExpressionFunction(operator)) {
                continue;
            }
            String externalName = renderedSqlName(operator);
            if (names.contains(externalName.toUpperCase(Locale.ROOT))) {
                matchesByName.computeIfAbsent(
                    externalName.toUpperCase(Locale.ROOT),
                    ignored -> new ArrayList<>()).add(operator);
            }
        }
        var selected = new ArrayList<SqlOperator>();
        Set<SqlOperator> selectedIdentities = java.util.Collections.newSetFromMap(
            new java.util.IdentityHashMap<SqlOperator, Boolean>());
        for (List<SqlOperator> matches : matchesByName.values()) {
            List<SqlOperator> preferred = matches.stream()
                .filter(dialectOperators::contains)
                .toList();
            if (preferred.isEmpty() && hasDialectOperatorLibrary) {
                preferred = matches.stream()
                    .filter(STANDARD_OPERATORS::contains)
                    .toList();
            }
            if (preferred.isEmpty()) {
                preferred = equivalentCanonicalOperator(matches);
            }
            for (SqlOperator operator : preferred) {
                if (selectedIdentities.add(operator)) {
                    selected.add(operator);
                }
            }
        }
        return new OperatorEvidence(
            List.copyOf(selected),
            java.util.Collections.unmodifiableSet(selectedIdentities));
    }

    private static List<SqlOperator> equivalentCanonicalOperator(
        List<SqlOperator> operators) {
        if (operators.isEmpty()) {
            return List.of();
        }
        OperatorSemanticIdentity identity = semanticIdentity(operators.getFirst());
        if (operators.stream().skip(1).anyMatch(operator ->
            !semanticIdentity(operator).equals(identity))) {
            return List.of();
        }
        return List.of(operators.getFirst());
    }

    private static OperatorSemanticIdentity semanticIdentity(SqlOperator operator) {
        int minimum;
        int maximum;
        try {
            minimum = operator.getOperandCountRange().getMin();
            maximum = operator.getOperandCountRange().getMax();
        } catch (RuntimeException unavailableRange) {
            minimum = Integer.MIN_VALUE;
            maximum = Integer.MIN_VALUE;
        }
        return new OperatorSemanticIdentity(
            operator.getClass().getName(),
            operator.getSyntax(),
            operator.kind,
            minimum,
            maximum,
            operator.isAggregator(),
            operator.requiresOver());
    }

    private static Set<SqlOperator> identitySet(List<SqlOperator> operators) {
        Set<SqlOperator> identities = java.util.Collections.newSetFromMap(
            new java.util.IdentityHashMap<SqlOperator, Boolean>());
        identities.addAll(operators);
        return java.util.Collections.unmodifiableSet(identities);
    }

    private static List<SqlLibrary> allOperatorLibraries() {
        var libraries = new ArrayList<SqlLibrary>();
        libraries.add(SqlLibrary.STANDARD);
        libraries.addAll(SqlLibrary.expand(List.of(SqlLibrary.ALL)));
        return List.copyOf(libraries);
    }

    private static List<Candidate> createCandidates(
        SqlOperatorTable operatorTable,
        Set<SqlOperator> dialectOperators,
        Set<SqlOperator> intrinsicOperators,
        List<CatalogRoutineFunction> routineOperators,
        CatalogMetadataCoverage routineCoverage,
        CatalogMetadataCoverage intrinsicCoverage) {
        Set<String> corroboratedSimpleNames = routineOperators.stream()
            .filter(routine -> routine.completionNameParts().size() == 1)
            .map(routine -> routine.getName().toUpperCase(Locale.ROOT))
            .collect(java.util.stream.Collectors.toUnmodifiableSet());
        var builders = new LinkedHashMap<String, CandidateBuilder>();
        for (SqlOperator operator : operatorTable.getOperatorList()) {
            Insertion insertion = insertion(operator.getSyntax());
            if (insertion == null
                || !isExpressionFunction(operator)
                || !isAvailableForCompletion(
                    operator,
                    dialectOperators,
                    intrinsicOperators,
                    corroboratedSimpleNames,
                    routineCoverage,
                    intrinsicCoverage)) {
                continue;
            }
            String label = renderedSqlName(operator);
            if (!isSafeIdentifier(label)) {
                continue;
            }
            List<String> nameParts = operator instanceof CatalogRoutineFunction routine
                ? routine.completionNameParts()
                : List.of(label);
            String key = String.join("\u0000", nameParts).toUpperCase(Locale.ROOT)
                + '\u0000' + insertion;
            builders.computeIfAbsent(
                key,
                ignored -> new CandidateBuilder(label, nameParts, insertion))
                .add(operator);
        }
        List<Candidate> candidates = builders.values().stream()
            .map(CandidateBuilder::build)
            .toList();
        Set<String> grammarBareNames = operatorTable.getOperatorList().stream()
            .filter(operator -> !(operator instanceof CatalogRoutineFunction))
            .filter(operator -> operator.getSyntax() == SqlSyntax.FUNCTION_ID)
            .map(SqlFunctionCatalog::renderedSqlName)
            .map(name -> name.toUpperCase(Locale.ROOT))
            .collect(java.util.stream.Collectors.toUnmodifiableSet());
        return candidates.stream()
            .filter(candidate -> !(candidate.insertion() == Insertion.CALL
                && candidate.catalogOnly()
                && candidate.nameParts().size() == 1
                && grammarBareNames.contains(
                    candidate.label().toUpperCase(Locale.ROOT))))
            .toList();
    }

    private static boolean isAvailableForCompletion(
        SqlOperator operator,
        Set<SqlOperator> dialectOperators,
        Set<SqlOperator> intrinsicOperators,
        Set<String> corroboratedSimpleNames,
        CatalogMetadataCoverage routineCoverage,
        CatalogMetadataCoverage intrinsicCoverage) {
        if (operator instanceof CatalogRoutineFunction
            || intrinsicOperators.contains(operator)) {
            return true;
        }
        String externalName = renderedSqlName(operator).toUpperCase(Locale.ROOT);
        if (insertion(operator.getSyntax()) != Insertion.BARE
            && (corroboratedSimpleNames.contains(operator.getName().toUpperCase(Locale.ROOT))
                || corroboratedSimpleNames.contains(externalName))) {
            return true;
        }
        if (!dialectOperators.contains(operator)) {
            return false;
        }
        return insertion(operator.getSyntax()) != Insertion.BARE
            && routineCoverage != CatalogMetadataCoverage.COMPLETE
            && intrinsicCoverage != CatalogMetadataCoverage.COMPLETE;
    }

    private static boolean isExpressionFunction(SqlOperator operator) {
        if (!(operator instanceof SqlFunction function)
            || operator instanceof SqlTableFunction) {
            return false;
        }
        SqlFunctionCategory category = function.getFunctionType();
        return category != null
            && category.isFunction()
            && !category.isTableFunction();
    }

    private static String renderedSqlName(SqlOperator operator) {
        int operandCount;
        try {
            operandCount = Math.max(0, operator.getOperandCountRange().getMin());
        } catch (RuntimeException unavailableRange) {
            operandCount = 0;
        }
        if (operandCount > MAXIMUM_RENDER_OPERANDS) {
            return operator.getName();
        }
        SqlNode[] operands = new SqlNode[operandCount];
        for (int index = 0; index < operands.length; index++) {
            operands[index] = new SqlIdentifier("value", SqlParserPos.ZERO);
        }
        try {
            String rendered = operator.createCall(SqlParserPos.ZERO, operands)
                .toSqlString(AnsiSqlDialect.DEFAULT)
                .getSql()
                .strip();
            int parenthesis = rendered.indexOf('(');
            String candidate = parenthesis < 0
                ? rendered
                : rendered.substring(0, parenthesis).strip();
            if (candidate.startsWith("\"") && candidate.endsWith("\"")
                && candidate.length() > 1) {
                candidate = candidate.substring(1, candidate.length() - 1)
                    .replace("\"\"", "\"");
            }
            return isSafeIdentifier(candidate) ? candidate : operator.getName();
        } catch (RuntimeException | AssertionError unsupportedRendering) {
            return operator.getName();
        }
    }

    private static Insertion insertion(SqlSyntax syntax) {
        return switch (syntax) {
            case FUNCTION, FUNCTION_STAR, ORDERED_FUNCTION, FUNCTION_ID_CONSTANT ->
                Insertion.CALL;
            case FUNCTION_ID -> Insertion.BARE;
            default -> null;
        };
    }

    private static boolean isSafeIdentifier(String name) {
        if (name.isEmpty() || !(name.charAt(0) == '_' || Character.isLetter(name.charAt(0)))) {
            return false;
        }
        for (int index = 1; index < name.length(); index++) {
            char value = name.charAt(index);
            if (value != '_' && !Character.isLetterOrDigit(value)) {
                return false;
            }
        }
        return true;
    }

    enum Insertion {
        CALL,
        BARE
    }

    record Candidate(
        String label,
        List<String> nameParts,
        String detail,
        Insertion insertion,
        boolean aggregateOnly,
        boolean requiresOverOnly,
        boolean catalogOnly) {
    }

    private static final class CandidateBuilder {
        private final String label;
        private final List<String> nameParts;
        private final Insertion insertion;
        private final Map<String, String> signatures = new LinkedHashMap<>();
        private boolean hasAggregateOverload;
        private boolean hasWindowOnlyOverload;
        private boolean hasScalarOverload;
        private boolean catalogOnly = true;

        CandidateBuilder(String label, List<String> nameParts, Insertion insertion) {
            this.label = label;
            this.nameParts = List.copyOf(nameParts);
            this.insertion = insertion;
        }

        CandidateBuilder add(SqlOperator operator) {
            if (operator.requiresOver()) {
                hasWindowOnlyOverload = true;
            } else if (operator.isAggregator()) {
                hasAggregateOverload = true;
            } else {
                hasScalarOverload = true;
            }
            catalogOnly &= operator instanceof CatalogRoutineFunction;
            String signature = allowedSignatures(operator);
            if (!signature.isBlank()) {
                signatures.putIfAbsent(signature, signature);
            }
            return this;
        }

        Candidate build() {
            String detail = String.join("\n", signatures.values());
            if (detail.length() > MAXIMUM_SIGNATURE_DETAIL_LENGTH) {
                detail = detail.substring(0, MAXIMUM_SIGNATURE_DETAIL_LENGTH - 1) + "…";
            }
            return new Candidate(
                label,
                nameParts,
                detail.isBlank() ? null : detail,
                insertion,
                !hasScalarOverload && hasAggregateOverload,
                !hasScalarOverload && !hasAggregateOverload && hasWindowOnlyOverload,
                catalogOnly);
        }

        private static String allowedSignatures(SqlOperator operator) {
            try {
                String signatures = operator.getAllowedSignatures();
                return signatures == null ? "" : signatures.strip();
            } catch (RuntimeException unsupportedMetadata) {
                return "";
            }
        }
    }

    private static final class CatalogRoutineFunction extends SqlFunction {
        private final CatalogRoutine routine;
        private final List<String> completionNameParts;

        CatalogRoutineFunction(
            CatalogRoutine routine,
            List<String> completionNameParts,
            JavaTypeFactoryImpl typeFactory) {
            super(
                new SqlIdentifier(routine.id().name(), SqlParserPos.ZERO),
                ReturnTypes.explicit(returnType(routine, typeFactory)),
                null,
                OperandTypes.variadic(operandCountRange(routine)),
                parameterTypes(routine, typeFactory),
                SqlFunctionCategory.USER_DEFINED_FUNCTION);
            this.routine = routine;
            this.completionNameParts = List.copyOf(completionNameParts);
        }

        List<String> completionNameParts() {
            return completionNameParts;
        }

        @Override
        public boolean isAggregator() {
            return routine.kind().equals("aggregate");
        }

        @Override
        public boolean requiresOver() {
            return routine.kind().equals("window");
        }

        @Override
        public String getAllowedSignatures(String operatorName) {
            return routine.signature();
        }

        private static RelDataType returnType(
            CatalogRoutine routine,
            JavaTypeFactoryImpl typeFactory) {
            RelDataType type = SqlTypeMapping.create(
                typeFactory,
                routine.returnTypeName(),
                routine.returnValueKind());
            return typeFactory.createTypeWithNullability(type, true);
        }

        private static List<RelDataType> parameterTypes(
            CatalogRoutine routine,
            JavaTypeFactoryImpl typeFactory) {
            return inputParameters(routine).stream()
                .map(parameter -> SqlTypeMapping.create(
                    typeFactory,
                    parameter.dataTypeName(),
                    parameter.valueKind()))
                .toList();
        }

        private static List<CatalogRoutineParameter> inputParameters(CatalogRoutine routine) {
            return routine.parameters().stream()
                .filter(parameter -> !parameter.mode().equals("out"))
                .toList();
        }

        private static org.apache.calcite.sql.SqlOperandCountRange operandCountRange(
            CatalogRoutine routine) {
            if (routine.minimumArgumentCount() != null) {
                return routine.maximumArgumentCount() == null
                    ? SqlOperandCountRanges.from(routine.minimumArgumentCount())
                    : routine.minimumArgumentCount().equals(routine.maximumArgumentCount())
                        ? SqlOperandCountRanges.of(routine.minimumArgumentCount())
                        : SqlOperandCountRanges.between(
                            routine.minimumArgumentCount(),
                            routine.maximumArgumentCount());
            }
            List<CatalogRoutineParameter> inputs = inputParameters(routine);
            if (inputs.isEmpty()) {
                return SqlOperandCountRanges.any();
            }
            int minimum = (int) inputs.stream()
                .filter(parameter -> !parameter.isOptional() && !parameter.isVariadic())
                .count();
            if (inputs.stream().anyMatch(CatalogRoutineParameter::isVariadic)) {
                return SqlOperandCountRanges.from(minimum);
            }
            return minimum == inputs.size()
                ? SqlOperandCountRanges.of(minimum)
                : SqlOperandCountRanges.between(minimum, inputs.size());
        }
    }

    private static final class CatalogRoutineOperatorTable implements SqlOperatorTable {
        private final List<RoutineEntry> entries;

        CatalogRoutineOperatorTable(
            CatalogSnapshot snapshot,
            SqlDialectProfile profile,
            List<CatalogRoutineFunction> operators) {
            this.entries = operators.stream()
                .map(operator -> new RoutineEntry(
                    operator,
                    lookupPaths(
                        profile,
                        operator.routine.id(),
                        operator.completionNameParts())))
                .toList();
        }

        @Override
        public void lookupOperatorOverloads(
            SqlIdentifier operatorName,
            SqlFunctionCategory category,
            SqlSyntax syntax,
            List<SqlOperator> operators,
            SqlNameMatcher nameMatcher) {
            if (syntax != SqlSyntax.FUNCTION
                || category != null && !category.isFunction()) {
                return;
            }
            for (RoutineEntry entry : entries) {
                if (entry.paths().stream().anyMatch(path -> matches(
                    operatorName,
                    path,
                    nameMatcher))) {
                    operators.add(entry.operator());
                }
            }
        }

        @Override
        public List<SqlOperator> getOperatorList() {
            return entries.stream()
                .map(entry -> (SqlOperator) entry.operator())
                .toList();
        }

        private static boolean matches(
            SqlIdentifier reference,
            List<String> path,
            SqlNameMatcher matcher) {
            if (reference.names.size() != path.size()) {
                return false;
            }
            for (int index = 0; index < path.size(); index++) {
                if (reference.isComponentQuoted(index)
                    ? !reference.names.get(index).equals(path.get(index))
                    : !matcher.matches(reference.names.get(index), path.get(index))) {
                    return false;
                }
            }
            return true;
        }

        private static List<List<String>> lookupPaths(
            SqlDialectProfile profile,
            CatalogObjectId id,
            List<String> invocationParts) {
            var paths = new ArrayList<List<String>>();
            paths.add(invocationParts);
            if (id.schema() == null || profile.driverId().equals("sqlite")) {
                return List.copyOf(paths);
            }
            if (invocationParts.size() == 1) {
                List<String> qualified = List.of(id.schema(), id.name());
                if (!paths.contains(qualified)) {
                    paths.add(qualified);
                }
            }
            return List.copyOf(paths);
        }

        private record RoutineEntry(
            CatalogRoutineFunction operator,
            List<List<String>> paths) {
        }
    }

    private record OperatorEvidence(
        List<SqlOperator> operators,
        Set<SqlOperator> identities) {
    }

    private record OperatorSemanticIdentity(
        String implementationClass,
        SqlSyntax syntax,
        SqlKind kind,
        int minimumOperands,
        int maximumOperands,
        boolean aggregator,
        boolean requiresOver) {
    }
}
