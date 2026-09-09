using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>
/// A workspace as the rails and lists show it.
///
/// A class rather than a record because half of it is durable — what the
/// definition says — and half is what the workspace is doing right now. The
/// durable half is still replaced wholesale when the catalog changes; the
/// runtime half has to survive that and change without it, so it is observable
/// and the item identity is the thing that persists.
/// </summary>
public sealed class LauncherWorkspaceViewModel(
    WorkspaceId id,
    long revision,
    string name,
    string description,
    string accent,
    string initials,
    Symbol iconSymbol,
    int itemCount,
    bool isIsolated = false,
    bool isIsolationAvailable = true) : ObservableObject
{
    private bool _isOpen;
    private bool _isInFront;
    private bool _hasAttention;
    private bool _hasAgentActivity;
    private bool _canSaveLayout;

    public bool CanSaveLayout
    {
        get => _canSaveLayout;
        internal set => SetProperty(ref _canSaveLayout, value);
    }
    private bool _isIsolated = isIsolated;

    public WorkspaceId Id { get; } = id;

    public long Revision { get; } = revision;

    public string Name { get; } = name;

    public string Description { get; } = description;

    public string Accent { get; } = accent;

    public string Initials { get; } = initials;

    public Symbol IconSymbol { get; } = iconSymbol;

    public int ItemCount { get; } = itemCount;

    /// <summary>
    /// The active execution scope while the workspace is open; otherwise the
    /// isolation intent saved on its durable definition.
    /// </summary>
    public bool IsIsolated
    {
        get => _isIsolated;
        internal set
        {
            if (SetProperty(ref _isIsolated, value))
            {
                OnPropertyChanged(nameof(CanToggleIsolation));
            }
        }
    }

    /// <summary>
    /// An unavailable host may only turn an existing portable setting off.
    /// Open workspaces remain editable because the shell restarts them after
    /// the user confirms the execution-boundary change.
    /// </summary>
    public bool CanToggleIsolation =>
        isIsolationAvailable || IsIsolated;

    /// <summary>The Main workspace always exists; only the rest can go.</summary>
    public bool CanDelete => !string.Equals(
        Id.Value,
        WorkspaceDefinition.DefaultWorkspaceId,
        StringComparison.Ordinal);

    /// <summary>
    /// Whether this workspace is running. Open and in-front are separate
    /// states and read differently: several workspaces are alive at once, and
    /// only one of them is the one you are looking at.
    /// </summary>
    public bool IsOpen
    {
        get => _isOpen;
        internal set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(CanClose));
            }
        }
    }

    /// <summary>
    /// Whether the rail offers to end this workspace. Only something running
    /// can be ended, and the Main workspace is never offered: it is where
    /// closing anything else puts you back.
    /// </summary>
    public bool CanClose => IsOpen && CanDelete;

    public bool IsInFront
    {
        get => _isInFront;
        internal set => SetProperty(ref _isInFront, value);
    }

    /// <summary>Whether anything inside it asked to be noticed.</summary>
    public bool HasAttention
    {
        get => _hasAttention;
        internal set => SetProperty(ref _hasAttention, value);
    }

    /// <summary>Whether a governed agent is operating a panel in this workspace.</summary>
    public bool HasAgentActivity
    {
        get => _hasAgentActivity;
        internal set => SetProperty(ref _hasAgentActivity, value);
    }

    /// <summary>
    /// Whether the durable half would draw identically. The runtime flags are
    /// deliberately excluded: they live on the item that is already in the
    /// list, and a change to one must not cause it to be replaced.
    /// </summary>
    public bool PresentsSameAs(LauncherWorkspaceViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && IconSymbol == other.IconSymbol
        && ItemCount == other.ItemCount
        && IsIsolated == other.IsIsolated
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Description, other.Description, StringComparison.Ordinal)
        && string.Equals(Accent, other.Accent, StringComparison.Ordinal)
        && string.Equals(Initials, other.Initials, StringComparison.Ordinal);
}

public sealed record LauncherConnectionViewModel(
    ConnectionId Id,
    long Revision,
    string Name,
    string Kind,
    string Detail,
    string Status,
    bool CanOpen,
    IReadOnlyList<string> Tags,
    SavedConnectionFamily Family = SavedConnectionFamily.Terminal,
    string? DefinitionId = null)
{
    public bool HasTags => Tags.Count > 0;

    /// <summary>
    /// The durable definition id inside this card's family. Terminal cards use
    /// <see cref="Id"/>; file and database cards carry their own id here and
    /// keep <see cref="Id"/> only as a list-identity wrapper.
    /// </summary>
    public string TargetId => DefinitionId ?? Id.Value;

    /// <summary>
    /// Whether the card would look identical. Record equality cannot say: the
    /// tags are a list, which records compare by reference, so two cards built
    /// from the same connection are never equal and the launcher rebuilds every
    /// card on every catalog refresh.
    /// </summary>
    public bool PresentsSameAs(LauncherConnectionViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && Family == other.Family
        && CanOpen == other.CanOpen
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
        && string.Equals(Status, other.Status, StringComparison.Ordinal)
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);
}

