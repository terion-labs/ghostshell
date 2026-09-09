namespace Asura.App.Views.Components;

public sealed partial class CodeEditBox
{
    private enum SqlLexicalState
    {
        Code,
        SingleQuotedString,
        DoubleQuotedIdentifier,
        BacktickQuotedIdentifier,
        BracketedIdentifier,
        LineComment,
        BlockComment,
        DollarQuotedString,
    }

    private bool IsIdentifierInput(string? enteredText)
    {
        if (string.IsNullOrEmpty(enteredText)
            || enteredText.Any(character => !IsIdentifierPart(character)))
        {
            return false;
        }

        var tokenEnd = Math.Clamp(Editor.CaretOffset, 0, Editor.Document.TextLength);
        var tokenStart = tokenEnd;
        while (tokenStart > 0
               && IsIdentifierPart(Editor.Document.GetCharAt(tokenStart - 1)))
        {
            tokenStart--;
        }

        if (tokenStart >= tokenEnd
            || !IsIdentifierStart(Editor.Document.GetCharAt(tokenStart)))
        {
            return false;
        }

        return GetSqlLexicalState(tokenEnd) is
            SqlLexicalState.Code
            or SqlLexicalState.DoubleQuotedIdentifier
            or SqlLexicalState.BacktickQuotedIdentifier
            or SqlLexicalState.BracketedIdentifier;
    }

    private static bool IsIdentifierStart(char character) =>
        character == '_' || char.IsLetter(character);

    private static bool IsIdentifierPart(char character) =>
        character is '_' or '$' || char.IsLetterOrDigit(character);

    private bool HasPlausibleDotQualifier()
    {
        var dotOffset = Editor.CaretOffset - 1;
        if (dotOffset < 1
            || dotOffset >= Editor.Document.TextLength
            || Editor.Document.GetCharAt(dotOffset) != '.'
            || !IsSqlCodePosition(dotOffset))
        {
            return false;
        }

        var qualifierEnd = dotOffset;
        if (qualifierEnd == 0)
        {
            return false;
        }

        var finalCharacter = Editor.Document.GetCharAt(qualifierEnd - 1);
        if (IsIdentifierPart(finalCharacter))
        {
            var qualifierStart = qualifierEnd - 1;
            while (qualifierStart > 0
                   && IsIdentifierPart(Editor.Document.GetCharAt(qualifierStart - 1)))
            {
                qualifierStart--;
            }

            return IsIdentifierStart(Editor.Document.GetCharAt(qualifierStart));
        }

        return finalCharacter switch
        {
            '"' => HasDelimitedIdentifier(qualifierEnd, '"'),
            '`' => HasDelimitedIdentifier(qualifierEnd, '`'),
            ']' => HasBracketedIdentifier(qualifierEnd),
            _ => false,
        };
    }

    // This scanner only suppresses obviously noisy UI requests. Calcite remains
    // the authority for parsing and validating the statement itself.
    private bool IsSqlCodePosition(int offset) =>
        GetSqlLexicalState(offset) == SqlLexicalState.Code;

