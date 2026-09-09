using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Closed Docker observations accepted by the governed execution boundary.
/// Resource identities are opaque leases; lifecycle and exec operations have
/// no request variant.
/// </summary>
public abstract record AgentDockerReadRequest
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private AgentDockerReadRequest(
        PanelInstanceId panelId,
        string toolName,
        string requiredSessionCapability)
    {
        if (string.IsNullOrWhiteSpace(panelId.Value)
            || panelId.Value.Length > 256
            || panelId.Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Docker observation requires a bounded panel identifier.",
                nameof(panelId));
        }

        PanelId = panelId;
        ToolName = toolName;
        RequiredSessionCapability = requiredSessionCapability;
    }

    public PanelInstanceId PanelId { get; }

    public string ToolName { get; }

    public string RequiredSessionCapability { get; }

    public sealed record ReadState : AgentDockerReadRequest
    {
        public ReadState(PanelInstanceId panelId, int maximumResourcesPerKind)
            : base(
                panelId,
                BuiltInAgentTools.DockerReadState,
                SessionCapabilities.DockerReadState)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumResourcesPerKind, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResourcesPerKind, 100);
            MaximumResourcesPerKind = maximumResourcesPerKind;
        }

        public int MaximumResourcesPerKind { get; }
    }

    public sealed record Inspect : AgentDockerReadRequest
    {
        public Inspect(
            PanelInstanceId panelId,
            DockerResourceReferenceId reference)
            : base(
                panelId,
                BuiltInAgentTools.DockerInspect,
                SessionCapabilities.DockerInspect)
        {
            Reference = reference;
        }

        public DockerResourceReferenceId Reference { get; }
    }

    public sealed record Logs : AgentDockerReadRequest
    {
        public Logs(
            PanelInstanceId panelId,
            DockerResourceReferenceId container,
            int limit,
            string? beforeTimestamp,
            string? sinceTimestamp,
            string? searchText,
            int contextLines)
            : base(
                panelId,
                BuiltInAgentTools.DockerLogs,
                SessionCapabilities.DockerReadLogs)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
            ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(contextLines, 50);
            if (beforeTimestamp is not null && sinceTimestamp is not null)
            {
                throw new ArgumentException(
                    "A Docker log observation cannot page in both directions.");
            }

            Container = container;
            Limit = limit;
            BeforeTimestamp = RequireOptionalText(
                beforeTimestamp,
                nameof(beforeTimestamp),
                128);
            SinceTimestamp = RequireOptionalText(
                sinceTimestamp,
                nameof(sinceTimestamp),
                128);
            SearchText = RequireOptionalText(
                searchText,
                nameof(searchText),
                512);
            ContextLines = contextLines;
        }

        public DockerResourceReferenceId Container { get; }

        public int Limit { get; }

        public string? BeforeTimestamp { get; }

        public string? SinceTimestamp { get; }

        public string? SearchText { get; }

        public int ContextLines { get; }

        public DockerLogReadRequest ToSessionRequest() => new(
            Container,
            Limit,
            BeforeTimestamp,
            SinceTimestamp,
            SearchText,
            ContextLines);
    }

    public sealed record FilesList : AgentDockerReadRequest
    {
        public FilesList(
            PanelInstanceId panelId,
            DockerResourceReferenceId resource,
            string path,
            int maximumEntries)
            : base(
                panelId,
                BuiltInAgentTools.DockerFilesList,
                SessionCapabilities.DockerFilesList)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntries, 200);
            Resource = resource;
            Path = RequirePath(path);
            MaximumEntries = maximumEntries;
        }

        public DockerResourceReferenceId Resource { get; }

        public string Path { get; }

        public int MaximumEntries { get; }

        public DockerFileListRequest ToSessionRequest() =>
            new(Resource, Path, MaximumEntries);
    }

    public sealed record FilesStat : AgentDockerReadRequest
    {
        public FilesStat(
            PanelInstanceId panelId,
            DockerResourceReferenceId resource,
            string path)
            : base(
                panelId,
                BuiltInAgentTools.DockerFilesStat,
                SessionCapabilities.DockerFilesStat)
        {
            Resource = resource;
            Path = RequirePath(path);
        }

        public DockerResourceReferenceId Resource { get; }

        public string Path { get; }

        public DockerFileStatRequest ToSessionRequest() => new(Resource, Path);
    }

    public sealed record FileRead : AgentDockerReadRequest
    {
        public FileRead(
            PanelInstanceId panelId,
            DockerResourceReferenceId resource,
            string path,
            int maximumBytes)
            : base(
                panelId,
                BuiltInAgentTools.DockerFileRead,
                SessionCapabilities.DockerFilesRead)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 16 * 1_024);
            Resource = resource;
            Path = RequirePath(path);
            MaximumBytes = maximumBytes;
        }

        public DockerResourceReferenceId Resource { get; }

        public string Path { get; }

        public int MaximumBytes { get; }

        public DockerFileReadRequest ToSessionRequest() =>
            new(Resource, Path, MaximumBytes);
    }

    private static string RequirePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Utf8Length(value, nameof(value)) > 4_096
            || value[0] != '/'
            || value.Contains('\0', StringComparison.Ordinal)
            || value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..")
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "A Docker file observation requires a bounded absolute path without traversal or literal secrets.",
                nameof(value));
        }

        return string.Concat(value);
    }

    private static string? RequireOptionalText(
        string? value,
        string parameterName,
        int maximumBytes)
    {
        if (value is null)
        {
            return null;
        }

        if (Utf8Length(value, parameterName) > maximumBytes
            || value.Any(char.IsControl)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "A Docker observation argument is invalid or contains literal secret material.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static int Utf8Length(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A Docker observation argument is not valid Unicode.",
                parameterName,
                exception);
        }
    }
}
