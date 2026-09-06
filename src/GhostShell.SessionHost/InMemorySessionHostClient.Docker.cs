using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<SessionSnapshot>> EnsureDockerSessionAsync(
        EnsureDockerSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var binding = request.Target.Binding;
        var fingerprint = Fingerprint(
            ApplicationOperations.DockerOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            binding.ConnectionId.Value,
            binding.BindingRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            binding.ConnectionKind.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_dockerPanelFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no Docker session factory.",
                0);
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>(0);
        }

        try
        {
            ThrowIfDisposed();
            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.ValidateSessionOwner(
                        request.Owner,
                        PanelKind.Docker)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryReplay(
                    context,
                    fingerprint,
                    0,
                    out HostResult<SessionSnapshot>? inGateReplay))
            {
                return inGateReplay;
            }

            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            if (operationCancellation.Token.IsCancellationRequested)
            {
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }

            if (TryGetSession(request.SessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != request.Owner
                    || existing.Engine is not IDockerPanelSession docker
                    || existing.Engine.Kind != PanelKind.Docker
                    || docker.Binding != binding)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID belongs to another panel or Docker binding."),
                        existingSnapshot.Descriptor.Revision);
                }

                if (existingSnapshot.Descriptor.Lifecycle is
                    SessionLifecycle.Closed or SessionLifecycle.Failed)
                {
                    return ClosedSession<SessionSnapshot>(
                        existingSnapshot.Descriptor.Revision);
                }

                var existingReservation = ReserveReplay<SessionSnapshot>(
                    context,
                    fingerprint,
                    existingSnapshot.Descriptor.Revision,
                    out var existingOutcomeReserved);
                if (existingReservation is not null)
                {
                    return existingReservation;
                }

                if (WorkspaceGraphFailure<SessionSnapshot>(
                        _workspaceGraphs.LinkSession(
                            request.Owner,
                            PanelKind.Docker,
                            request.SessionId)) is { } linkFailure)
                {
                    return existingOutcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(
                            existingSnapshot.Descriptor.Revision)
                        : linkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                CompleteReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            var reservationReplay = ReserveReplay<SessionSnapshot>(
                context,
                fingerprint,
                currentRevision: 0,
                out var outcomeReserved);
            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            IDockerPanelSession? createdEngine = null;
            HostedSession hosted;
            try
            {
                createdEngine = await _dockerPanelFactory
                    .CreateAsync(
                        request.Owner.WorkspaceId,
                        request.SessionId,
                        request.Target,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                if (createdEngine.Id != request.SessionId
                    || createdEngine.Kind != PanelKind.Docker
                    || createdEngine.Binding != binding)
                {
                    await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                    createdEngine = null;
                    var mismatch = HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The Docker engine returned an invalid session."),
                        0);
                    return outcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(0)
                        : mismatch;
                }

                var engineSnapshot = await createdEngine
                    .SnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                hosted = new HostedSession(
                    createdEngine,
                    request.Owner,
                    request.Title,
                    engineSnapshot,
                    _eventRetention,
                    _timeProvider);
                lock (_gate)
                {
                    _sessions.Add(request.SessionId, hosted);
                }

                createdEngine = null;
            }
            catch (OperationCanceledException)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : operationCancellation.DeadlineElapsed
                        ? DeadlineExceeded<SessionSnapshot>(0)
                        : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                var failure = HostResult<SessionSnapshot>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        "docker_open_failed",
                        "The Docker session could not be opened."),
                    0);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : failure;
            }

            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.LinkSession(
                        request.Owner,
                        PanelKind.Docker,
                        request.SessionId)) is { } rejected)
            {
                var removed = await RemoveRejectedSessionAsync(hosted, rejected)
                    .ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : removed;
            }

            var snapshot = hosted.Snapshot();
            var result = HostResult<SessionSnapshot>.Succeed(
                snapshot,
                snapshot.Descriptor.Revision);
            CompleteReplay(context, fingerprint, result);
            return result;
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }
}
