using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns the host-side identity linked to one accepted runtime panel. Human
/// presentation remains usable when the hosted adapter is unavailable; agent
/// reachability is admitted only after an exact, active session receipt.
/// </summary>
internal sealed class HostedPanelSessionLink
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ISessionHostClient _sessionClient;
    private readonly ClientId _clientId;
    private readonly SessionOwner _owner;
    private readonly PanelKind _kind;
    private SessionSnapshot? _snapshot;
    private bool _invalidated;
    private bool _disposed;

    public HostedPanelSessionLink(
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner,
        PanelKind kind)
    {
        _sessionClient = sessionClient
            ?? throw new ArgumentNullException(nameof(sessionClient));
        _clientId = clientId;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _kind = kind;
        if (owner.PanelId.Value.Length == 0)
        {
            throw new ArgumentException(
                "A hosted panel owner must identify its panel.",
                nameof(owner));
        }
    }

    public SessionId? SessionId
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot?.Descriptor.Id;
            }
        }
    }

    public CapabilitySet Capabilities
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot?.Descriptor.Capabilities ?? CapabilitySet.Empty;
            }
        }
    }

    public bool IsLinked => SessionId is not null;

    public SessionOwner Owner => _owner;

    public async Task<bool> EnsureAsync(
        Func<SessionId, OperationContext, CancellationToken,
            ValueTask<HostResult<SessionSnapshot>>> ensure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ensure);
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            if (IsDisposed())
            {
                return false;
            }

            if (IsInvalidated() && !await CloseCurrentAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (IsLinked)
            {
                return true;
            }

            var sessionId = Asura.Core.SessionId.New();
            HostResult<SessionSnapshot> result;
            try
            {
                result = await ensure(
                        sessionId,
                        NewContext(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CloseCandidateAsync(sessionId).ConfigureAwait(false);
                return false;
            }
            catch
            {
                // The human panel is a separate presentation path. Transport
                // failures disable agent reachability without surfacing
                // possibly provider-authored text through the panel.
                await CloseCandidateAsync(sessionId).ConfigureAwait(false);
                return false;
            }

            if (result is not HostResult<SessionSnapshot>.Success success
                || success.ResultingRevision != success.Value.Descriptor.Revision
                || !IsValidReceipt(success.Value, sessionId))
            {
                await CloseCandidateAsync(sessionId).ConfigureAwait(false);
                return false;
            }

            lock (_stateGate)
            {
                if (!_disposed && !_invalidated)
                {
                    _snapshot = success.Value;
                    return true;
                }
            }

            await CloseCandidateAsync(sessionId).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task InvalidateAsync()
    {
        lock (_stateGate)
        {
            _invalidated = true;
        }

        return CloseAsync(CancellationToken.None);
    }

    public async Task<bool> CloseAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CloseCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _invalidated = true;
        }

        _ = CloseAsync(CancellationToken.None);
    }

    private async Task<bool> CloseCurrentAsync(CancellationToken cancellationToken)
    {
        SessionSnapshot? current;
        lock (_stateGate)
        {
            current = _snapshot;
            if (current is null)
            {
                _invalidated = false;
                return true;
            }
        }

        HostResult<CloseScopeResult> result;
        try
        {
            result = await _sessionClient.CloseAsync(
                    CloseScopeRequest.Session(
                        current.Descriptor.Id,
                        CloseDecision.Confirm),
                    NewContext(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }

        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            })
        {
            return false;
        }

        var exactTarget = completed.Scope == CloseScopeKind.Session
            && string.Equals(
                completed.TargetId,
                current.Descriptor.Id.Value,
                StringComparison.Ordinal);
        var acceptedOutcome = completed.Sessions.Count == 0
            || completed.Sessions is
            [
            {
                SessionId: var closedSessionId,
                Outcome: SessionCloseOutcome.GracefullyClosed
                        or SessionCloseOutcome.ForceTerminated
                        or SessionCloseOutcome.AlreadyClosed,
            },
            ] && closedSessionId == current.Descriptor.Id;
        if (!exactTarget || !acceptedOutcome)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_snapshot?.Descriptor.Id == current.Descriptor.Id)
            {
                _snapshot = null;
            }

            _invalidated = false;
        }

        return true;
    }

    private async Task CloseCandidateAsync(SessionId sessionId)
    {
        try
        {
            _ = await _sessionClient.CloseAsync(
                    CloseScopeRequest.Session(sessionId, CloseDecision.Confirm),
                    NewContext(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // This is cleanup for a receipt that was never admitted locally;
            // the enclosing graph close remains the final ownership boundary.
        }
    }

    private bool IsValidReceipt(SessionSnapshot snapshot, SessionId sessionId) =>
        snapshot.Descriptor.Id == sessionId
        && snapshot.Descriptor.Owner == _owner
        && snapshot.Descriptor.Kind == _kind
        && snapshot.Descriptor.Lifecycle == SessionLifecycle.Active;

    private bool IsDisposed()
    {
        lock (_stateGate)
        {
            return _disposed;
        }
    }

    private bool IsInvalidated()
    {
        lock (_stateGate)
        {
            return _invalidated;
        }
    }

    private OperationContext NewContext() => OperationContext.ForHuman(
        _clientId,
        idempotencyKey: IdempotencyKey.New());
}
