using System.Collections.ObjectModel;
using System.Globalization;
using Asura.Git;

namespace Asura.App.ViewModels;

public enum GitPanelSection
{
    LocalChanges,
    AllCommits,
}

public enum GitCommitDetailTab
{
    Commit,
    Changes,
    FileTree,
}

/// <summary>
/// One node of a commit's file tree. Directories load their children the
/// first time they expand, so huge trees cost only what the user opens.
/// </summary>
public sealed class GitTreeNodeViewModel : ObservableObject
{
    private readonly Func<string, Task<IReadOnlyList<GitTreeNodeViewModel>>>? _loadChildren;
    private bool _isExpanded;
    private bool _isLoaded;

    public GitTreeNodeViewModel(
        string name,
        string path,
        bool isDirectory,
        Func<string, Task<IReadOnlyList<GitTreeNodeViewModel>>>? loadChildren)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(path);
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        _loadChildren = isDirectory ? loadChildren : null;
        if (_loadChildren is not null)
        {
            // An unexpanded directory holds a placeholder so the chevron
            // renders; the first expansion swaps it for the real children.
            Children.Add(Placeholder());
        }
    }

    private GitTreeNodeViewModel()
    {
        Name = "…";
        Path = "";
        IsDirectory = false;
        IsPlaceholder = true;
    }

    private static GitTreeNodeViewModel Placeholder() => new();

    public string Name { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    public bool IsPlaceholder { get; }

    public ObservableCollection<GitTreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded)
            {
                _isLoaded = true;
                _ = LoadChildrenAsync();
            }
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_loadChildren is null)
        {
            return;
        }

        var children = await _loadChildren(Path);
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }
    }
}

/// <summary>One changed file in the unstaged or staged list.</summary>
public sealed class GitChangeItemViewModel(GitFileChange change)
{
    public GitFileChange Change { get; } = change ?? throw new ArgumentNullException(nameof(change));

    public string Path => Change.Path;

    public string FileName
    {
        get
        {
            var separator = Change.Path.LastIndexOf('/');
            return separator >= 0 ? Change.Path[(separator + 1)..] : Change.Path;
        }
    }

    public string Directory
    {
        get
        {
            var separator = Change.Path.LastIndexOf('/');
            return separator >= 0 ? Change.Path[..separator] : "";
        }
    }

    public bool HasDirectory => Directory.Length > 0;

    /// <summary>Rename provenance, shown under the file name.</summary>
    public string? OriginDetail =>
        Change.OriginalPath is { } original ? $"from {original}" : null;

    public bool IsStaged => Change.Area == GitChangeArea.Staged;

    public bool IsConflicted => Change.Kind == GitChangeKind.Conflicted;

    public string KindBadge => Change.Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Copied => "C",
        GitChangeKind.TypeChanged => "T",
        GitChangeKind.Untracked => "?",
        GitChangeKind.Conflicted => "U",
        _ => "·",
    };

    public string KindLabel => Change.Kind switch
    {
        GitChangeKind.Modified => "Modified",
        GitChangeKind.Added => "Added",
        GitChangeKind.Deleted => "Deleted",
        GitChangeKind.Renamed => "Renamed",
        GitChangeKind.Copied => "Copied",
        GitChangeKind.TypeChanged => "Type changed",
        GitChangeKind.Untracked => "Untracked",
        GitChangeKind.Conflicted => "Conflicted",
        _ => "Changed",
    };

    public string AccessibleSummary => $"{KindLabel} {Change.Path}";

    // Tone groups for the badge styles: additions read as success, removals
    // as danger, conflicts as warning, everything else as a neutral edit.
    public bool IsAdditionLike => Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked;

    public bool IsRemovalLike => Change.Kind == GitChangeKind.Deleted;
}

/// <summary>
/// One node of a working-set section shown as a directory tree. Unlike the
/// commit tree, the whole working set is already in memory, so the tree is
/// built eagerly from the flat change list and rebuilt with every snapshot.
/// </summary>
public sealed class GitChangeTreeNodeViewModel : ObservableObject
{
    private bool _isExpanded = true;

