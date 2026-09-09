using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class FileProviderSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_file_provider_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.FileProviderSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(FileProviderSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(FileProviderSettingsViewModel));
    }

    [Fact]
    public void Projection_editor_persistence_and_subscription_live_in_the_owner()
    {
        var root = ReadViewModel("MainWindowViewModel.cs");
        var owner = ReadViewModel("FileProviderSettingsViewModel.cs");

        Assert.DoesNotContain("new FileProviderProfileEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveFileProviderProfileAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("OnFileProviderProfilesChanged", root, StringComparison.Ordinal);
        Assert.Contains("new FileProviderProfileEditorViewModel", owner, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveFileProviderProfileAsync", owner, StringComparison.Ordinal);
        Assert.Contains("ProfilesChanged += OnProfilesChanged", owner, StringComparison.Ordinal);
        Assert.Contains("ProfilesChanged -= OnProfilesChanged", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void File_provider_settings_owner_has_no_runtime_panel_transfer_or_secret_effects()
    {
        var source = ReadViewModel("FileProviderSettingsViewModel.cs");

        Assert.DoesNotContain("IFilePanelClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileTransferQueueClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTab", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchFileProviderAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_launch_remains_root_while_secrets_and_transfers_have_owners()
    {
        var root = ReadViewModel("MainWindowViewModel.cs");
        var secrets = ReadViewModel("SecretSettingsViewModel.cs");
        var transfers = ReadViewModel("FileTransferViewModel.cs");

        Assert.Contains("LaunchFileProviderAsync", root, StringComparison.Ordinal);
        Assert.Contains("AddFileProviderPanelAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_secretVault.CreateAsync", root, StringComparison.Ordinal);
        Assert.Contains("CreateFileProviderAsync", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshFileTransfers", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_fileTransferQueue.EnqueueAsync", root, StringComparison.Ordinal);
        Assert.Contains("_queue.EnqueueAsync", transfers, StringComparison.Ordinal);
    }

    private static string ReadViewModel(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
