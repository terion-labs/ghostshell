using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Owns one mutable keymap draft without performing UI or persistence work. The caller must keep
/// mutations on one thread and persist <see cref="CreateSaveRequest"/> through the definition catalog.
/// </summary>
public sealed class KeybindingSettingsEditor
{
    private readonly CommandRegistry _registry;
    private readonly KeymapProfile _initialProfile;
    private readonly KeymapProfile _resetSource;
    private readonly List<DraftBinding> _bindings = [];
    private string _name;
    private PrefixConfiguration? _prefix;
    private long _nextRowId = 1;

    private KeybindingSettingsEditor(
        KeymapProfile profile,
        long? expectedRevision,
        CommandRegistry registry,
        KeymapProfile resetSource)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resetSource);
        if (resetSource.Layer != profile.Layer)
        {
            throw new ArgumentException("A keymap can only reset from a preset in the same layer.", nameof(resetSource));
        }

        _registry = registry;
        _initialProfile = profile;
        _resetSource = resetSource;
        _name = profile.Name;
        _prefix = profile.Prefix;
        ExpectedRevision = expectedRevision;
        AddInitialBindings(profile, resetSource);
    }

    public event EventHandler? Changed;

    public KeymapProfileId ProfileId => _initialProfile.Id;

    public string Name => _name;

    public KeymapLayer Layer => _initialProfile.Layer;

    public KeymapProfileId? BasedOn => _initialProfile.BasedOn;

    public KeymapProfileId ResetSourceId => _resetSource.Id;

    public long? ExpectedRevision { get; }

    public long Version { get; private set; }

    public PrefixConfiguration? Prefix => _prefix;

    public IReadOnlyList<KeybindingEditorRow> Rows => Evaluate().Rows;

    public IReadOnlyList<KeybindingEditorIssue> Issues => Evaluate().Issues;

    public bool CanSave => !Evaluate().Issues.Any(issue => issue.Severity == KeymapIssueSeverity.Error);

    public bool IsDirty => !ProfilesEqual(_initialProfile, CreateDraftProfile());

    // Draft lifecycle. Profiles remain the import/export boundary so editor-only state never leaks
    // into persistence or requires a parallel serialization contract.
    public static KeybindingSettingsEditor ClonePreset(
        KeymapProfile preset,
        KeymapProfileId cloneId,
        string cloneName,
        CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (cloneId == preset.Id)
        {
            throw new ArgumentException("A cloned keymap requires a new profile ID.", nameof(cloneId));
        }

        return new KeybindingSettingsEditor(
            preset.CloneAs(cloneId, cloneName),
            expectedRevision: null,
            registry,
            preset);
    }

    public static KeybindingSettingsEditor Edit(
        KeymapProfile profile,
        long expectedRevision,
        CommandRegistry registry,
        KeymapProfile? resetSource = null) =>
        new(profile, expectedRevision, registry, resetSource ?? profile);

    public static KeybindingSettingsEditor Import(
        KeymapProfile profile,
        CommandRegistry registry,
        KeymapProfile? resetSource = null) =>
        new(profile, expectedRevision: null, registry, resetSource ?? profile);

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (string.Equals(_name, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _name = normalized;
        NotifyChanged();
    }

    public void RecordShortcut(
        KeybindingEditorRowId rowId,
        IReadOnlyList<KeyStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        SetShortcut(rowId, new KeySequence(strokes));
    }

    public void SetShortcut(KeybindingEditorRowId rowId, KeySequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var draft = GetBinding(rowId);
        var template = draft.Current ?? draft.Reset ?? draft.Original
            ?? throw new InvalidOperationException($"Keybinding row '{rowId}' has no command metadata.");
        var replacement = new CommandBinding(
            template.CommandId,
            sequence,
            template.Contexts,
            template.Arguments);
        if (BindingsEqual(draft.Current, replacement))
        {
            return;
        }

        draft.Current = replacement;
        NotifyChanged();
    }

    public KeybindingEditorRowId AddBinding(CommandBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var rowId = NextRowId();
        _bindings.Add(new DraftBinding(rowId, original: binding, reset: null, binding));
        NotifyChanged();
        return rowId;
    }

    public void Unbind(KeybindingEditorRowId rowId)
    {
        var draft = GetBinding(rowId);
        if (draft.Current is null)
        {
            return;
        }

        draft.Current = null;
        NotifyChanged();
    }

    public void ResetShortcut(KeybindingEditorRowId rowId)
    {
        var draft = GetBinding(rowId);
        if (BindingsEqual(draft.Current, draft.Reset))
        {
            return;
        }

        draft.Current = draft.Reset;
        NotifyChanged();
    }

    public void SetPrefix(PrefixConfiguration? prefix)
    {
        if (Equals(_prefix, prefix))
        {
            return;
        }

        _prefix = prefix;
        NotifyChanged();
    }

    public void ResetPrefix() => SetPrefix(_resetSource.Prefix);

    public void ResetBindingsAndPrefix()
    {
        var changed = !Equals(_prefix, _resetSource.Prefix);
        _prefix = _resetSource.Prefix;
        foreach (var binding in _bindings)
        {
            if (BindingsEqual(binding.Current, binding.Reset))
            {
                continue;
            }

            binding.Current = binding.Reset;
            changed = true;
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    public IReadOnlyList<KeybindingEditorRow> Search(string? query)
    {
        var rows = Evaluate().Rows;
        if (string.IsNullOrWhiteSpace(query))
        {
            return rows;
        }

        var term = query.Trim();
        return [.. rows.Where(row => Matches(row, term))];
    }

    /// <summary>
    /// Returns the complete durable representation, including unknown commands and validation errors.
    /// This is suitable for preview and export; use <see cref="CreateSaveRequest"/> before persistence.
    /// </summary>
    public KeymapProfile CreateDraftProfile() => new(
        _initialProfile.Id,
        _name,
        _initialProfile.Layer,
        [.. _bindings
            .Where(binding => binding.Current is not null)
            .Select(binding => binding.Current!)],
        _prefix,
        _initialProfile.BasedOn);

    public KeybindingSettingsSaveRequest CreateSaveRequest()
    {
        var blockingIssues = Evaluate().Issues
            .Where(issue => issue.Severity == KeymapIssueSeverity.Error)
            .ToArray();
        if (blockingIssues.Length > 0)
        {
            throw new InvalidOperationException(
                $"Resolve {blockingIssues.Length} keybinding conflict(s) before saving. "
                + string.Join(' ', blockingIssues.Select(issue => issue.Message)));
        }

        return new KeybindingSettingsSaveRequest(CreateDraftProfile(), ExpectedRevision);
    }

    private void AddInitialBindings(KeymapProfile profile, KeymapProfile resetSource)
    {
        var resetCandidates = resetSource.Bindings
            .Select(binding => new ResetCandidate(binding))
            .ToArray();

        foreach (var binding in profile.Bindings)
        {
            var candidate = resetCandidates.FirstOrDefault(item =>
                !item.IsMatched && HasSameIdentity(binding, item.Binding));
            CommandBinding? reset;
            if (candidate is not null)
            {
                candidate.IsMatched = true;
                reset = candidate.Binding;
            }
            else
            {
                // A downgrade cannot silently erase bindings for commands it does not understand.
                reset = _registry.Contains(binding.CommandId) ? null : binding;
            }

            _bindings.Add(new DraftBinding(NextRowId(), binding, reset, binding));
        }

        foreach (var candidate in resetCandidates.Where(item => !item.IsMatched))
        {
            _bindings.Add(new DraftBinding(NextRowId(), original: null, candidate.Binding, current: null));
        }
    }

    // The projection maps validator indexes back to stable row IDs. UI sorting and filtering can
    // therefore change freely without making conflict messages point at the wrong binding.
    private EditorEvaluation Evaluate()
    {
        var activeBindings = _bindings
            .Where(binding => binding.Current is not null)
            .ToArray();
        var issues = KeymapConflictValidator
            .Validate(CreateDraftProfile(), _registry)
            .Select(issue => new KeybindingEditorIssue(
                issue.Severity,
                issue.Kind,
                activeBindings[issue.BindingIndex].Id,
                issue.OtherBindingIndex is { } otherIndex ? activeBindings[otherIndex].Id : null,
                issue.Message))
            .ToArray();

        var rows = _bindings.Select(binding =>
            {
                var template = binding.Current ?? binding.Reset ?? binding.Original
                    ?? throw new InvalidOperationException($"Keybinding row '{binding.Id}' has no command metadata.");
                var isKnown = _registry.TryGet(template.CommandId, out var command) && command is not null;
                var rowIssues = issues.Where(issue =>
                        issue.RowId == binding.Id || issue.OtherRowId == binding.Id)
                    .ToArray();
                return new KeybindingEditorRow(
                    binding.Id,
                    template.CommandId,
                    command?.Title ?? template.CommandId.Value,
                    command?.Category ?? "Unknown",
                    template.Contexts,
                    binding.Current?.Sequence,
                    template.Arguments,
                    !isKnown,
                    !BindingsEqual(binding.Current, binding.Reset),
                    rowIssues);
            })
            .OrderBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id.Value)
            .ToArray();
        return new EditorEvaluation(rows, issues);
    }

    private DraftBinding GetBinding(KeybindingEditorRowId rowId) =>
        _bindings.SingleOrDefault(binding => binding.Id == rowId)
        ?? throw new KeyNotFoundException($"Keybinding row '{rowId}' does not exist.");

    private KeybindingEditorRowId NextRowId() => new(_nextRowId++);

    private void NotifyChanged()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(KeybindingEditorRow row, string query)
    {
        var state = string.Join(
            ' ',
            row.IsUnknownCommand ? "unknown" : string.Empty,
            row.HasBlockingConflict ? "conflict" : string.Empty,
            row.IsBound ? "active" : "unbound");
        if (row.CommandId.Value.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Shortcut.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Contexts.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || state.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return row.Arguments.Any(argument =>
            argument.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || argument.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSameIdentity(CommandBinding left, CommandBinding right) =>
        left.CommandId == right.CommandId
        && left.Contexts == right.Contexts
        && DictionariesEqual(left.Arguments, right.Arguments);

    // Domain records intentionally expose collection interfaces, so draft equality must compare
    // their contents instead of relying on collection reference equality.
    private static bool BindingsEqual(CommandBinding? left, CommandBinding? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && left.CommandId == right.CommandId
            && left.Contexts == right.Contexts
            && left.Sequence.Equals(right.Sequence)
            && DictionariesEqual(left.Arguments, right.Arguments);
    }

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(item => right.TryGetValue(item.Key, out var value)
            && string.Equals(item.Value, value, StringComparison.Ordinal));

    private static bool ProfilesEqual(KeymapProfile left, KeymapProfile right) =>
        left.Id == right.Id
        && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && left.Layer == right.Layer
        && left.BasedOn == right.BasedOn
        && Equals(left.Prefix, right.Prefix)
        && left.Bindings.Count == right.Bindings.Count
        && left.Bindings.Zip(right.Bindings).All(pair => BindingsEqual(pair.First, pair.Second));

    private sealed class DraftBinding(
        KeybindingEditorRowId id,
        CommandBinding? original,
        CommandBinding? reset,
        CommandBinding? current)
    {
        public KeybindingEditorRowId Id { get; } = id;

        public CommandBinding? Original { get; } = original;

        public CommandBinding? Reset { get; } = reset;

        public CommandBinding? Current { get; set; } = current;
    }

    private sealed class ResetCandidate(CommandBinding binding)
    {
        public CommandBinding Binding { get; } = binding;

        public bool IsMatched { get; set; }
    }

    private sealed record EditorEvaluation(
        IReadOnlyList<KeybindingEditorRow> Rows,
        IReadOnlyList<KeybindingEditorIssue> Issues);
}
