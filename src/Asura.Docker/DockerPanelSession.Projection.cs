using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Asura.Application;

namespace Asura.Docker;

internal sealed partial class DockerPanelSession
{
    private const int MaximumInspectionProperties = 128;
    private const int MaximumMetadataBytes = 4_096;
    private const int MaximumLogMessageBytes = 8_192;
    private const int MaximumProjectedPayloadBytes = 48 * 1_024;
    private const int MaximumProjectedFileBytes = 32 * 1_024;
    private const int MaximumSerializedResultBytes = 64 * 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions ProjectionJsonOptions =
        CreateProjectionJsonOptions();
    private static readonly IReadOnlySet<string> SafeInspectionProperties =
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

    private DockerPanelSnapshot ProjectSnapshot(
        DockerEngineSnapshot snapshot,
        int maximumResourcesPerKind)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Engine);
        ArgumentNullException.ThrowIfNull(snapshot.Containers);
        ArgumentNullException.ThrowIfNull(snapshot.Images);
        ArgumentNullException.ThrowIfNull(snapshot.Volumes);
        ArgumentNullException.ThrowIfNull(snapshot.Networks);
        if (snapshot.CapturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A Docker capture timestamp must be UTC.");
        }

        var engine = ProjectEngine(snapshot.Engine);
        var remainingBytes = MaximumProjectedPayloadBytes
            - 1_024
            - EngineCost(engine);
        var truncated = false;
        var containers = new List<DockerContainerItem>();
        foreach (var source in snapshot.Containers.Take(maximumResourcesPerKind))
        {
            var item = ProjectContainer(source);
            if (!Consume(ItemCost(item), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            containers.Add(item);
        }

        var images = new List<DockerImageItem>();
        foreach (var source in snapshot.Images.Take(maximumResourcesPerKind))
        {
            var item = ProjectImage(source);
            if (!Consume(ItemCost(item), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            images.Add(item);
        }

        var volumes = new List<DockerVolumeItem>();
        foreach (var source in snapshot.Volumes.Take(maximumResourcesPerKind))
        {
            var item = ProjectVolume(source);
            if (!Consume(ItemCost(item), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            volumes.Add(item);
        }

        var networks = new List<DockerNetworkItem>();
        foreach (var source in snapshot.Networks.Take(maximumResourcesPerKind))
        {
            var item = ProjectNetwork(source);
            if (!Consume(ItemCost(item), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            networks.Add(item);
        }

        truncated |= snapshot.Containers.Count > containers.Count
            || snapshot.Images.Count > images.Count
            || snapshot.Volumes.Count > volumes.Count
            || snapshot.Networks.Count > networks.Count;
        var result = new DockerPanelSnapshot(
            engine,
            Array.AsReadOnly(containers.ToArray()),
            Array.AsReadOnly(images.ToArray()),
            Array.AsReadOnly(volumes.ToArray()),
            Array.AsReadOnly(networks.ToArray()),
            snapshot.CapturedAtUtc,
            truncated);
        EnsureSerializedBound(result);
        return result;
    }

    private DockerContainerItem ProjectContainer(DockerContainerSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var resource = ProjectResource(
            new DockerResourceReference(
                DockerResourceKind.Container,
                RequireIdentity(value.Id),
                BoundedText(value.Name, 256, "Container")));
        return new DockerContainerItem(
            resource,
            BoundedText(value.Image, 1_024),
            BoundedText(value.State, 64),
            BoundedText(value.Status, 1_024),
            BoundedText(value.Ports, 2_048),
            BoundedText(value.Created, 256),
            BoundedText(value.Cpu, 64),
            BoundedText(value.Memory, 128),
            BoundedText(value.NetworkIo, 128),
            BoundedText(value.BlockIo, 128),
            OptionalBoundedText(value.ComposeProject, 256),
            OptionalBoundedText(value.ComposeService, 256),
            _containerRevisions.Mint(State.EngineGeneration, value));
    }

    private DockerImageItem ProjectImage(DockerImageSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var displayName = string.IsNullOrWhiteSpace(value.Repository)
            ? value.Id
            : $"{value.Repository}:{value.Tag}";
        var resource = ProjectResource(new DockerResourceReference(
            DockerResourceKind.Image,
            RequireIdentity(value.Id),
            BoundedText(displayName, 1_024, "Image")));
        return new DockerImageItem(
            resource,
            BoundedText(value.Repository, 768),
            BoundedText(value.Tag, 256),
            BoundedText(value.Size, 128),
            BoundedText(value.Created, 256));
    }

    private DockerVolumeItem ProjectVolume(DockerVolumeSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SizeBytes is < 0)
        {
            throw new InvalidDataException("Docker returned invalid volume size metadata.");
        }

        var name = RequireIdentity(value.Name);
        var resource = ProjectResource(new DockerResourceReference(
            DockerResourceKind.Volume,
            name,
            BoundedText(value.Name, 256, "Volume")));
        return new DockerVolumeItem(
            resource,
            BoundedText(value.Driver, 256),
            BoundedText(value.Scope, 128),
            BoundedText(value.Size, 128),
            value.SizeBytes);
    }

    private DockerNetworkItem ProjectNetwork(DockerNetworkSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var resource = ProjectResource(new DockerResourceReference(
            DockerResourceKind.Network,
            RequireIdentity(value.Id),
            BoundedText(value.Name, 256, "Network")));
        return new DockerNetworkItem(
            resource,
            BoundedText(value.Driver, 256),
            BoundedText(value.Scope, 128),
            BoundedText(value.Created, 256));
    }

    private DockerInspectionSnapshot ProjectInspection(
        DockerResourceReference expected,
        DockerResourceInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(inspection.Resource);
        ArgumentNullException.ThrowIfNull(inspection.Properties);
        if (inspection.Resource.Kind != expected.Kind
            || !string.Equals(
                inspection.Resource.Id,
                expected.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Docker inspect returned a different resource.");
        }

        // Raw docker-inspect JSON can contain environment secrets and is never
        // admitted to this read-only hosted contract. Only an explicit safe
        // property allowlist crosses the boundary.
        var resource = ProjectResource(expected);
        var remainingBytes = MaximumProjectedPayloadBytes
            - ResourceCost(resource)
            - 512;
        var safe = new List<DockerInspectionProperty>();
        var truncated = false;
        foreach (var property in inspection.Properties)
        {
            ArgumentNullException.ThrowIfNull(property);
            if (!SafeInspectionProperties.Contains(property.Name))
            {
                truncated = true;
                continue;
            }

            if (safe.Count >= MaximumInspectionProperties)
            {
                truncated = true;
                break;
            }

            var projected = new DockerInspectionProperty(
                BoundedText(property.Name, 256),
                BoundedText(property.Value, MaximumMetadataBytes));
            if (!Consume(InspectionPropertyCost(projected), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            safe.Add(projected);
        }

        var result = new DockerInspectionSnapshot(
            resource,
            Array.AsReadOnly(safe.ToArray()),
            truncated);
        EnsureSerializedBound(result);
        return result;
    }

    private static DockerContainerLogPage ProjectLogs(
        DockerContainerLogPage page,
        int maximumLines)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Lines);
        if (page.Lines.Count > maximumLines)
        {
            throw new InvalidDataException(
                "Docker returned more log rows than requested.");
        }

        var oldestTimestamp = OptionalBoundedText(page.OldestTimestamp, 128);
        var newestTimestamp = OptionalBoundedText(page.NewestTimestamp, 128);
        var remainingBytes = MaximumProjectedPayloadBytes
            - 512
            - Utf8Length(oldestTimestamp)
            - Utf8Length(newestTimestamp);
        var clipped = false;
        var lines = new List<DockerContainerLogLine>();
        foreach (var line in page.Lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (!Consume(128, ref remainingBytes))
            {
                clipped = true;
                break;
            }

            var timestamp = BoundedText(line.Timestamp, 128);
            if (!Consume(Utf8Length(timestamp), ref remainingBytes))
            {
                clipped = true;
                break;
            }

            var message = CopyBudgetedText(
                line.Message,
                MaximumLogMessageBytes,
                ref remainingBytes,
                ref clipped);
            lines.Add(new DockerContainerLogLine(
                timestamp,
                message,
                line.StartsContextBlock));
        }

        var result = new DockerContainerLogPage(
            Array.AsReadOnly(lines.ToArray()),
            page.HasOlder || clipped || lines.Count < page.Lines.Count,
            oldestTimestamp,
            newestTimestamp);
        EnsureSerializedBound(result);
        return result;
    }

    private DockerFilePage ProjectFilePage(
        DockerResourceReference expected,
        string expectedPath,
        DockerFileListing listing,
        int maximumEntries)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ValidateReturnedResource(expected, listing.Resource);
        ValidateExactReturnedPath(expectedPath, listing.Path);
        ArgumentNullException.ThrowIfNull(listing.Entries);
        var resource = ProjectResource(expected);
        var path = BoundedText(listing.Path, MaximumMetadataBytes);
        var remainingBytes = MaximumProjectedPayloadBytes
            - ResourceCost(resource)
            - Utf8Length(path)
            - 512;
        var entries = new List<DockerFileEntry>();
        var truncated = false;
        foreach (var source in listing.Entries.Take(maximumEntries))
        {
            var entry = ProjectFileEntry(
                source,
                expectedPath,
                requireExactPath: false);
            if (!Consume(FileEntryCost(entry), ref remainingBytes))
            {
                truncated = true;
                break;
            }

            entries.Add(entry);
        }

        truncated |= listing.Entries.Count > entries.Count;
        var result = new DockerFilePage(
            resource,
            path,
            Array.AsReadOnly(entries.ToArray()),
            truncated);
        EnsureSerializedBound(result);
        return result;
    }

    private static DockerFileEntry ProjectFileEntry(
        DockerFileEntry entry,
        string expectedPath,
        bool requireExactPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateReturnedPath(entry.Path);
        if (string.IsNullOrEmpty(entry.Name)
            || Utf8Length(entry.Name) > 1_024
            || entry.Name.Contains('\0')
            || entry.Name.Contains('/'))
        {
            throw new InvalidDataException("Docker returned an invalid file name.");
        }

        if (!Enum.IsDefined(entry.Kind)
            || entry.Size is < 0
            || entry.ModifiedAt is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Docker returned invalid file metadata.");
        }

        if (requireExactPath)
        {
            ValidateExactReturnedPath(expectedPath, entry.Path);
        }
        else
        {
            ValidateReturnedChildPath(expectedPath, entry.Name, entry.Path);
        }

        return new DockerFileEntry(
            BoundedText(entry.Name, 1_024),
            BoundedText(entry.Path, MaximumMetadataBytes),
            entry.Kind,
            entry.Size,
            entry.ModifiedAt);
    }

    private static DockerFileEntry ProjectFileStat(
        DockerFileEntry entry,
        string expectedPath)
    {
        var result = ProjectFileEntry(entry, expectedPath, requireExactPath: true);
        EnsureSerializedBound(result);
        return result;
    }

    private DockerFileSnapshot ProjectFileContent(
        DockerResourceReference expected,
        string expectedPath,
        DockerFileContent content,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateReturnedResource(expected, content.Resource);
        ValidateExactReturnedPath(expectedPath, content.Path);
        if (content.Content.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "Docker returned more file bytes than requested.");
        }

        var resource = ProjectResource(expected);
        var path = BoundedText(content.Path, MaximumMetadataBytes);
        var allowedBytes = Math.Min(
            Math.Min(maximumBytes, MaximumProjectedFileBytes),
            Math.Max(
                MaximumProjectedPayloadBytes
                - ResourceCost(resource)
                - Utf8Length(path)
                - 512,
                0));
        var clipped = content.Content.Length > allowedBytes;
        var result = new DockerFileSnapshot(
            resource,
            path,
            content.Content[..Math.Min(content.Content.Length, allowedBytes)].ToArray(),
            content.IsTruncated || clipped);
        EnsureSerializedBound(result);
        return result;
    }

    private DockerResourceItem ProjectResource(DockerResourceReference resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var identity = RequireIdentity(resource.Id);
        var normalized = new DockerResourceReference(
            resource.Kind,
            identity,
            BoundedText(resource.DisplayName, 1_024, resource.Kind.ToString()));
        return new DockerResourceItem(
            _resources.Lease(normalized),
            normalized.Kind,
            normalized.DisplayName);
    }

    private static DockerEngineSummary ProjectEngine(DockerEngineSummary engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var result = new DockerEngineSummary(
            BoundedText(engine.Version, 256),
            BoundedText(engine.OperatingSystem, 256),
            BoundedText(engine.Architecture, 128),
            BoundedText(engine.ApiVersion, 128));
        EnsureSerializedBound(result);
        return result;
    }

    private static void ValidateReturnedResource(
        DockerResourceReference expected,
        DockerResourceReference actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (actual.Kind != expected.Kind
            || !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Docker returned a different resource than requested.");
        }
    }

    private static void ValidateReturnedPath(string path)
    {
        ValidatePath(path);
    }

    private static void ValidateExactReturnedPath(
        string expectedPath,
        string actualPath)
    {
        ValidateReturnedPath(actualPath);
        if (!string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Docker returned a different path than requested.");
        }
    }

    private static void ValidateReturnedChildPath(
        string parentPath,
        string name,
        string actualPath)
    {
        ValidateReturnedPath(actualPath);
        var expectedPath = string.Equals(parentPath, "/"
, StringComparison.Ordinal) ? $"/{name}"
            : $"{parentPath.TrimEnd('/')}/{name}";
        if (!string.Equals(expectedPath, actualPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Docker returned a file outside the requested directory.");
        }
    }

    private static string RequireIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Utf8Length(value) > MaximumMetadataBytes || value.Contains('\0'))
        {
            throw new InvalidDataException("Docker returned an invalid resource identity.");
        }

        return value;
    }

    private static string BoundedText(
        string? value,
        int maximumLength,
        string fallback = "")
    {
        var text = value ?? fallback;
        if (Utf8Length(text) > maximumLength)
        {
            throw new InvalidDataException("Docker returned an oversized text field.");
        }

        return text;
    }

    private static string? OptionalBoundedText(
        string? value,
        int maximumLength) =>
        value is null ? null : BoundedText(value, maximumLength);

    private static string CopyBudgetedText(
        string value,
        int maximumFieldBytes,
        ref int remainingBytes,
        ref bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = Utf8Length(value);
        var maximumBytes = Math.Min(maximumFieldBytes, Math.Max(remainingBytes, 0));
        if (byteCount <= maximumBytes)
        {
            remainingBytes -= byteCount;
            return string.Concat(value);
        }

        truncated = true;
        var builder = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var copiedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (copiedBytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            copiedBytes += rune.Utf8SequenceLength;
        }

        remainingBytes -= copiedBytes;
        return builder.ToString();
    }

    private static bool Consume(int bytes, ref int remainingBytes)
    {
        if (bytes < 0 || bytes > remainingBytes)
        {
            return false;
        }

        remainingBytes -= bytes;
        return true;
    }

    private static int Utf8Length(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Docker returned invalid Unicode text.",
                exception);
        }
    }

    private static int EngineCost(DockerEngineSummary value) =>
        192
        + Utf8Length(value.Version)
        + Utf8Length(value.OperatingSystem)
        + Utf8Length(value.Architecture)
        + Utf8Length(value.ApiVersion);

    private static int ResourceCost(DockerResourceItem value) =>
        160
        + Utf8Length(value.Reference.Value)
        + Utf8Length(value.DisplayName);

    private static int ItemCost(DockerContainerItem value) =>
        512
        + ResourceCost(value.Resource)
        + Utf8Length(value.Image)
        + Utf8Length(value.State)
        + Utf8Length(value.Status)
        + Utf8Length(value.Ports)
        + Utf8Length(value.Created)
        + Utf8Length(value.Cpu)
        + Utf8Length(value.Memory)
        + Utf8Length(value.NetworkIo)
        + Utf8Length(value.BlockIo)
        + Utf8Length(value.ComposeProject)
        + Utf8Length(value.ComposeService)
        + Utf8Length(value.ControlRevision?.Value);

    private static int ItemCost(DockerImageItem value) =>
        256
        + ResourceCost(value.Resource)
        + Utf8Length(value.Repository)
        + Utf8Length(value.Tag)
        + Utf8Length(value.Size)
        + Utf8Length(value.Created);

    private static int ItemCost(DockerVolumeItem value) =>
        224
        + ResourceCost(value.Resource)
        + Utf8Length(value.Driver)
        + Utf8Length(value.Scope)
        + Utf8Length(value.Size);

    private static int ItemCost(DockerNetworkItem value) =>
        224
        + ResourceCost(value.Resource)
        + Utf8Length(value.Driver)
        + Utf8Length(value.Scope)
        + Utf8Length(value.Created);

    private static int InspectionPropertyCost(DockerInspectionProperty value) =>
        96 + Utf8Length(value.Name) + Utf8Length(value.Value);

    private static int FileEntryCost(DockerFileEntry value) =>
        160 + Utf8Length(value.Name) + Utf8Length(value.Path);

    private static void EnsureSerializedBound<T>(T value)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<T>)ProjectionJsonOptions.GetTypeInfo(typeof(T));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                value,
                typeInfo);
            if (bytes.Length > MaximumSerializedResultBytes)
            {
                throw new InvalidDataException(
                    "The Docker result exceeds its serialized byte bound.");
            }
        }
        catch (Exception exception) when (exception is
            JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                "The Docker result cannot be serialized safely.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateProjectionJsonOptions()
    {
        var resolver = JsonTypeInfoResolver.WithAddedModifier(
            DockerProjectionJsonContext.Default,
            static typeInfo =>
            {
                for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
                {
                    if (typeInfo.Properties[index].Name is
                        nameof(DockerContainerSummary.IsRunning)
                        or nameof(DockerContainerSummary.IsPaused)
                        or nameof(DockerContainerSummary.IsStandalone)
                        or nameof(DockerContainerSummary.StackName)
                        or nameof(DockerContainerLogLine.RawText))
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
}