    private GitChangeTreeNodeViewModel(string name, string relativePath)
    {
        Name = name;
        RelativePath = relativePath;
        IsDirectory = true;
    }

    private GitChangeTreeNodeViewModel(GitChangeItemViewModel item)
    {
        Item = item;
        Name = item.FileName;
        RelativePath = item.Path;
        IsDirectory = false;
    }

    /// <summary>
    /// Mutates a live tree to match the given flat list, touching only what
    /// actually changed. Kept nodes keep their instances — and with them
    /// their expansion state — because the bound TreeView does not
    /// virtualize: replacing the roots wholesale would rebuild every row on
    /// every stage, unstage, and reconciliation.
    /// </summary>
    public static void Reconcile(
        ObservableCollection<GitChangeTreeNodeViewModel> current,
        IReadOnlyList<GitChangeItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(current);
        ReconcileLevel(current, Build(items));
    }

    private static void ReconcileLevel(
        ObservableCollection<GitChangeTreeNodeViewModel> current,
        IReadOnlyList<GitChangeTreeNodeViewModel> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var target = desired[index];
            var existingIndex = IndexOfMatch(current, target, index);
            if (existingIndex < 0)
            {
                current.Insert(index, target);
                continue;
            }

            if (existingIndex != index)
            {
                current.Move(existingIndex, index);
            }

            if (target.IsDirectory)
            {
                ReconcileLevel(current[index].Children, target.Children);
            }
        }

        // Unmatched leftovers have all drifted past the desired length.
        while (current.Count > desired.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
    }

    // A file node only matches an identical change: a path whose kind moved
    // (say, modified to conflicted) gets a fresh node so its badge is right.
    private static int IndexOfMatch(
        ObservableCollection<GitChangeTreeNodeViewModel> current,
        GitChangeTreeNodeViewModel target,
        int startIndex)
    {
        for (var index = startIndex; index < current.Count; index++)
        {
            var node = current[index];
            if (node.IsDirectory == target.IsDirectory
                && string.Equals(node.RelativePath, target.RelativePath, StringComparison.Ordinal)
                && (node.IsDirectory || Equals(node.Item?.Change, target.Item?.Change)))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Folds a section's flat change list into a directory tree.</summary>
    public static IReadOnlyList<GitChangeTreeNodeViewModel> Build(
        IReadOnlyList<GitChangeItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var roots = new List<GitChangeTreeNodeViewModel>();
        var directories = new Dictionary<string, GitChangeTreeNodeViewModel>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var children = roots;
            var segments = item.Path.Split('/');
            var path = "";
            for (var index = 0; index < segments.Length - 1; index++)
            {
                path = path.Length == 0 ? segments[index] : $"{path}/{segments[index]}";
                if (!directories.TryGetValue(path, out var directory))
                {
                    directory = new GitChangeTreeNodeViewModel(segments[index], path);
                    directories.Add(path, directory);
                    children.Add(directory);
                }

                children = directory._childList;
            }

            children.Add(new GitChangeTreeNodeViewModel(item));
        }

        return Attach(roots);
    }

    // Children are collected into plain lists while building and moved into
    // the bound collections once, so the tree never renders half-built.
    private readonly List<GitChangeTreeNodeViewModel> _childList = [];

    private static IReadOnlyList<GitChangeTreeNodeViewModel> Attach(
        List<GitChangeTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var child in Attach(node._childList))
            {
                node.Children.Add(child);
            }
        }

        return nodes;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    /// <summary>The wrapped change; null on directory nodes.</summary>
    public GitChangeItemViewModel? Item { get; }

    public ObservableCollection<GitChangeTreeNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string FileName => Name;

    public string KindBadge => Item?.KindBadge ?? "";

    public bool IsAdditionLike => Item?.IsAdditionLike ?? false;

    public bool IsRemovalLike => Item?.IsRemovalLike ?? false;

    public bool IsConflicted => Item?.IsConflicted ?? false;

