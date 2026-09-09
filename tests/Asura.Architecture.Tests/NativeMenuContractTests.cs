using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class NativeMenuContractTests
{
    private static readonly string RepositoryRoot =
        ApplicationViewCatalog.Load().RepositoryRoot;

    [Fact]
    public void Application_menu_uses_macOS_application_commands()
    {
        var application = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml"));
        var headers = application
            .Descendants()
            .Where(element => element.Name.LocalName == "NativeMenuItem")
            .Select(element => AttributeValue(element, "Header") ?? string.Empty)
            .ToArray();

        Assert.Equal(["About Asura…", "Settings…"], headers);
    }

    [Fact]
    public void Main_window_exports_standard_grouped_menus_and_new_window_shortcut()
    {
        var mainWindow = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "MainWindow.axaml"));
        var root = Assert.IsType<XElement>(mainWindow.Root);
        var menu = Assert.Single(
            root.Elements(),
            element => element.Name.LocalName == "NativeMenu.Menu");
        var topLevelHeaders = menu
            .Elements()
            .Single(element => element.Name.LocalName == "NativeMenu")
            .Elements()
            .Where(element => element.Name.LocalName == "NativeMenuItem")
            .Select(element => AttributeValue(element, "Header") ?? string.Empty)
            .ToArray();
        var newWindow = Assert.Single(
            menu.Descendants(),
            element => AttributeValue(element, "Header") == "New Window");

        Assert.Equal(["File", "Edit", "View", "Window"], topLevelHeaders);
        Assert.Equal("Meta+N", AttributeValue(newWindow, "Gesture"));
        Assert.Equal("OnNewWindowMenuClick", AttributeValue(newWindow, "Click"));
    }

    [Fact]
    public void Desktop_lifetime_is_explicit_and_closes_after_the_last_main_window()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml.cs"));

        Assert.Contains("ShutdownMode.OnExplicitShutdown", source, StringComparison.Ordinal);
        Assert.Contains("FirstOrDefault(window => !ReferenceEquals(window, mainWindow))", source, StringComparison.Ordinal);
        Assert.Contains("desktop.Shutdown();", source, StringComparison.Ordinal);
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;
}
