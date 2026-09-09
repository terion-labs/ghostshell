using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class FileTransferOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_file_transfer_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.FileTransferState),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(FileTransferViewModel), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(FileTransferViewModel));
    }

    [Fact]
    public void Queue_mutation_projection_and_subscription_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("FileTransferViewModel.cs");

        Assert.DoesNotContain("_fileTransferQueue.EnqueueAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("TransfersChanged +=", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SynchronizeFileTransfers", root, StringComparison.Ordinal);
        Assert.Contains("_queue.EnqueueAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_queue.TransfersChanged +=", owner, StringComparison.Ordinal);
        Assert.Contains("_queue.TransfersChanged -=", owner, StringComparison.Ordinal);
        Assert.Contains("private void Synchronize", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Transfer_owner_has_no_runtime_workspace_or_panel_dependency()
    {
        var owner = Read("FileTransferViewModel.cs");
        Assert.DoesNotContain("RuntimeWorkspace", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimePanel", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("FileRuntimePanelViewModel", owner, StringComparison.Ordinal);
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(FileTransferViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
