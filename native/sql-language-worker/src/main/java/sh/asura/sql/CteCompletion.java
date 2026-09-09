package sh.asura.sql;

import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.SqlWith;
import org.apache.calcite.sql.SqlWithItem;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;

import java.util.ArrayList;
import java.util.List;

/** Supplements a Calcite SqlAdvisor limitation for qualified CTE projections. */
final class CteCompletion {
    private CteCompletion() {
    }

    static List<String> projectedColumns(
        String sql,
        int replacementStart,
        int replacementEnd,
        SqlDialectProfile profile) {
        String qualifier = qualifierBeforeDot(sql, replacementStart);
        if (qualifier == null) {
            return List.of();
        }

        SqlNode parsed;
        try {
            String parseableSql = sql.substring(0, replacementStart)
                + "*"
                + sql.substring(replacementEnd);
            parsed = SqlParser.create(parseableSql, profile.parserConfig()).parseStmt();
        } catch (SqlParseException error) {
            return List.of();
        }
        if (!(parsed instanceof SqlWith with)) {
            return List.of();
        }

        String cteName = relationForQualifier(with.body, qualifier, profile);
        if (cteName == null) {
            return List.of();
        }
        for (SqlNode node : with.withList) {
            SqlWithItem item = (SqlWithItem) node;
            if (identifiersEqual(item.name.getSimple(), cteName, profile)) {
                return projectionNames(item);
            }
        }
        return List.of();
    }

    private static String qualifierBeforeDot(String sql, int replacementStart) {
        int dot = replacementStart - 1;
        if (dot < 0 || sql.charAt(dot) != '.') {
            return null;
        }
        int start = dot;
        while (start > 0 && Character.isJavaIdentifierPart(sql.charAt(start - 1))) {
            start--;
        }
        return start == dot ? null : sql.substring(start, dot);
    }

    private static String relationForQualifier(
        SqlNode body,
        String qualifier,
        SqlDialectProfile profile) {
        if (!(body instanceof SqlSelect select) || select.getFrom() == null) {
            return null;
        }
        return relationInFrom(select.getFrom(), qualifier, profile);
    }

    private static String relationInFrom(
        SqlNode from,
        String qualifier,
        SqlDialectProfile profile) {
        if (from instanceof SqlIdentifier identifier
            && identifiersEqual(identifier.getSimple(), qualifier, profile)) {
            return identifier.getSimple();
        }
        if (from instanceof SqlJoin join) {
            String left = relationInFrom(join.getLeft(), qualifier, profile);
            return left != null ? left : relationInFrom(join.getRight(), qualifier, profile);
        }
        if (!(from instanceof SqlCall call) || call.getKind() != SqlKind.AS) {
            return null;
        }

        SqlNode relation = call.operand(0);
        SqlNode alias = call.operand(1);
        if (relation instanceof SqlIdentifier relationId
            && alias instanceof SqlIdentifier aliasId
            && identifiersEqual(aliasId.getSimple(), qualifier, profile)) {
            return relationId.getSimple();
        }
        return null;
    }

    private static List<String> projectionNames(SqlWithItem item) {
        if (item.columnList != null) {
            return identifierNames(item.columnList);
        }
        if (!(item.query instanceof SqlSelect select)) {
            return List.of();
        }

        var names = new ArrayList<String>();
        for (SqlNode projection : select.getSelectList()) {
            if (projection instanceof SqlIdentifier identifier && !identifier.isStar()) {
                names.add(identifier.getSimple());
                continue;
            }
            if (projection instanceof SqlCall call && call.getKind() == SqlKind.AS) {
                SqlNode alias = call.operand(1);
                if (alias instanceof SqlIdentifier identifier) {
                    names.add(identifier.getSimple());
                }
            }
        }
        return List.copyOf(names);
    }

    private static List<String> identifierNames(SqlNodeList nodes) {
        var names = new ArrayList<String>(nodes.size());
        for (SqlNode node : nodes) {
            if (node instanceof SqlIdentifier identifier) {
                names.add(identifier.getSimple());
            }
        }
        return List.copyOf(names);
    }

    private static boolean identifiersEqual(
        String first,
        String second,
        SqlDialectProfile profile) {
        return profile.normalizeColumnIdentifier(first)
            .equals(profile.normalizeColumnIdentifier(second));
    }
}
