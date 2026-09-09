namespace Asura.Core;

/// <summary>
/// Recognizes credential-shaped literal material that must stay out of agent
/// approval and execution payloads until an opaque secret-reference path exists.
/// </summary>
public static class LiteralSecretValidator
{
    private static readonly string[] SecretMarkers =
    [
        "authorization: bearer ",
        "authorization=bearer ",
        "authorization: basic ",
        "authorization=basic ",
        "-----begin private key-----",
        "-----begin encrypted private key-----",
        "-----begin openssh private key-----",
    ];

    private static readonly string[] TokenPrefixes =
    [
        "ghp_",
        "github_pat_",
        "sk-",
        "akia",
        "xoxb-",
        "xoxp-",
    ];

    private static readonly string[] SecretAssignmentKeys =
    [
        "access-token",
        "access_token",
        "api-key",
        "api_key",
        "apikey",
        "authorization",
        "client-secret",
        "client_secret",
        "password",
        "passwd",
        "private-key",
        "private_key",
        "refresh-token",
        "refresh_token",
        "secret",
        "token",
    ];

    private static readonly string[] SecretOptions =
    [
        "--api-key",
        "--password",
        "--passwd",
        "--token",
    ];

    public static bool ContainsLikelyLiteralSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (SecretMarkers.Any(marker =>
                value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var token in value.Split(
                     [' ', '\t', '\r', '\n', '"', '\'', ',', ';'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 12
                && TokenPrefixes.Any(prefix =>
                    token.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (SecretAssignmentKeys.Any(key =>
                ContainsSecretBearingAssignment(value, key))
            || SecretOptions.Any(option =>
                ContainsSecretBearingOption(value, option)))
        {
            return true;
        }

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return false;
        }

        var authorityStart = scheme + 3;
        var authorityEnd = value.IndexOfAny(
            ['/', ' ', '\t', '\r', '\n'],
            authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = value.Length;
        }

        var at = value.IndexOf('@', authorityStart, authorityEnd - authorityStart);
        var colon = value.IndexOf(':', authorityStart, authorityEnd - authorityStart);
        return colon >= authorityStart && at > colon;
    }

    public static bool ContainsLikelyLiteralSecret(
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index]
                ?? throw new ArgumentException(
                    "Secret validation values cannot contain null entries.",
                    nameof(values));
            if (ContainsLikelyLiteralSecret(value))
            {
                return true;
            }

            if (index + 1 < values.Count
                && SecretOptions.Contains(
                    value,
                    StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(values[index + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSecretBearingAssignment(
        string value,
        string key)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var keyStart = value.IndexOf(
                key,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (keyStart < 0)
            {
                return false;
            }

            searchStart = keyStart + key.Length;
            if (!HasKeyStartBoundary(value, keyStart))
            {
                continue;
            }

            var cursor = searchStart;
            if (cursor < value.Length && value[cursor] is '"' or '\'')
            {
                cursor++;
            }
            else if (cursor < value.Length
                     && IsAssignmentIdentifier(value[cursor]))
            {
                continue;
            }

            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length || value[cursor] is not (':' or '='))
            {
                continue;
            }

            var separator = value[cursor];
            if (cursor + 1 < value.Length
                && separator == ':'
                && value[cursor + 1] == ':')
            {
                continue;
            }

            cursor++;
            if (separator == '='
                && cursor < value.Length
                && value[cursor] == '=')
            {
                while (cursor < value.Length && value[cursor] == '=')
                {
                    cursor++;
                }
            }
            else if (separator == '='
                     && cursor < value.Length
                     && value[cursor] is '~' or '>')
            {
                continue;
            }

            if (separator == ':'
                && cursor < value.Length
                && value[cursor] == '=')
            {
                cursor++;
            }

            if (HasSecretBearingValue(value, cursor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSecretBearingOption(
        string value,
        string option)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var optionStart = value.IndexOf(
                option,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (optionStart < 0)
            {
                return false;
            }

            searchStart = optionStart + option.Length;
            if (optionStart > 0
                && IsAssignmentIdentifier(value[optionStart - 1]))
            {
                continue;
            }

            var cursor = searchStart;
            if (cursor >= value.Length)
            {
                continue;
            }

            if (value[cursor] == '=')
            {
                cursor++;
            }
            else if (!char.IsWhiteSpace(value[cursor]))
            {
                continue;
            }

            if (HasSecretBearingValue(value, cursor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSecretBearingValue(string value, int start)
    {
        while (start < value.Length && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        if (start >= value.Length
            || value[start] == '#'
            || IsValueDelimiter(value[start]))
        {
            return false;
        }

        var cursor = start;
        var quote = '\0';
        var escaped = false;
        while (cursor < value.Length)
        {
            var character = value[cursor];
            if (quote != '\0')
            {
                cursor++;
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                cursor++;
                continue;
            }

            if (char.IsWhiteSpace(character) || IsValueDelimiter(character))
            {
                break;
            }

            cursor++;
        }

        var candidate = value[start..cursor];
        var isNonSecretLiteral =
            candidate is "\"\"" or "''"
            || candidate.Equals(
                "null",
                StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(
                "false",
                StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(
                "$null",
                StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(
                "$true",
                StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(
                "$false",
                StringComparison.OrdinalIgnoreCase);
        if (!isNonSecretLiteral)
        {
            return true;
        }

        while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
        {
            cursor++;
        }

        return cursor < value.Length && value[cursor] is '+' or '?';
    }

    private static bool HasKeyStartBoundary(string value, int keyStart)
    {
        if (keyStart == 0 || !IsAssignmentIdentifier(value[keyStart - 1]))
        {
            return true;
        }

        return value[keyStart - 1] == '-'
               && keyStart > 1
               && value[keyStart - 2] == '-';
    }

    private static bool IsValueDelimiter(char character) =>
        character is ',' or ';' or '}' or ']' or ')' or '&' or '|';

    private static bool IsAssignmentIdentifier(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-';
}
