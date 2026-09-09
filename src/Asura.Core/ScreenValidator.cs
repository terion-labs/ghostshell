namespace Asura.Core;

public static class ScreenValidator
{
    public static DefinitionValidationResult Validate(
        ScreenDefinition definition,
        LayoutDefinition layout)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(layout);
        List<DefinitionValidationIssue> issues = [];

        ValidateHeader(definition, issues);
        var layoutValidation = LayoutValidator.Validate(layout);
        issues.AddRange(layoutValidation.Issues);

        if (definition.LayoutId != layout.Id)
        {
            issues.Add(new(
                DefinitionValidationCode.LayoutMismatch,
                "The supplied layout does not match the screen's layout ID.",
                definition.LayoutId.Value));
        }

        AddDuplicateIssues(
            definition.Panels.Select(panel => panel.Id.Value),
            "Screen panel IDs must be present and unique.",
            issues);
        AddDuplicateIssues(
            definition.Panels.Select(panel => panel.SlotId.Value),
            "Each layout slot can be mapped by only one screen panel.",
            issues);

        var knownSlots = layout.Slots
            .Select(slot => slot.Id)
            .ToHashSet();
        var mappedSlots = new HashSet<LayoutSlotId>();
        foreach (var panel in definition.Panels)
        {
            ValidatePanel(panel, issues);
            if (!knownSlots.Contains(panel.SlotId))
            {
                issues.Add(new(
                    DefinitionValidationCode.UnknownSlot,
                    $"Panel '{panel.Id}' maps to a slot that is not in the selected layout.",
                    panel.SlotId.Value));
                continue;
            }

            mappedSlots.Add(panel.SlotId);
        }

        foreach (var missingSlot in knownSlots.Except(mappedSlots))
        {
            issues.Add(new(
                DefinitionValidationCode.MissingSlot,
                "Every layout slot must have a screen panel mapping.",
                missingSlot.Value));
        }

        return new(issues);
    }

    internal static void ValidatePanel(
        ScreenPanelDefinition panel,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(panel.Id.Value)
            || string.IsNullOrWhiteSpace(panel.SlotId.Value))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "A screen panel requires both a panel ID and a layout slot ID.",
                panel.Id.Value));
        }

        if (!Enum.IsDefined(panel.Kind))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "A screen panel kind is not recognized.",
                panel.Id.Value));
        }

        if (panel.ConnectionId is { } connectionId
            && string.IsNullOrWhiteSpace(connectionId.Value))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "A panel connection reference cannot be empty.",
                panel.Id.Value));
        }

        if (panel.FileProviderProfileId is { } fileProviderProfileId
            && string.IsNullOrWhiteSpace(fileProviderProfileId.Value))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "A panel file-provider profile reference cannot be empty.",
                panel.Id.Value));
        }

        if (panel.FileProviderProfileId is not null
            && panel.Kind != ScreenPanelKind.FileViewer)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "Only File Viewer panels can bind a file-provider profile.",
                panel.Id.Value));
        }

        if (panel.Startup is null)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "A screen panel requires startup behavior.",
                panel.Id.Value));
            return;
        }

        if (panel.Startup.Commands.Any(string.IsNullOrWhiteSpace))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "Panel startup commands cannot be empty.",
                panel.Id.Value));
        }

        var supportsCommands = panel.Kind == ScreenPanelKind.Terminal;
        if (!supportsCommands && panel.Startup.Commands.Count > 0)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "Only terminal panels support startup commands.",
                panel.Id.Value));
        }

        if (!supportsCommands
            && panel.Startup.DeliveryFailurePolicy
                != StartupCommandDeliveryFailurePolicy.RetryWhileLive)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "Only terminal panels support a non-default startup-command delivery failure policy.",
                panel.Id.Value));
        }

        // A database viewer's location is its saved target: "driverId:connection
        // string". A Git panel's location is the repository path to open.
        var supportsLocation = panel.Kind is ScreenPanelKind.Terminal
            or ScreenPanelKind.Browser
            or ScreenPanelKind.FileViewer
            or ScreenPanelKind.DatabaseViewer
            or ScreenPanelKind.Git;
        if (!supportsLocation && panel.Startup.Location is not null)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidPanel,
                "This panel kind does not support a startup location.",
                panel.Id.Value));
        }
    }

    private static void ValidateHeader(
        ScreenDefinition definition,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
        {
            issues.Add(new(DefinitionValidationCode.Required, "A screen ID is required."));
        }

        if (definition.SchemaVersion < 1)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidSchemaVersion,
                "A screen schema version must be at least one.",
                definition.Id.Value));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A screen name is required.",
                definition.Id.Value));
        }

        if (string.IsNullOrWhiteSpace(definition.LayoutId.Value))
        {
            issues.Add(new(
                DefinitionValidationCode.MissingDependency,
                "A screen requires a layout reference.",
                definition.Id.Value));
        }

        if (definition.Tags.Any(string.IsNullOrWhiteSpace)
            || definition.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != definition.Tags.Count)
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "Screen tags must be non-empty and unique.",
                definition.Id.Value));
        }

        if (definition.AgentPolicyOverride is { } policy
            && !policy.IsValidForDurableStorage())
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidAgentPolicy,
                "A saved screen agent policy must be structurally valid. "
                    + "YOLO is run-local and cannot be persisted.",
                definition.Id.Value));
        }
    }

    private static void AddDuplicateIssues(
        IEnumerable<string> values,
        string message,
        ICollection<DefinitionValidationIssue> issues)
    {
        foreach (var duplicate in values
                     .GroupBy(value => value, StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(DefinitionValidationCode.DuplicateId, message, duplicate.Key));
        }
    }
}
