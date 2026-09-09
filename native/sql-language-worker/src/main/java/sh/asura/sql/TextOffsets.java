package sh.asura.sql;

/** Converts Calcite's one-based line/column ranges to Java/.NET UTF-16 offsets. */
final class TextOffsets {
    private TextOffsets() {
    }

    static int fromLineColumn(String text, int line, int column) {
        if (line <= 0 || column <= 0) {
            return 0;
        }

        int currentLine = 1;
        int offset = 0;
        while (offset < text.length() && currentLine < line) {
            char character = text.charAt(offset++);
            if (character == '\n') {
                currentLine++;
            }
        }

        int requested = offset + column - 1;
        int lineEnd = text.indexOf('\n', offset);
        int maximum = lineEnd == -1 ? text.length() : lineEnd;
        return Math.clamp(requested, offset, maximum);
    }

    static int inclusiveRangeLength(
        String text,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn) {
        int start = fromLineColumn(text, startLine, startColumn);
        int end = fromLineColumn(text, endLine, endColumn);
        if (end < start) {
            return 0;
        }
        return Math.min(text.length(), end + 1) - start;
    }
}
