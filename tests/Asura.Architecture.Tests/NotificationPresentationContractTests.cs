using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class NotificationPresentationContractTests
{
    private static readonly string RepositoryRoot =
        ApplicationViewCatalog.Load().RepositoryRoot;

    [Fact]
    public void Every_runtime_panel_routes_its_unread_state_into_shared_chrome()
    {
        var viewsDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "RuntimePanels");
        var panelViews = Directory
            .EnumerateFiles(viewsDirectory, "*PanelView.axaml")
            .Select(XDocument.Load)
            .Select(document => Assert.IsType<XElement>(document.Root))
            .Select(root => root.DescendantsAndSelf()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "PanelChrome", StringComparison.Ordinal)))
            .OfType<XElement>()
            .ToArray();

        Assert.NotEmpty(panelViews);
        Assert.All(
            panelViews,
            chrome =>
            {
                Assert.Equal(
                    "{Binding HasAttention}",
                    AttributeValue(chrome, "HasAttention"));
                Assert.Equal(
                    "{Binding IsNotificationPulseActive}",
                    AttributeValue(chrome, "IsNotificationPulseActive"));
            });
    }

    [Fact]
    public void Shared_panel_chrome_draws_the_exact_source_indicator()
    {
        var designSystem = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "Styles",
            "DesignSystem.axaml"));

        Assert.Contains(
            designSystem.Descendants(),
            element => string.Equals(element.Name.LocalName, "SignalDot"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible")
, "{TemplateBinding HasAttention}", StringComparison.Ordinal));
        Assert.Contains(
            designSystem.Descendants(),
            element => string.Equals(AttributeValue(element, "Name")
, "PART_NotificationPulse"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "IsVisible"), "False", StringComparison.Ordinal));
        Assert.Contains(
            designSystem.Descendants(),
            element => element.Name.LocalName == "Style"
                && AttributeValue(element, "Selector")
                    == "^:notification-pulse /template/ Border#PART_NotificationPulse");
    }

    [Fact]
    public void Attention_indicators_use_the_legible_shared_size_token()
    {
        var applicationResources = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.App",
            "App.axaml.cs"));

        Assert.Contains(
            "Publish(\"ShellSignalDotSize\", Math.Round(resources.ControlMinHeight * 0.42));",
            applicationResources,
            StringComparison.Ordinal);
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
