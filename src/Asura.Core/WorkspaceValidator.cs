namespace Asura.Core;

public static class WorkspaceValidator
{
    private static readonly string[] ProviderOwnedGuestPaths =
    [
        "/bin",
        "/boot",
        "/dev",
        "/etc",
        "/lib",
        "/lib64",
        "/proc",
        "/root",
        "/run",
        "/sbin",
        "/sys",
        "/usr",
        "/var",
    ];

    public static DefinitionValidationResult Validate(WorkspaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DefinitionValidationIssue> issues = [];

        ValidateHeader(definition, issues);
        foreach (var duplicate in definition.Entries
                     .GroupBy(entry => entry.Id.Value, StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                "Workspace entry IDs must be present and unique.",
                duplicate.Key));
        }

        foreach (var entry in definition.Entries)
        {
            ValidateEntry(entry, issues);
        }

        return new(issues);
    }

    private static void ValidateHeader(
        WorkspaceDefinition definition,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
        {
            issues.Add(new(DefinitionValidationCode.Required, "A workspace ID is required."));
        }

        if (definition.SchemaVersion < 1)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidSchemaVersion,
                "A workspace schema version must be at least one.",
                definition.Id.Value));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A workspace name is required.",
                definition.Id.Value));
        }

        if (!WorkspaceDefinition.IsValidIcon(definition.Icon))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A workspace icon must be a lowercase semantic identifier.",
                definition.Id.Value));
        }

        if (definition.AgentPolicyOverride is { } policy
            && !policy.IsValidForDurableStorage())
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidAgentPolicy,
                "A saved workspace agent policy must be structurally valid. "
                    + "YOLO is run-local and cannot be persisted.",
                definition.Id.Value));
        }

        if (definition.IsolationImageReference is { } imageReference
            && (imageReference.Length > WorkspaceDefinition.MaximumIsolationImageReferenceLength
                || imageReference.Any(char.IsWhiteSpace)
                || imageReference.Any(char.IsControl)))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "The workspace isolation image must be a valid OCI image reference without whitespace or control characters.",
                definition.Id.Value));
        }

        if (definition.RunAgentInIsolation && !definition.IsIsolated)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "The AI agent can use workspace isolation only when the workspace is isolated.",
                definition.Id.Value));
        }

        ValidateIsolationMounts(definition, issues);
    }

    private static void ValidateIsolationMounts(
        WorkspaceDefinition definition,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (definition.IsolationMounts.Count > WorkspaceDefinition.MaximumIsolationMountCount)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                $"A workspace cannot define more than {WorkspaceDefinition.MaximumIsolationMountCount} isolation mounts.",
                definition.Id.Value));
        }

        var normalizedGuestPaths = new List<string>(definition.IsolationMounts.Count);
        foreach (var mount in definition.IsolationMounts)
        {
            if (mount is null
                || string.IsNullOrWhiteSpace(mount.HostPath)
                || mount.HostPath.Contains('\0', StringComparison.Ordinal)
                || !IsPortableAbsoluteHostPath(mount.HostPath))
            {
                issues.Add(new(
                    DefinitionValidationCode.InvalidEntry,
                    "Each isolation mount requires an absolute host path.",
                    definition.Id.Value));
            }

            if (mount is null
                || !TryNormalizeGuestMountPath(mount.GuestPath, out var normalizedGuestPath)
                || IsReservedGuestMountPath(normalizedGuestPath))
            {
                issues.Add(new(
                    DefinitionValidationCode.InvalidEntry,
                    "Each isolation mount requires an absolute guest path outside system-managed directories.",
                    definition.Id.Value));
            }
            else
            {
                normalizedGuestPaths.Add(normalizedGuestPath);
            }
        }

        foreach (var duplicate in normalizedGuestPaths
                     .GroupBy(path => path, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                $"Isolation mount guest path '{duplicate.Key}' is used more than once.",
                definition.Id.Value));
        }
    }

    private static bool IsPortableAbsoluteHostPath(string path)
    {
        if (path[0] == '/')
        {
            return true;
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '/' or '\\')
        {
            return true;
        }

        if (!path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return false;
        }

        var share = path.AsSpan(2);
        var serverSeparator = share.IndexOfAny('/', '\\');
        return serverSeparator > 0
            && serverSeparator < share.Length - 1
            && share[(serverSeparator + 1)..].IndexOfAny('/', '\\') is not 0;
    }

    private static bool TryNormalizeGuestMountPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || path[0] != '/'
            || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        normalized = $"/{string.Join('/', segments)}";
        return true;
    }

    private static bool IsReservedGuestMountPath(string path)
    {
        var normalized = path.TrimEnd('/');
        return ProviderOwnedGuestPaths.Any(reserved =>
            string.Equals(normalized, reserved, StringComparison.Ordinal)
            || normalized.StartsWith($"{reserved}/", StringComparison.Ordinal));
    }

    private static void ValidateEntry(
        WorkspaceEntry entry,
        ICollection<DefinitionValidationIssue> issues)
    {
        switch (entry)
        {
            case WorkspaceEntry.ConnectionReference connection
                when string.IsNullOrWhiteSpace(connection.ConnectionId.Value):
                issues.Add(new(
                    DefinitionValidationCode.MissingDependency,
                    "A workspace connection entry requires a connection ID.",
                    entry.Id.Value));
                break;

            case WorkspaceEntry.ScreenReference screen
                when string.IsNullOrWhiteSpace(screen.ScreenId.Value):
                issues.Add(new(
                    DefinitionValidationCode.MissingDependency,
                    "A workspace screen entry requires a screen ID.",
                    entry.Id.Value));
                break;

            case WorkspaceEntry.Tab tab:
                ValidateTab(tab, issues);
                break;
        }
    }

    private static void ValidateTab(
        WorkspaceEntry.Tab tab,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(tab.Name)
            || string.IsNullOrWhiteSpace(tab.LayoutId.Value)
            || tab.Panels.Count == 0)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A workspace-only tab requires a name, layout, and at least one panel.",
                tab.Id.Value));
        }

        foreach (var panel in tab.Panels)
        {
            ScreenValidator.ValidatePanel(panel, issues);
        }

        var hasDuplicatePanels = tab.Panels
            .GroupBy(panel => panel.Id.Value, StringComparer.Ordinal)
            .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        var hasDuplicateSlots = tab.Panels
            .GroupBy(panel => panel.SlotId.Value, StringComparer.Ordinal)
            .Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (hasDuplicatePanels || hasDuplicateSlots)
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                "Workspace-only tab panel and slot IDs must be unique.",
                tab.Id.Value));
        }
    }
}
