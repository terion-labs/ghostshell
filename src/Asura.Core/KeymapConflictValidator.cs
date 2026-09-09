using System.Collections.Immutable;

namespace Asura.Core;

public enum KeymapIssueSeverity
{
    Warning,
    Error,
}

public enum KeymapIssueKind
{
    UnknownCommand,
    TerminalSequence,
    UnsupportedTerminalKey,
    ExactBinding,
    PrefixCollision,
    OverlappingContexts,
    ShadowedBinding,
}

public sealed record KeymapValidationIssue(
    KeymapIssueSeverity Severity,
    KeymapIssueKind Kind,
    int BindingIndex,
    int? OtherBindingIndex,
    string Message);

public static class KeymapConflictValidator
{
    public static ImmutableArray<KeymapValidationIssue> Validate(
        KeymapProfile profile,
        CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);

        var issues = ImmutableArray.CreateBuilder<KeymapValidationIssue>();
        for (var index = 0; index < profile.Bindings.Count; index++)
        {
            var binding = profile.Bindings[index];
            if (!registry.Contains(binding.CommandId))
            {
                issues.Add(new KeymapValidationIssue(
                    KeymapIssueSeverity.Warning,
                    KeymapIssueKind.UnknownCommand,
                    index,
                    null,
                    $"Command '{binding.CommandId}' is not available in this version. Its binding is preserved."));
            }

            if (profile.Layer == KeymapLayer.Terminal && binding.Sequence.Count != 1)
            {
                issues.Add(new KeymapValidationIssue(
                    KeymapIssueSeverity.Error,
                    KeymapIssueKind.TerminalSequence,
                    index,
                    null,
                    $"Terminal shortcut '{binding.Sequence}' must contain exactly one key stroke. Multi-stroke sequences are available in application keymaps."));
            }
            else if (profile.Layer == KeymapLayer.Terminal
                && !TerminalKeyBindingRules.IsSupported(binding.Sequence[0]))
            {
                issues.Add(new KeymapValidationIssue(
                    KeymapIssueSeverity.Error,
                    KeymapIssueKind.UnsupportedTerminalKey,
                    index,
                    null,
                    $"Terminal key '{binding.Sequence[0].Key}' is not supported by every desktop renderer."));
            }
        }

        for (var leftIndex = 0; leftIndex < profile.Bindings.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < profile.Bindings.Count; rightIndex++)
            {
                ValidatePair(profile.Bindings, leftIndex, rightIndex, issues);
            }
        }

        return issues.ToImmutable();
    }

    private static void ValidatePair(
        IReadOnlyList<CommandBinding> bindings,
        int leftIndex,
        int rightIndex,
        ImmutableArray<KeymapValidationIssue>.Builder issues)
    {
        var left = bindings[leftIndex];
        var right = bindings[rightIndex];
        var relationship = GetContextRelationship(left.Contexts, right.Contexts);
        if (relationship == ContextRelationship.Disjoint)
        {
            return;
        }

        if (left.Sequence.Equals(right.Sequence))
        {
            AddExactSequenceIssue(left, right, leftIndex, rightIndex, relationship, issues);
            return;
        }

        if (left.Sequence.IsPrefixOf(right.Sequence) || right.Sequence.IsPrefixOf(left.Sequence))
        {
            issues.Add(new KeymapValidationIssue(
                KeymapIssueSeverity.Error,
                KeymapIssueKind.PrefixCollision,
                leftIndex,
                rightIndex,
                $"'{left.Sequence}' and '{right.Sequence}' cannot both resolve while their contexts overlap."));
        }
    }

    private static void AddExactSequenceIssue(
        CommandBinding left,
        CommandBinding right,
        int leftIndex,
        int rightIndex,
        ContextRelationship relationship,
        ImmutableArray<KeymapValidationIssue>.Builder issues)
    {
        if (left.Contexts == right.Contexts)
        {
            issues.Add(new KeymapValidationIssue(
                KeymapIssueSeverity.Error,
                KeymapIssueKind.ExactBinding,
                leftIndex,
                rightIndex,
                $"'{left.Sequence}' is assigned more than once in {left.Contexts}."));
            return;
        }

        if (relationship == ContextRelationship.SamePriorityOverlap)
        {
            issues.Add(new KeymapValidationIssue(
                KeymapIssueSeverity.Error,
                KeymapIssueKind.OverlappingContexts,
                leftIndex,
                rightIndex,
                $"'{left.Sequence}' is ambiguous in contexts that can be active together."));
            return;
        }

        issues.Add(new KeymapValidationIssue(
            KeymapIssueSeverity.Warning,
            KeymapIssueKind.ShadowedBinding,
            leftIndex,
            rightIndex,
            $"'{left.Sequence}' in the lower-priority context is shadowed whenever both contexts are active."));
    }

    private static ContextRelationship GetContextRelationship(CommandContext left, CommandContext right)
    {
        var hasOverlap = false;
        foreach (var leftContext in CommandContextRules.Enumerate(left))
        {
            foreach (var rightContext in CommandContextRules.Enumerate(right))
            {
                if (!CommandContextRules.CanBeActiveTogether(leftContext, rightContext))
                {
                    continue;
                }

                hasOverlap = true;
                if (CommandContextRules.ResolutionPriority(leftContext)
                    == CommandContextRules.ResolutionPriority(rightContext))
                {
                    return ContextRelationship.SamePriorityOverlap;
                }
            }
        }

        return hasOverlap ? ContextRelationship.PriorityOverlap : ContextRelationship.Disjoint;
    }

    private enum ContextRelationship
    {
        Disjoint,
        PriorityOverlap,
        SamePriorityOverlap,
    }
}