    private SqlLexicalState GetSqlLexicalState(int offset)
    {
        var sql = Editor.Document.Text;
        var state = SqlLexicalState.Code;
        var blockCommentDepth = 0;
        string? dollarQuoteDelimiter = null;
        for (var position = 0; position < offset; position++)
        {
            var character = sql[position];
            switch (state)
            {
                case SqlLexicalState.Code:
                    if (character == '-' && IsNextCharacter(sql, position, offset, '-'))
                    {
                        state = SqlLexicalState.LineComment;
                        position++;
                    }
                    else if (character == '/' && IsNextCharacter(sql, position, offset, '*'))
                    {
                        state = SqlLexicalState.BlockComment;
                        blockCommentDepth = 1;
                        position++;
                    }
                    else if (character == '\'')
                    {
                        state = SqlLexicalState.SingleQuotedString;
                    }
                    else if (character == '"')
                    {
                        state = SqlLexicalState.DoubleQuotedIdentifier;
                    }
                    else if (character == '`')
                    {
                        state = SqlLexicalState.BacktickQuotedIdentifier;
                    }
                    else if (character == '[')
                    {
                        state = SqlLexicalState.BracketedIdentifier;
                    }
                    else if (character == '$'
                             && TryReadDollarQuoteDelimiter(
                                 sql,
                                 position,
                                 offset,
                                 out var openingDollarQuoteDelimiter))
                    {
                        state = SqlLexicalState.DollarQuotedString;
                        dollarQuoteDelimiter = openingDollarQuoteDelimiter;
                        position += openingDollarQuoteDelimiter.Length - 1;
                    }

                    break;

                case SqlLexicalState.SingleQuotedString:
                    if (character == '\\' && position + 1 < offset)
                    {
                        position++;
                    }
                    else if (character == '\'')
                    {
                        if (IsNextCharacter(sql, position, offset, '\''))
                        {
                            position++;
                        }
                        else
                        {
                            state = SqlLexicalState.Code;
                        }
                    }

                    break;

                case SqlLexicalState.DoubleQuotedIdentifier:
                    if (character == '"')
                    {
                        if (IsNextCharacter(sql, position, offset, '"'))
                        {
                            position++;
                        }
                        else
                        {
                            state = SqlLexicalState.Code;
                        }
                    }

                    break;

                case SqlLexicalState.BacktickQuotedIdentifier:
                    if (character == '`')
                    {
                        if (IsNextCharacter(sql, position, offset, '`'))
                        {
                            position++;
                        }
                        else
                        {
                            state = SqlLexicalState.Code;
                        }
                    }

                    break;

                case SqlLexicalState.BracketedIdentifier:
                    if (character == ']')
                    {
                        if (IsNextCharacter(sql, position, offset, ']'))
                        {
                            position++;
                        }
                        else
                        {
                            state = SqlLexicalState.Code;
                        }
                    }

                    break;

                case SqlLexicalState.LineComment:
                    if (character is '\r' or '\n')
                    {
                        state = SqlLexicalState.Code;
                    }

                    break;

                case SqlLexicalState.BlockComment:
                    if (character == '/' && IsNextCharacter(sql, position, offset, '*'))
                    {
                        blockCommentDepth++;
                        position++;
                    }
                    else if (character == '*' && IsNextCharacter(sql, position, offset, '/'))
                    {
                        blockCommentDepth--;
                        position++;
                        if (blockCommentDepth == 0)
                        {
                            state = SqlLexicalState.Code;
                        }
                    }

                    break;

                case SqlLexicalState.DollarQuotedString:
                    if (dollarQuoteDelimiter is not null
                        && position + dollarQuoteDelimiter.Length <= offset
                        && sql.AsSpan(position, dollarQuoteDelimiter.Length)
                            .SequenceEqual(dollarQuoteDelimiter))
                    {
                        position += dollarQuoteDelimiter.Length - 1;
                        dollarQuoteDelimiter = null;
                        state = SqlLexicalState.Code;
                    }

                    break;
            }
        }

        return state;
    }

    private static bool IsNextCharacter(
        string sql,
        int position,
        int limit,
        char expected) =>
        position + 1 < limit && sql[position + 1] == expected;

    private static bool TryReadDollarQuoteDelimiter(
        string sql,
        int offset,
        int limit,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (offset > 0 && IsIdentifierPart(sql[offset - 1]))
        {
            return false;
        }

        var closingDollar = offset + 1;
        if (closingDollar < limit && sql[closingDollar] == '$')
        {
            delimiter = "$$";
            return true;
        }

        if (closingDollar >= limit || !IsIdentifierStart(sql[closingDollar]))
        {
            return false;
        }

        closingDollar++;
        while (closingDollar < limit
               && (sql[closingDollar] == '_'
                   || char.IsLetterOrDigit(sql[closingDollar])))
        {
            closingDollar++;
        }

        if (closingDollar >= limit || sql[closingDollar] != '$')
        {
            return false;
        }

        delimiter = sql[offset..(closingDollar + 1)];
        return true;
    }

    private bool HasDelimitedIdentifier(int endOffset, char delimiter)
    {
        var closingDelimiter = endOffset - 1;
        for (var offset = closingDelimiter - 1; offset >= 0; offset--)
        {
            if (Editor.Document.GetCharAt(offset) != delimiter)
            {
                continue;
            }

            if (offset > 0 && Editor.Document.GetCharAt(offset - 1) == delimiter)
            {
                offset--;
                continue;
            }

            return offset < closingDelimiter - 1;
        }

        return false;
    }

    private bool HasBracketedIdentifier(int endOffset)
    {
        var closingBracket = endOffset - 1;
        for (var offset = closingBracket - 1; offset >= 0; offset--)
        {
            var character = Editor.Document.GetCharAt(offset);
            if (character == ']' && offset > 0
                                 && Editor.Document.GetCharAt(offset - 1) == ']')
            {
                offset--;
                continue;
            }

            if (character == '[')
            {
                return offset < closingBracket - 1;
            }
        }

        return false;
    }
}