public sealed record LauncherScreenViewModel(
    ScreenId Id,
    long Revision,
    string Name,
    string Description,
    string Layout,
    int PanelCount,
    IReadOnlyList<LauncherScreenPanelPreviewViewModel> PreviewPanels,
    string Summary)
{
    /// <summary>
    /// Whether the card would look identical; the preview panels are a list, so
    /// record equality would report every rebuild as a change.
    /// </summary>
    public bool PresentsSameAs(LauncherScreenViewModel other) =>
        other is not null
        && Id == other.Id
        && Revision == other.Revision
        && PanelCount == other.PanelCount
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Summary, other.Summary, StringComparison.Ordinal)
        && string.Equals(Layout, other.Layout, StringComparison.Ordinal)
        && PreviewPanels.SequenceEqual(other.PreviewPanels);
}

public sealed record LauncherScreenPanelPreviewViewModel(
    int Columns,
    int Rows,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan,
    bool IsPrimary);

public enum LauncherSearchResultKind
{
    CreatePanel,
    Command,
    Connection,
    Screen,
    Workspace,
    RecentSession,
}

public abstract record LauncherSearchTarget
{
    private LauncherSearchTarget()
    {
    }

    public sealed record Command : LauncherSearchTarget
    {
        public Command(
            CommandId id,
            IEnumerable<KeyValuePair<string, string>>? arguments = null)
        {
            Id = id;
            Arguments = arguments?.ToImmutableDictionary(StringComparer.Ordinal)
                ?? [];
            InvocationKey = Arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Aggregate(
                    string.Empty,
                    (key, argument) =>
                        $"{key}|{argument.Key.Length}:{argument.Key}{argument.Value.Length}:{argument.Value}");
        }

        public CommandId Id { get; }

        public IReadOnlyDictionary<string, string> Arguments { get; }

        public string InvocationKey { get; }
    }

    /// <summary>
    /// Whether two targets name the same thing.
    ///
    /// Record equality cannot answer it for a command: the arguments live in a
    /// dictionary, which records compare by reference, so two commands built from
    /// the same source are never equal. <see cref="Command.InvocationKey"/> exists
    /// precisely to identify one, and this is where it earns its keep — without it
    /// the command palette treated every refresh as a fresh set of results and
    /// rebuilt every row.
    /// </summary>
    public bool IdentifiesSameAs(LauncherSearchTarget? other) => (this, other) switch
    {
        (Command first, Command second) => first.Id == second.Id
            && string.Equals(first.InvocationKey, second.InvocationKey, StringComparison.Ordinal),
        _ => Equals(this, other),
    };

    public sealed record CreatePanel(PanelKind Kind) : LauncherSearchTarget;

    public sealed record Connection(ConnectionId Id) : LauncherSearchTarget;

    public sealed record FileConnection(FileProviderProfileId Id) : LauncherSearchTarget;

    public sealed record DatabaseConnection(DatabaseConnectionProfileId Id) : LauncherSearchTarget;

    public sealed record Screen(ScreenId Id) : LauncherSearchTarget;

    public sealed record Workspace(WorkspaceId Id) : LauncherSearchTarget;

    public sealed record RecentSession(SessionId Id) : LauncherSearchTarget;
}

public sealed record LauncherSearchResultViewModel(
    LauncherSearchTarget Target,
    Symbol IconSymbol,
    string Group,
    string Title,
    string Detail,
    string TrailingText,
    bool IsAvailable,
    string? UnavailableReason,
    IReadOnlyList<string> SearchTerms)
{
    /// <summary>
    /// Whether two results would present identically.
    ///
    /// Record equality cannot answer this: the search terms are an array, so two
    /// results built from the same source are never equal. Without it, every
    /// refresh looks like a fresh set of results and the palette rebuilds every
    /// row — which moves what the pointer is hovering while the pointer has not
    /// moved at all.
    /// </summary>
    public bool PresentsSameAs(LauncherSearchResultViewModel other) =>
        other is not null
        && Target.IdentifiesSameAs(other.Target)
        && IconSymbol == other.IconSymbol
        && string.Equals(Group, other.Group, StringComparison.Ordinal)
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
        && string.Equals(TrailingText, other.TrailingText, StringComparison.Ordinal)
        && IsAvailable == other.IsAvailable
        && string.Equals(UnavailableReason, other.UnavailableReason, StringComparison.Ordinal);

    public LauncherSearchResultKind Kind => Target switch
    {
        LauncherSearchTarget.CreatePanel => LauncherSearchResultKind.CreatePanel,
        LauncherSearchTarget.Command => LauncherSearchResultKind.Command,
        LauncherSearchTarget.Connection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.FileConnection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.DatabaseConnection => LauncherSearchResultKind.Connection,
        LauncherSearchTarget.Screen => LauncherSearchResultKind.Screen,
        LauncherSearchTarget.Workspace => LauncherSearchResultKind.Workspace,
        LauncherSearchTarget.RecentSession => LauncherSearchResultKind.RecentSession,
        _ => throw new ArgumentOutOfRangeException(nameof(Target), Target, null),
    };

    public string DisplayDetail => IsAvailable
        ? Detail
        : UnavailableReason ?? Detail;

    public bool HasTrailingText => TrailingText.Length > 0;
}
