using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceEditorIsolationContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Workspace_editor_exposes_the_isolation_toggle_and_runtime_requirement()
    {
        var root = Assert.IsType<XElement>(LoadWorkspaceEditor().Root);
        var toggle = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "Isolate workspace",
                StringComparison.Ordinal));

        Assert.Equal("{Binding IsIsolated, Mode=TwoWay}", AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{Binding CanToggleIsolation}",
            AttributeValue(toggle, "IsEnabled"));
        var isolationGroup = toggle.Ancestors().Single(element =>
            string.Equals(
                AttributeValue(element, "Heading"),
                "Isolation",
                StringComparison.Ordinal));
        Assert.Contains(
            "separate from the host and other workspaces",
            AttributeValue(isolationGroup, "Description"),
            StringComparison.Ordinal);
        var isolationToggleRow = toggle.Ancestors().Single(element =>
            string.Equals(
                AttributeValue(element, "Label"),
                "Isolate workspace",
                StringComparison.Ordinal));
        Assert.Contains(
            "execution and network boundary",
            AttributeValue(isolationToggleRow, "Description"),
            StringComparison.Ordinal);
        Assert.Contains(
            "All workspace panels, connections, and local terminals use it",
            AttributeValue(isolationToggleRow, "Description"),
            StringComparison.Ordinal);
        var runtimeRequirement = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsVisible"),
                "{Binding IsIsolationUnavailable}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding IsolationRuntimeRequirementLabel}",
            AttributeValue(runtimeRequirement, "Label"));
        Assert.Equal(
            "{Binding IsolationRuntimeRequirementDescription}",
            AttributeValue(runtimeRequirement, "Description"));

        var imageRow = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Runtime image",
                StringComparison.Ordinal));
        Assert.Equal("{Binding IsIsolated}", AttributeValue(imageRow, "IsVisible"));
        Assert.Equal(
            "{Binding IsolationImageDescription}",
            AttributeValue(imageRow, "Description"));
        var image = FindAccessibleElement(imageRow, "Workspace isolation OCI image");
        Assert.Equal(
            "{Binding IsolationImageReference, Mode=TwoWay}",
            AttributeValue(image, "Text"));
        Assert.Null(AttributeValue(image, "PlaceholderText"));

        var recreate = FindAccessibleElement(
            isolationGroup,
            "Recreate workspace environment");
        Assert.Equal(
            "OnRecreateWorkspaceIsolationClick",
            AttributeValue(recreate, "Click"));
        Assert.Equal(
            "{Binding CanRecreateIsolationEnvironment}",
            AttributeValue(recreate, "IsEnabled"));

        var install = FindAccessibleElement(
            runtimeRequirement,
            "{Binding InstallIsolationRuntimeAccessibleName}");
        Assert.Equal(
            "{Binding InstallIsolationRuntimeLabel}",
            AttributeValue(install, "Content"));
        Assert.Equal(
            "{Binding CanInstallIsolationRuntime}",
            AttributeValue(install, "IsVisible"));
        Assert.Equal(
            "OnInstallWorkspaceIsolationRuntimeClick",
            AttributeValue(install, "Click"));

        Assert.DoesNotContain(
            root.Descendants(),
            element => AttributeValue(element, "Text")?.Contains(
                "Preview",
                StringComparison.OrdinalIgnoreCase) == true);

        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Host mounts locked while running",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Workspace_editor_exposes_source_guest_access_and_mount_actions()
    {
        var root = Assert.IsType<XElement>(LoadWorkspaceEditor().Root);
        var mountRow = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Label"),
                "Host mounts",
                StringComparison.Ordinal));

        Assert.Equal("{Binding IsIsolated}", AttributeValue(mountRow, "IsVisible"));
        Assert.Null(AttributeValue(mountRow, "IsEnabled"));
        Assert.Contains(
            "Saving mount changes restarts an open workspace",
            AttributeValue(mountRow, "Description"),
            StringComparison.Ordinal);
        Assert.Contains(
            mountRow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "ItemsSource"),
                "{Binding IsolationMounts}",
                StringComparison.Ordinal));

        var hostPath = FindAccessibleElement(mountRow, "Host mount source directory");
        Assert.Equal("{Binding HostPath, Mode=TwoWay}", AttributeValue(hostPath, "Text"));
        var guestPath = FindAccessibleElement(mountRow, "Host mount guest path");
        Assert.Equal("{Binding GuestPath, Mode=TwoWay}", AttributeValue(guestPath, "Text"));
        var readOnly = FindAccessibleElement(mountRow, "Mount host path read only");
        Assert.Equal("{Binding IsReadOnly, Mode=TwoWay}", AttributeValue(readOnly, "IsChecked"));
        var readWrite = FindAccessibleElement(mountRow, "Mount host path read and write");
        Assert.Equal("{Binding IsReadWrite, Mode=TwoWay}", AttributeValue(readWrite, "IsChecked"));
        // A radio cannot be clicked off, so a mount always has exactly one
        // access level; a toggle segment could be left with neither.
        Assert.Equal("RadioButton", readOnly.Name.LocalName);
        Assert.Equal("RadioButton", readWrite.Name.LocalName);
        Assert.Equal("True", AttributeValue(mountRow, "IsStacked"));
        var browse = FindAccessibleElement(mountRow, "Choose a host folder");
        Assert.Equal("OnBrowseIsolationMountHostPathClick", AttributeValue(browse, "Click"));

        var add = FindAccessibleElement(mountRow, "Add host mount");
        Assert.Equal("OnAddIsolationMountClick", AttributeValue(add, "Click"));
        Assert.Equal("{Binding CanAddIsolationMount}", AttributeValue(add, "IsEnabled"));
        var remove = Assert.Single(
            mountRow.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                "{Binding RemoveAccessibleName}",
                StringComparison.Ordinal));
        Assert.Equal("OnRemoveIsolationMountClick", AttributeValue(remove, "Click"));
        Assert.DoesNotContain(
            mountRow.Descendants(),
            element => AttributeValue(element, "Text")?.Contains(
                "macOS or Windows programs",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Workspace_settings_list_states_isolation_and_offers_runtime_installation()
    {
        var root = Assert.IsType<XElement>(LoadSettings().Root);

        // Isolation is read from the list and changed in the editor, beside the
        // mounts and image it governs: flipping it restarts a running workspace,
        // which is not something a list row should do on one click.
        var chip = FindAccessibleElement(root, "{Binding Name, StringFormat={}{0} is isolated}");
        Assert.Equal("{Binding IsIsolated}", AttributeValue(chip, "IsVisible"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "IsCheckedChanged"),
                "OnWorkspaceIsolationChanged",
                StringComparison.Ordinal));

        var install = FindAccessibleElement(root, "Install Apple container runtime");
        Assert.Equal(
            "OnInstallWorkspaceIsolationRuntimeClick",
            AttributeValue(install, "Click"));
        var callout = install.Ancestors().First(element => string.Equals(
            element.Name.LocalName,
            "Callout",
            StringComparison.Ordinal));
        Assert.Equal(
            "Install Apple container to enable workspace isolation",
            AttributeValue(callout, "Title"));
        Assert.Equal(
            "{Binding CanInstallWorkspaceIsolationRuntime}",
            AttributeValue(callout, "IsVisible"));
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Apple container is required",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Running_workspace_isolation_changes_require_the_restart_confirmation()
    {
        var views = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views");
        var confirmation = File.ReadAllText(Path.Combine(views, "Confirmations.cs"));
        var editorHandler = File.ReadAllText(Path.Combine(views, "MainWindow.axaml.cs"));

        Assert.Contains(
            "Workspace will be restarted to change isolation configuration.",
            confirmation,
            StringComparison.Ordinal);
        Assert.Contains(
            "Confirmations.WorkspaceIsolationRestart(",
            editorHandler,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.WorkspaceEditorImageChangeRebuildsIsolate(request)",
            editorHandler,
            StringComparison.Ordinal);
    }

    private static XElement FindAccessibleElement(XElement root, string accessibleName) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.Name"),
                accessibleName,
                StringComparison.Ordinal));

    private static XDocument LoadWorkspaceEditor() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "WorkspaceEditorView.axaml"));

    private static XDocument LoadSettings() =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "SettingsView.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                name,
                StringComparison.Ordinal))
            ?.Value;
}
