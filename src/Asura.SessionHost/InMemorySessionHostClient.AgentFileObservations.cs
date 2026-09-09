using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const int MaximumAgentFileObservationTextBytes = 4 * 1024;

    private static async ValueTask<HostResult<AgentFileActionResult>>
        SearchAgentFilesAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var request = (AgentFileRequest.Search)dispatch.Request;
        var entries = ImmutableArray.CreateBuilder<FilePanelEntry>(
            request.MaximumResults);
        var seen = new HashSet<FilePanelPath>();
        var truncated = false;
        await foreach (var providerResult in dispatch.Files.SearchAsync(
            new FilePanelSearchRequest(
                dispatch.Location,
                request.Query,
                request.Scope,
                showHidden: false),
            cancellationToken).ConfigureAwait(false))
        {
            if (!providerResult.IsSuccess)
            {
                return MapAgentFileProviderFailure(
                    providerResult.Error!,
                    dispatch.Revision);
            }

            if (!TryNormalizeSearchEntry(
                    providerResult.Value!,
                    dispatch.Location,
                    dispatch.Metadata.TrustedRoot,
                    request.Query,
                    request.Scope,
                    out var normalized)
                || normalized!.Location.Address
                    is not FilePanelAddress.Hierarchical address)
            {
                return InvalidAgentFileProviderResult(dispatch.Revision);
            }

            if (!seen.Add(address.Path))
            {
                continue;
            }

            if (entries.Count == request.MaximumResults)
            {
                truncated = true;
                break;
            }

            entries.Add(normalized);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.SearchResults(
                entries.ToImmutable(),
                truncated),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static async ValueTask<HostResult<AgentFileActionResult>>
        ReadAgentFileAccessAsync(
            AgentFileDispatch dispatch,
            CancellationToken cancellationToken)
    {
        var providerResult = await dispatch.Files.GetAccessControlAsync(
                new FilePanelAccessControlRequest(dispatch.Location),
                cancellationToken)
            .ConfigureAwait(false);
        if (!providerResult.IsSuccess)
        {
            return MapAgentFileProviderFailure(
                providerResult.Error!,
                dispatch.Revision);
        }

        var value = providerResult.Value!;
        if (!LocationsMatchIgnoringVersion(value.Location, dispatch.Location)
            || !IsAtOrBelowTrustedRoot(
                value.Location,
                dispatch.Metadata.TrustedRoot)
            || !IsBoundedAgentFileObservationText(value.Owner)
            || !IsBoundedAgentFileObservationText(value.Group))
        {
            return InvalidAgentFileProviderResult(dispatch.Revision);
        }

        var maximum = AgentFileActionComposer.MaximumAgentAccessGrants;
        var grants = new List<FilePanelAccessGrant>(
            Math.Min(value.Grants.Count, maximum));
        var truncated = value.Grants.Count > maximum;
        foreach (var grant in value.Grants.Take(maximum))
        {
            if (grant?.Grantee is null
                || !Enum.IsDefined(grant.Grantee.Kind)
                || (grant.Rights & ~FilePanelAccessRight.FullControl) != FilePanelAccessRight.None || !IsBoundedAgentFileObservationText(grant.Grantee.Id)
                || !IsBoundedAgentFileObservationText(
                    grant.Grantee.DisplayName)
                || (grant.Grantee.Kind == FilePanelGranteeKind.User
                    && string.IsNullOrWhiteSpace(grant.Grantee.Id)))
            {
                return InvalidAgentFileProviderResult(dispatch.Revision);
            }

            grants.Add(new FilePanelAccessGrant(
                new FilePanelGrantee(
                    grant.Grantee.Kind,
                    grant.Grantee.Id,
                    grant.Grantee.DisplayName),
                grant.Rights));
        }

        var normalized = new FilePanelAccessControl(
            dispatch.Location,
            value.Mode,
            value.Owner,
            value.Group,
            grants,
            version: null);
        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.AccessControl(normalized, truncated),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static HostResult<AgentFileActionResult> ReadAgentFileTransfers(
        AgentFileDispatch dispatch)
    {
        var maximum = AgentFileActionComposer.MaximumAgentTransfers;
        var source = dispatch.Files.Transfers;
        var snapshots = ImmutableArray.CreateBuilder<FilePanelTransferSnapshot>(
            Math.Min(source.Count, maximum));
        var truncated = source.Count > maximum;
        foreach (var snapshot in source.Take(maximum))
        {
            if (!TryNormalizeAgentFileTransfer(snapshot, out var normalized))
            {
                return InvalidAgentFileProviderResult(dispatch.Revision);
            }

            snapshots.Add(normalized!);
        }

        return HostResult<AgentFileActionResult>.Succeed(
            new AgentFileActionResult.Transfers(
                snapshots.ToImmutable(),
                truncated),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private static bool TryNormalizeSearchEntry(
        FilePanelEntry entry,
        FilePanelLocation searchRoot,
        FilePanelLocation trustedRoot,
        string query,
        FilePanelDiscoveryScope scope,
        out FilePanelEntry? normalized)
    {
        normalized = null;
        if (entry is null
            || entry.IsHidden
            || entry.Location.Address
                is not FilePanelAddress.Hierarchical entryAddress
            || searchRoot.Address
                is not FilePanelAddress.Hierarchical searchAddress
            || entryAddress.Path.Segments.Length
                <= searchAddress.Path.Segments.Length
            || scope == FilePanelDiscoveryScope.CurrentDirectory
                && entryAddress.Path.Segments.Length
                    != searchAddress.Path.Segments.Length + 1
            || !HasPathPrefix(entryAddress.Path, searchAddress.Path)
            || !IsAtOrBelowTrustedRoot(entry.Location, trustedRoot)
            || !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = entryAddress.Path.Segments[^1].Value;
        if (!string.Equals(entry.Name, name, StringComparison.Ordinal)
            || !IsSafeAgentFileName(name))
        {
            return false;
        }

        normalized = new FilePanelEntry(
            new FilePanelLocation(
                searchRoot.ProviderProfileId,
                searchRoot.Authority,
                new FilePanelAddress.Hierarchical(entryAddress.Path)),
            name,
            entry.Kind,
            entry.Size,
            entry.LastModifiedAt,
            IsHidden: false);
        return true;
    }

    private static bool TryNormalizeAgentFileTransfer(
        FilePanelTransferSnapshot? snapshot,
        out FilePanelTransferSnapshot? normalized)
    {
        normalized = null;
        if (snapshot is null
            || snapshot.Id.Value == Guid.Empty
            || snapshot.Request is null
            || !Enum.IsDefined(snapshot.Request.Operation)
            || !Enum.IsDefined(snapshot.Request.ConflictPolicy)
            || !Enum.IsDefined(snapshot.State)
            || snapshot.BytesTransferred < 0
            || snapshot.TotalBytes is < 0
            || snapshot.TotalBytes is { } total
                && snapshot.BytesTransferred > total
            || !IsBoundedAgentFileObservationText(snapshot.Stage, required: true)
            || !IsBoundedAgentFileLocation(snapshot.Request.Source)
            || !IsBoundedAgentFileLocation(snapshot.Request.Destination)
            || !IsBoundedAgentFileLocation(snapshot.EffectiveDestination)
            || snapshot.Error is { } error
                && (!Enum.IsDefined(error.Code)
                    || !IsBoundedAgentFileObservationText(
                        error.StableCode,
                        required: true)))
        {
            return false;
        }

        var errorCopy = snapshot.Error is null
            ? null
            : new FilePanelError(
                snapshot.Error.Code,
                snapshot.Error.StableCode,
                "The file transfer failed.",
                snapshot.Error.Retryable);
        normalized = new FilePanelTransferSnapshot(
            snapshot.Id,
            new FilePanelTransferRequest(
                WithoutVersion(snapshot.Request.Source),
                WithoutVersion(snapshot.Request.Destination),
                snapshot.Request.Operation,
                snapshot.Request.ConflictPolicy),
            WithoutVersion(snapshot.EffectiveDestination),
            snapshot.State,
            snapshot.Stage,
            snapshot.BytesTransferred,
            snapshot.TotalBytes,
            errorCopy,
            snapshot.QueuedAt,
            snapshot.StartedAt,
            snapshot.CompletedAt)
        {
            CancellationRequested = snapshot.CancellationRequested,
        };
        return true;
    }

    private static bool IsBoundedAgentFileLocation(FilePanelLocation? location)
    {
        if (location is null)
        {
            return false;
        }

        return location.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical =>
                hierarchical.Path.Segments.Length
                    <= AgentFileActionComposer.MaximumRelativePathSegments
                && hierarchical.Path.Segments.All(segment =>
                    IsBoundedAgentFileObservationText(
                        segment.Value,
                        required: true)),
            FilePanelAddress.ObjectKey objectKey =>
                IsBoundedAgentFileObservationText(
                    objectKey.Key,
                    required: true),
            FilePanelAddress.ContainerRoot => true,
            _ => false,
        };
    }

    private static FilePanelLocation WithoutVersion(FilePanelLocation value) =>
        value.Version is null ? value : value.WithVersion(version: null);

    private static bool IsBoundedAgentFileObservationText(
        string? value,
        bool required = false)
    {
        if (value is null)
        {
            return !required;
        }

        if ((required && string.IsNullOrWhiteSpace(value))
            || value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
        {
            return false;
        }

        try
        {
            return StrictAgentFileUtf8.GetByteCount(value)
                <= MaximumAgentFileObservationTextBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
