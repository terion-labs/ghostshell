using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns the optimistic-concurrency metadata draft shared by workspace and
/// saved-screen definition editors. Shell routing remains the host's concern.
/// </summary>
public sealed class DefinitionEditSessionViewModel : ObservableObject
{
    private readonly IDefinitionCatalog _catalog;
    private DefinitionKey? _definition;
    private long? _revision;
    private string _name = string.Empty;
    private string _description = string.Empty;

    public DefinitionEditSessionViewModel(IDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public bool HasSession => _definition is not null;

    public string EditorTitle => _definition?.Kind == WorkspaceDefinition.Kind
        ? "Edit workspace"
        : "Edit saved screen";

    public string EditorName
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string EditorDescription
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public void Begin(
        DefinitionKey definition,
        long? revision,
        string name,
        string? description)
    {
        _definition = definition;
        _revision = revision;
        EditorName = name;
        EditorDescription = description ?? string.Empty;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void Clear()
    {
        if (_definition is null
            && _revision is null
            && _name.Length == 0
            && _description.Length == 0)
        {
            return;
        }

        _definition = null;
        _revision = null;
        EditorName = string.Empty;
        EditorDescription = string.Empty;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public async ValueTask<DefinitionStoreResult<Unit>> SaveAsync(
        CancellationToken cancellationToken)
    {
        if (_definition is not { } key || _revision is not { } revision)
        {
            return Fail("Choose a workspace or saved screen to edit.");
        }

        if (key.Kind == WorkspaceDefinition.Kind)
        {
            var current = _catalog.Snapshot.Workspaces
                .Select(item => item.Value)
                .SingleOrDefault(item => item.Key == key);
            if (current is null)
            {
                return Fail("That workspace no longer exists.");
            }

            var updated = new WorkspaceDefinition(
                current.Id,
                current.SchemaVersion,
                RequireName(EditorName, current.Name),
                EditorDescription,
                current.Accent,
                current.Entries,
                current.AgentPolicyOverride,
                current.Icon,
                current.AutoSave,
                current.Color,
                current.AgentPanelPinned,
                current.TerminalMultiplexingOverride,
                current.BrowserProfileOverride,
                current.HasExplicitAccent,
                current.IsIsolated,
                current.IsolationMounts,
                current.IsolationImageReference,
                current.RunAgentInIsolation,
                current.NetworkOverride);
            return ToUnit(await _catalog.SaveWorkspaceAsync(
                updated,
                revision,
                cancellationToken));
        }

        if (key.Kind == ScreenDefinition.Kind)
        {
            var current = _catalog.Snapshot.Screens
                .Select(item => item.Value)
                .SingleOrDefault(item => item.Key == key);
            if (current is null)
            {
                return Fail("That saved screen no longer exists.");
            }

            var updated = new ScreenDefinition(
                current.Id,
                current.SchemaVersion,
                RequireName(EditorName, current.Name),
                EditorDescription,
                current.LayoutId,
                current.Panels,
                current.Tags,
                current.AgentPolicyOverride);
            return ToUnit(await _catalog.SaveScreenAsync(
                updated,
                revision,
                cancellationToken));
        }

        return Fail("This definition type cannot be edited here.");
    }

    private static DefinitionStoreResult<Unit> ToUnit<T>(
        DefinitionStoreResult<StoredDefinition<T>> result)
        where T : IDurableDefinition =>
        result.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(result.Error!);

    private static DefinitionStoreResult<Unit> Fail(string message) =>
        DefinitionStoreResult<Unit>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));

    private static string RequireName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
