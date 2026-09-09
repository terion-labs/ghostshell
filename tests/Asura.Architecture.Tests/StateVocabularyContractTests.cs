using System.Text.RegularExpressions;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class StateVocabularyContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly string[] RequiredKinds =
    [
        "Empty",
        "NoResults",
        "Loading",
        "Offline",
        "PermissionRequired",
        "Unsupported",
        "Stale",
        "Partial",
        "Conflict",
        "Retry",
        "TerminalError",
        "Cancelled",
        "DestructiveAction",
    ];

    [Fact]
    public void The_shared_control_and_visual_gallery_cover_every_state()
    {
        var presentation = ReadControl("StateOverlayPresentation.cs");
        var gallery = ReadView("DesignSystemGalleryWindow.axaml");

        foreach (var kind in RequiredKinds)
        {
            Assert.Contains($"StateOverlayKind.{kind}", presentation, StringComparison.Ordinal);
            Assert.Contains($"Kind=\"{kind}\"", gallery, StringComparison.Ordinal);
        }

        Assert.Contains("AutomationLiveSetting", presentation, StringComparison.Ordinal);
        Assert.Contains("Symbol Glyph", presentation, StringComparison.Ordinal);
        Assert.Contains("SurfaceTone Tone", presentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_product_areas_use_the_typed_state_control()
    {
        AssertArea(
            ReadView("Components", "LauncherView.axaml"),
            "Kind=\"Loading\"",
            "Kind=\"Retry\"",
            "Kind=\"NoResults\"");
        AssertArea(
            ReadView("SettingsView.axaml"),
            "Kind=\"Conflict\"",
            "Kind=\"Loading\"",
            "Kind=\"Retry\"",
            "No MCP server configured");
        AssertArea(
            ReadView("RuntimePanels", "FileRuntimePanelView.axaml"),
            "Kind=\"Loading\"",
            "Kind=\"Retry\"",
            "Kind=\"Empty\"",
            "Kind=\"NoResults\"");
        AssertArea(
            ReadView("RuntimePanels", "BrowserRuntimePanelView.axaml"),
            "Kind=\"Loading\"",
            "Kind=\"TerminalError\"");
        AssertArea(
            ReadView("AgentWorkspaceView.axaml"),
            "Kind=\"PermissionRequired\"",
            "Kind=\"Offline\"",
            "Kind=\"Retry\"");
        AssertArea(
            ReadView("RecoveryDataControlView.axaml"),
            "Kind=\"Loading\"",
            "Kind=\"Empty\"",
            "Kind=\"Retry\"");
    }

    [Fact]
    public void Every_state_overlay_declares_its_meaning()
    {
        var application = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App");
        foreach (var file in Directory.EnumerateFiles(
                     application,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(
                         source,
                         "<controls:StateOverlay(?<attributes>.*?)>",
                         RegexOptions.Singleline | RegexOptions.CultureInvariant,
                         TimeSpan.FromSeconds(1)))
            {
                var attributes = match.Groups["attributes"].Value;
                var isProgrammaticConfirmation = attributes.Contains(
                    "x:Name=\"ConfirmationState\"",
                    StringComparison.Ordinal);
                Assert.True(
                    attributes.Contains("Kind=\"", StringComparison.Ordinal)
                    || isProgrammaticConfirmation,
                    $"{Path.GetRelativePath(ApplicationViews.RepositoryRoot, file)} "
                    + "contains a StateOverlay without an explicit state kind.");
            }
        }
    }

    [Fact]
    public void Destructive_confirmations_use_the_typed_state_and_name_both_consequences()
    {
        var dialog = ReadView("ConfirmationDialog.axaml.cs");
        var catalog = ReadView("Confirmations.cs");

        Assert.Contains("StateOverlayKind.DestructiveAction", dialog, StringComparison.Ordinal);
        Assert.Contains("Active sessions stay open", catalog, StringComparison.Ordinal);
        Assert.Contains("Saved connections", catalog, StringComparison.Ordinal);
        Assert.Contains("cannot be restored", catalog, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertArea(string source, params string[] required)
    {
        Assert.Contains("<controls:StateOverlay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<controls:EmptyStatePanel", source, StringComparison.Ordinal);
        foreach (var marker in required)
        {
            Assert.Contains(marker, source, StringComparison.Ordinal);
        }
    }

    private static string ReadControl(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        "Controls",
        fileName));

    private static string ReadView(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        "Views",
        Path.Combine(path)));
}
