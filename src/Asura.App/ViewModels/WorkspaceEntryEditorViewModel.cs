using System.ComponentModel;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// Presents the three durable workspace entry variants through one ordered list while
/// retaining the variant-specific editor needed to rebuild the original union member.
/// </summary>
public sealed class WorkspaceEntryEditorViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceEntry _original;
    private string _alias;
    private ScreenConnectionOption? _selectedConnection;
    private WorkspaceScreenOption? _selectedScreen;
    private bool _disposed;

    private WorkspaceEntryEditorViewModel(
        WorkspaceEntry original,
        WorkspaceEditorEntryKind kind,
        ScreenConnectionOption? selectedConnection,
        WorkspaceScreenOption? selectedScreen,
        WorkspaceTabEditorViewModel? tab)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        Kind = kind;
        _selectedConnection = selectedConnection;
        _selectedScreen = selectedScreen;
        Tab = tab;
        _alias = original switch
        {
            WorkspaceEntry.ConnectionReference connection => connection.Alias ?? string.Empty,
            WorkspaceEntry.ScreenReference screen => screen.Alias ?? string.Empty,
            _ => string.Empty,
        };
        Tab?.PropertyChanged += OnTabChanged;
    }

    public WorkspaceEntryId Id => _original.Id;

    public WorkspaceEditorEntryKind Kind { get; }

    public bool IsConnection => Kind == WorkspaceEditorEntryKind.Connection;

    public bool IsSavedScreen => Kind == WorkspaceEditorEntryKind.SavedScreen;

    public bool IsWorkspaceTab => Kind == WorkspaceEditorEntryKind.WorkspaceTab;

    public bool CanEditAlias => !IsWorkspaceTab;

    public string KindLabel => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => "Connection",
        WorkspaceEditorEntryKind.SavedScreen => "Saved screen",
        WorkspaceEditorEntryKind.WorkspaceTab => "Workspace-only screen",
        _ => throw new ArgumentOutOfRangeException(),
    };

    /// <summary>
    /// Where this tab's definition lives. A reference stays in step with the
    /// saved connection or screen it points at; a workspace-only tab is a copy
    /// that nothing else shares — which is the difference that decides whether
    /// editing it elsewhere changes what this workspace opens.
    /// </summary>
    public string BadgeLabel => IsWorkspaceTab ? "Workspace-only" : "Saved";

    public bool IsWorkspaceOnly => IsWorkspaceTab;

    /// <summary>The row's mark: what kind of thing the tab opens, at a glance.</summary>
    public Symbol RowSymbol => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => Symbol.Server,
        WorkspaceEditorEntryKind.SavedScreen => Symbol.Grid,
        WorkspaceEditorEntryKind.WorkspaceTab => Symbol.Window,
        _ => Symbol.Window,
    };

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                PublishDisplayState();
            }
        }
    }

    public ScreenConnectionOption? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (!IsConnection)
            {
                throw new InvalidOperationException("Only connection entries select a connection.");
            }

            if (SetProperty(ref _selectedConnection, value))
            {
                PublishDisplayState();
            }
        }
    }

    public WorkspaceScreenOption? SelectedScreen
    {
        get => _selectedScreen;
        set
        {
            if (!IsSavedScreen)
            {
                throw new InvalidOperationException("Only saved-screen entries select a screen.");
            }

            if (SetProperty(ref _selectedScreen, value))
            {
                PublishDisplayState();
            }
        }
    }

    public WorkspaceTabEditorViewModel? Tab { get; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Alias))
            {
                return Alias.Trim();
            }

            return Kind switch
            {
                WorkspaceEditorEntryKind.Connection => SelectedConnection?.Name ?? "Missing connection",
                WorkspaceEditorEntryKind.SavedScreen => SelectedScreen?.Name ?? "Missing saved screen",
                WorkspaceEditorEntryKind.WorkspaceTab => Tab?.Name ?? "Workspace screen",
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
    }

    /// <summary>
    /// What the row says under its name. The name is already on screen, so this
    /// carries what the name does not — the transport, the layout — and only
    /// repeats the underlying name when an alias has replaced it above.
    /// </summary>
    public string Detail => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => SelectedConnection is not { IsAvailable: true } connection
            ? "Select a connection"
            : RepeatsName(connection.Name)
                ? connection.Kind
                : $"{connection.Name} · {connection.Kind}",
        WorkspaceEditorEntryKind.SavedScreen => SelectedScreen is not { IsAvailable: true } screen
            ? "Select a saved screen"
            : RepeatsName(screen.Name)
                ? screen.LayoutName
                : $"{screen.Name} · {screen.LayoutName}",
        WorkspaceEditorEntryKind.WorkspaceTab => Tab is null
            ? "Tab definition unavailable"
            : $"{Tab.SelectedLayout.DisplayName} · {Tab.Panels.Count} panel{(Tab.Panels.Count == 1 ? string.Empty : "s")}",
        _ => throw new ArgumentOutOfRangeException(),
    };

    /// <summary>
    /// Whether the row's own heading already says this. An alias that repeats
    /// the underlying name is the common case — the row would otherwise print
    /// the same words twice, once above the other.
    /// </summary>
    private bool RepeatsName(string name) =>
        string.IsNullOrWhiteSpace(Alias)
        || string.Equals(Alias.Trim(), name, StringComparison.OrdinalIgnoreCase);

    public bool HasMissingReference => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => SelectedConnection?.IsAvailable != true,
        WorkspaceEditorEntryKind.SavedScreen => SelectedScreen?.IsAvailable != true,
        WorkspaceEditorEntryKind.WorkspaceTab => Tab?.HasMissingDefinition != false,
        _ => true,
    };

    public string ReferenceStatus => HasMissingReference ? "Repair required" : "Available";

    internal static WorkspaceEntryEditorViewModel Create(
        WorkspaceEntry entry,
        IReadOnlyList<ScreenConnectionOption> connectionOptions,
        IReadOnlyList<WorkspaceScreenOption> screenOptions,
        IReadOnlyList<WorkspaceLayoutOption> layoutOptions,
        IReadOnlyList<ScreenFileProviderOption> fileProviderOptions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry switch
        {
            WorkspaceEntry.ConnectionReference connection => new(
                connection,
                WorkspaceEditorEntryKind.Connection,
                connectionOptions.Single(option => option.Id == connection.ConnectionId),
                null,
                null),
            WorkspaceEntry.ScreenReference screen => new(
                screen,
                WorkspaceEditorEntryKind.SavedScreen,
                null,
                screenOptions.Single(option => option.Id == screen.ScreenId),
                null),
            WorkspaceEntry.Tab tab => new(
                tab,
                WorkspaceEditorEntryKind.WorkspaceTab,
                null,
                null,
                new WorkspaceTabEditorViewModel(
                    tab,
                    layoutOptions,
                    connectionOptions,
                    fileProviderOptions)),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown workspace entry type."),
        };
    }

    internal WorkspaceEntry Build() => Kind switch
    {
        WorkspaceEditorEntryKind.Connection => new WorkspaceEntry.ConnectionReference(
            Id,
            SelectedConnection?.Id
                ?? ((WorkspaceEntry.ConnectionReference)_original).ConnectionId,
            Alias),
        WorkspaceEditorEntryKind.SavedScreen => new WorkspaceEntry.ScreenReference(
            Id,
            SelectedScreen?.Id
                ?? ((WorkspaceEntry.ScreenReference)_original).ScreenId,
            Alias),
        WorkspaceEditorEntryKind.WorkspaceTab => Tab?.Build()
            ?? throw new InvalidOperationException("A workspace tab requires its tab definition."),
        _ => throw new ArgumentOutOfRangeException(),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Tab is not null)
        {
            Tab.PropertyChanged -= OnTabChanged;
            Tab.Dispose();
        }

        _disposed = true;
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        PublishDisplayState();
    }

    private void PublishDisplayState()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(HasMissingReference));
        OnPropertyChanged(nameof(ReferenceStatus));
    }
}
