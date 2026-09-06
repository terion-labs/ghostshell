using System.Text.Json.Serialization;

namespace GhostShell.Infrastructure;

// These records match native/workspace-runtime/Sources/WorkspaceRuntime/Contract.swift.
// The process boundary remains structured; none of these values is a shell command.
internal sealed record WorkspaceSdkConfiguration(
    string Id,
    string ControlSocketPath,
    string RootfsPath,
    string KernelPath,
    string InitfsPath,
    string GatewayExecutablePath,
    int Cpus,
    ulong MemoryBytes,
    string Hostname,
    IReadOnlyList<WorkspaceSdkMount> Mounts,
    IReadOnlyList<string> InitialArguments);

internal sealed record WorkspaceSdkMount(string Source, string Destination, bool ReadOnly);

internal sealed record WorkspaceSdkExecRequest(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    [property: JsonPropertyName("userID")] uint UserId,
    [property: JsonPropertyName("groupID")] uint GroupId,
    bool Terminal = false,
    ushort Columns = 80,
    ushort Rows = 24,
    IReadOnlyList<uint>? SupplementaryGroups = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceSdkConfiguration))]
[JsonSerializable(typeof(WorkspaceSdkExecRequest))]
internal sealed partial class WorkspaceSdkJsonContext : JsonSerializerContext;
