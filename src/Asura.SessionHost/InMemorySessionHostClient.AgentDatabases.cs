using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentDatabaseReadResult>>
        RunAgentDatabaseReadAsync(
            AgentAuthorizationId authorizationId,
            AgentDatabaseReadAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentDatabaseReadActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentDatabaseReadResult>(
                "The governed database execution bridge is not composed.",
                0);
        }

        AgentDatabaseDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentDatabaseReadResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentDatabaseReadResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentDatabaseReadResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactPanel = exactContext.Panels.SingleOrDefault(
                panel => panel.PanelId == action.Request.PanelId);
            if (exactPanel?.SessionId is not { } sessionId
                || exactPanel.SessionRevision is not long expectedSessionRevision)
            {
                return InvalidAgentDatabaseAction(
                    "The exact Database Viewer context has no matching live session.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentDatabaseReadResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentDatabaseReadActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentDatabaseDispatch(
                    action.Request,
                    session,
                    expectedSessionRevision,
                    exactPanel.WorkspaceRevision,
                    exactPanel.GraphSequence,
                    revision,
                    binding);
            }
            catch (AgentDatabaseDispatchException exception)
            {
                return HostResult<AgentDatabaseReadResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                return InvalidAgentDatabaseAction(
                    "The prepared database action no longer matches its exact typed request.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(authorizationId, binding, cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapAgentDatabaseAuthorizationFailure(denied.Error, revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateAgentDatabaseDispatch(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentDatabaseReadResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledAgentDatabaseAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentDatabaseReadResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledAgentDatabaseAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentDatabaseReadResult>.Fail(
                new HostError(
                    HostErrorCode.EngineFailed,
                    "database_authorization_unavailable",
                    "The database authorization broker is unavailable.",
                    Retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = AgentDatabaseFailure(
                "database_read_failed",
                revision,
                retryable: true);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentDatabaseActionAsync(
                    permit!,
                    preDispatchFailure)
                .ConfigureAwait(false);
        }

        return await CaptureAndCompleteAgentDatabaseReadAsync(
                action,
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<AgentDatabaseReadResult>>
        CaptureAndCompleteAgentDatabaseReadAsync(
            AgentDatabaseReadAction action,
            AgentDatabaseDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        HostResult<AgentDatabaseReadResult>? result = null;
        object? captured = null;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    callerCancellation);
            if (operationCancellation.IsCancellationRequested)
            {
                result = CancelledAgentDatabaseAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
            }
            else
            {
                captured = await ExecuteAgentDatabaseReadAsync(
                        dispatch,
                        action.Request,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentDatabaseAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (ObjectDisposedException)
        {
            result = CancelledAgentDatabaseAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (KeyNotFoundException)
        {
            result = AgentDatabaseFailure(
                "database_reference_expired",
                dispatch.InitialRevision,
                HostErrorCode.InvalidRequest);
        }
        catch (NotSupportedException)
        {
            result = AgentDatabaseFailure(
                "database_operation_unavailable",
                dispatch.InitialRevision,
                HostErrorCode.CapabilityNotSupported);
        }
        catch (ArgumentException)
        {
            result = AgentDatabaseFailure(
                "database_read_rejected",
                dispatch.InitialRevision,
                HostErrorCode.InvalidRequest);
        }
        catch (InvalidDataException)
        {
            result = AgentDatabaseFailure(
                "database_result_invalid",
                dispatch.InitialRevision);
        }
        catch (Exception)
        {
            result = AgentDatabaseFailure(
                "database_read_failed",
                dispatch.InitialRevision,
                retryable: true);
        }

        if (result is null)
        {
            await _sessionGraphGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var driftFailure = RevalidateAgentDatabaseDispatch(
                    action,
                    dispatch,
                    permit,
                    dispatch.Binding,
                    callerCancellation,
                    out var currentRevision);
                if (driftFailure is not null)
                {
                    result = driftFailure;
                }
                else
                {
                    try
                    {
                        var projection = ProjectAgentDatabaseResult(
                            action,
                            captured!);
                        result = permit.CancellationToken.IsCancellationRequested
                            || dispatch.RuntimeCancellation.IsCancellationRequested
                            || callerCancellation.IsCancellationRequested
                                ? CancelledAgentDatabaseAction(
                                    permit,
                                    dispatch.RuntimeCancellation,
                                    callerCancellation,
                                    currentRevision)
                                : HostResult<AgentDatabaseReadResult>.Succeed(
                                    projection,
                                    currentRevision);
                    }
                    catch (Exception exception) when (exception is
                        ArgumentException
                        or InvalidOperationException
                        or OverflowException)
                    {
                        result = AgentDatabaseFailure(
                            "database_result_invalid",
                            currentRevision);
                    }
                }
            }
            finally
            {
                _sessionGraphGate.Release();
            }
        }

        return await CompleteAgentDatabaseActionAsync(permit, result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentDatabaseReadResult>?
        RevalidateAgentDatabaseDispatch(
            AgentDatabaseReadAction action,
            AgentDatabaseDispatch dispatch,
            AgentActionPermit permit,
            AgentActionExecutionBinding consumedBinding,
            CancellationToken callerCancellation,
            out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!HasAgentDatabaseAuthorization(permit.Authorization, action.Request))
        {
            return InvalidAgentDatabaseAction(
                "The consumed authorization does not grant this database observation.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentDatabaseAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                revision);
        }

        var contextResult = ResolveExactAgentContext(action.Proposal.Target);
        if (contextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
        {
            return HostResult<AgentDatabaseReadResult>.Fail(
                contextFailure.Error,
                contextFailure.CurrentRevision);
        }

        var context = ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
        revision = context.Revision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentDatabaseReadActionComposer!
                .BindForExecution(action, context);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return InvalidAgentDatabaseAction(
                "The exact Database Viewer changed during authorization or capture.",
                revision);
        }

        if (!AgentDatabaseBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(permit.Authorization, currentBinding))
        {
            return InvalidAgentDatabaseAction(
                "The exact database execution binding changed before projection.",
                revision);
        }

        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == action.Request.PanelId
            && candidate.SessionId == dispatch.Session.Id);
        if (panel?.SessionRevision != dispatch.ExpectedSessionRevision
            || panel.WorkspaceRevision != dispatch.ExpectedWorkspaceRevision
            || panel.GraphSequence != dispatch.ExpectedGraphSequence
            || panel.Kind != PanelKind.DatabaseViewer
            || !panel.Capabilities.Contains(
                action.Request.RequiredSessionCapability,
                StringComparer.Ordinal))
        {
            return InvalidAgentDatabaseAction(
                "The exact hosted database session changed before projection.",
                revision);
        }

        if (!TryGetSession(dispatch.Session.Id, out var currentSession)
            || !ReferenceEquals(currentSession, dispatch.Session)
            || !dispatch.Session.CanExecuteAgentDatabaseRead(
                dispatch.Database,
                dispatch.ExpectedSessionRevision,
                action.Request.RequiredSessionCapability,
                dispatch.RuntimeCancellation))
        {
            return InvalidAgentDatabaseAction(
                "The exact hosted database authority changed before projection.",
                revision);
        }

        return null;
    }

    private static AgentDatabaseDispatch CaptureAgentDatabaseDispatch(
        AgentDatabaseReadRequest request,
        HostedSession session,
        long expectedSessionRevision,
        long expectedWorkspaceRevision,
        long expectedGraphSequence,
        long initialRevision,
        AgentActionExecutionBinding binding)
    {
        var descriptor = session.Snapshot().Descriptor;
        if (descriptor.Lifecycle != SessionLifecycle.Active
            || descriptor.Revision != expectedSessionRevision)
        {
            throw AgentDatabaseDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact Database Viewer session is no longer active.");
        }

        if (descriptor.Owner.PanelId != request.PanelId
            || session.Engine is not IDatabasePanelSession database
            || session.Engine.Kind != PanelKind.DatabaseViewer
            || !IsDatabaseRequestCompatible(request, database))
        {
            throw AgentDatabaseDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session does not support this database read.");
        }

        if (!descriptor.Capabilities.Contains(request.RequiredSessionCapability)
            || !database.Capabilities.Contains(request.RequiredSessionCapability))
        {
            throw AgentDatabaseDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The live Database Viewer does not advertise this read capability.");
        }

        return new AgentDatabaseDispatch(
            session,
            database,
            expectedSessionRevision,
            expectedWorkspaceRevision,
            expectedGraphSequence,
            session.CaptureRuntimeAuthority(),
            initialRevision,
            binding);
    }

    private static bool IsDatabaseRequestCompatible(
        AgentDatabaseReadRequest request,
        IDatabasePanelSession database) => request switch
        {
            AgentDatabaseReadRequest.ReadState => true,
            AgentDatabaseReadRequest.ListObjects
                or AgentDatabaseReadRequest.DescribeObject
                or AgentDatabaseReadRequest.ReadTable
                or AgentDatabaseReadRequest.SchemaGraph =>
                database is IRelationalDatabasePanelSession,
            AgentDatabaseReadRequest.RedisScan
                or AgentDatabaseReadRequest.RedisRead
                or AgentDatabaseReadRequest.RedisListIndexes
                or AgentDatabaseReadRequest.RedisSearch =>
                database is IRedisDatabasePanelSession,
            _ => false,
        };

    private static async ValueTask<object> ExecuteAgentDatabaseReadAsync(
        AgentDatabaseDispatch dispatch,
        AgentDatabaseReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request switch
        {
            AgentDatabaseReadRequest.ReadState => dispatch.Database.State,
            AgentDatabaseReadRequest.ListObjects value =>
                await ((IRelationalDatabasePanelSession)dispatch.Database)
                    .ListObjectsAsync(value.MaximumObjects, cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.DescribeObject value =>
                await ((IRelationalDatabasePanelSession)dispatch.Database)
                    .DescribeObjectAsync(value.Reference, cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.ReadTable value =>
                await ((IRelationalDatabasePanelSession)dispatch.Database)
                    .ReadTableAsync(value.ToSessionRequest(), cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.SchemaGraph value =>
                await ((IRelationalDatabasePanelSession)dispatch.Database)
                    .ReadSchemaGraphAsync(value.MaximumObjects, cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.RedisScan value =>
                await ((IRedisDatabasePanelSession)dispatch.Database)
                    .ScanAsync(value.Pattern, value.Cursor, value.Count, cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.RedisRead value =>
                await ((IRedisDatabasePanelSession)dispatch.Database)
                    .ReadAsync(
                        new RedisKeyReadRequest(
                            value.Reference,
                            value.MaximumEntries),
                        cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.RedisListIndexes value =>
                await ((IRedisDatabasePanelSession)dispatch.Database)
                    .ListSearchIndexesAsync(
                        value.MaximumIndexes,
                        cancellationToken)
                    .ConfigureAwait(false),
            AgentDatabaseReadRequest.RedisSearch value =>
                await ((IRedisDatabasePanelSession)dispatch.Database)
                    .SearchAsync(value.Index, value.Query, value.Limit, cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                "The database request variant is unknown."),
        };
    }

    private AgentDatabaseReadResult ProjectAgentDatabaseResult(
        AgentDatabaseReadAction action,
        object captured) => (action.Request, captured) switch
        {
            (AgentDatabaseReadRequest.ReadState, DatabasePanelSessionState value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.ListObjects, DatabaseObjectPage value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.DescribeObject, DatabaseObjectSnapshot value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.ReadTable, DatabaseTableSnapshot value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.SchemaGraph, DatabaseSchemaGraphSnapshot value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.RedisScan, RedisKeyPage value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.RedisRead, RedisKeyValueSnapshot value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.RedisListIndexes, RedisSearchIndexPage value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            (AgentDatabaseReadRequest.RedisSearch, RedisSearchResult value) =>
                _agentDatabaseReadActionComposer!.Project(action, value),
            _ => throw new ArgumentException(
                "The hosted database returned a mismatched result type.",
                nameof(captured)),
        };

    private static bool HasAgentDatabaseAuthorization(
        AgentActionAuthorization authorization,
        AgentDatabaseReadRequest request) =>
        string.Equals(
            authorization.ToolName,
            request.ToolName,
            StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(request.ToolName, out var descriptor)
        && descriptor!.Capability == AgentCapability.DatabaseRead
        && descriptor.Risk == AgentActionRisk.Observation;

    private static bool AgentDatabaseBindingsMatch(
        AgentActionExecutionBinding left,
        AgentActionExecutionBinding right) =>
        left.ActionId == right.ActionId
        && left.RunId == right.RunId
        && left.ActorId == right.ActorId
        && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
        && left.Target == right.Target
        && left.TargetIdentity == right.TargetIdentity
        && left.TargetFingerprint == right.TargetFingerprint
        && left.ArgumentDigest == right.ArgumentDigest
        && left.PolicyGeneration == right.PolicyGeneration;

    private async ValueTask<HostResult<AgentDatabaseReadResult>>
        CompleteAgentDatabaseActionAsync(
            AgentActionPermit permit,
            HostResult<AgentDatabaseReadResult> result)
    {
        var (outcome, stableCode, resultCount) = result switch
        {
            HostResult<AgentDatabaseReadResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<AgentDatabaseReadResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<AgentDatabaseReadResult>.Success success =>
                (AgentActionOutcome.Succeeded, "database_read_completed", ResultCount(success.Value)),
            _ => throw new InvalidOperationException(
                "A governed database dispatch returned an unknown result."),
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, resultCount),
                result,
                AgentDatabaseResultRevision(result))
            .ConfigureAwait(false);
    }

    private static int ResultCount(AgentDatabaseReadResult result) => result switch
    {
        AgentDatabaseReadResult.State => 1,
        AgentDatabaseReadResult.Objects value => value.Value.Objects.Count,
        AgentDatabaseReadResult.ObjectDescription value => value.Value.Columns.Count,
        AgentDatabaseReadResult.Table value => value.Value.Page.Result.Rows.Count,
        AgentDatabaseReadResult.Schema value => value.Value.Tables.Count,
        AgentDatabaseReadResult.RedisKeys value => value.Value.Keys.Count,
        AgentDatabaseReadResult.RedisValue value => value.Value.Entries.Count,
        AgentDatabaseReadResult.RedisSearch value => value.Value.Values.Count,
        _ => 0,
    };

    private static HostResult<AgentDatabaseReadResult>
        MapAgentDatabaseAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                new HostError(
                    HostErrorCode.DeadlineExceeded,
                    "database_authorization_expired",
                    "The one-action database authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                new HostError(
                    HostErrorCode.Cancelled,
                    "database_read_cancelled",
                    "The governed database observation was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                new HostError(
                    HostErrorCode.EngineFailed,
                    "database_audit_unavailable",
                    "The database-agent audit trail is unavailable.",
                    Retryable: true),
            _ => new HostError(
                HostErrorCode.InvalidRequest,
                "database_authorization_rejected",
                "The exact one-action database authorization was rejected."),
        };
        return HostResult<AgentDatabaseReadResult>.Fail(hostError, revision);
    }

    private static HostResult<AgentDatabaseReadResult>
        CancelledAgentDatabaseAction(
            AgentActionPermit permit,
            CancellationToken runtimeCancellation,
            CancellationToken callerCancellation,
            long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : runtimeCancellation.IsCancellationRequested
                ? "session_revoked"
                : callerCancellation.IsCancellationRequested
                    ? "caller_cancelled"
                    : "operation_cancelled";
        return HostResult<AgentDatabaseReadResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed database observation was cancelled."),
            revision);
    }

    private static HostResult<AgentDatabaseReadResult>
        InvalidAgentDatabaseAction(string message, long revision) =>
        HostResult<AgentDatabaseReadResult>.Fail(
            new HostError(
                HostErrorCode.InvalidRequest,
                "database_action_invalid",
                message),
            revision);

    private static HostResult<AgentDatabaseReadResult> AgentDatabaseFailure(
        string stableCode,
        long revision,
        HostErrorCode code = HostErrorCode.EngineFailed,
        bool retryable = false) =>
        HostResult<AgentDatabaseReadResult>.Fail(
            new HostError(
                code,
                stableCode,
                "The Database Viewer could not complete the governed observation.",
                retryable),
            revision);

    private static long AgentDatabaseResultRevision(
        HostResult<AgentDatabaseReadResult> result) => result switch
        {
            HostResult<AgentDatabaseReadResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentDatabaseReadResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed database action returned an unknown result."),
        };

    private static AgentDatabaseDispatchException AgentDatabaseDispatchFailure(
        HostErrorCode code,
        string message) => new(HostError.Create(code, message));

    private sealed record AgentDatabaseDispatch(
        HostedSession Session,
        IDatabasePanelSession Database,
        long ExpectedSessionRevision,
        long ExpectedWorkspaceRevision,
        long ExpectedGraphSequence,
        CancellationToken RuntimeCancellation,
        long InitialRevision,
        AgentActionExecutionBinding Binding);

    private sealed class AgentDatabaseDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;

    }
}
