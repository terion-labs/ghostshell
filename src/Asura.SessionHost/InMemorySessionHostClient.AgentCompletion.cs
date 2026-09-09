using Asura.Application;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private static readonly TimeSpan AgentCompletionAuditTimeout =
        TimeSpan.FromSeconds(5);
    private const int MaximumCompletionAuditAttempts = 2;

    private async ValueTask<bool> ConfirmConsumedAgentActionAsync(
        AgentActionPermit permit,
        AgentActionCompletion completion)
    {
        for (var attempt = 1; attempt <= MaximumCompletionAuditAttempts; attempt++)
        {
            AgentAuthorizationError? completionError;
            try
            {
                using var completionTimeout =
                    new CancellationTokenSource(AgentCompletionAuditTimeout);
                completionError = await _agentAuthorizationConsumer!
                    .CompleteAsync(
                        permit,
                        completion,
                        completionTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (attempt < MaximumCompletionAuditAttempts)
            {
                continue;
            }
            catch (Exception)
            {
                return false;
            }

            if (completionError is null)
            {
                return true;
            }

            if (completionError.Code != AgentAuthorizationErrorCode.AuditUnavailable
                || attempt == MaximumCompletionAuditAttempts)
            {
                return false;
            }
        }

        return false;
    }

    private static HostError AgentCompletionAuditError() =>
        new(
            HostErrorCode.EngineFailed,
            AgentActionFailureCodes.CompletionAuditUnavailable,
            "The action completed, but its audit outcome could not be persisted.",
            Retryable: false);
}
