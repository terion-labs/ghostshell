using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentWebToolResult>> RunAgentWebToolAsync(
        AgentAuthorizationId authorizationId,
        AgentWebToolAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentWebToolActionComposer is null
            || _agentWebToolExecutor is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentWebToolResult>(
                "The governed web tool bridge is not composed.",
                revision: 0);
        }

        AgentActionPermit? permit = null;
        HostResult<AgentWebToolResult>? preDispatchFailure = null;
        WorkspaceInstanceId? workspaceId = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentWebToolResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var contextResult = ResolveAgentWebContext(action.Proposal.Target);
            if (contextResult is HostResult<AgentContextSnapshot>.Failure failure)
            {
                return HostResult<AgentWebToolResult>.Fail(
                    failure.Error,
                    failure.CurrentRevision);
            }

            var context = ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
            workspaceId = context.Panels[0].WorkspaceId;
            revision = context.Revision;
            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentWebToolActionComposer.BindForExecution(action, context);
            }
            catch (ArgumentException)
            {
                return InvalidWebAction(
                    "The web run target changed before authorization.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidWebAction(
                    "The prepared web action no longer matches its typed request.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer.ConsumeAsync(
                    authorizationId,
                    binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapWebAuthorizationFailure(
                    denied.Error,
                    action.Request.ToolName,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            if (!HasWebAuthorization(permit.Authorization, action.Request.ToolName)
                || !AuthorizationMatchesBinding(permit.Authorization, binding))
            {
                preDispatchFailure = InvalidWebAction(
                    "The consumed authorization does not grant this web action.",
                    revision);
            }
            else if (permit.CancellationToken.IsCancellationRequested
                || cancellationToken.IsCancellationRequested)
            {
                preDispatchFailure = CancelledWebAction(
                    permit,
                    cancellationToken,
                    action.Request.ToolName,
                    revision);
            }
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentWebToolResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledWebAction(
                permit!,
                cancellationToken,
                action.Request.ToolName,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentWebToolResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledWebAction(
                permit!,
                cancellationToken,
                action.Request.ToolName,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return WebEngineFailure(action.Request.ToolName, revision);
        }
        catch (Exception)
        {
            preDispatchFailure = WebEngineFailure(action.Request.ToolName, revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteWebAsync(permit!, action, preDispatchFailure)
                .ConfigureAwait(false);
        }

        HostResult<AgentWebToolResult> result;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit!.CancellationToken,
                    cancellationToken);
            var executed = await _agentWebToolExecutor.ExecuteAsync(
                    workspaceId!.Value,
                    action.Request,
                    operationCancellation.Token)
                .ConfigureAwait(false);
            result = executed switch
            {
                AgentWebToolExecutionResult.Succeeded succeeded
                    when !operationCancellation.IsCancellationRequested =>
                    HostResult<AgentWebToolResult>.Succeed(succeeded.Result, revision),
                AgentWebToolExecutionResult.Succeeded =>
                    CancelledWebAction(
                        permit,
                        cancellationToken,
                        action.Request.ToolName,
                        revision),
                AgentWebToolExecutionResult.Failed failed =>
                    MapWebExecutionFailure(
                        failed.Code,
                        action.Request.ToolName,
                        revision),
                _ => WebEngineFailure(action.Request.ToolName, revision),
            };
        }
        catch (OperationCanceledException)
        {
            result = CancelledWebAction(
                permit!,
                cancellationToken,
                action.Request.ToolName,
                revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            result = WebEngineFailure(action.Request.ToolName, revision);
        }

        return await CompleteWebAsync(permit!, action, result).ConfigureAwait(false);
    }

    private HostResult<AgentContextSnapshot> ResolveAgentWebContext(AgentTarget target)
    {
        HostedSession[] hostedSessions;
        lock (_gate)
        {
            hostedSessions = [.. _sessions.Values];
        }

        var sessions = hostedSessions
            .Select(session => session.Snapshot().Descriptor)
            .ToDictionary(session => session.Id);
        return ResolveAgentContext(
            new AgentContextRequest(target, AgentTarget.SelectedPanels.MaximumPanelCount),
            sessions);
    }

    private static bool HasWebAuthorization(
        AgentActionAuthorization authorization,
        string toolName) =>
        string.Equals(authorization.ToolName, toolName, StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor)
        && descriptor!.Capability == AgentCapability.WebFetch
        && descriptor.Risk == AgentActionRisk.Observation;

    private async ValueTask<HostResult<AgentWebToolResult>> CompleteWebAsync(
        AgentActionPermit permit,
        AgentWebToolAction action,
        HostResult<AgentWebToolResult> result)
    {
        var (outcome, stableCode, count) = result switch
        {
            HostResult<AgentWebToolResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<AgentWebToolResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<AgentWebToolResult>.Success success =>
                (AgentActionOutcome.Succeeded, CompletedCode(action.Request.ToolName), ResultCount(success.Value)),
            _ => throw new InvalidOperationException(
                "A governed web tool returned an unknown result."),
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, count),
                result,
                WebResultRevision(result))
            .ConfigureAwait(false);
    }

    private static HostResult<AgentWebToolResult> MapWebExecutionFailure(
        AgentWebToolErrorCode code,
        string toolName,
        long revision)
    {
        var prefix = StablePrefix(toolName);
        var mapped = code switch
        {
            AgentWebToolErrorCode.InvalidUrl =>
                (HostErrorCode.InvalidRequest, prefix + "invalid_url", false),
            AgentWebToolErrorCode.DestinationDenied =>
                (HostErrorCode.InvalidRequest, prefix + "destination_denied", false),
            AgentWebToolErrorCode.DnsFailed =>
                (HostErrorCode.EngineFailed, prefix + "dns_failed", true),
            AgentWebToolErrorCode.RedirectLimit =>
                (HostErrorCode.InvalidRequest, prefix + "redirect_limit", false),
            AgentWebToolErrorCode.TimedOut =>
                (HostErrorCode.DeadlineExceeded, prefix + "timed_out", true),
            AgentWebToolErrorCode.BodyTooLarge =>
                (HostErrorCode.InvalidRequest, prefix + "body_too_large", false),
            AgentWebToolErrorCode.UnsupportedContentType =>
                (HostErrorCode.InvalidRequest, prefix + "unsupported_content_type", false),
            AgentWebToolErrorCode.LoadFailed =>
                (HostErrorCode.EngineFailed, prefix + "load_failed", true),
            AgentWebToolErrorCode.RenderProcessFailed =>
                (HostErrorCode.EngineFailed, prefix + "render_process_failed", true),
            AgentWebToolErrorCode.ExtractionFailed =>
                (HostErrorCode.EngineFailed, prefix + "extraction_failed", true),
            AgentWebToolErrorCode.ConverterFailed =>
                (HostErrorCode.EngineFailed, prefix + "converter_failed", true),
            AgentWebToolErrorCode.SearchInterstitial =>
                (HostErrorCode.EngineFailed, prefix + "interstitial", false),
            AgentWebToolErrorCode.Cancelled =>
                (HostErrorCode.Cancelled, prefix + "cancelled", false),
            _ => (HostErrorCode.EngineFailed, prefix + "unavailable", true),
        };
        return HostResult<AgentWebToolResult>.Fail(
            new HostError(
                mapped.Item1,
                mapped.Item2,
                "The governed web tool could not complete the request.",
                mapped.Item3),
            revision);
    }

    private static HostResult<AgentWebToolResult> MapWebAuthorizationFailure(
        AgentAuthorizationError error,
        string toolName,
        long revision)
    {
        var prefix = StablePrefix(toolName);
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                new HostError(
                    HostErrorCode.DeadlineExceeded,
                    prefix + "timed_out",
                    "The one-action web authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                new HostError(
                    HostErrorCode.Cancelled,
                    prefix + "cancelled",
                    "The governed web action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The web action audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action web authorization was rejected."),
        };
        return HostResult<AgentWebToolResult>.Fail(hostError, revision);
    }

    private static HostResult<AgentWebToolResult> CancelledWebAction(
        AgentActionPermit permit,
        CancellationToken callerCancellation,
        string toolName,
        long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : callerCancellation.IsCancellationRequested
                ? StablePrefix(toolName) + "cancelled"
                : "operation_cancelled";
        return HostResult<AgentWebToolResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed web action was cancelled."),
            revision);
    }

    private static HostResult<AgentWebToolResult> InvalidWebAction(
        string message,
        long revision) =>
        HostResult<AgentWebToolResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentWebToolResult> WebEngineFailure(
        string toolName,
        long revision) =>
        HostResult<AgentWebToolResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                StablePrefix(toolName) + "unavailable",
                "The governed web engine is unavailable.",
                Retryable: true),
            revision);

    private static int? ResultCount(AgentWebToolResult result) => result switch
    {
        AgentWebSearchResult search => search.Entries.Count,
        AgentHttpFetchResult fetch => fetch.Content.Length,
        AgentWebReadResult read => read.Content.Length,
        _ => null,
    };

    private static string StablePrefix(string toolName) => toolName switch
    {
        BuiltInAgentTools.HttpFetch => "http_fetch_",
        BuiltInAgentTools.WebRead => "web_read_",
        BuiltInAgentTools.WebSearch => "web_search_",
        _ => "web_",
    };

    private static string CompletedCode(string toolName) =>
        StablePrefix(toolName) + "completed";

    private static long WebResultRevision(HostResult<AgentWebToolResult> result) =>
        result switch
        {
            HostResult<AgentWebToolResult>.Success success => success.ResultingRevision,
            HostResult<AgentWebToolResult>.Failure failure => failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed web tool returned an unknown result."),
        };
}
