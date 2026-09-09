using System.ComponentModel;
using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Git;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class GitRuntimePanelView : UserControl
{
    private bool _isSyncingSelection;
    private GitRuntimePanelViewModel? _observedViewModel;

    public GitRuntimePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        GitSidebar.PropertyChanged += OnSidebarPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _observedViewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _observedViewModel = ViewModel;
        _observedViewModel?.PropertyChanged += OnViewModelPropertyChanged;
        PresentChangeSelection();
        PresentSidebarCollapse();
    }

    private void OnSidebarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.Property == IsVisibleProperty)
        {
            PresentSidebarCollapse();
        }
    }

    // The container query hides the sidebar in markup, but only the view
    // model can withdraw the column's width and minimum; the view reports
    // what the query decided.
    private void PresentSidebarCollapse()
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsSidebarCollapsed = !GitSidebar.IsVisible;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (string.Equals(e.PropertyName, nameof(GitRuntimePanelViewModel.SelectedChange), StringComparison.Ordinal))
        {
            PresentChangeSelection();
        }
    }

    // A refresh rebuilds the item view models, so the view re-derives which
    // list visually holds the selection from the view model's choice.
    private void PresentChangeSelection()
    {
        if (_isSyncingSelection)
        {
            return;
        }

        _isSyncingSelection = true;
        try
        {
            var selected = ViewModel?.SelectedChange;
            UnstagedList.SelectedItem = selected is { IsStaged: false } ? selected : null;
            StagedList.SelectedItem = selected is { IsStaged: true } ? selected : null;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private GitRuntimePanelViewModel? ViewModel => DataContext as GitRuntimePanelViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        CloseRequested?.Invoke(this, e);
    }

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation)
    {
        _ = sender;
        SplitRequested?.Invoke(this, orientation);
    }

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e)
    {
        _ = sender;
        ConnectionSelected?.Invoke(this, e);
    }

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        NewConnectionRequested?.Invoke(this, e);
    }

    private void OnToggleBranchesSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsBranchesSectionExpanded = !viewModel.IsBranchesSectionExpanded;
        }
    }

    private void OnToggleRemotesSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsRemotesSectionExpanded = !viewModel.IsRemotesSectionExpanded;
        }
    }

    private void OnToggleTagsSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsTagsSectionExpanded = !viewModel.IsTagsSectionExpanded;
        }
    }

    private void OnToggleStashesSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsStashesSectionExpanded = !viewModel.IsStashesSectionExpanded;
        }
    }

    private void OnToggleWorktreesSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsWorktreesSectionExpanded = !viewModel.IsWorktreesSectionExpanded;
        }
    }

    private void OnToggleSubmodulesSection(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.IsSubmodulesSectionExpanded = !viewModel.IsSubmodulesSectionExpanded;
        }
    }

    private void OnLocalChangesClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.Section = GitPanelSection.LocalChanges;
        }
    }

    private void OnAllCommitsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.Section = GitPanelSection.AllCommits;
        }
    }

    private void OnCommitTabClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.DetailTab = GitCommitDetailTab.Commit;
        }
    }

    private void OnChangesTabClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.DetailTab = GitCommitDetailTab.Changes;
        }
    }

    private void OnFileTreeTabClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.DetailTab = GitCommitDetailTab.FileTree;
        }
    }

    private void OnParentShaClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { DataContext: string sha } && ViewModel is { } viewModel)
        {
            viewModel.SelectCommitBySha(sha);
        }
    }

    private void OnCommitChangeRowClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { DataContext: GitChangeItemViewModel change }
            && ViewModel is { } viewModel)
        {
            viewModel.OpenCommitChange(change);
        }
    }

    private async void OnBrowseRepositoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { } viewModel
            || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        // Asura's own picker, browsing the panel's connection target, so
        // choosing a repository works over SSH exactly as it does locally.
        var path = await new GitRepositoryPickerDialog(viewModel.CreateRepositoryPicker())
            .ShowDialog<string?>(window);
        if (path is not null)
        {
            viewModel.RepositoryPathInput = path;
            await viewModel.OpenRepositoryAsync(path);
        }
    }

    private async void OnTrustRepositoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { UntrustedRepositoryPath: { } path } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitTrustRepository(path).ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.TrustRepositoryAsync();
        }
    }

    private void OnRepositoryPathKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter && ViewModel is { } viewModel)
        {
            e.Handled = true;
            _ = viewModel.OpenRepositoryAsync(viewModel.RepositoryPathInput);
        }
    }

    // One selection spans both working-set lists; picking rows in one list
    // withdraws the highlight from the other so the diff always follows the
    // most recently chosen file, while staging acts on the whole selection.
    private void OnUnstagedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        SyncChangeSelection(UnstagedList, StagedList, e, sourceIsStaged: false);
    }

    private void OnStagedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        SyncChangeSelection(StagedList, UnstagedList, e, sourceIsStaged: true);
    }

    private void SyncChangeSelection(
        ListBox source,
        ListBox other,
        SelectionChangedEventArgs e,
        bool sourceIsStaged)
    {
        if (_isSyncingSelection || ViewModel is not { } viewModel)
        {
            return;
        }

        IReadOnlyList<GitChangeItemViewModel> items = source.SelectedItems is { } selected
            ? [.. selected.OfType<GitChangeItemViewModel>()]
            : [];
        _isSyncingSelection = true;
        try
        {
            if (items.Count > 0)
            {
                other.SelectedItem = null;
            }

            if (sourceIsStaged)
            {
                viewModel.SelectedStagedItems = items;
                if (items.Count > 0)
                {
                    viewModel.SelectedUnstagedItems = [];
                }
            }
            else
            {
                viewModel.SelectedUnstagedItems = items;
                if (items.Count > 0)
                {
                    viewModel.SelectedStagedItems = [];
                }
            }

            // The diff anchor is the row added last, so Shift-extending a
            // selection keeps the comparison on the newest pick.
            var anchor = e.AddedItems.OfType<GitChangeItemViewModel>().LastOrDefault()
                ?? items.LastOrDefault();
            if (anchor is not null)
            {
                viewModel.SelectedChange = anchor;
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void OnUnstagedViewToggleClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.UnstagedViewIsTree = !viewModel.UnstagedViewIsTree;
        }
    }

    private void OnStagedViewToggleClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.StagedViewIsTree = !viewModel.StagedViewIsTree;
        }
    }

    private void OnUnstagedTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        SyncTreeSelection(sender, StagedTree, sourceIsStaged: false);
    }

    private void OnStagedTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = e;
        SyncTreeSelection(sender, UnstagedTree, sourceIsStaged: true);
    }

    private void SyncTreeSelection(object? sender, TreeView otherTree, bool sourceIsStaged)
    {
        if (_isSyncingSelection
            || sender is not TreeView source
            || source.SelectedItem is not GitChangeTreeNodeViewModel node
            || ViewModel is not { } viewModel)
        {
            return;
        }

        _isSyncingSelection = true;
        try
        {
            otherTree.SelectedItem = null;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        // Outside the guard: the anchor change must reach the hidden lists
        // through the usual presentation path.
        viewModel.SelectTreeChange(node, sourceIsStaged);
    }

    private void OnUnstagedDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = sender;
        _ = e;

        // The command re-checks its own guard, so a double-tap on an already
        // staged row is a no-op rather than an error.
        ViewModel?.StageCommand.Execute(null);
    }

    private void OnStagedDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel?.UnstageCommand.Execute(null);
    }

    private async void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { SelectedUnstagedItems.Count: > 0 } viewModel
            || TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var items = viewModel.SelectedUnstagedItems;
        var confirmed = await Confirmations.GitDiscardChange(items.Count, items[0].Path)
            .ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.DiscardAsync(items);
        }
    }

    private static GitRefItemViewModel? RefItem(object? sender) =>
        sender is Control { DataContext: GitRefItemViewModel item } ? item : null;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void OnBranchRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = e;

        // Checking out is navigation, not destruction: no confirmation.
        if (RefItem(sender) is { IsCurrent: false } item && ViewModel is { } viewModel)
        {
            await viewModel.CheckoutBranchAsync(item.Name);
        }
    }

    private async void OnCheckoutBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is { IsCurrent: false } item && ViewModel is { } viewModel)
        {
            await viewModel.CheckoutBranchAsync(item.Name);
        }
    }

    private async void OnCheckoutWorktreeClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { IsCurrent: false } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        // Prefill a sibling of the repository root named after the branch.
        var prefill = $"{viewModel.RepositoryRoot.TrimEnd('/')}-{item.Name.Replace('/', '-')}";
        var path = await new FileNameDialog(
                "Checkout as worktree",
                "Create worktree",
                prefill,
                $"“{item.Name}” is checked out into a new linked worktree at this path.")
            .ShowDialog<string?>(window);
        if (path is not null)
        {
            await viewModel.CheckoutAsWorktreeAsync(path, item.Name);
        }
    }

    private async void OnFastForwardBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;

        // Fast-forwarding cannot lose commits: no confirmation, like checkout.
        if (RefItem(sender) is { HasUpstream: true, Upstream: { } upstream } item
            && ViewModel is { } viewModel)
        {
            await viewModel.FastForwardBranchAsync(item.Name, upstream, item.IsCurrent);
        }
    }

    private async void OnPushBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { PushRemoteName: { } remote } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitPushBranch(item.Name, remote)
            .ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.PushBranchAsync(remote, item.Name);
        }
    }

    private async void OnRebaseBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { IsCurrent: false } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitRebaseBranch(viewModel.BranchName, item.Name)
            .ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.RebaseOntoAsync(item.Name);
        }
    }

    private async void OnNewTagAtBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var result = await new GitTagCreateDialog().ShowDialog<GitTagCreateResult?>(window);
        if (result is not null)
        {
            // The tag lands on the branch's tip, not on HEAD.
            await viewModel.CreateTagAsync(result.Name, result.Message, item.Name);
        }
    }

    private async void OnCopyBranchNameClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is { } item
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(item.Name);
        }
    }

    private async void OnMergeBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { IsCurrent: false } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitMergeBranch(item.Name, viewModel.BranchName)
            .ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.MergeBranchAsync(item.Name);
        }
    }

    private async void OnNewBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { } viewModel || OwnerWindow is not { } window)
        {
            return;
        }

        var name = await new FileNameDialog(
                "New branch",
                "Create branch",
                description: "The branch is created at HEAD and checked out. Git validates the name.")
            .ShowDialog<string?>(window);
        if (name is not null)
        {
            await viewModel.CreateBranchAsync(name);
        }
    }

    private async void OnRenameBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var name = await new FileNameDialog(
                "Rename branch",
                "Rename",
                item.Name,
                "Only the branch's own name changes; its upstream keeps pointing where it did.")
            .ShowDialog<string?>(window);
        if (name is not null && !string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            await viewModel.RenameBranchAsync(item.Name, name);
        }
    }

    private async void OnDeleteBranchClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { IsCurrent: false } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitDeleteBranch(item.Name).ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.DeleteBranchAsync(item.Name);
        }
    }

    private async void OnFetchRemoteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.FetchRemoteAsync(item.RemoteName);
        }
    }

    private async void OnAddRemoteClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { } viewModel || OwnerWindow is not { } window)
        {
            return;
        }

        var result = await new GitRemoteEditorDialog("Add remote", "Add remote")
            .ShowDialog<GitRemoteEditorResult?>(window);
        if (result is not null)
        {
            await viewModel.AddRemoteAsync(result.Name, result.Url);
        }
    }

    private async void OnEditRemoteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var remoteName = item.RemoteName;
        var url = viewModel.Remotes
            .FirstOrDefault(remote => string.Equals(remote.Name, remoteName, StringComparison.Ordinal))
            ?.FetchUrl;
        var result = await new GitRemoteEditorDialog("Edit remote", "Save", remoteName, url)
            .ShowDialog<GitRemoteEditorResult?>(window);
        if (result is not null)
        {
            await viewModel.EditRemoteAsync(remoteName, result.Name, result.Url);
        }
    }

    private async void OnRemoveRemoteClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitRemoveRemote(item.RemoteName)
            .ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.RemoveRemoteAsync(item.RemoteName);
        }
    }

    private async void OnCreateTagClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { } viewModel || OwnerWindow is not { } window)
        {
            return;
        }

        var result = await new GitTagCreateDialog().ShowDialog<GitTagCreateResult?>(window);
        if (result is not null)
        {
            await viewModel.CreateTagAsync(result.Name, result.Message);
        }
    }

    private async void OnDeleteTagClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (RefItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var remoteNames = viewModel.Remotes.Select(remote => remote.Name).ToArray();
        var result = await new GitTagDeleteDialog(item.Name, remoteNames)
            .ShowDialog<GitTagDeleteResult?>(window);
        if (result is not null)
        {
            await viewModel.DeleteTagAsync(item.Name, result.AlsoOnRemotes ? remoteNames : []);
        }
    }

    private async void OnStashClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { } viewModel || OwnerWindow is not { } window)
        {
            return;
        }

        var result = await new GitStashPushDialog().ShowDialog<GitStashPushResult?>(window);
        if (result is not null)
        {
            await viewModel.StashPushAsync(result.Message);
        }
    }

    private static GitStashItem? StashItem(object? sender) =>
        sender is Control { DataContext: GitStashItem item } ? item : null;

    private async void OnStashApplyClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (StashItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.StashApplyAsync(item.Reference);
        }
    }

    private async void OnStashPopClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (StashItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.StashPopAsync(item.Reference);
        }
    }

    private async void OnStashDropClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (StashItem(sender) is not { } item
            || ViewModel is not { } viewModel
            || OwnerWindow is not { } window)
        {
            return;
        }

        var confirmed = await Confirmations.GitDropStash(item.Subject).ShowDialog<bool>(window);
        if (confirmed)
        {
            await viewModel.StashDropAsync(item.Reference);
        }
    }
}
