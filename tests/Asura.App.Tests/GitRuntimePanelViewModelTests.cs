using Asura.App.ViewModels;
using Asura.Core;
using Asura.Git;

namespace Asura.App.Tests;

public sealed class GitRuntimePanelViewModelTests
{
    [Fact]
    public async Task OpeningARepositoryLoadsSnapshotHistoryAndDiff()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);

        await panel.OpenRepositoryAsync("/repo/inner");
        await panel.DiffLoading;

        Assert.Equal(PanelKind.Git, panel.Kind);
        Assert.True(panel.IsRepositoryOpen);
        Assert.Equal("/repo", panel.RepositoryRoot);
        Assert.Equal("dev", panel.BranchName);
        Assert.Equal("↑2 ↓1", panel.TrackingText);
        Assert.Equal(2, panel.UnstagedCount);
        Assert.Equal(1, panel.StagedCount);
        Assert.Equal(2, panel.Commits.Count);
        Assert.Equal("dev · ↑2 ↓1 · 3 changes", panel.StatusText);

        // The first unstaged change is selected and its diff parsed into rows.
        Assert.Equal("src/a.cs", panel.SelectedChange?.Path);
        Assert.True(panel.HasDiff);
        Assert.True(panel.DiffLines[0].IsHunkHeader);
        Assert.Contains(panel.DiffLines, line => line.IsAdded);
    }

    [Fact]
    public async Task StagingRefreshesOnlyTheWorkingSetAndTargetsTheSelectedPath()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        var snapshotReadsBefore = client.SnapshotReads;
        Assert.True(panel.StageCommand.CanExecute(null));
        panel.StageCommand.Execute(null);
        await WaitForAsync(() => client.StagedPaths.Count > 0);

        Assert.Equal(["src/a.cs"], client.StagedPaths);

        // Staging cannot move refs or remotes, so it answers with one status
        // read and never repeats the full six-command snapshot.
        await WaitForAsync(() => client.WorkingSetReads > 0);
        Assert.Equal(snapshotReadsBefore, client.SnapshotReads);
    }

    [Fact]
    public async Task StagingMovesTheItemBeforeGitAnswers()
    {
        var client = new FakeGitRepositoryClient { StageGate = new TaskCompletionSource() };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        panel.SelectedUnstagedItems = [.. panel.UnstagedItems];
        panel.StageCommand.Execute(null);

        // The optimistic move lands synchronously: the command has not
        // finished (the fake is still gated) and no refresh has run yet.
        Assert.Empty(client.StagedPaths);
        Assert.Equal(0, client.WorkingSetReads);
        Assert.Empty(panel.UnstagedItems);
        Assert.Contains(panel.StagedItems, item => item.Path == "src/a.cs");
        var predictedUntracked = Assert.Single(
            panel.StagedItems,
            item => item.Path == "new.txt");
        Assert.Equal("A", predictedUntracked.KindBadge);

        // Releasing the gate lets the mutation finish and reconciliation
        // replace the prediction with Git's own answer.
        client.StageGate.SetResult();
        await WaitForAsync(() => client.WorkingSetReads > 0);
        await WaitForAsync(() => panel.UnstagedItems.Count == 2);
        Assert.Equal(["src/a.cs", "new.txt"], client.StagedPaths);
    }

    [Fact]
    public async Task CommittingClearsTheComposerAndReloadsHistory()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        panel.CommitSubject = "panel commit";
        panel.CommitBody = "body";
        Assert.True(panel.CommitCommand.CanExecute(null));
        panel.CommitCommand.Execute(null);
        await WaitForAsync(() => client.Commits.Count > 0);

        var request = Assert.Single(client.Commits);
        Assert.Equal("panel commit", request.Subject);
        Assert.Equal("body", request.Body);
        await WaitForAsync(() => panel.CommitSubject.Length == 0);
        Assert.Equal("", panel.CommitBody);
    }

    [Fact]
    public async Task CheckingOutABranchRunsThroughTheGateAndRefreshes()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        var refreshesBefore = client.SnapshotReads;
        await panel.CheckoutBranchAsync("main");

        Assert.Equal(["checkout main"], client.RefOperations);
        Assert.True(client.SnapshotReads > refreshesBefore);
    }

    [Fact]
    public async Task StagingActsOnTheWholeWorkingSelection()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        panel.SelectedUnstagedItems = [.. panel.UnstagedItems];
        Assert.True(panel.StageCommand.CanExecute(null));
        panel.StageCommand.Execute(null);
        await WaitForAsync(() => client.StagedPaths.Count == 2);

        Assert.Equal(["src/a.cs", "new.txt"], client.StagedPaths);
    }

    [Fact]
    public async Task ReconcilingACorrectPredictionRaisesAndRepaintsNothing()
    {
        var client = new FakeGitRepositoryClient { StageGate = new TaskCompletionSource() };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        panel.SelectedUnstagedItems = [.. panel.UnstagedItems];
        panel.StageCommand.Execute(null);

        // Git will answer exactly what the optimistic move predicted.
        client.NextWorkingSet = new GitWorkingSet(
            0,
            client.CurrentHead,
            [.. panel.UnstagedItems.Select(item => item.Change)],
            [.. panel.StagedItems.Select(item => item.Change)]);

        var raised = new List<string>();
        panel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        var treeMutations = 0;
        panel.UnstagedTreeRoots.CollectionChanged += (_, _) => treeMutations++;
        panel.StagedTreeRoots.CollectionChanged += (_, _) => treeMutations++;

        client.StageGate.SetResult();
        await WaitForAsync(() => client.WorkingSetReads > 0);
        await WaitForAsync(() => !panel.IsMutating && !panel.IsRefreshing);

        // The reconciliation confirmed the prediction, so the UI-facing
        // surface stays untouched: no list swaps, no tree mutations, and no
        // diff re-request.
        string[] mustStaySilent =
        [
            nameof(GitRuntimePanelViewModel.UnstagedItems),
            nameof(GitRuntimePanelViewModel.StagedItems),
            nameof(GitRuntimePanelViewModel.SelectedChange),
            nameof(GitRuntimePanelViewModel.IsDiffLoading),
        ];
        Assert.DoesNotContain(raised, name => mustStaySilent.Contains(name, StringComparer.Ordinal));
        Assert.Equal(0, treeMutations);
    }

    [Fact]
    public async Task StagingTwoHundredFilesMovesThemBeforeGitIsAwaited()
    {
        var client = new FakeGitRepositoryClient
        {
            StageGate = new TaskCompletionSource(),
            UnstagedChangesOverride = [.. Enumerable.Range(0, 200).Select(index =>
                new GitFileChange(
                    $"src/area{index % 8}/file{index:D3}.cs",
                    null,
                    GitChangeKind.Modified,
                    GitChangeArea.Unstaged))],
        };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");
        Assert.Equal(200, panel.UnstagedCount);

        panel.SelectedUnstagedItems = [.. panel.UnstagedItems];
        panel.StageCommand.Execute(null);

        // The optimistic move completes synchronously inside Execute: every
        // row is already on the staged side while Git (the gated fake) has
        // not yet answered and no reconciliation has run. Render-side cost
        // cannot be measured headlessly; this pins the view-model contract
        // that a click never waits on Git.
        Assert.Equal(0, client.WorkingSetReads);
        Assert.Empty(panel.UnstagedItems);
        Assert.Equal(201, panel.StagedItems.Count);

        client.StageGate.SetResult();
        await WaitForAsync(() => client.WorkingSetReads > 0);
    }

    [Fact]
    public async Task SyncPullsThenPushesUnderOneGateHold()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/repo");

        var snapshotReadsBefore = client.SnapshotReads;
        await panel.SyncAsync();

        Assert.Equal(["pull", "push"], client.RefOperations);

        // Repository-wide gestures take the full snapshot refresh.
        Assert.True(client.SnapshotReads > snapshotReadsBefore);
    }

    [Fact]
    public void CollapsingTheSidebarWithdrawsItsWidthAndMinimumAndRestoresThem()
    {
        var client = new FakeGitRepositoryClient();
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);

        Assert.Equal(220, panel.SidebarColumnWidth.Value);
        Assert.Equal(160, panel.SidebarColumnMinWidth);

        panel.SidebarColumnWidth = new Avalonia.Controls.GridLength(300);
        panel.IsSidebarCollapsed = true;
        Assert.Equal(0, panel.SidebarColumnWidth.Value);
        Assert.Equal(0, panel.SidebarColumnMinWidth);

        panel.IsSidebarCollapsed = false;
        Assert.Equal(300, panel.SidebarColumnWidth.Value);
        Assert.Equal(160, panel.SidebarColumnMinWidth);
    }

    [Fact]
    public async Task FailuresSurfaceAsAnIssueInsteadOfThrowing()
    {
        var client = new FakeGitRepositoryClient { FailSnapshots = true };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);

        await panel.OpenRepositoryAsync("/repo");

        Assert.True(panel.HasIssue);
        Assert.Equal("Git unavailable", panel.IssueTitle);
    }

    [Fact]
    public async Task AnOwnershipRefusalOffersTrustAndTrustingReopens()
    {
        var client = new FakeGitRepositoryClient { RefuseOpenForOwnership = true };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);

        await panel.OpenRepositoryAsync("/srv/repo");

        Assert.False(panel.IsRepositoryOpen);
        Assert.True(panel.HasIssue);
        Assert.True(panel.CanTrustRepository);
        Assert.Equal("/srv/repo", panel.UntrustedRepositoryPath);

        await panel.TrustRepositoryAsync();

        // The trust op ran for the failing path, the retry opened the
        // repository, and the offer withdrew with the refusal it answered.
        Assert.Equal(["/srv/repo"], client.TrustedPaths);
        Assert.True(panel.IsRepositoryOpen);
        Assert.False(panel.CanTrustRepository);
        Assert.False(panel.HasIssue);
    }

    [Fact]
    public async Task AFailureOfAnotherKindWithdrawsTheTrustOffer()
    {
        var client = new FakeGitRepositoryClient { RefuseOpenForOwnership = true };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);
        await panel.OpenRepositoryAsync("/srv/repo");
        Assert.True(panel.CanTrustRepository);

        // The next open fails for an unrelated reason: trusting the earlier
        // path would no longer answer the callout the person is reading.
        client.RefuseOpenForOwnership = false;
        client.FailNextOpen = true;
        await panel.OpenRepositoryAsync("/elsewhere");

        Assert.True(panel.HasIssue);
        Assert.False(panel.CanTrustRepository);
    }

    [Fact]
    public async Task EffectiveGitUserFlowsFromTheOpenedHandle()
    {
        var client = new FakeGitRepositoryClient { OpenRunAsUser = "coder" };
        using var panel = new GitRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Git",
            client,
            BuiltInConnections.Local);

        await panel.OpenRepositoryAsync("/repo");

        Assert.Equal("coder", panel.EffectiveGitUser);
        Assert.True(panel.HasEffectiveGitUser);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
