using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(McpServerTransport.Stdio), "stdio")]
[JsonDerivedType(
    typeof(McpServerTransport.StreamableHttp),
    "streamable-http")]
public abstract record McpServerTransport
{
    private McpServerTransport()
    {
    }

    [JsonIgnore]
    public abstract McpServerTransportKind Kind { get; }

    public sealed record Stdio : McpServerTransport
    {
        [JsonConstructor]
        public Stdio(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            IReadOnlyList<McpServerEnvironmentVariable> environment)
        {
            Executable = McpServerProfile.RequireSecretFreeText(
                McpServerProfile.RequireTrimmedText(
                    executable,
                    nameof(executable),
                    McpServerProfile.MaximumExecutableBytes),
                nameof(executable));
            Arguments = McpServerProfile.CopyArguments(arguments);
            WorkingDirectory = workingDirectory is null
                ? null
                : McpServerProfile.RequireSecretFreeText(
                    McpServerProfile.RequireTrimmedText(
                        workingDirectory,
                        nameof(workingDirectory),
                        McpServerProfile.MaximumWorkingDirectoryBytes),
                    nameof(workingDirectory));
            Environment = McpServerProfile.CopyEnvironment(environment);
        }

        public string Executable { get; }

        public IReadOnlyList<string> Arguments { get; }

        public string? WorkingDirectory { get; }

        public IReadOnlyList<McpServerEnvironmentVariable> Environment { get; }

        public override McpServerTransportKind Kind =>
            McpServerTransportKind.Stdio;
    }

    public sealed record StreamableHttp : McpServerTransport
    {
        [JsonConstructor]
        public StreamableHttp(
            Uri endpoint,
            IReadOnlyList<McpServerHttpHeader> headers,
            bool allowInsecureTransport = false)
        {
            Endpoint = ValidateEndpoint(endpoint, allowInsecureTransport);
            Headers = CopyHeaders(headers);
            AllowInsecureTransport = allowInsecureTransport;
        }

        public Uri Endpoint { get; }

        public IReadOnlyList<McpServerHttpHeader> Headers { get; }

        public bool AllowInsecureTransport { get; }

        public override McpServerTransportKind Kind =>
            McpServerTransportKind.StreamableHttp;

        private static Uri ValidateEndpoint(
            Uri endpoint,
            bool allowInsecureTransport)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (!endpoint.IsAbsoluteUri
                || endpoint.IsFile
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || !string.IsNullOrEmpty(endpoint.Fragment)
                || string.IsNullOrWhiteSpace(endpoint.Host)
                || endpoint.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException(
                    "A Streamable HTTP MCP endpoint must be an absolute HTTP(S) URI without user information or a fragment.",
                    nameof(endpoint));
            }

            if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp
, StringComparison.Ordinal) && (!allowInsecureTransport || !IsExactLoopback(endpoint)))
            {
                throw new ArgumentException(
                    "Plaintext Streamable HTTP is allowed only for an exact loopback endpoint with explicit insecure-transport acknowledgement.",
                    nameof(allowInsecureTransport));
            }

            var absoluteUri = McpServerProfile.RequireSecretFreeText(
                McpServerProfile.RequireTrimmedText(
                    endpoint.AbsoluteUri,
                    nameof(endpoint),
                    McpServerProfile.MaximumEndpointBytes),
                nameof(endpoint));
            return new Uri(absoluteUri, UriKind.Absolute);
        }

        private static bool IsExactLoopback(Uri endpoint)
        {
            if (string.Equals(
                    endpoint.Host,
                    "localhost",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return System.Net.IPAddress.TryParse(endpoint.Host, out var address)
                && System.Net.IPAddress.IsLoopback(address);
        }

        private static IReadOnlyList<McpServerHttpHeader> CopyHeaders(
            IReadOnlyList<McpServerHttpHeader> headers)
        {
            ArgumentNullException.ThrowIfNull(headers);
            if (headers.Count > McpServerProfile.MaximumHttpHeaderCount)
            {
                throw new ArgumentException(
                    $"An MCP server cannot define more than {McpServerProfile.MaximumHttpHeaderCount} secret HTTP headers.",
                    nameof(headers));
            }

            var copies = headers
                .Select(header => header ?? throw new ArgumentException(
                    "MCP HTTP headers cannot contain null entries.",
                    nameof(headers)))
                .OrderBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(header => header.Name, StringComparer.Ordinal)
                .ToArray();
            if (copies
                .Select(header => header.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != copies.Length)
            {
                throw new ArgumentException(
                    "MCP HTTP header names must be distinct ignoring case.",
                    nameof(headers));
            }

            return new ReadOnlyCollection<McpServerHttpHeader>(copies);
        }
    }
}

public enum McpServerTransportKind
{
    Stdio,
    StreamableHttp,
}
