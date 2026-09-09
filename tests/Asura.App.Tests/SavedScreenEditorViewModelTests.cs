using System.Collections.Immutable;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class SavedScreenEditorViewModelTests
{
    [Fact]
    public void AgentPolicyEditorPreservesProviderModelAndEveryDurableCapability()
    {
        var connection = LocalConnection("local");
        var provider = Provider(
            "trusted-provider",
            "Trusted provider",
            "profile-default-model");
        var permissions = AgentPolicy.Capabilities
            .Select((capability, index) => KeyValuePair.Create(
                capability,
                (index % 3) switch
                {
                    0 => AgentPermission.Off,
                    1 => AgentPermission.Ask,
                    _ => AgentPermission.Auto,
                }))
            .ToImmutableDictionary();
        var policy = new AgentPolicy(provider.Id.Value, "screen-model", permissions)
        {
            CompactionModel = new AgentModelSelection(provider.Id.Value, "compact-model"),
            TitleModel = new AgentModelSelection(provider.Id.Value, "title-model"),
        };
        var original = Screen(connection.Id);
        var screen = new ScreenDefinition(
            original.Id,
            original.SchemaVersion,
            original.Name,
            original.Description,
            original.LayoutId,
            original.Panels,
            original.Tags,
            policy);
        using var editor = Editor(screen, 7, [connection], aiProviders: [provider]);

        Assert.True(editor.AgentPolicy.IsEnabled);
        Assert.Equal(policy.Provider, editor.AgentPolicy.Provider);
        Assert.Equal(policy.Model, editor.AgentPolicy.Model);
        Assert.Same(
            editor.AgentPolicy.ProviderOptions.Single(),
            editor.AgentPolicy.SelectedProvider);
        Assert.Equal(AgentPolicy.Capabilities.Length, editor.AgentPolicy.Capabilities.Count);
        Assert.All(
            editor.AgentPolicy.Capabilities,
            capability =>
            {
                Assert.Equal(
                    policy.GetPermission(capability.Capability),
                    capability.SelectedPermission);
                Assert.Equal(
                    [AgentPermission.Off, AgentPermission.Ask, AgentPermission.Auto],
                    capability.Options.Select(option => option.Permission));
                Assert.DoesNotContain(
                    capability.Options,
                    option => option.Permission == AgentPermission.Yolo);
            });

        var savedPolicy = Assert.IsType<AgentPolicy>(
            editor.CreateSaveRequest().Definition.AgentPolicyOverride);
        Assert.Equal(policy.Provider, savedPolicy.Provider);
        Assert.Equal(policy.Model, savedPolicy.Model);
        Assert.Equal(policy.Permissions, savedPolicy.Permissions);
    }

    [Fact]
    public void AgentPolicyProviderSelectionStoresTheExactProfileIdAndDefaultsItsModel()
    {
        var connection = LocalConnection("local");
        var primary = Provider("provider-primary", "Primary", "primary-model", order: 0);
        var secondary = Provider("provider-secondary", "Secondary", "secondary-model", order: 1);
        using var editor = Editor(
            Screen(connection.Id),
            3,
            [connection],
            aiProviders: [secondary, primary]);

        editor.AgentPolicy.IsEnabled = true;

        Assert.Equal(primary.Id.Value, editor.AgentPolicy.Provider);
        Assert.Equal(primary.DefaultModel, editor.AgentPolicy.Model);

        editor.AgentPolicy.Model = "primary-exact-override";
        editor.AgentPolicy.SelectedProvider = editor.AgentPolicy.ProviderOptions
            .Single(option => option.Id == secondary.Id);

        Assert.Equal(secondary.Id.Value, editor.AgentPolicy.Provider);
        Assert.Equal(secondary.DefaultModel, editor.AgentPolicy.Model);

        editor.AgentPolicy.Model = "secondary-exact-override";
        var saved = Assert.IsType<AgentPolicy>(
            editor.CreateSaveRequest().Definition.AgentPolicyOverride);

        Assert.Equal(secondary.Id.Value, saved.Provider);
        Assert.Equal("secondary-exact-override", saved.Model);
    }

    [Fact]
    public void AgentPolicySelectsIndependentCompactionAndTitleModels()
    {
        var connection = LocalConnection("local");
        var primary = Provider("provider-primary", "Primary", "primary-model", order: 0);
        var secondary = Provider(
            "provider-secondary",
            "Secondary",
            "secondary-model",
            order: 1);
        using var editor = Editor(
            Screen(connection.Id),
            3,
            [connection],
            aiProviders: [primary, secondary]);
        editor.AgentPolicy.IsEnabled = true;
        editor.AgentPolicy.SelectedCompactionModel = editor.AgentPolicy
            .AgentTaskModelOptions
            .Single(option => option.Selection == new AgentModelSelection(
                secondary.Id.Value,
                secondary.DefaultModel));
        editor.AgentPolicy.SelectedTitleModel = editor.AgentPolicy
            .TitleModelOptions
            .Single(option => option.Selection == new AgentModelSelection(
                primary.Id.Value,
                primary.DefaultModel));

        var saved = Assert.IsType<AgentPolicy>(
            editor.CreateSaveRequest().Definition.AgentPolicyOverride);

        Assert.Equal(
            new AgentModelSelection(secondary.Id.Value, secondary.DefaultModel),
            saved.CompactionModel);
        Assert.Equal(
            new AgentModelSelection(primary.Id.Value, primary.DefaultModel),
            saved.TitleModel);
    }

    [Fact]
    public void AgentPolicyUsesPrimaryModelForTitlesInsteadOfOfferingFirstMessageSentinel()
    {
        var connection = LocalConnection("local");
        var provider = Provider("provider", "Provider", "model");
        using var editor = Editor(
            Screen(connection.Id),
            3,
            [connection],
            aiProviders: [provider]);
        editor.AgentPolicy.IsEnabled = true;
        editor.AgentPolicy.SelectedCompactionModel =
            editor.AgentPolicy.AgentTaskModelOptions[0];
        editor.AgentPolicy.SelectedTitleModel =
            editor.AgentPolicy.TitleModelOptions[0];

        var saved = Assert.IsType<AgentPolicy>(
            editor.CreateSaveRequest().Definition.AgentPolicyOverride);

        Assert.Equal(
            new AgentModelSelection(provider.Id.Value, provider.DefaultModel),
            saved.CompactionModel);
        Assert.DoesNotContain(
            editor.AgentPolicy.TitleModelOptions,
            option => option.Selection is null);
        Assert.Equal(
            new AgentModelSelection(provider.Id.Value, provider.DefaultModel),
            saved.TitleModel);
    }

    [Fact]
    public void MissingSavedPolicyProviderRemainsVisibleAndFailsClosedUntilRepaired()
    {
        var connection = LocalConnection("local");
        var available = Provider(
            "provider-available",
            "Available",
            "available-model");
        var original = Screen(connection.Id);
        var screen = new ScreenDefinition(
            original.Id,
            original.SchemaVersion,
            original.Name,
            original.Description,
            original.LayoutId,
            original.Panels,
            original.Tags,
            AgentPolicy.Default with
            {
                Provider = "provider-removed",
                Model = "preserved-model",
            });
        using var editor = Editor(
            screen,
            3,
            [connection],
            aiProviders: [available]);

        Assert.Equal("provider-removed", editor.AgentPolicy.Provider);
        Assert.Equal("preserved-model", editor.AgentPolicy.Model);
        Assert.False(editor.AgentPolicy.SelectedProvider?.IsAvailable);
        Assert.Contains(
            "Unavailable",
            editor.AgentPolicy.SelectedProvider?.DisplayName,
            StringComparison.Ordinal);
        Assert.False(editor.AgentPolicy.IsValid);
        Assert.False(editor.CanSave);
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.AgentPolicy.SelectedProvider = editor.AgentPolicy.ProviderOptions
            .Single(option => option.Id == available.Id);
        var availableRoute = new AgentModelSelection(
            available.Id.Value,
            available.DefaultModel);
        editor.AgentPolicy.SelectedCompactionModel = editor.AgentPolicy
            .AgentTaskModelOptions
            .Single(option => option.Selection == availableRoute);
        editor.AgentPolicy.SelectedTitleModel = editor.AgentPolicy
            .TitleModelOptions
            .Single(option => option.Selection == availableRoute);

        Assert.True(editor.AgentPolicy.IsValid);
        Assert.True(editor.CanSave);
        Assert.Equal(available.Id.Value, editor.AgentPolicy.Provider);
        Assert.Equal(available.DefaultModel, editor.AgentPolicy.Model);
    }

    [Fact]
    public void DisabledSavedPolicyProviderIsShownButCannotBePersisted()
    {
        var connection = LocalConnection("local");
        var disabled = Provider(
            "provider-disabled",
            "Disabled",
            "default-model",
            isEnabled: false);
        var original = Screen(connection.Id);
        var screen = new ScreenDefinition(
            original.Id,
            original.SchemaVersion,
            original.Name,
            original.Description,
            original.LayoutId,
            original.Panels,
            original.Tags,
            AgentPolicy.Default with
            {
                Provider = disabled.Id.Value,
                Model = "preserved-model",
            });
        using var editor = Editor(
            screen,
            3,
            [connection],
            aiProviders: [disabled]);

        Assert.True(editor.AgentPolicy.SelectedProvider?.IsAvailable);
        Assert.False(editor.AgentPolicy.SelectedProvider?.IsSelectable);
        Assert.Equal("preserved-model", editor.AgentPolicy.Model);
        Assert.False(editor.AgentPolicy.IsValid);
        Assert.False(editor.CanSave);
    }

    [Fact]
    public void AgentPolicyChangesAreDirtyValidatedAndCanRemoveTheOverride()
    {
        var connection = LocalConnection("local");
        var provider = Provider("local-provider", "Local provider", "model-1");
        using var editor = Editor(
            Screen(connection.Id),
            3,
            [connection],
            aiProviders: [provider]);

        Assert.False(editor.AgentPolicy.IsEnabled);
        editor.AgentPolicy.IsEnabled = true;
        editor.AgentPolicy.Provider = new string('p', AgentPolicy.MaximumProviderLength + 1);

        Assert.True(editor.IsDirty);
        Assert.False(editor.AgentPolicy.IsValid);
        Assert.False(editor.CanSave);
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.AgentPolicy.Provider = provider.Id.Value;
        editor.AgentPolicy.Model = "model-1";
        editor.AgentPolicy.Capabilities
            .Single(item => item.Capability == AgentCapability.RunCommands)
            .SelectedPermission = AgentPermission.Auto;

        Assert.True(editor.CanSave);
        var saved = Assert.IsType<AgentPolicy>(
            editor.CreateSaveRequest().Definition.AgentPolicyOverride);
        Assert.Equal(AgentPermission.Auto, saved.GetPermission(AgentCapability.RunCommands));

        editor.AgentPolicy.IsEnabled = false;

        Assert.Null(editor.CreateSaveRequest().Definition.AgentPolicyOverride);
    }

    [Fact]
    public void CancelRequiresConfirmationOnlyWhenChangesWouldBeLost()
    {
        var connection = LocalConnection("local");
        var screen = Screen(connection.Id);
        var editor = Editor(screen, 1, [connection]);

        Assert.Equal(
            SavedScreenEditorCancelDisposition.Close,
            editor.RequestCancel());

        editor.Description = "Updated description";

        Assert.Equal(
            SavedScreenEditorCancelDisposition.ConfirmDiscard,
            editor.RequestCancel());
    }

    [Fact]
    public void LegacyFileViewerWithoutProviderDefaultsToBuiltInHome()
    {
        var screen = new ScreenDefinition(
            new ScreenId("legacy-files"),
            ScreenDefinition.CurrentSchemaVersion,
            "Files",
            null,
            new LayoutId("layout-test"),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("legacy-files-panel"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.FileViewer,
                    "Files",
                    null,
                    PanelStartupBehavior.None),
            ]);
        var editor = Editor(screen, 1, []);

        Assert.False(editor.HasMissingDefinitions);
        var request = editor.CreateSaveRequest();
        Assert.Equal(
            new FileProviderProfileId("builtin.files.home"),
            request.Definition.Panels[0].FileProviderProfileId);
    }

    [Fact]
    public void MissingConnectionMustBeExplicitlyRepaired()
    {
        var missing = new ConnectionId("removed-connection");
        var screen = Screen(missing);
        var available = LocalConnection("available");
        var editor = Editor(screen, 7, [available]);

        Assert.True(editor.HasMissingConnections);
        Assert.Equal(1, editor.MissingConnectionCount);
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.Panels[0].SelectedConnection = editor.ConnectionOptions.Single(option => option.IsAvailable);
        var request = editor.CreateSaveRequest();

        Assert.False(editor.HasMissingConnections);
        Assert.Equal(available.Id, request.Definition.Panels[0].ConnectionId);
        Assert.Equal(7, request.ExpectedRevision);
    }

    [Fact]
    public void StartupEditorBuildsAnIndependentDefinitionSnapshot()
    {
        var connection = LocalConnection("local");
        var original = Screen(connection.Id);
        var editor = Editor(original, 3, [connection]);
        var panel = editor.Panels[0];
        panel.Title = "Build terminal";
        panel.StartupLocation = "/work/project";
        panel.StartupCommands = "dotnet restore\n\ndotnet test\n";

        var request = editor.CreateSaveRequest();

        Assert.True(editor.IsDirty);
        Assert.Equal("Unsaved changes", editor.DirtyStatus);
        Assert.Equal("Build terminal", request.Definition.Panels[0].Title);
        Assert.Equal("/work/project", request.Definition.Panels[0].Startup.Location);
        Assert.Equal(["dotnet restore", "dotnet test"], request.Definition.Panels[0].Startup.Commands);
        Assert.Equal("Original", original.Panels[0].Title);
        Assert.Empty(original.Panels[0].Startup.Commands);
    }

    [Fact]
    public void StartupDeliveryFailurePolicyHasClosedChoicesAndPersistsInCopies()
    {
        var connection = LocalConnection("local");
        var editor = Editor(Screen(connection.Id), 3, [connection]);
        var panel = editor.Panels[0];

        Assert.Collection(
            panel.DeliveryFailurePolicyOptions,
            option =>
            {
                Assert.Equal(
                    StartupCommandDeliveryFailurePolicy.RetryWhileLive,
                    option.Policy);
                Assert.Equal("Retry while live", option.DisplayName);
            },
            option =>
            {
                Assert.Equal(
                    StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
                    option.Policy);
                Assert.Equal("Stop after first delivery failure", option.DisplayName);
            });
        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.RetryWhileLive,
            panel.SelectedDeliveryFailurePolicy);

        panel.SelectedDeliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure;

        var saved = editor.CreateSaveRequest();
        var duplicate = editor.CreateDuplicateRequest();

        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            saved.Definition.Panels[0].Startup.DeliveryFailurePolicy);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            duplicate.Definition.Panels[0].Startup.DeliveryFailurePolicy);
    }

    [Fact]
    public void DuplicateGetsANewIdentityAndNoExpectedRevision()
    {
        var connection = LocalConnection("local");
        var original = Screen(connection.Id);
        var editor = Editor(original, 11, [connection]);

        var duplicate = editor.CreateDuplicateRequest();

        Assert.NotEqual(original.Id, duplicate.Definition.Id);
        Assert.Equal("Screen copy", duplicate.Definition.Name);
        Assert.Null(duplicate.ExpectedRevision);
        Assert.Equal(original.Panels, duplicate.Definition.Panels);
    }

    [Fact]
    public void FileViewerPanelPersistsTheSelectedProviderProfile()
    {
        var provider = new FileProviderProfile(
            new FileProviderProfileId("files.projects"),
            FileProviderProfile.CurrentSchemaVersion,
            "Projects",
            new FileProviderConfiguration.Local(Path.GetTempPath()));
        var screen = new ScreenDefinition(
            new ScreenId("screen-files"),
            ScreenDefinition.CurrentSchemaVersion,
            "Files",
            null,
            new LayoutId("layout-test"),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-files"),
                    new LayoutSlotId("slot-one"),
                    ScreenPanelKind.FileViewer,
                    "Project files",
                    null,
                    PanelStartupBehavior.None,
                    provider.Id),
            ]);
        var editor = Editor(screen, 5, [], [provider]);

        var request = editor.CreateSaveRequest();

        Assert.False(editor.HasMissingDefinitions);
        Assert.Equal(provider.Id, request.Definition.Panels[0].FileProviderProfileId);
        Assert.Null(request.Definition.Panels[0].ConnectionId);
    }

    [Fact]
    public void NewScreenIsAnUnsavedEditorDraftWithAnExplicitLayout()
    {
        var layout = Layout(new LayoutId("layout-new"), "Columns", "left", "right");
        var connection = LocalConnection("local");

        var editor = SavedScreenEditorViewModel.CreateNew(
            "Operations",
            [layout],
            [connection]);

        Assert.True(editor.IsNew);
        Assert.Equal("Create saved screen", editor.EditorTitle);
        Assert.Equal(
            SavedScreenEditorCancelDisposition.ConfirmDiscard,
            editor.RequestCancel());
        Assert.Equal(layout.Id, editor.SelectedLayout.Id);
        Assert.Equal(2, editor.Panels.Count);
        Assert.All(editor.Panels, panel => Assert.Equal(ScreenPanelKind.Terminal, panel.Kind));

        var request = editor.CreateSaveRequest();

        Assert.Null(request.ExpectedRevision);
        Assert.Equal("Operations", request.Definition.Name);
        Assert.Equal(layout.Id, request.Definition.LayoutId);
    }

    [Fact]
    public void LayoutSelectionReconcilesSlotsAndSavesMixedPanelKinds()
    {
        var connection = LocalConnection("local");
        var firstLayout = Layout(
            new LayoutId("layout-first"),
            "First",
            "shared",
            "removed");
        var secondLayout = Layout(
            new LayoutId("layout-second"),
            "Second",
            "shared",
            "added");
        var sharedPanelId = new ScreenPanelId("panel-shared");
        var screen = new ScreenDefinition(
            new ScreenId("screen-layout-change"),
            ScreenDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            firstLayout.Id,
            [
                new ScreenPanelDefinition(
                    sharedPanelId,
                    new LayoutSlotId("shared"),
                    ScreenPanelKind.Terminal,
                    "Shell",
                    connection.Id,
                    PanelStartupBehavior.None),
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-removed"),
                    new LayoutSlotId("removed"),
                    ScreenPanelKind.Browser,
                    "Docs",
                    null,
                    new PanelStartupBehavior("https://example.test")),
            ]);
        var editor = new SavedScreenEditorViewModel(
            screen,
            9,
            [connection],
            [],
            [firstLayout, secondLayout]);
        var sharedEditor = editor.Panels.Single(panel => string.Equals(panel.SlotId.Value, "shared", StringComparison.Ordinal));

        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == secondLayout.Id);

        Assert.Same(sharedEditor, editor.Panels[0]);
        Assert.Equal(sharedPanelId, editor.Panels[0].Id);
        Assert.Equal(["shared", "added"], editor.Panels.Select(panel => panel.SlotId.Value), StringComparer.Ordinal);
        Assert.DoesNotContain(editor.Panels, panel => string.Equals(panel.Id.Value, "panel-removed", StringComparison.Ordinal));

        editor.Panels[0].Kind = ScreenPanelKind.FileViewer;
        editor.Panels[1].Kind = ScreenPanelKind.Statistics;
        var request = editor.CreateSaveRequest();

        Assert.Equal(secondLayout.Id, request.Definition.LayoutId);
        Assert.Equal(
            [ScreenPanelKind.FileViewer, ScreenPanelKind.Statistics],
            request.Definition.Panels.Select(panel => panel.Kind));
        Assert.Equal(
            new FileProviderProfileId("builtin.files.home"),
            request.Definition.Panels[0].FileProviderProfileId);
        Assert.All(request.Definition.Panels, panel => Assert.Null(panel.ConnectionId));
    }

    [Fact]
    public void ReturningToALayoutRestoresItsUnsavedPanelDrafts()
    {
        var connection = LocalConnection("local");
        var firstLayout = Layout(
            new LayoutId("layout-first"),
            "First",
            "shared",
            "first-only");
        var secondLayout = Layout(
            new LayoutId("layout-second"),
            "Second",
            "shared",
            "second-only");
        var screen = new ScreenDefinition(
            new ScreenId("screen-layout-cache"),
            ScreenDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            firstLayout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-shared"),
                    new LayoutSlotId("shared"),
                    ScreenPanelKind.Terminal,
                    "Shell",
                    connection.Id,
                    PanelStartupBehavior.None),
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-first-only"),
                    new LayoutSlotId("first-only"),
                    ScreenPanelKind.Terminal,
                    "Logs",
                    connection.Id,
                    PanelStartupBehavior.None),
            ]);
        var editor = new SavedScreenEditorViewModel(
            screen,
            3,
            [connection],
            [],
            [firstLayout, secondLayout]);
        var firstOnly = editor.Panels[1];
        firstOnly.Kind = ScreenPanelKind.Browser;
        firstOnly.Title = "Deployment docs";
        firstOnly.StartupLocation = "https://docs.example.test";

        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == secondLayout.Id);
        editor.Panels[1].Kind = ScreenPanelKind.FileViewer;
        editor.Panels[1].StartupLocation = "deployments";
        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == firstLayout.Id);

        Assert.Same(firstOnly, editor.Panels[1]);
        Assert.Equal(new ScreenPanelId("panel-first-only"), editor.Panels[1].Id);
        Assert.Equal(ScreenPanelKind.Browser, editor.Panels[1].Kind);
        Assert.Equal("Deployment docs", editor.Panels[1].Title);
        Assert.Equal("https://docs.example.test", editor.Panels[1].StartupLocation);

        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == secondLayout.Id);

        Assert.Equal(ScreenPanelKind.FileViewer, editor.Panels[1].Kind);
        Assert.Equal("deployments", editor.Panels[1].StartupLocation);
    }

    [Fact]
    public void ReturningToALayoutRestoresItsDeliveryFailurePolicyDraft()
    {
        var connection = LocalConnection("local");
        var firstLayout = Layout(
            new LayoutId("layout-first-policy"),
            "First",
            "shared",
            "first-only");
        var secondLayout = Layout(
            new LayoutId("layout-second-policy"),
            "Second",
            "shared",
            "second-only");
        var screen = new ScreenDefinition(
            new ScreenId("screen-layout-policy-cache"),
            ScreenDefinition.CurrentSchemaVersion,
            "Operations",
            null,
            firstLayout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-shared"),
                    new LayoutSlotId("shared"),
                    ScreenPanelKind.Terminal,
                    "Shell",
                    connection.Id,
                    PanelStartupBehavior.None),
                new ScreenPanelDefinition(
                    new ScreenPanelId("panel-first-only"),
                    new LayoutSlotId("first-only"),
                    ScreenPanelKind.Terminal,
                    "Logs",
                    connection.Id,
                    PanelStartupBehavior.None),
            ]);
        var editor = new SavedScreenEditorViewModel(
            screen,
            3,
            [connection],
            [],
            [firstLayout, secondLayout]);
        var firstOnly = editor.Panels[1];
        firstOnly.SelectedDeliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure;

        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == secondLayout.Id);
        var secondOnly = editor.Panels[1];
        secondOnly.SelectedDeliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure;
        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == firstLayout.Id);

        Assert.Same(firstOnly, editor.Panels[1]);
        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure,
            editor.Panels[1].SelectedDeliveryFailurePolicy);

        editor.SelectedLayout = editor.LayoutOptions.Single(option =>
            option.Id == secondLayout.Id);

        Assert.Same(secondOnly, editor.Panels[1]);
        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure,
            editor.Panels[1].SelectedDeliveryFailurePolicy);
    }

    [Fact]
    public void ChangingPanelKindClearsIncompatibleBindingsAndStartup()
    {
        var connection = LocalConnection("local");
        var screen = Screen(connection.Id);
        var editor = Editor(screen, 4, [connection]);
        var panel = editor.Panels[0];
        panel.StartupLocation = "/work";
        panel.StartupCommands = "dotnet test";
        panel.SelectedDeliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicyOption.StopAfterFirstDeliveryFailure;

        panel.Kind = ScreenPanelKind.FileViewer;

        Assert.Null(panel.SelectedConnection);
        Assert.Equal(
            new FileProviderProfileId("builtin.files.home"),
            panel.SelectedFileProvider?.Id);
        Assert.Empty(panel.StartupCommands);
        Assert.Equal("/work", panel.StartupLocation);
        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.RetryWhileLive,
            panel.SelectedDeliveryFailurePolicy);

        panel.Kind = ScreenPanelKind.ProcessMonitor;

        Assert.Null(panel.SelectedConnection);
        Assert.Null(panel.SelectedFileProvider);
        Assert.Empty(panel.StartupLocation);

        panel.Kind = ScreenPanelKind.Terminal;

        Assert.Equal(connection.Id, panel.SelectedConnection?.Id);
        Assert.Null(panel.SelectedFileProvider);
        Assert.Same(
            StartupCommandDeliveryFailurePolicyOption.RetryWhileLive,
            panel.SelectedDeliveryFailurePolicy);
    }

    [Fact]
    public void AllPanelKindsRoundTripWithOnlyCompatibleFields()
    {
        var connection = LocalConnection("local");
        var layout = Layout(
            new LayoutId("layout-all-kinds"),
            "All kinds",
            "terminal",
            "browser",
            "files",
            "statistics",
            "processes");
        var editor = SavedScreenEditorViewModel.CreateNew(
            "All kinds",
            [layout],
            [connection]);
        editor.Panels[0].StartupLocation = "/workspace";
        editor.Panels[0].StartupCommands = "dotnet test";
        editor.Panels[1].Kind = ScreenPanelKind.Browser;
        editor.Panels[1].StartupLocation = "https://example.test";
        editor.Panels[2].Kind = ScreenPanelKind.FileViewer;
        editor.Panels[2].StartupLocation = "projects";
        editor.Panels[3].Kind = ScreenPanelKind.Statistics;
        editor.Panels[4].Kind = ScreenPanelKind.ProcessMonitor;

        var saved = editor.CreateSaveRequest().Definition;
        var reopened = new SavedScreenEditorViewModel(
            saved,
            1,
            [connection],
            [],
            [layout]);
        var roundTrip = reopened.CreateSaveRequest().Definition.Panels;

        Assert.Equal(
            [
                ScreenPanelKind.Terminal,
                ScreenPanelKind.Browser,
                ScreenPanelKind.FileViewer,
                ScreenPanelKind.Statistics,
                ScreenPanelKind.ProcessMonitor,
            ],
            roundTrip.Select(panel => panel.Kind));
        Assert.Equal(connection.Id, roundTrip[0].ConnectionId);
        Assert.Null(roundTrip[0].FileProviderProfileId);
        Assert.Equal("/workspace", roundTrip[0].Startup.Location);
        Assert.Equal(["dotnet test"], roundTrip[0].Startup.Commands);
        Assert.Equal(connection.Id, roundTrip[1].ConnectionId);
        Assert.Null(roundTrip[1].FileProviderProfileId);
        Assert.Equal("https://example.test", roundTrip[1].Startup.Location);
        Assert.Empty(roundTrip[1].Startup.Commands);
        Assert.Null(roundTrip[2].ConnectionId);
        Assert.Equal(
            new FileProviderProfileId("builtin.files.home"),
            roundTrip[2].FileProviderProfileId);
        Assert.Equal("projects", roundTrip[2].Startup.Location);
        Assert.Empty(roundTrip[2].Startup.Commands);
        Assert.All(
            roundTrip.Skip(3),
            panel =>
            {
                Assert.Null(panel.ConnectionId);
                Assert.Null(panel.FileProviderProfileId);
                Assert.Null(panel.Startup.Location);
                Assert.Empty(panel.Startup.Commands);
            });
        Assert.All(
            roundTrip.Skip(1),
            panel => Assert.Equal(
                StartupCommandDeliveryFailurePolicy.RetryWhileLive,
                panel.Startup.DeliveryFailurePolicy));
    }

    [Fact]
    public void BrowserStartupAddressMustBeEmptyOrACompleteSupportedAddress()
    {
        var connection = LocalConnection("local");
        var screen = Screen(connection.Id);
        var editor = Editor(screen, 4, [connection]);
        var panel = editor.Panels[0];
        panel.Kind = ScreenPanelKind.Browser;

        Assert.True(editor.CanSave);
        Assert.False(panel.HasInvalidBrowserAddress);

        panel.StartupLocation = "/relative/path";

        Assert.True(panel.HasInvalidBrowserAddress);
        Assert.True(editor.HasInvalidBrowserAddresses);
        Assert.False(editor.CanSave);
        var error = Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());
        Assert.Contains("HTTP or HTTPS", error.Message, StringComparison.Ordinal);

        panel.StartupLocation = "https://example.test/path";

        Assert.False(panel.HasInvalidBrowserAddress);
        Assert.False(editor.HasInvalidBrowserAddresses);
        Assert.True(editor.CanSave);
        Assert.Equal(
            "https://example.test/path",
            editor.CreateSaveRequest().Definition.Panels[0].Startup.Location);
    }

    [Theory]
    [InlineData(DefinitionStoreErrorCode.RevisionConflict)]
    [InlineData(DefinitionStoreErrorCode.DependencyConflict)]
    [InlineData(DefinitionStoreErrorCode.Cancelled)]
    [InlineData(DefinitionStoreErrorCode.StorageFailure)]
    public async Task TypedPersistenceFailuresRemainVisibleOnTheLiveDraft(
        DefinitionStoreErrorCode code)
    {
        var connection = LocalConnection("local");
        var screen = Screen(connection.Id);
        var editor = Editor(screen, 8, [connection]);
        editor.Description = "Unsaved operator changes";

        var saved = await editor.SaveAsync(
            (request, cancellationToken) =>
            {
                _ = request;
                _ = cancellationToken;
                return ValueTask.FromResult(
                    DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Failure(
                        new DefinitionStoreError(code, "Typed persistence failure.")));
            },
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(code, editor.PersistenceError?.Code);
        Assert.True(editor.HasPersistenceError);
        Assert.True(editor.CanEdit);
        Assert.False(editor.IsSaving);
        Assert.Equal("Unsaved operator changes", editor.Description);
        Assert.Equal("Original", editor.Panels[0].Title);
    }

    [Fact]
    public async Task FailedPersistenceKeepsTheDraftAndRetrySucceeds()
    {
        var connection = LocalConnection("local");
        var screen = Screen(connection.Id);
        var editor = Editor(screen, 8, [connection]);
        editor.Panels[0].Title = "Unsaved build shell";
        var attempts = 0;
        SavedScreenEditorSaveRequest? successfulRequest = null;

        async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> Persist(
            SavedScreenEditorSaveRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (attempts == 1)
            {
                return DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "The saved screen changed."));
            }

            successfulRequest = request;
            return DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Success(
                new StoredDefinition<ScreenDefinition>(
                    request.Definition,
                    9,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch));
        }

        var first = await editor.SaveAsync(Persist, CancellationToken.None);

        Assert.False(first);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, editor.PersistenceError?.Code);
        Assert.Equal("Unsaved build shell", editor.Panels[0].Title);
        Assert.True(editor.CanEdit);

        var retried = await editor.SaveAsync(Persist, CancellationToken.None);

        Assert.True(retried);
        Assert.Equal(2, attempts);
        Assert.Null(editor.PersistenceError);
        Assert.False(editor.HasPersistenceError);
        Assert.Equal("Unsaved build shell", successfulRequest?.Definition.Panels[0].Title);
        Assert.Equal(8, successfulRequest?.ExpectedRevision);
    }

    [Fact]
    public async Task ThrownCancellationBecomesATypedRetryableDraftError()
    {
        var connection = LocalConnection("local");
        var editor = Editor(Screen(connection.Id), 2, [connection]);

        var saved = await editor.SaveAsync(
            (_, _) => ValueTask.FromException<
                DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>(
                    new OperationCanceledException()),
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(DefinitionStoreErrorCode.Cancelled, editor.PersistenceError?.Code);
        Assert.True(editor.CanEdit);
        Assert.True(editor.CanSave);
    }

    private static ScreenDefinition Screen(ConnectionId connectionId) => new(
        new ScreenId("screen-test"),
        ScreenDefinition.CurrentSchemaVersion,
        "Screen",
        "Description",
        new LayoutId("layout-test"),
        [
            new ScreenPanelDefinition(
                new ScreenPanelId("panel-one"),
                new LayoutSlotId("slot-one"),
                ScreenPanelKind.Terminal,
                "Original",
                connectionId,
                PanelStartupBehavior.None),
        ]);

    private static SavedScreenEditorViewModel Editor(
        ScreenDefinition screen,
        long revision,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<FileProviderProfile>? fileProviders = null,
        IReadOnlyList<AiProviderProfileDescriptor>? aiProviders = null) => new(
            screen,
            revision,
            connections,
            fileProviders ?? [],
            [LayoutFor(screen)],
            aiProviders);

    private static AiProviderProfileDescriptor Provider(
        string id,
        string name,
        string defaultModel,
        int order = 0,
        bool isEnabled = true) =>
        new(
            new AiProviderProfileId(id),
            name,
            AiProviderKind.OpenAiCompatible,
            new Uri("https://provider.example.test/v1/"),
            defaultModel,
            order,
            isEnabled,
            RequiresCredential: false);

    private static LayoutDefinition LayoutFor(ScreenDefinition screen) => Layout(
        screen.LayoutId,
        "Layout",
        [.. screen.Panels.Select(panel => panel.SlotId.Value)]);

    private static LayoutDefinition Layout(
        LayoutId id,
        string name,
        params string[] slotIds) => new(
            id,
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(slotIds.Length, 1),
            [.. slotIds
                .Select((slotId, index) => new LayoutSlotDefinition(
                    new LayoutSlotId(slotId),
                    new LayoutGridBounds(index, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)))]);

    private static ConnectionProfile LocalConnection(string id) => new(
        new ConnectionId(id),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);
}
