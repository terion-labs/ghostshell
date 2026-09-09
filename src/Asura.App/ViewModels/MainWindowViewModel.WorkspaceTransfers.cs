using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<bool> MoveActiveTabToWorkspaceAsync(
        int destinationPosition,
        CancellationToken cancellationToken = default)
    {
        var source = RuntimeWorkspace;
        if (source?.ActiveTab is not { } movedTab
            || destinationPosition < 0
            || destinationPosition >= _openWorkspaces.Count
            || ReferenceEquals(source, _openWorkspaces[destinationPosition])
            || source.Tabs.Count <= 1)
        {
            return false;
        }

        var destination = _openWorkspaces[destinationPosition];
        if (!SharesExecutionScope(source, destination))
        {
            SetError(
                "Live tabs and panels cannot move between different workspace isolation scopes.");
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, source)
                || !source.Tabs.Contains(movedTab)
                || !_openWorkspaces.Contains(destination))
            {
                return false;
            }

            var sourceBefore = await RuntimeGraph.ObserveWorkspaceAsync(
                source.Id,
                linkedCancellation.Token);
            var destinationBefore = await RuntimeGraph.ObserveWorkspaceAsync(
                destination.Id,
                linkedCancellation.Token);
            if (sourceBefore is null
                || destinationBefore is null
                || sourceBefore.Revision != source.HostRevision
                || destinationBefore.Revision != destination.HostRevision
                || !WorkspaceTopologyMatches(
                    CaptureRuntimeWorkspaceGraph(source),
                    sourceBefore.Workspace)
                || !WorkspaceTopologyMatches(
                    CaptureRuntimeWorkspaceGraph(destination),
                    destinationBefore.Workspace)
                || sourceBefore.Workspace.Tabs.SingleOrDefault(
                    tab => tab.Id == movedTab.Id) is not { } movedGraph)
            {
                SetError("The runtime workspaces changed before the tab transfer could start.");
                return false;
            }

            var sourceIndex = source.Tabs.IndexOf(movedTab);
            var sourceTabs = sourceBefore.Workspace.Tabs
                .Where(tab => tab.Id != movedTab.Id)
                .ToArray();
            var sourceActiveTabId = sourceBefore.Workspace.ActiveTabId == movedTab.Id
                ? sourceTabs[Math.Min(sourceIndex, sourceTabs.Length - 1)].Id
                : sourceBefore.Workspace.ActiveTabId;
            var sourceAfter = new WorkspaceInstance(
                source.Id,
                source.Name,
                sourceTabs,
                sourceActiveTabId);
            var destinationAfter = new WorkspaceInstance(
                destination.Id,
                destination.Name,
                [.. destinationBefore.Workspace.Tabs, movedGraph],
                movedGraph.Id);
            WorkspaceTransferStatus = string.Empty;
            return await RuntimeGraph.TransferTabUnderGateAsync(
                source,
                destination,
                new TransferWorkspaceTabRequest(
                    WindowId,
                    sourceAfter,
                    sourceBefore.Revision,
                    WindowId,
                    destinationAfter,
                    destinationBefore.Revision,
                    movedTab.Id),
                () =>
                {
                    foreach (var panel in movedTab.Panels)
                    {
                        StopTrackingRecovery(panel);
                    }

                    source.Tabs.Remove(movedTab);
                    if (ReferenceEquals(source.ActiveTab, movedTab))
                    {
                        source.ActiveTab = source.Tabs[
                            Math.Min(sourceIndex, source.Tabs.Count - 1)];
                    }

                    destination.Tabs.Add(movedTab);
                    destination.ActiveTab = movedTab;
                    WorkspaceTransferStatus =
                        $"Moved tab “{movedTab.Title}” from “{source.Name}” "
                        + $"to “{destination.Name}”.";
                    Launcher.RefreshSearchResults();
                },
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> MoveActivePanelToWorkspaceAsync(
        int destinationPosition,
        CancellationToken cancellationToken = default)
    {
        var source = RuntimeWorkspace;
        var sourceTab = source?.ActiveTab;
        var movedPanel = sourceTab?.ActivePanel;
        if (source is null
            || sourceTab is null
            || movedPanel is null
            || sourceTab.Panels.Count <= 1
            || destinationPosition < 0
            || destinationPosition >= _openWorkspaces.Count
            || ReferenceEquals(source, _openWorkspaces[destinationPosition]))
        {
            return false;
        }

        var destination = _openWorkspaces[destinationPosition];
        if (!SharesExecutionScope(source, destination))
        {
            SetError(
                "Live tabs and panels cannot move between different workspace isolation scopes.");
            return false;
        }

        if (destination.ActiveTab is not { } destinationTab)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, source)
                || !sourceTab.Panels.Contains(movedPanel)
                || !_openWorkspaces.Contains(destination)
                || !destination.Tabs.Contains(destinationTab))
            {
                return false;
            }

            var sourceBefore = await RuntimeGraph.ObserveWorkspaceAsync(
                source.Id,
                linkedCancellation.Token);
            var destinationBefore = await RuntimeGraph.ObserveWorkspaceAsync(
                destination.Id,
                linkedCancellation.Token);
            var sourceGraphTab = sourceBefore?.Workspace.Tabs.SingleOrDefault(
                tab => tab.Id == sourceTab.Id);
            var destinationGraphTab = destinationBefore?.Workspace.Tabs.SingleOrDefault(
                tab => tab.Id == destinationTab.Id);
            var movedGraph = sourceGraphTab?.Panels.SingleOrDefault(
                panel => panel.Id == movedPanel.Id);
            if (sourceBefore is null
                || destinationBefore is null
                || sourceGraphTab is null
                || destinationGraphTab is null
                || movedGraph is null
                || sourceBefore.Revision != source.HostRevision
                || destinationBefore.Revision != destination.HostRevision
                || !WorkspaceTopologyMatches(
                    CaptureRuntimeWorkspaceGraph(source),
                    sourceBefore.Workspace)
                || !WorkspaceTopologyMatches(
                    CaptureRuntimeWorkspaceGraph(destination),
                    destinationBefore.Workspace))
            {
                SetError("The runtime workspaces changed before the panel transfer could start.");
                return false;
            }

            var sourcePanelIndex = sourceTab.Panels.IndexOf(movedPanel);
            var sourcePanels = sourceGraphTab.Panels
                .Where(panel => panel.Id != movedPanel.Id)
                .ToArray();
            var sourceActivePanelId = sourceGraphTab.ActivePanelId == movedPanel.Id
                ? sourcePanels[Math.Min(sourcePanelIndex, sourcePanels.Length - 1)].Id
                : sourceGraphTab.ActivePanelId;
            var sourceAfterTab = new TabInstance(
                sourceGraphTab.Id,
                sourceGraphTab.Title,
                sourcePanels,
                sourceActivePanelId);
            var destinationAfterTab = new TabInstance(
                destinationGraphTab.Id,
                destinationGraphTab.Title,
                [.. destinationGraphTab.Panels, movedGraph],
                movedGraph.Id);
            var sourceAfter = ReplaceRuntimeTab(
                sourceBefore.Workspace,
                sourceAfterTab,
                sourceBefore.Workspace.ActiveTabId);
            var destinationAfter = ReplaceRuntimeTab(
                destinationBefore.Workspace,
                destinationAfterTab,
                destinationTab.Id);
            WorkspaceTransferStatus = string.Empty;
            return await RuntimeGraph.TransferPanelUnderGateAsync(
                source,
                destination,
                new TransferWorkspacePanelRequest(
                    WindowId,
                    sourceAfter,
                    sourceBefore.Revision,
                    sourceTab.Id,
                    WindowId,
                    destinationAfter,
                    destinationBefore.Revision,
                    destinationTab.Id,
                    movedPanel.Id),
                () =>
                {
                    if (!sourceTab.TakePanelForTransfer(movedPanel.Id))
                    {
                        throw new InvalidOperationException(
                            "The panel changed before the host-approved transfer was applied.");
                    }

                    StopTrackingRecovery(movedPanel);
                    destinationTab.AddPanel(movedPanel);
                    destinationTab.ActivatePanel(movedPanel.Id);
                    destination.ActiveTab = destinationTab;
                    WorkspaceTransferStatus =
                        $"Moved panel “{movedPanel.Title}” from “{source.Name}” "
                        + $"to “{destination.Name}”.";
                    Launcher.RefreshSearchResults();
                },
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }
}
