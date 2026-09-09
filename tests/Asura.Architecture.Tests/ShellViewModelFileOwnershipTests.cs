using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ShellViewModelFileOwnershipTests
{
    [Fact]
    public void Launcher_and_history_types_live_outside_the_mixed_shell_module()
    {
        var runtime = ReadViewModelSource("RuntimeWorkspaceViewModels.cs");
        var support = ReadShellSupportSources();
        var launcher = ReadViewModelSource("LauncherViewModels.cs");
        var history = ReadViewModelSource("RecentSessionHistoryViewModels.cs");

        string[] launcherDeclarations =
        [
            "public sealed class LauncherWorkspaceViewModel",
            "public sealed record LauncherConnectionViewModel",
            "public sealed record LauncherScreenViewModel",
            "public sealed record LauncherScreenPanelPreviewViewModel",
            "public enum LauncherSearchResultKind",
            "public abstract record LauncherSearchTarget",
            "public sealed record LauncherSearchResultViewModel",
        ];
        foreach (var declaration in launcherDeclarations)
        {
            Assert.Contains(declaration, launcher, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, runtime, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, support, StringComparison.Ordinal);
        }

        string[] historyDeclarations =
        [
            "public enum HistoryExportScope",
            "public sealed record HistoryRetentionOption",
        ];
        foreach (var declaration in historyDeclarations)
        {
            Assert.Contains(declaration, history, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, runtime, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, support, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Settings_presentation_types_live_outside_the_mixed_shell_module()
    {
        var runtime = ReadViewModelSource("RuntimeWorkspaceViewModels.cs");
        var support = ReadShellSupportSources();
        var settings = ReadViewModelSource("SettingsPresentationViewModels.cs");

        string[] settingsDeclarations =
        [
            "public sealed record AnsiSwatchViewModel",
            "public sealed record LayoutCardViewModel",
            "public sealed record ProductComponentViewModel",
            "public sealed record KeybindingRowViewModel",
            "public sealed record ThemeChromePreference",
        ];
        foreach (var declaration in settingsDeclarations)
        {
            Assert.Contains(declaration, settings, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, runtime, StringComparison.Ordinal);
            Assert.DoesNotContain(declaration, support, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runtime_types_live_in_a_runtime_only_module()
    {
        var runtime = ReadViewModelSource("RuntimeWorkspaceViewModels.cs");
        var support = ReadShellSupportSources();

        Assert.Contains(
            "public sealed class RuntimeWorkspaceViewModel",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed class RuntimeTabViewModel",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "public abstract class RuntimePanelViewModel",
            runtime,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspaceViewModel", support, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTabViewModel", support, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimePanelViewModel", support, StringComparison.Ordinal);
        Assert.False(File.Exists(ViewModelPath("ShellViewModels.cs")));
    }

    [Fact]
    public void Supporting_shell_rows_are_grouped_by_product_concern()
    {
        var transfers = ReadViewModelSource("SecretAndTransferViewModels.cs");
        var providers = ReadViewModelSource("ProviderProfileViewModels.cs");

        Assert.Contains("public sealed record SecretMetadataViewModel", transfers, StringComparison.Ordinal);
        Assert.Contains("public sealed class FileTransferItemViewModel", transfers, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderProfileItemViewModel", transfers, StringComparison.Ordinal);
        Assert.Contains("public sealed record FileProviderProfileItemViewModel", providers, StringComparison.Ordinal);
        Assert.Contains("public sealed record AiProviderProfileItemViewModel", providers, StringComparison.Ordinal);
        Assert.Contains("public sealed class McpServerProfileItemViewModel", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("FileTransferItemViewModel", providers, StringComparison.Ordinal);
    }

    private static string ReadShellSupportSources() => string.Concat(
        ReadViewModelSource("SecretAndTransferViewModels.cs"),
        ReadViewModelSource("ProviderProfileViewModels.cs"));

    private static string ReadViewModelSource(string fileName) =>
        File.ReadAllText(ViewModelPath(fileName));

    private static string ViewModelPath(string fileName) => Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            fileName);
}