    public string AccessibleSummary => Item?.AccessibleSummary ?? $"Folder {RelativePath}";

    /// <summary>This node's own change plus every descendant file's.</summary>
    public IReadOnlyList<GitChangeItemViewModel> CollectItems()
    {
        var items = new List<GitChangeItemViewModel>();
        Collect(this, items);
        return items;

        static void Collect(GitChangeTreeNodeViewModel node, List<GitChangeItemViewModel> items)
        {
            if (node.Item is { } item)
            {
                items.Add(item);
            }

            foreach (var child in node.Children)
            {
                Collect(child, items);
            }
        }
    }
}

/// <summary>One commit row in the history list.</summary>
public sealed class GitCommitItemViewModel(GitCommitItem commit)
{
    public GitCommitItem Commit { get; } = commit ?? throw new ArgumentNullException(nameof(commit));

    /// <summary>
    /// This row's graph slice. Lane assignment is a forward pass, so rows
    /// keep their value when later pages append; it is set before display.
    /// </summary>
    public GitGraphRow? Graph { get; set; }

    public string Subject => Commit.Subject.Length > 0 ? Commit.Subject : "(no subject)";

    public string ShortSha => Commit.ShortSha;

    public string AuthorName => Commit.AuthorName;

    public bool IsMerge => Commit.ParentShas.Count > 1;

    public IReadOnlyList<string> RefNames => Commit.RefNames;

    public bool HasRefs => Commit.RefNames.Count > 0;

    public string AuthoredAtText =>
        Commit.AuthoredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string AccessibleSummary => $"{Subject} by {AuthorName}, {AuthoredAtText}, {ShortSha}";
}

/// <summary>
/// One row of the rendered diff. Hunk headers ride in the same list as
/// content lines so a single virtualized list presents the whole document.
/// </summary>
public sealed class GitDiffLineViewModel
{
    private GitDiffLineViewModel(
        string oldNumber,
        string newNumber,
        string text,
        bool isAdded,
        bool isRemoved,
        bool isHunkHeader)
    {
        OldNumberText = oldNumber;
        NewNumberText = newNumber;
        Text = text;
        IsAdded = isAdded;
        IsRemoved = isRemoved;
        IsHunkHeader = isHunkHeader;
    }

    public static GitDiffLineViewModel Hunk(string header) =>
        new("", "", header, isAdded: false, isRemoved: false, isHunkHeader: true);

    public static GitDiffLineViewModel Content(GitDiffLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new GitDiffLineViewModel(
            line.OldLineNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            line.NewLineNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            line.Text,
            line.Kind == GitDiffLineKind.Added,
            line.Kind == GitDiffLineKind.Removed,
            isHunkHeader: false);
    }

    public string OldNumberText { get; }

    public string NewNumberText { get; }

    public string Text { get; }

    public bool IsAdded { get; }

    public bool IsRemoved { get; }

    public bool IsHunkHeader { get; }

    public string MarkerText => IsAdded ? "+" : IsRemoved ? "−" : "";
}

/// <summary>
/// One row of the side-by-side diff. Removed lines sit left, added lines
/// right; a change block pairs its removal run with its addition run
/// index-wise, leaving the longer run's tail against a blank side.
/// </summary>
public sealed class GitDiffSplitRowViewModel
{
    private GitDiffSplitRowViewModel(
        string headerText,
        GitDiffLine? left,
        GitDiffLine? right)
    {
        HeaderText = headerText;
        IsHunkHeader = headerText.Length > 0;
        LeftNumberText = left?.OldLineNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        LeftText = left?.Text ?? "";
        LeftIsRemoved = left?.Kind == GitDiffLineKind.Removed;
        RightNumberText = right?.NewLineNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        RightText = right?.Text ?? "";
        RightIsAdded = right?.Kind == GitDiffLineKind.Added;
    }

