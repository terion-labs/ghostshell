using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Components;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class DatabaseDiagramClipboardHeadlessTests
{
    [Theory]
    [InlineData("detached")]
    [InlineData("panel-changed")]
    [InlineData("diagram-changed")]
    [InlineData("newer-copy")]
    [InlineData("unchanged")]
    public async Task ActualCopyActionChecksViewSessionAndRequestBeforePublishing(string change)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var headless = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await headless.Dispatch(async () =>
            {
                using var panel = new DatabaseRuntimePanelViewModel(PanelInstanceId.New(), "Database",
                    new DatabaseRuntimePanelViewModelTests.FakeDatabasePanelClient(), "sqlite", "Data Source=demo.db");
                await panel.Initialization;
                var view = new DatabaseWorkspaceView { DataContext = panel };
                var window = new Window { Content = view, Width = 1000, Height = 700 };
                window.Show();
                try
                {
                    await panel.ShowDatabaseDiagramAsync();
                    var diagram = Assert.IsType<DatabaseRuntimePanelViewModelTests.FakeDiagramSession>(panel.DiagramSession);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    diagram.ExportGate = release.Task;
                    var clipboard = window.Clipboard!;
                    await clipboard.SetTextAsync("existing clipboard");
                    var copying = view.CopyDiagramAsync(DatabaseDiagramExport.Svg);
                    switch (change)
                    {
                        case "detached": window.Content = null; break;
                        case "panel-changed": view.DataContext = null; break;
                        case "diagram-changed": panel.SuspendDatabaseDiagram(); break;
                        case "newer-copy":
                            diagram.ExportGate = null;
                            await view.CopyDiagramAsync(DatabaseDiagramExport.MermaidMarkdown);
                            break;
                    }
                    release.SetResult();
                    await copying;
                    Assert.Equal(change is "unchanged" or "newer-copy" ? "complete diagram" : "existing clipboard",
                        await clipboard.TryGetTextAsync());
                    return true;
                }
                finally { window.Close(); }
            }, timeout.Token));
        }
        finally { await headless.DisposeAsync(); }
    }
}
