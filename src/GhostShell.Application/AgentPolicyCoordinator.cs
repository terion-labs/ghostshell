using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Application;

public sealed class AgentPolicyCoordinator
{
    private readonly IAgentPolicyPreferenceStore _store;
    private AgentPolicy? _policy;

    public AgentPolicyCoordinator(IAgentPolicyPreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event EventHandler? Changed;

    public AgentPolicy? Policy => _policy;

    public ApplicationRunError? InitializationError { get; private set; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result is { IsSuccess: true })
        {
            _policy = result.Value;
            InitializationError = null;
            return;
        }

        // Missing and unreadable are different states. A failed read must not
        // seed permissive defaults or overwrite the original preference row.
        InitializationError = result.Error;
        _policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability, _ => AgentPermission.Off),
        };
    }

    public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
        AgentPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "The configured agent policy must be valid for durable storage.",
                nameof(policy));
        }

        var normalized = NormalizeConfiguredPolicy(policy);
        var result = await _store.WriteAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        _policy = normalized;
        InitializationError = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private static AgentPolicy NormalizeConfiguredPolicy(AgentPolicy policy) =>
        policy with
        {
            Provider = policy.Provider.Trim(),
            Model = policy.Model.Trim(),
            CompactionModel = NormalizeSelection(policy.CompactionModel),
            TitleModel = NormalizeSelection(policy.TitleModel),
            SystemPrompt = string.IsNullOrWhiteSpace(policy.SystemPrompt)
                ? null
                : policy.SystemPrompt.Trim(),
        };

    private static AgentModelSelection NormalizeSelection(
        AgentModelSelection selection) =>
        new(
            selection.Provider.Trim(),
            selection.Model.Trim());
}
