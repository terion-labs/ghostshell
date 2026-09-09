using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class AgentApprovalCardViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Agent_workspace_delegates_the_pending_approval_to_one_safety_component()
    {
        var workspace = LoadView("AgentWorkspaceView");
        var approval = Assert.Single(
            workspace.Descendants(),
            element => string.Equals(element.Name.LocalName, "AgentApprovalCardView", StringComparison.Ordinal));

        Assert.Equal(
            "{Binding AgentChat.HasPendingApproval, FallbackValue=False}",
            AttributeValue(approval, "IsVisible"));
        Assert.Equal(
            "OnApproveAgentActionClick",
            AttributeValue(approval, "ApproveRequested"));
        Assert.Equal(
            "OnDenyAgentActionClick",
            AttributeValue(approval, "DenyRequested"));

        Assert.DoesNotContain(
            workspace.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Content"),
                    "Approve once",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Approval_component_keeps_exact_scope_and_explicit_keyboard_order()
    {
        var document = LoadComponent();
        var root = Assert.IsType<XElement>(document.Root);
        var card = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "SurfaceCard", StringComparison.Ordinal));

        Assert.Null(AttributeValue(root, "DataContext"));
        Assert.Equal("True", AttributeValue(card, "Focusable"));
        Assert.Equal("Local", AttributeValue(card, "KeyboardNavigation.TabNavigation"));
        Assert.Null(AttributeValue(card, "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "AI agent action approval request",
            AttributeValue(card, "AutomationProperties.Name"));
        Assert.Contains(
            "Approval applies once",
            AttributeValue(card, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);

        var deny = FindButton(document, "Deny");
        var approve = FindButton(document, "Approve once");
        Assert.Equal("0", AttributeValue(deny, "KeyboardNavigation.TabIndex"));
        Assert.Equal("1", AttributeValue(approve, "KeyboardNavigation.TabIndex"));
        Assert.Equal("OnDenyClick", AttributeValue(deny, "Click"));
        Assert.Equal("OnApproveClick", AttributeValue(approve, "Click"));
        Assert.Equal(
            "{Binding AgentChat.CanDecideApproval}",
            AttributeValue(deny, "IsEnabled"));
        Assert.Equal(
            "{Binding AgentChat.CanDecideApproval}",
            AttributeValue(approve, "IsEnabled"));
    }

    [Fact]
    public void Approval_component_forwards_decisions_without_owning_policy()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class AgentApprovalCardView");

        Assert.Contains("ApproveRequested?.Invoke(sender, e);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DenyRequested?.Invoke(sender, e);", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
    }

    private static XElement FindButton(XDocument document, string content) =>
        Assert.Single(
            document.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Content"),
                    content,
                    StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            "Components",
            "AgentApprovalCardView.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
