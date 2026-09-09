using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Durable configuration for one stdio or Streamable HTTP MCP server. Secrets
/// are represented only as opaque profile-scoped vault references.
/// </summary>
public sealed record McpServerProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumIdentifierBytes = 256;
    public const int MaximumNameBytes = 128;
    public const int MaximumExecutableBytes = 2_048;
    public const int MaximumArgumentCount = 64;
    public const int MaximumArgumentBytes = 2_048;
    public const int MaximumArgumentsBytes = 16 * 1_024;
    public const int MaximumWorkingDirectoryBytes = 4_096;
    public const int MaximumEnvironmentVariableCount = 64;
    public const int MaximumEndpointBytes = 4_096;
    public const int MaximumHttpHeaderCount = 32;
    public const int MaximumEnabledToolCount = 128;
    public const int MaximumToolNameBytes = 128;
    public const int MaximumSecretReferenceBytes = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    [JsonConstructor]
    public McpServerProfile(
        McpServerProfileId id,
        int schemaVersion,
        string name,
        McpServerTransport transport,
        IReadOnlyList<string> enabledTools,
        bool isEnabled = true,
        bool isTrusted = true)
    {
        ValidateIdentifier(id.Value, nameof(id), MaximumIdentifierBytes);
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The MCP-server schema version is not supported.");
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RequireSecretFreeText(
            RequireTrimmedText(name, nameof(name), MaximumNameBytes),
            nameof(name));
        Transport = transport
            ?? throw new ArgumentNullException(nameof(transport));
        EnabledTools = CopyEnabledTools(enabledTools);
        IsEnabled = isEnabled;
        IsTrusted = isTrusted;
    }

    public static DefinitionKind Kind => DefinitionKind.McpServerProfile;

    public McpServerProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public McpServerTransport Transport { get; }

    public IReadOnlyList<string> EnabledTools { get; }

    public bool IsEnabled { get; }

    /// <summary>
    /// True only after a local user reviewed the executable/endpoint, ordered
    /// arguments, credential bindings, and tool allowlist. Enablement controls
    /// agent selection independently from this launch authority.
    /// </summary>
    public bool IsTrusted { get; }

    internal static void ValidateSecretReference(
        SecretRef reference,
        string parameterName)
    {
        ValidateIdentifier(
            reference.Value,
            parameterName,
            MaximumSecretReferenceBytes);
        RequireSecretFreeText(reference.Value, parameterName);
    }

    internal static IReadOnlyList<string> CopyArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumArgumentCount)
        {
            throw new ArgumentException(
                $"An MCP server cannot define more than {MaximumArgumentCount} arguments.",
                nameof(arguments));
        }

        var copies = new string[arguments.Count];
        var totalBytes = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index] ?? throw new ArgumentException(
                    "MCP server arguments cannot contain null values.",
                    nameof(arguments));
            copies[index] = CopyPrintableText(
                argument,
                nameof(arguments),
                MaximumArgumentBytes);
            totalBytes += StrictUtf8.GetByteCount(copies[index]);
        }

        if (totalBytes > MaximumArgumentsBytes)
        {
            throw new ArgumentException(
                $"MCP server arguments cannot exceed {MaximumArgumentsBytes} UTF-8 bytes in total.",
                nameof(arguments));
        }

        if (LiteralSecretValidator.ContainsLikelyLiteralSecret(copies))
        {
            throw new ArgumentException(
                "MCP server arguments cannot contain literal credentials; bind secrets through profile-scoped vault environment references.",
                nameof(arguments));
        }

        return new ReadOnlyCollection<string>(copies);
    }

    internal static IReadOnlyList<McpServerEnvironmentVariable> CopyEnvironment(
        IReadOnlyList<McpServerEnvironmentVariable> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Count > MaximumEnvironmentVariableCount)
        {
            throw new ArgumentException(
                $"An MCP server cannot define more than {MaximumEnvironmentVariableCount} environment variables.",
                nameof(environment));
        }

        var copies = environment
            .Select(variable => variable ?? throw new ArgumentException(
                "MCP server environment cannot contain null entries.",
                nameof(environment)))
            .OrderBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(variable => variable.Name, StringComparer.Ordinal)
            .ToArray();
        if (copies
            .Select(variable => variable.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != copies.Length)
        {
            throw new ArgumentException(
                "MCP server environment variable names must be distinct across platforms.",
                nameof(environment));
        }

        return new ReadOnlyCollection<McpServerEnvironmentVariable>(copies);
    }

    private static IReadOnlyList<string> CopyEnabledTools(
        IReadOnlyList<string> enabledTools)
    {
        ArgumentNullException.ThrowIfNull(enabledTools);
        if (enabledTools.Count > MaximumEnabledToolCount)
        {
            throw new ArgumentException(
                $"An MCP server cannot enable more than {MaximumEnabledToolCount} tools.",
                nameof(enabledTools));
        }

        var copies = enabledTools
            .Select(tool => RequireToolName(tool, nameof(enabledTools)))
            .OrderBy(tool => tool, StringComparer.Ordinal)
            .ToArray();
        if (copies.Distinct(StringComparer.Ordinal).Count() != copies.Length)
        {
            throw new ArgumentException(
                "An MCP server enabled-tool allowlist cannot contain duplicates.",
                nameof(enabledTools));
        }

        return new ReadOnlyCollection<string>(copies);
    }

    private static string RequireToolName(
        string value,
        string parameterName)
    {
        var copy = RequireSecretFreeText(
            RequireTrimmedText(
                value,
                parameterName,
                MaximumToolNameBytes),
            parameterName);
        if (copy.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'
                    and not '.'))
        {
            throw new ArgumentException(
                "MCP tool names must use protocol identifier syntax: ASCII letters, digits, underscore, hyphen, or period.",
                parameterName);
        }

        return copy;
    }

    internal static string RequireSecretFreeText(
        string value,
        string parameterName)
    {
        if (LiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "MCP server definitions cannot contain literal credentials; use an opaque vault reference.",
                parameterName);
        }

        return value;
    }

    internal static string RequireTrimmedText(
        string value,
        string parameterName,
        int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "MCP server configuration text cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return CopyPrintableText(
            value,
            parameterName,
            maximumBytes);
    }

    private static string CopyPrintableText(
        string value,
        string parameterName,
        int maximumBytes)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "MCP server configuration text must contain valid Unicode.",
                parameterName,
                exception);
        }

        if (byteCount > maximumBytes
            || value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
        {
            throw new ArgumentException(
                "MCP server configuration text must be bounded and printable.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static void ValidateIdentifier(
        string? value,
        string parameterName,
        int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An MCP server identifier must be non-empty and contain no edge whitespace.",
                parameterName);
        }

        _ = CopyPrintableText(
            value,
            parameterName,
            maximumBytes);
    }
}
