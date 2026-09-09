using System.Collections.Immutable;
using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const int MaximumAgentFileReferences = 256;
    private static readonly TimeSpan AgentFileReferenceLifetime = TimeSpan.FromMinutes(5);
    private readonly object _agentFileReferenceGate = new();
    private readonly Dictionary<AgentFileEntryReference, AgentFileReferenceRecord>
        _agentFileReferences = [];

    private AgentFileEntryReference IssueAgentFileReference(
        AgentFileDispatch dispatch,
        FilePanelEntry entry)
    {
        var version = entry.Location.Version
            ?? throw new InvalidOperationException(
                "A governed stat reference requires a provider version.");
        var now = _timeProvider.GetUtcNow();
        lock (_agentFileReferenceGate)
        {
            RemoveExpiredAgentFileReferences(now);
            while (_agentFileReferences.Count >= MaximumAgentFileReferences)
            {
                var oldest = _agentFileReferences.MinBy(pair => pair.Value.IssuedAt).Key;
                _agentFileReferences.Remove(oldest);
            }

            AgentFileEntryReference reference;
            do
            {
                var bytes = RandomNumberGenerator.GetBytes(32);
                try
                {
                    reference = new AgentFileEntryReference(
                        Convert.ToBase64String(bytes)
                            .TrimEnd('=')
                            .Replace('+', '-')
                            .Replace('/', '_'));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            while (_agentFileReferences.ContainsKey(reference));

            _agentFileReferences.Add(reference, new AgentFileReferenceRecord(
                dispatch.RunId,
                dispatch.PanelId,
                dispatch.Session.Id,
                dispatch.ExpectedSessionRevision,
                GetAgentFileRelativePath(dispatch.Request),
                entry.Location.WithVersion(version),
                entry.Kind,
                entry.Size,
                now,
                now + AgentFileReferenceLifetime));
            return reference;
        }
    }

    private bool TryConsumeAgentFileReference(
        AgentFileDispatch dispatch,
        out HostError? error) =>
        TryResolveAgentFileReference(dispatch, consume: true, out error);

    private bool TryResolveAgentFileReference(
        AgentFileDispatch dispatch,
        bool consume,
        out HostError? error)
    {
        error = null;
        var reference = dispatch.Request switch
        {
            AgentFileRequest.ReplaceText replace => replace.EntryReference,
            AgentFileRequest.Copy copy => copy.EntryReference,
            AgentFileRequest.Move move => move.EntryReference,
            AgentFileRequest.Delete delete => delete.EntryReference,
            _ => null,
        };
        if (reference is null)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_agentFileReferenceGate)
        {
            RemoveExpiredAgentFileReferences(now);
            if (!_agentFileReferences.TryGetValue(reference.Value, out var record)
                || record.ExpiresAt <= now
                || record.RunId != dispatch.RunId
                || record.PanelId != dispatch.PanelId
                || record.SessionId != dispatch.Session.Id
                || record.SessionRevision != dispatch.ExpectedSessionRevision
                || !record.RelativePath.SequenceEqual(
                    GetAgentFileRelativePath(dispatch.Request))
                || !LocationsMatchIgnoringVersion(record.VersionedLocation, dispatch.Location)
                || record.VersionedLocation.Version is null
                || dispatch.Request is (AgentFileRequest.ReplaceText or AgentFileRequest.Copy)
                    && record.Kind != FilePanelEntryKind.File
                || dispatch.Request is AgentFileRequest.Copy
                    && record.Size is null
                || dispatch.Request is AgentFileRequest.Copy
                    && record.Size is > AgentFileActionComposer.MaximumAgentCopyBytes)
            {
                error = new HostError(
                    HostErrorCode.InvalidRequest,
                    "file_reference_invalid",
                    "The opaque file reference is expired, consumed, or does not match this action.",
                    Retryable: false);
                return false;
            }

            dispatch.VersionedSource = record.VersionedLocation;
            if (consume)
            {
                _agentFileReferences.Remove(reference.Value);
            }

            return true;
        }
    }

    private void RemoveExpiredAgentFileReferences(DateTimeOffset now)
    {
        foreach (var reference in _agentFileReferences
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _agentFileReferences.Remove(reference);
        }
    }

    private sealed record AgentFileReferenceRecord(
        AgentRunId RunId,
        PanelInstanceId PanelId,
        SessionId SessionId,
        long SessionRevision,
        ImmutableArray<FilePanelPathSegment> RelativePath,
        FilePanelLocation VersionedLocation,
        FilePanelEntryKind Kind,
        long? Size,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
}
