using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// One MCP tool frozen for the lifetime of a governed agent run. The
/// provider-facing alias is Asura-owned; server metadata never becomes
/// executable authority.
/// </summary>
public sealed record AgentMcpToolManifest
{
    public const int MaximumInputSchemaBytes = 64 * 1024;
    public const int ProviderAliasLength = 64;

    private const int MaximumJsonDepth = 32;
    private const int MaximumJsonNodes = 16_384;
    private const int ProviderAliasDigestLength =
        ProviderAliasLength - 4;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentMcpToolManifest(
        McpServerProfileId profileId,
        long profileRevision,
        string profileName,
        string executable,
        string workingDirectory,
        string serverName,
        string serverVersion,
        string protocolVersion,
        string toolName,
        JsonElement inputSchema,
        AgentActionDigest opaqueToolIdentity,
        bool toolNameRedacted = false)
        : this(
            profileId,
            profileRevision,
            profileName,
            McpServerTransportKind.Stdio,
            executable,
            workingDirectory,
            serverName,
            serverVersion,
            protocolVersion,
            toolName,
            inputSchema,
            opaqueToolIdentity,
            toolNameRedacted)
    {
    }

    public AgentMcpToolManifest(
        McpServerProfileId profileId,
        long profileRevision,
        string profileName,
        McpServerTransportKind transportKind,
        string transportTarget,
        string? workingDirectory,
        string serverName,
        string serverVersion,
        string protocolVersion,
        string toolName,
        JsonElement inputSchema,
        AgentActionDigest opaqueToolIdentity,
        bool toolNameRedacted = false)
    {
        RequireText(profileId.Value, 256, nameof(profileId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(profileRevision);
        ProfileId = profileId;
        ProfileRevision = profileRevision;
        ProfileName = RequireText(profileName, 128, nameof(profileName));
        if (!Enum.IsDefined(transportKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportKind),
                transportKind,
                null);
        }

        TransportKind = transportKind;
        TransportTarget = RequireText(
            transportTarget,
            McpServerProfile.MaximumEndpointBytes,
            nameof(transportTarget));
        WorkingDirectory = transportKind switch
        {
            McpServerTransportKind.Stdio => RequireText(
                workingDirectory!,
                AgentApprovalPresentation.MaximumWorkingDirectoryBytes,
                nameof(workingDirectory)),
            McpServerTransportKind.StreamableHttp
                when workingDirectory is null => null,
            _ => throw new ArgumentException(
                "A remote MCP manifest cannot define a process working directory.",
                nameof(workingDirectory)),
        };
        ServerName = RequireText(serverName, 256, nameof(serverName));
        ServerVersion = RequireText(serverVersion, 256, nameof(serverVersion));
        ProtocolVersion = RequireText(
            protocolVersion,
            64,
            nameof(protocolVersion));
        ToolName = RequireText(toolName, 256, nameof(toolName));
        ToolNameRedacted = toolNameRedacted;
        if (string.IsNullOrWhiteSpace(opaqueToolIdentity.Value))
        {
            throw new ArgumentException(
                "An MCP tool manifest requires an opaque tool identity.",
                nameof(opaqueToolIdentity));
        }

        ToolIdentityDigest = opaqueToolIdentity;
        InputSchema = CopySchema(inputSchema);
        InputSchemaDigest = AgentActionDigest.FromUtf8(
            InputSchema.GetRawText());
        ManifestDigest = CreateManifestDigest(this);
        ProviderAlias =
            $"mcp_{ManifestDigest.Value[..ProviderAliasDigestLength]}";
    }

    public McpServerProfileId ProfileId { get; }

    public long ProfileRevision { get; }

    public string ProfileName { get; }

    public McpServerTransportKind TransportKind { get; }

    public string TransportTarget { get; }

    public string Executable => TransportKind == McpServerTransportKind.Stdio
        ? TransportTarget
        : string.Empty;

    public string? WorkingDirectory { get; }

    public string ServerName { get; }

    public string ServerVersion { get; }

    public string ProtocolVersion { get; }

    public string ToolName { get; }

    public bool ToolNameRedacted { get; }

    public JsonElement InputSchema { get; }

    public AgentActionDigest InputSchemaDigest { get; }

    public AgentActionDigest ManifestDigest { get; }

    public string ProviderAlias { get; }

    private AgentActionDigest ToolIdentityDigest { get; }

    private static JsonElement CopySchema(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "An MCP tool input schema must be a JSON object.",
                nameof(inputSchema));
        }
        if (!inputSchema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(
                type.GetString(),
                "object",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An MCP tool input schema must declare an object input.",
                nameof(inputSchema));
        }

        string raw;
        try
        {
            raw = inputSchema.GetRawText();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                "An MCP tool input schema is unavailable.",
                nameof(inputSchema),
                exception);
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(raw);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An MCP tool input schema must contain valid Unicode.",
                nameof(inputSchema),
                exception);
        }

        if (byteCount > MaximumInputSchemaBytes)
        {
            throw new ArgumentException(
                "An MCP tool input schema exceeds its byte limit.",
                nameof(inputSchema));
        }

        var remainingNodes = MaximumJsonNodes;
        ValidateJson(
            inputSchema,
            depth: 1,
            ref remainingNodes,
            nameof(inputSchema));
        return inputSchema.Clone();
    }

    private static void ValidateJson(
        JsonElement value,
        int depth,
        ref int remainingNodes,
        string parameterName)
    {
        if (depth > MaximumJsonDepth || --remainingNodes < 0)
        {
            throw new ArgumentException(
                "An MCP tool input schema exceeds its structural limits.",
                parameterName);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException(
                        "An MCP tool input schema contains duplicate properties.",
                        parameterName);
                }

                ValidateJson(
                    property.Value,
                    checked(depth + 1),
                    ref remainingNodes,
                    parameterName);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateJson(
                    item,
                    checked(depth + 1),
                    ref remainingNodes,
                    parameterName);
            }
        }
    }

    private static AgentActionDigest CreateManifestDigest(
        AgentMcpToolManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-mcp-tool-manifest");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, manifest.ProfileId.Value);
        AppendCanonical(
            hash,
            manifest.ProfileRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(hash, manifest.ProfileName);
        AppendCanonical(hash, manifest.TransportKind.ToString());
        AppendCanonical(hash, manifest.TransportTarget);
        AppendCanonical(hash, manifest.WorkingDirectory ?? string.Empty);
        AppendCanonical(hash, manifest.ServerName);
        AppendCanonical(hash, manifest.ServerVersion);
        AppendCanonical(hash, manifest.ProtocolVersion);
        AppendCanonical(hash, manifest.ToolName);
        AppendCanonical(
            hash,
            manifest.ToolNameRedacted ? "redacted" : "visible");
        AppendCanonical(hash, manifest.ToolIdentityDigest.Value);
        AppendCanonical(hash, manifest.InputSchemaDigest.Value);
        return new AgentActionDigest(
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCanonical(
        IncrementalHash hash,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string RequireText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes
                || value.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "MCP manifest text must be bounded and printable.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "MCP manifest text must contain valid Unicode.",
                parameterName,
                exception);
        }

        return string.Concat(value);
    }
}
