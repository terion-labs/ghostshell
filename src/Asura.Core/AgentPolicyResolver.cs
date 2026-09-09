using System.Collections.Immutable;

namespace Asura.Core;

public enum AgentActionRisk
{
    Observation,
    Routine,
    Mutation,
    Destructive,
    Privileged,
}

public enum AgentPolicyDecision
{
    Denied,
    RequiresApproval,
    AuthorizedByAuto,
    AuthorizedByYolo,
}

/// <summary>
/// Resolves trusted policy layers and classifies one action. It does not issue
/// an execution authorization; that remains an application/session-host concern.
/// </summary>
public static class AgentPolicyResolver
{
    public static AgentPolicy Resolve(
        AgentPolicy global,
        AgentPolicy? workspace = null,
        AgentPolicy? screen = null,
        AgentPolicy? run = null)
    {
        ArgumentNullException.ThrowIfNull(global);
        var layers = new[] { global, workspace, screen, run };
        foreach (var layer in layers.Where(layer => layer is not null))
        {
            if (!layer!.IsStructurallyValid())
            {
                throw new ArgumentException(
                    "Every supplied agent policy layer must be structurally valid.",
                    nameof(global));
            }
        }

        var mostSpecific = layers.Last(layer => layer is not null)!;
        var permissions = AgentPolicy.Capabilities.ToImmutableDictionary(
            capability => capability,
            capability => ResolvePermission(layers, capability));
        return new AgentPolicy(
            mostSpecific.Provider.Trim(),
            mostSpecific.Model.Trim(),
            permissions)
        {
            CompactionModel = NormalizeModelSelection(
                mostSpecific.CompactionModel),
            TitleModel = NormalizeModelSelection(
                mostSpecific.TitleModel),
            SystemPrompt = ResolveSystemPrompt(layers),
        };
    }

    /// <summary>
    /// Resolves one policy for a scope spanning independently captured runtime
    /// policies. Every capability receives the least permissive value present.
    /// Provider/model disagreement is rejected instead of selecting one source.
    /// </summary>
    public static AgentPolicy ResolveLeastPrivilege(
        IEnumerable<AgentPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var normalized = policies
            .Select(policy =>
            {
                ArgumentNullException.ThrowIfNull(policy);
                if (!policy.IsValidForDurableStorage())
                {
                    throw new ArgumentException(
                        "Every aggregated agent policy must be valid for durable storage.",
                        nameof(policies));
                }

                return Resolve(policy);
            })
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "At least one agent policy is required.",
                nameof(policies));
        }

        var first = normalized[0];
        if (normalized.Any(policy =>
                !string.Equals(policy.Provider, first.Provider, StringComparison.Ordinal)
                || !string.Equals(policy.Model, first.Model, StringComparison.Ordinal)
                || policy.CompactionModel != first.CompactionModel
                || policy.TitleModel != first.TitleModel
                || !string.Equals(
                    policy.SystemPrompt,
                    first.SystemPrompt,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A broad agent scope cannot combine different providers or models.",
                nameof(policies));
        }

        var permissions = AgentPolicy.Capabilities.ToImmutableDictionary(
            capability => capability,
            capability => normalized
                .Select(policy => policy.GetPermission(capability))
                .Aggregate(MoreRestrictive));
        return new AgentPolicy(first.Provider, first.Model, permissions)
        {
            CompactionModel = first.CompactionModel,
            TitleModel = first.TitleModel,
            SystemPrompt = first.SystemPrompt,
        };
    }

    public static AgentPolicyDecision Evaluate(
        AgentPermission permission,
        AgentActionRisk risk)
    {
        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        return permission switch
        {
            AgentPermission.Off => AgentPolicyDecision.Denied,
            AgentPermission.Ask => AgentPolicyDecision.RequiresApproval,
            AgentPermission.Auto when risk is AgentActionRisk.Observation
                or AgentActionRisk.Routine => AgentPolicyDecision.AuthorizedByAuto,
            AgentPermission.Auto => AgentPolicyDecision.RequiresApproval,
            AgentPermission.Yolo => AgentPolicyDecision.AuthorizedByYolo,
            _ => throw new ArgumentOutOfRangeException(nameof(permission)),
        };
    }

    private static AgentPermission ResolvePermission(
        IReadOnlyList<AgentPolicy?> layers,
        AgentCapability capability)
    {
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var permissions = layers[index]?.Permissions;
            if (permissions is not null
                && permissions.TryGetValue(capability, out var permission))
            {
                return permission;
            }
        }

        return AgentPermission.Off;
    }

    private static AgentModelSelection NormalizeModelSelection(
        AgentModelSelection selection) =>
        new(selection.Provider.Trim(), selection.Model.Trim());

    private static string? ResolveSystemPrompt(IReadOnlyList<AgentPolicy?> layers)
    {
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (layers[index]?.SystemPrompt is { } prompt)
            {
                return prompt.Trim();
            }
        }

        return null;
    }

    private static AgentPermission MoreRestrictive(
        AgentPermission left,
        AgentPermission right) =>
        PermissionRank(left) <= PermissionRank(right) ? left : right;

    private static int PermissionRank(AgentPermission permission) =>
        permission switch
        {
            AgentPermission.Off => 0,
            AgentPermission.Ask => 1,
            AgentPermission.Auto => 2,
            AgentPermission.Yolo => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(permission)),
        };
}