    public static IReadOnlyList<GitDiffSplitRowViewModel> Build(IReadOnlyList<GitDiffHunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        var rows = new List<GitDiffSplitRowViewModel>();
        foreach (var hunk in hunks)
        {
            rows.Add(new GitDiffSplitRowViewModel(hunk.Header, null, null));
            var removed = new List<GitDiffLine>();
            var added = new List<GitDiffLine>();
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case GitDiffLineKind.Removed:
                        // A removal after additions starts a new change block.
                        if (added.Count > 0)
                        {
                            Flush(rows, removed, added);
                        }

                        removed.Add(line);
                        break;
                    case GitDiffLineKind.Added:
                        added.Add(line);
                        break;
                    default:
                        Flush(rows, removed, added);
                        rows.Add(new GitDiffSplitRowViewModel("", line, line));
                        break;
                }
            }

            Flush(rows, removed, added);
        }

        return rows;
    }

    private static void Flush(
        List<GitDiffSplitRowViewModel> rows,
        List<GitDiffLine> removed,
        List<GitDiffLine> added)
    {
        for (var index = 0; index < Math.Max(removed.Count, added.Count); index++)
        {
            rows.Add(new GitDiffSplitRowViewModel(
                "",
                index < removed.Count ? removed[index] : null,
                index < added.Count ? added[index] : null));
        }

        removed.Clear();
        added.Clear();
    }

    public string HeaderText { get; }

    public bool IsHunkHeader { get; }

    public string LeftNumberText { get; }

    public string LeftText { get; }

    public bool LeftIsRemoved { get; }

    public string RightNumberText { get; }

    public string RightText { get; }

    public bool RightIsAdded { get; }

    public string LeftMarkerText => LeftIsRemoved ? "−" : "";

    public string RightMarkerText => RightIsAdded ? "+" : "";
}

/// <summary>One ref row in the repository sidebar.</summary>
public sealed class GitRefItemViewModel(
    GitRefItem item,
    string? currentBranchName = null,
    string? defaultRemoteName = null)
{
    public GitRefItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));

    public string Name => Item.ShortName;

    public bool IsCurrent => Item.IsCurrent;

    /// <summary>The remote a remote-tracking ref belongs to.</summary>
    public string RemoteName
    {
        get
        {
            var separator = Item.ShortName.IndexOf('/', StringComparison.Ordinal);
            return Item.Kind == GitRefKind.RemoteBranch && separator > 0
                ? Item.ShortName[..separator]
                : Item.ShortName;
        }
    }

    // Menu labels live here so the markup can bind them: a DataTemplate has
    // no place to compose "merge X into Y" from two sources.
    public string CheckoutLabel => $"Checkout “{Name}”";

    public string MergeLabel => $"Merge “{Name}” into “{currentBranchName}”…";

    public string RebaseLabel => $"Rebase “{currentBranchName}” on “{Name}”…";

    public string FetchLabel => $"Fetch “{RemoteName}”";

    public string? Upstream => Item.Upstream;

    public bool HasUpstream => Item.Upstream is not null;

    public string FastForwardLabel => $"Fast-Forward to “{Item.Upstream}”";

    /// <summary>
    /// Where a push of this branch goes: its upstream's remote when it has
    /// one, the repository's first remote otherwise.
    /// </summary>
    public string? PushRemoteName
    {
        get
        {
            var separator = Item.Upstream?.IndexOf('/', StringComparison.Ordinal) ?? -1;
            return separator > 0 ? Item.Upstream![..separator] : defaultRemoteName;
        }
    }

    public bool HasPushRemote => PushRemoteName is not null;

    public string PushLabel => $"Push to “{PushRemoteName}”…";

    // One text run renders the whole "↑1 ↓3" cluster: arrows and digits
    // share a font and a baseline by construction, where icon-and-text
    // pairs drift apart line-box by line-box.
    public string TrackingText =>
        (Item.Ahead, Item.Behind) switch
        {
            (null, _) or (_, null) => "",
            (0, 0) => "",
            (var ahead, 0) => $"↑{ahead}",
            (0, var behind) => $"↓{behind}",
            var (ahead, behind) => $"↑{ahead} ↓{behind}",
        };

    public bool HasTracking => TrackingText.Length > 0;
}
