using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Asura.Core;
using Asura.Docker;

namespace Asura.Application;

public sealed partial class AgentDockerReadActionComposer
{
    private const int MaximumDockerResultBytes = 48 * 1_024;
    private const int MaximumSerializedDockerResultBytes = 64 * 1_024;
    private const int MaximumInspectionProperties = 128;
    private static readonly JsonSerializerOptions DockerProjectionJsonOptions =
        CreateDockerProjectionJsonOptions();
    private static readonly IReadOnlySet<string> AgentSafeInspectionProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Name",
            "Created",
            "CreatedAt",
            "State.Status",
            "State.Running",
            "State.StartedAt",
            "Config.Image",
            "Config.Hostname",
            "Config.WorkingDir",
            "HostConfig.NetworkMode",
            "NetworkSettings.IPAddress",
            "Os",
            "Architecture",
            "Size",
            "Driver",
            "Scope",
            "Internal",
            "IPAM.Config",
        };

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerEngineGeneration engineGeneration,
        DockerPanelSnapshot snapshot)
    {
        var request = RequireRequest<AgentDockerReadRequest.ReadState>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Engine);
        ArgumentNullException.ThrowIfNull(snapshot.Containers);
        ArgumentNullException.ThrowIfNull(snapshot.Images);
        ArgumentNullException.ThrowIfNull(snapshot.Volumes);
        ArgumentNullException.ThrowIfNull(snapshot.Networks);
        if (snapshot.CapturedAtUtc.Offset != TimeSpan.Zero
            || snapshot.Containers.Count > request.MaximumResourcesPerKind
            || snapshot.Images.Count > request.MaximumResourcesPerKind
            || snapshot.Volumes.Count > request.MaximumResourcesPerKind
            || snapshot.Networks.Count > request.MaximumResourcesPerKind)
        {
            throw InvalidResult("Docker state does not match the authorized bounds.");
        }

        var budget = NewBudget(1_024);
        var engine = CopyEngine(snapshot.Engine, ref budget);
        var containers = CopyList(
            snapshot.Containers,
            request.MaximumResourcesPerKind,
            512,
            (DockerContainerItem item, ref int remaining) =>
                CopyContainer(item, ref remaining),
            ref budget);
        var images = CopyList(
            snapshot.Images,
            request.MaximumResourcesPerKind,
            256,
            (DockerImageItem item, ref int remaining) =>
                CopyImage(item, ref remaining),
            ref budget);
        var volumes = CopyList(
            snapshot.Volumes,
            request.MaximumResourcesPerKind,
            224,
            (DockerVolumeItem item, ref int remaining) =>
                CopyVolume(item, ref remaining),
            ref budget);
        var networks = CopyList(
            snapshot.Networks,
            request.MaximumResourcesPerKind,
            224,
            (DockerNetworkItem item, ref int remaining) =>
                CopyNetwork(item, ref remaining),
            ref budget);
        var generation = CopyToken(engineGeneration.Value, 128, ref budget);
        var result = new AgentDockerReadResult.State(new AgentDockerStateSnapshot(
            new DockerEngineGeneration(generation),
            new DockerPanelSnapshot(
                engine,
                containers,
                images,
                volumes,
                networks,
                snapshot.CapturedAtUtc,
                snapshot.IsTruncated)));
        return EnsureDockerResultBound(result);
    }

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerInspectionSnapshot snapshot)
    {
        var request = RequireRequest<AgentDockerReadRequest.Inspect>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Properties);
        if (snapshot.Resource.Reference != request.Reference
            || snapshot.Properties.Count > MaximumInspectionProperties)
        {
            throw InvalidResult("Docker inspection does not match the authorized resource.");
        }

        var budget = NewBudget(512);
        var resource = CopyResource(snapshot.Resource, ref budget);
        var properties = new DockerInspectionProperty[snapshot.Properties.Count];
        for (var index = 0; index < properties.Length; index++)
        {
            Consume(96, ref budget);
            var property = snapshot.Properties[index]
                ?? throw InvalidResult("Docker inspection contains an invalid property.");
            if (!AgentSafeInspectionProperties.Contains(property.Name))
            {
                throw InvalidResult("Docker inspection contains a non-agent-safe property.");
            }

            properties[index] = new DockerInspectionProperty(
                CopyText(property.Name, 256, ref budget),
                CopyText(property.Value, 4_096, ref budget));
        }

        var result = new AgentDockerReadResult.Inspection(
            new DockerInspectionSnapshot(
                resource,
                Array.AsReadOnly(properties),
                snapshot.IsTruncated));
        return EnsureDockerResultBound(result);
    }

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerContainerLogPage page)
    {
        var request = RequireRequest<AgentDockerReadRequest.Logs>(action);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Lines);
        if (page.Lines.Count > request.Limit)
        {
            throw InvalidResult("Docker logs exceed the authorized line bound.");
        }

        var budget = NewBudget(512);
        var lines = new DockerContainerLogLine[page.Lines.Count];
        for (var index = 0; index < lines.Length; index++)
        {
            Consume(128, ref budget);
            var line = page.Lines[index]
                ?? throw InvalidResult("Docker logs contain an invalid line.");
            lines[index] = new DockerContainerLogLine(
                CopyText(line.Timestamp, 128, ref budget),
                CopyText(line.Message, 8_192, ref budget),
                line.StartsContextBlock);
        }

        var result = new AgentDockerReadResult.Logs(new DockerContainerLogPage(
            Array.AsReadOnly(lines),
            page.HasOlder,
            CopyOptionalText(page.OldestTimestamp, 128, ref budget),
            CopyOptionalText(page.NewestTimestamp, 128, ref budget)));
        return EnsureDockerResultBound(result);
    }

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerFilePage page)
    {
        var request = RequireRequest<AgentDockerReadRequest.FilesList>(action);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Entries);
        if (page.Resource.Reference != request.Resource
            || !string.Equals(page.Path, request.Path, StringComparison.Ordinal)
            || page.Entries.Count > request.MaximumEntries)
        {
            throw InvalidResult("Docker file listing does not match the authorized request.");
        }

        var budget = NewBudget(512);
        var resource = CopyResource(page.Resource, ref budget);
        var path = CopyExactPath(page.Path, request.Path, ref budget);
        var entries = new DockerFileEntry[page.Entries.Count];
        for (var index = 0; index < entries.Length; index++)
        {
            Consume(160, ref budget);
            entries[index] = CopyFileEntry(
                page.Entries[index],
                request.Path,
                requireExactPath: false,
                ref budget);
        }

        var result = new AgentDockerReadResult.Files(new DockerFilePage(
            resource,
            path,
            Array.AsReadOnly(entries),
            page.IsTruncated));
        return EnsureDockerResultBound(result);
    }

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerFileEntry entry)
    {
        var request = RequireRequest<AgentDockerReadRequest.FilesStat>(action);
        var budget = NewBudget(256);
        var projected = CopyFileEntry(
            entry,
            request.Path,
            requireExactPath: true,
            ref budget);
        return EnsureDockerResultBound(
            new AgentDockerReadResult.FileStat(projected));
    }

    public AgentDockerReadResult Project(
        AgentDockerReadAction action,
        DockerFileSnapshot snapshot)
    {
        var request = RequireRequest<AgentDockerReadRequest.FileRead>(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Resource.Reference != request.Resource
            || !string.Equals(snapshot.Path, request.Path, StringComparison.Ordinal)
            || snapshot.Content.Length > request.MaximumBytes)
        {
            throw InvalidResult("Docker file content does not match the authorized request.");
        }

        var budget = NewBudget(512);
        var resource = CopyResource(snapshot.Resource, ref budget);
        var path = CopyExactPath(snapshot.Path, request.Path, ref budget);
        string text;
        try
        {
            text = StrictUtf8.GetString(snapshot.Content.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "Docker file content is not strict UTF-8 text.",
                nameof(snapshot),
                exception);
        }

        if (text.Any(character =>
                char.IsControl(character)
                && character is not '\n' and not '\r' and not '\t'))
        {
            throw InvalidResult("Docker file content is not bounded text.");
        }

        var copiedText = CopyText(text, request.MaximumBytes, ref budget);
        var result = new AgentDockerReadResult.FileText(
            new AgentDockerTextFileSnapshot(
                resource,
                path,
                copiedText,
                snapshot.IsTruncated));
        return EnsureDockerResultBound(result);
    }

    private static TRequest RequireRequest<TRequest>(AgentDockerReadAction action)
        where TRequest : AgentDockerReadRequest
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidatePreparedAction(action);
        return action.Request as TRequest
            ?? throw new ArgumentException(
                "The Docker result does not match the prepared operation.",
                nameof(action));
    }

    private static DockerEngineSummary CopyEngine(
        DockerEngineSummary engine,
        ref int budget)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Consume(192, ref budget);
        return new DockerEngineSummary(
            CopyText(engine.Version, 256, ref budget),
            CopyText(engine.OperatingSystem, 256, ref budget),
            CopyText(engine.Architecture, 128, ref budget),
            CopyText(engine.ApiVersion, 128, ref budget));
    }

    private static DockerContainerItem CopyContainer(
        DockerContainerItem item,
        ref int budget)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DockerContainerItem(
            CopyResource(item.Resource, ref budget),
            CopyText(item.Image, 1_024, ref budget),
            CopyText(item.State, 64, ref budget),
            CopyText(item.Status, 1_024, ref budget),
            CopyText(item.Ports, 2_048, ref budget),
            CopyText(item.Created, 256, ref budget),
            CopyText(item.Cpu, 64, ref budget),
            CopyText(item.Memory, 128, ref budget),
            CopyText(item.NetworkIo, 128, ref budget),
            CopyText(item.BlockIo, 128, ref budget),
            CopyOptionalText(item.ComposeProject, 256, ref budget),
            CopyOptionalText(item.ComposeService, 256, ref budget),
            item.ControlRevision is { } revision
                ? new DockerContainerRevision(CopyToken(
                    revision.Value,
                    128,
                    ref budget))
                : null);
    }

    private static DockerImageItem CopyImage(DockerImageItem item, ref int budget)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DockerImageItem(
            CopyResource(item.Resource, ref budget),
            CopyText(item.Repository, 768, ref budget),
            CopyText(item.Tag, 256, ref budget),
            CopyText(item.Size, 128, ref budget),
            CopyText(item.Created, 256, ref budget));
    }

    private static DockerVolumeItem CopyVolume(DockerVolumeItem item, ref int budget)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SizeBytes is < 0)
        {
            throw InvalidResult("Docker volume size is invalid.");
        }

        return new DockerVolumeItem(
            CopyResource(item.Resource, ref budget),
            CopyText(item.Driver, 256, ref budget),
            CopyText(item.Scope, 128, ref budget),
            CopyText(item.Size, 128, ref budget),
            item.SizeBytes);
    }

    private static DockerNetworkItem CopyNetwork(DockerNetworkItem item, ref int budget)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DockerNetworkItem(
            CopyResource(item.Resource, ref budget),
            CopyText(item.Driver, 256, ref budget),
            CopyText(item.Scope, 128, ref budget),
            CopyText(item.Created, 256, ref budget));
    }

    private static DockerResourceItem CopyResource(
        DockerResourceItem resource,
        ref int budget)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!Enum.IsDefined(resource.Kind))
        {
            throw InvalidResult("Docker resource kind is invalid.");
        }

        Consume(160, ref budget);
        return new DockerResourceItem(
            new DockerResourceReferenceId(CopyToken(
                resource.Reference.Value,
                128,
                ref budget)),
            resource.Kind,
            CopyText(resource.DisplayName, 1_024, ref budget));
    }

    private static DockerFileEntry CopyFileEntry(
        DockerFileEntry entry,
        string expectedPath,
        bool requireExactPath,
        ref int budget)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enum.IsDefined(entry.Kind)
            || entry.Size is < 0
            || entry.ModifiedAt is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw InvalidResult("Docker file metadata is invalid.");
        }

        var name = CopyText(entry.Name, 1_024, ref budget);
        if (string.IsNullOrEmpty(name)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\0', StringComparison.Ordinal))
        {
            throw InvalidResult("Docker file name is invalid.");
        }

        var path = CopyText(entry.Path, 4_096, ref budget);
        var exact = requireExactPath
            ? expectedPath
            : string.Equals(expectedPath, "/"
, StringComparison.Ordinal) ? $"/{name}"
                : $"{expectedPath.TrimEnd('/')}/{name}";
        if (!string.Equals(path, exact, StringComparison.Ordinal))
        {
            throw InvalidResult("Docker returned a file outside the authorized path.");
        }

        return new DockerFileEntry(name, path, entry.Kind, entry.Size, entry.ModifiedAt);
    }

    private static string CopyExactPath(
        string value,
        string expected,
        ref int budget)
    {
        var copy = CopyText(value, 4_096, ref budget);
        if (!string.Equals(copy, expected, StringComparison.Ordinal))
        {
            throw InvalidResult("Docker returned a different path.");
        }

        return copy;
    }

    private static string CopyToken(string value, int maximumBytes, ref int budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw InvalidResult("Docker returned an invalid opaque token.");
        }

        return CopyText(value, maximumBytes, ref budget);
    }

    private static string CopyText(string? value, int maximumBytes, ref int budget)
    {
        ArgumentNullException.ThrowIfNull(value);
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Docker returned invalid Unicode text.",
                nameof(value),
                exception);
        }

        if (bytes > maximumBytes)
        {
            throw InvalidResult("Docker returned an oversized text field.");
        }

        Consume(bytes, ref budget);
        return string.Concat(value);
    }

    private static string? CopyOptionalText(
        string? value,
        int maximumBytes,
        ref int budget) =>
        value is null ? null : CopyText(value, maximumBytes, ref budget);

    private static IReadOnlyList<TOutput> CopyList<TInput, TOutput>(
        IReadOnlyList<TInput> source,
        int maximumCount,
        int structureBytes,
        RefProjector<TInput, TOutput> projector,
        ref int budget)
    {
        if (source.Count > maximumCount)
        {
            throw InvalidResult("Docker returned too many resources.");
        }

        var copy = new TOutput[source.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            Consume(structureBytes, ref budget);
            copy[index] = projector(source[index], ref budget);
        }

        return Array.AsReadOnly(copy);
    }

    private static int NewBudget(int structureBytes)
    {
        var budget = MaximumDockerResultBytes;
        Consume(structureBytes, ref budget);
        return budget;
    }

    private static void Consume(int bytes, ref int budget)
    {
        if (bytes < 0 || bytes > budget)
        {
            throw InvalidResult("Docker result exceeds its total byte budget.");
        }

        budget -= bytes;
    }

    private static T EnsureDockerResultBound<T>(T result)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)DockerProjectionJsonOptions.GetTypeInfo(typeof(T));
            if (JsonSerializer.SerializeToUtf8Bytes(
                    result,
                    typeInfo).Length
                > MaximumSerializedDockerResultBytes)
            {
                throw InvalidResult("Docker result exceeds its serialized byte bound.");
            }
        }
        catch (Exception exception) when (exception is
            JsonException or NotSupportedException)
        {
            throw new ArgumentException(
                "Docker result cannot be serialized safely.",
                nameof(result),
                exception);
        }

        return result;
    }

    private static JsonSerializerOptions CreateDockerProjectionJsonOptions()
    {
        var resolver = JsonTypeInfoResolver.WithAddedModifier(
            AgentProjectionJsonContext.Default,
            static typeInfo =>
            {
                for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(typeInfo.Properties[index].Name
    , nameof(DockerContainerLogLine.RawText), StringComparison.Ordinal))
                    {
                        typeInfo.Properties.RemoveAt(index);
                    }
                }
            });
        return new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = resolver,
        };
    }

    private static ArgumentException InvalidResult(string message) => new(message);

    private delegate TOutput RefProjector<TInput, TOutput>(
        TInput value,
        ref int budget);
}
