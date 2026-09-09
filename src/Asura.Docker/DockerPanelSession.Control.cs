using Asura.Application;

namespace Asura.Docker;

internal sealed partial class DockerPanelSession
{
    public async ValueTask<DockerContainerControlResult> ControlContainerAsync(
        DockerContainerControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_lifetime.IsOpen
            || !_client.SupportsContainerMutation
            || !OperatingSystem.IsMacOS()
            || _target.Connection.ConnectionKind != Asura.Core.ConnectionKind.Local)
        {
            return NotDispatched("docker_container_control_unavailable");
        }

        if (request.EngineGeneration != State.EngineGeneration
            || !TryResolve(request.Container, out var resource)
            || resource.Kind != DockerResourceKind.Container)
        {
            return NotDispatched("docker_container_precondition_expired");
        }

        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        try
        {
            await _containerControlGate.WaitAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NotDispatched("docker_container_control_cancelled");
        }

        try
        {
            if (!_containerRevisions.TryClaim(
                    request.ContainerRevision,
                    request.EngineGeneration,
                    resource.Id,
                    out var revision))
            {
                return NotDispatched("docker_container_precondition_expired");
            }

            var expectedState = NormalizeState(request.ExpectedState);
            if (!string.Equals(revision.State, expectedState, StringComparison.Ordinal)
                || !Allows(request.Action, revision))
            {
                return NotDispatched("docker_container_state_changed");
            }

            operation.Token.ThrowIfCancellationRequested();
            var current = await _client
                .ReadSnapshotAsync(_target.Connection, operation.Token)
                .ConfigureAwait(false);
            if (current is not DockerResult<DockerEngineSnapshot>.Success success)
            {
                return NotDispatched("docker_container_preflight_failed", retryable: true);
            }

            var matches = success.Value.Containers
                .Where(container => string.Equals(
                    container.Id,
                    resource.Id,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1
                || !revision.Matches(matches[0])
                || !Allows(request.Action, revision))
            {
                return NotDispatched("docker_container_state_changed");
            }

            operation.Token.ThrowIfCancellationRequested();
            var mutation = await _client.RunContainerMutationAsync(
                    _target.Connection,
                    resource.Id,
                    request.Action,
                    operation.Token)
                .ConfigureAwait(false);
            return mutation.Outcome switch
            {
                DockerContainerMutationOutcome.Applied => new(
                    DockerContainerControlOutcome.Applied,
                    mutation.StableCode,
                    Retryable: false),
                DockerContainerMutationOutcome.NotDispatched => new(
                    DockerContainerControlOutcome.NotDispatched,
                    mutation.StableCode,
                    mutation.Retryable),
                DockerContainerMutationOutcome.OutcomeUnknown => new(
                    DockerContainerControlOutcome.OutcomeUnknown,
                    "docker_mutation_outcome_unknown",
                    Retryable: false),
                _ => throw new InvalidOperationException(
                    "The Docker adapter returned an unknown mutation outcome."),
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation after the one-shot revision is claimed can race command
            // dispatch. Reconciliation is required; this result must not be retried.
            return new DockerContainerControlResult(
                DockerContainerControlOutcome.OutcomeUnknown,
                "docker_mutation_outcome_unknown",
                Retryable: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DockerContainerControlResult(
                DockerContainerControlOutcome.OutcomeUnknown,
                "docker_mutation_outcome_unknown",
                Retryable: false);
        }
        finally
        {
            _containerControlGate.Release();
        }
    }

    private static bool Allows(
        DockerContainerAction action,
        DockerContainerRevisionPool.Snapshot revision) => action switch
        {
            DockerContainerAction.Start => revision.State is "created" or "exited",
            DockerContainerAction.Stop
                or DockerContainerAction.Restart
                or DockerContainerAction.Pause => string.Equals(
                    revision.State,
                    "running",
                    StringComparison.Ordinal),
            DockerContainerAction.Resume => string.Equals(
                revision.State,
                "paused",
                StringComparison.Ordinal),
            DockerContainerAction.Remove =>
                revision.State is "created" or "exited" or "dead"
                && string.IsNullOrWhiteSpace(revision.ComposeProject),
            _ => false,
        };

    private static string NormalizeState(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Docker expected state must be bounded and printable.",
                nameof(value));
        }

        return value.Trim().ToLowerInvariant();
    }

    private static DockerContainerControlResult NotDispatched(
        string stableCode,
        bool retryable = false) => new(
            DockerContainerControlOutcome.NotDispatched,
            stableCode,
            retryable);
}
