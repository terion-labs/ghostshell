using System.Xml.Linq;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed class SettingsViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    private static readonly IReadOnlyDictionary<string, string> ShellInteractions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AboutSettingsRequested"] = "OnAboutSettingsClick",
            ["AddAiProviderRequested"] = "OnAddAiProviderClick",
            ["AddFileProviderRequested"] = "OnAddFileProviderClick",
            ["AddMcpServerRequested"] = "OnAddMcpServerClick",
            ["AgentSettingsRequested"] = "OnAgentSettingsClick",
            ["AppearanceSettingsRequested"] = "OnAppearanceSettingsClick",
            ["BrowserSettingsRequested"] = "OnBrowserSettingsClick",
            ["ClearKeybindingPrefixRequested"] = "OnClearKeybindingPrefixClick",
            ["CheckForUpdatesRequested"] = "OnCheckForUpdatesClick",
            ["CloneKeybindingPresetRequested"] = "OnCloneKeybindingPresetClick",
            ["CreateAiProviderSecretRequested"] = "OnCreateAiProviderSecretClick",
            ["CreateConnectionSecretRequested"] = "OnCreateConnectionSecretClick",
            ["CreateFileProviderSecretRequested"] = "OnCreateFileProviderSecretClick",
            ["CreateMcpServerSecretRequested"] = "OnCreateMcpServerSecretClick",
            ["DeleteAiProviderRequested"] = "OnDeleteAiProviderClick",
            ["DeleteFileProviderRequested"] = "OnDeleteFileProviderClick",
            ["DeleteMcpServerRequested"] = "OnDeleteMcpServerClick",
            ["DeleteScreenRequested"] = "OnDeleteScreenClick",
            ["DeleteSecretRequested"] = "OnDeleteSecretClick",
            ["DeleteWorkspaceRequested"] = "OnDeleteWorkspaceClick",
            ["DiagnosticsSettingsRequested"] = "OnDiagnosticsSettingsClick",
            ["DownloadUpdateRequested"] = "OnDownloadUpdateClick",
            ["DismissSavedScreenDeleteUndoRequested"] =
                "OnDismissSavedScreenDeleteUndoClick",
            ["EditAiProviderRequested"] = "OnEditAiProviderClick",
            ["EditFileProviderRequested"] = "OnEditFileProviderClick",
            ["EditLayoutRequested"] = "OnEditLayoutClick",
            ["EditMcpServerRequested"] = "OnEditMcpServerClick",
            ["EditScreenRequested"] = "OnEditScreenClick",
            ["EditSecretRequested"] = "OnEditSecretClick",
            ["EditWorkspaceRequested"] = "OnEditWorkspaceClick",
            ["ExportDefinitionsRequested"] = "OnExportDefinitionsClick",
            ["DisableStartupProtectionRequested"] = "OnDisableStartupProtectionClick",
            ["EnableStartupProtectionRequested"] = "OnEnableStartupProtectionClick",
            ["FilesSettingsRequested"] = "OnFilesSettingsClick",
            ["LockNowRequested"] = "OnLockNowClick",
            ["ImportDefinitionsRequested"] = "OnImportDefinitionsClick",
            ["KeybindingPrefixOptionsChangedRequested"] =
                "OnKeybindingPrefixOptionsChanged",
            ["KeybindingProfileSelectionChangedRequested"] =
                "OnKeybindingProfileSelectionChanged",
            ["KeybindingSettingsRequested"] = "OnKeybindingSettingsClick",
            ["McpSettingsRequested"] = "OnMcpSettingsClick",
            ["NetworkingSettingsRequested"] = "OnNetworkingSettingsClick",
            ["OpenThirdPartyNoticesRequested"] = "OnOpenThirdPartyNoticesClick",
            ["QuickTerminalSettingsRequested"] = "OnQuickTerminalSettingsClick",
            ["RecordKeybindingPrefixRequested"] = "OnRecordKeybindingPrefixClick",
            ["RecordKeybindingRequested"] = "OnRecordKeybindingClick",
            ["ResetAllKeybindingsRequested"] = "OnResetAllKeybindingsClick",
            ["ResetKeybindingRequested"] = "OnResetKeybindingClick",
            ["ReviewHistoryPrivacyRequested"] = "OnReviewHistoryPrivacyClick",
            ["ReviewOnboardingRequested"] = "OnReviewOnboardingClick",
            ["RestartToApplyUpdateRequested"] = "OnRestartToApplyUpdateClick",
            ["RestoreSessionsOnStartChangedRequested"] =
                "OnRestoreSessionsOnStartChanged",
            ["ApplicationAppearanceChangedRequested"] =
                "OnApplicationAppearanceChanged",
            ["TerminalAppearanceChangedRequested"] = "OnTerminalAppearanceChanged",
            ["PickColorRequested"] = "OnPickColorRequested",
            ["SaveKeybindingsRequested"] = "OnSaveKeybindingsClick",
            ["SaveQuickTerminalSettingsRequested"] =
                "OnSaveQuickTerminalSettingsClick",
            ["SaveDefaultAgentPolicyRequested"] =
                "OnSaveDefaultAgentPolicyClick",
            ["SaveTerminalProfileRequested"] = "OnSaveTerminalProfileClick",
            ["SecretsSettingsRequested"] = "OnSecretsSettingsClick",
            ["SelectTerminalPaletteRequested"] = "OnSelectTerminalPaletteClick",
            ["SettingsBackRequested"] = "OnSettingsBackClick",
            ["ShowCommandPaletteRequested"] = "OnShowCommandPaletteClick",
            ["ShowLayoutDesignerRequested"] = "OnShowLayoutDesignerClick",
            ["ShowNewItemRequested"] = "OnShowNewItemClick",
            ["TerminalSettingsRequested"] = "OnTerminalSettingsClick",
            ["TestMcpServerRequested"] = "OnTestMcpServerClick",
            ["UnbindKeybindingRequested"] = "OnUnbindKeybindingClick",
            ["UndoDeletedSavedScreenRequested"] =
                "OnUndoDeletedSavedScreenClick",
            ["WorkspaceSettingsRequested"] = "OnWorkspaceSettingsClick",
        };

    [Fact]
    public void Default_agent_permissions_start_collapsed_without_hiding_model_settings()
    {
        var settings = LoadView("SettingsView");
        var permissions = Assert.Single(settings.Descendants(), element => string.Equals(
            AttributeValue(element, "ItemsSource"), "{Binding DefaultAgentPolicy.Capabilities}", StringComparison.Ordinal));
        var expander = Assert.IsType<XElement>(permissions.Parent);
        Assert.Equal("Expander", expander.Name.LocalName);
        Assert.Equal("False", AttributeValue(expander, "IsExpanded"));
        Assert.Equal("Tool permissions", AttributeValue(expander, "Header"));
        Assert.Equal("Default agent tool permissions", AttributeValue(expander, "AutomationProperties.Name"));
        Assert.Equal(["{Binding IsOff, Mode=TwoWay}", "{Binding IsAsk, Mode=TwoWay}", "{Binding IsAuto, Mode=TwoWay}"],
            permissions.Descendants().Where(element => string.Equals(element.Name.LocalName, "ToggleButton", StringComparison.Ordinal))
                .Select(element => AttributeValue(element, "IsChecked")), StringComparer.Ordinal);
        var model = Assert.Single(settings.Descendants(), element => string.Equals(
            AttributeValue(element, "AutomationProperties.Name"), "Default AI model", StringComparison.Ordinal));
        Assert.DoesNotContain(expander, model.Ancestors());
    }

    [Fact]
    public void Main_window_defers_the_settings_route_behind_one_named_host()
    {
        var mainWindow = LoadView("MainWindow");
        var settings = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "SettingsView", StringComparison.Ordinal));
        var template = Assert.IsType<XElement>(settings.Parent);

        Assert.Equal("DataTemplate", template.Name.LocalName);
        Assert.Equal("SettingsRouteTemplate", AttributeValue(template, "Key"));
        var host = Assert.Single(
            mainWindow.Descendants(),
            element => AttributeValue(element, "Name") == "SettingsRouteHost");
        Assert.Equal(
            "{Binding IsSettingsVisible}",
            AttributeValue(host, "IsVisible"));
        Assert.Empty(host.Elements());

        foreach (var (interaction, handler) in ShellInteractions)
        {
            Assert.Equal(handler, AttributeValue(settings, interaction));
        }

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                mainWindow.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Networking_settings_keep_secret_references_out_of_user_editors()
    {
        var page = LoadSettingsPage("NetworkingSettingsPageView");
        var dialog = LoadView("NetworkConnectionEditorDialog");
        var attributes = page.Descendants().Attributes()
            .Concat(dialog.Descendants().Attributes())
            .ToArray();

        Assert.DoesNotContain(
            attributes,
            attribute => attribute.Value.Contains(
                "SecretReference",
                StringComparison.Ordinal));

        // Every credential slot is the one picker component, bound to the
        // option the editor resolves — never to the reference string itself.
        var pickers = dialog.Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "NetworkCredentialPicker",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(
            pickers,
            picker => string.Equals(
                AttributeValue(picker, "SelectedItem"),
                "{Binding SelectedConfigurationCredential, Mode=TwoWay}",
                StringComparison.Ordinal));
        Assert.Contains(
            pickers,
            picker => string.Equals(
                AttributeValue(picker, "SelectedItem"),
                "{Binding SelectedPasswordCredential, Mode=TwoWay}",
                StringComparison.Ordinal));
        Assert.All(
            pickers,
            picker => Assert.Equal(
                "OnAddCredentialRequested",
                AttributeValue(picker, "AddRequested")));
        Assert.Contains(
            dialog.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Click"),
                "OnTestProfileClick",
                StringComparison.Ordinal));

        // The page lists and chooses; editing is the dialog's job.
        Assert.DoesNotContain(
            page.Descendants(),
            element => string.Equals(
                element.Name.LocalName,
                "NetworkCredentialPicker",
                StringComparison.Ordinal));
    }

    [Fact]
    public void About_exposes_source_aware_update_controls()
    {
        var settings = LoadView("SettingsView");
        var about = Assert.Single(
            settings.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Heading"),
                "About GhostSHELL",
                StringComparison.Ordinal))
            .Parent!;
        var visibleText = about
            .Descendants()
            .Select(element => AttributeValue(element, "Text"))
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains(
            visibleText,
            text => string.Equals(
                text,
                "{Binding ApplicationUpdates.Channel}",
                StringComparison.Ordinal));
        Assert.Contains(
            visibleText,
            text => string.Equals(
                text,
                "{Binding ApplicationUpdates.Status}",
                StringComparison.Ordinal));
        Assert.Contains(
            about.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Content"),
                "Check for updates",
                StringComparison.Ordinal));
        Assert.Contains(
            about.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Content"),
                "Download update",
                StringComparison.Ordinal));
        Assert.Contains(
            about.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Content"),
                "Restart to update",
                StringComparison.Ordinal));

        var viewModel = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        Assert.Contains("ApplicationUpdateViewModel", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual · GitHub Releases", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_settings_group_defaults_providers_and_mcp_without_a_second_navigation_page()
    {
        var settings = LoadView("SettingsView");
        var aiNavigation = Assert.Single(
            settings.Descendants(),
            element => string.Equals(element.Name.LocalName, "ShellNavigationItem"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Label"), "AI", StringComparison.Ordinal));
        Assert.Equal("AI settings", AttributeValue(aiNavigation, "AutomationName"));
        Assert.DoesNotContain(
            settings.Descendants(),
            element => string.Equals(element.Name.LocalName, "ShellNavigationItem"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Label"), "MCP servers", StringComparison.Ordinal));
        Assert.Contains(
            settings.Descendants(),
            element => string.Equals(AttributeValue(element, "Heading")
, "Default agent configuration", StringComparison.Ordinal));
        Assert.Contains(
            settings.Descendants(),
            element => string.Equals(AttributeValue(element, "Heading"), "System prompt", StringComparison.Ordinal));
        var systemPrompt = Assert.Single(
            settings.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBox"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "Default agent system prompt", StringComparison.Ordinal));
        Assert.Equal(
            "{Binding DefaultAgentPolicy.SystemPrompt, Mode=TwoWay}",
            AttributeValue(systemPrompt, "Text"));
        Assert.DoesNotContain(
            settings.Descendants(),
            element => string.Equals(AttributeValue(element, "Content")
, "Use first message as title"
, StringComparison.Ordinal) || string.Equals(AttributeValue(element, "Text")
, "Use first message as title", StringComparison.Ordinal));
        var aiPage = FindNamedElement(settings.Root!, "AiSettingsPage");
        Assert.Equal(
            "{Binding IsAgentSettingsVisible}",
            AttributeValue(aiPage, "IsVisible"));
        var visibleCopy = string.Join(
            '\n',
            aiPage.DescendantsAndSelf()
                .Attributes()
                .Where(attribute => attribute.Name.LocalName is
                    "Text" or "Description" or "Body" or "Footnote")
                .Select(attribute => attribute.Value));
        foreach (var internalPhrase in new[]
        {
            "bounded request",
            "capability broker",
            "governed agent run",
            "inert tool proposals",
            "not live MCP session state",
            "opaque SecretRef",
            "provider-private",
            "session-host authority",
        })
        {
            Assert.DoesNotContain(
                internalPhrase,
                visibleCopy,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Workspace_settings_expose_the_persisted_session_restore_toggle()
    {
        var settings = LoadView("SettingsView");
        var toggle = Assert.Single(
            settings.Descendants(),
            element => string.Equals(element.Name.LocalName, "ToggleSwitch"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Restore sessions on start",
                    StringComparison.Ordinal));

        Assert.Equal(
            "{Binding RestoreSessionsOnStart, Mode=OneWay}",
            AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{Binding CanChangeRestoreSessionsOnStart}",
            AttributeValue(toggle, "IsEnabled"));
        Assert.Equal(
            "OnRestoreSessionsOnStartChanged",
            AttributeValue(toggle, "IsCheckedChanged"));
    }

    [Fact]
    public void Settings_view_preserves_pages_secure_inputs_and_live_regions()
    {
        var settings = LoadView("SettingsView");
        var root = Assert.IsType<XElement>(settings.Root);
        var surface = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal));

        Assert.Equal("Auto,*", AttributeValue(surface, "RowDefinitions"));
        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));
        var titleBar = Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Classes"),
                "TopChrome",
                StringComparison.Ordinal));
        Assert.Equal(
            "TitleBar",
            AttributeValue(titleBar, "WindowDecorationProperties.ElementRole"));
        Assert.Null(AttributeValue(titleBar, "PointerPressed"));
        Assert.Equal(
            "{Binding $parent[views:MainWindow].TitleBarChromeHeight}",
            AttributeValue(titleBar, "MinHeight"));
        Assert.Equal(
            "User",
            AttributeValue(
                FindNamedElement(root, "SettingsBackButton"),
                "WindowDecorationProperties.ElementRole"));
        Assert.Contains(
            "ChromeNavigation",
            AttributeValue(FindNamedElement(root, "SettingsBackButton"), "Classes")
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                ?? [], StringComparer.Ordinal);

        var body = Assert.Single(
            surface.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Grid.Row"), "1", StringComparison.Ordinal));
        Assert.Equal("244,*", AttributeValue(body, "ColumnDefinitions"));
        Assert.Contains(
            body.Elements(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Classes"),
                    "FloatingSidebar",
                    StringComparison.Ordinal));

        var appearancePage = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "AppearanceSettingsPageView", StringComparison.Ordinal));
        Assert.Equal(
            "AppearanceSettingsPage",
            AttributeValue(appearancePage, "Name"));
        Assert.Equal(
            "{Binding IsAppearanceSettingsVisible}",
            AttributeValue(appearancePage, "IsVisible"));
        // Appearance previews are forwarded by section; Apply/Cancel remain
        // explicit operations owned by the page.
        Assert.Equal(
            "OnApplicationAppearanceChangedRequested",
            AttributeValue(appearancePage, "ApplicationAppearanceChanged"));
        Assert.Equal(
            "OnTerminalAppearanceChangedRequested",
            AttributeValue(appearancePage, "TerminalAppearanceChanged"));
        Assert.Null(AttributeValue(appearancePage, "SaveRequested"));

        var quickTerminalPage = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "QuickTerminalSettingsPageView", StringComparison.Ordinal));
        Assert.Equal(
            "QuickTerminalSettingsPage",
            AttributeValue(quickTerminalPage, "Name"));
        Assert.Equal(
            "{Binding IsQuickTerminalSettingsVisible}",
            AttributeValue(quickTerminalPage, "IsVisible"));
        Assert.Equal(
            "OnQuickTerminalSettingsSaveRequested",
            AttributeValue(quickTerminalPage, "SaveRequested"));

        var networkingPage = FindNamedElement(root, "NetworkingSettingsPage");
        Assert.Equal(
            "{Binding IsNetworkingSettingsVisible}",
            AttributeValue(networkingPage, "IsVisible"));
        var networkingSettings = Assert.Single(
            networkingPage.Elements(),
            element => string.Equals(
                element.Name.LocalName,
                "NetworkingSettingsPageView",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding NetworkSettings}",
            AttributeValue(networkingSettings, "DataContext"));

        var pageHeaders = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "SettingsPageHeader", StringComparison.Ordinal))
            .ToArray();
        // AI is one navigation page with two explicit groups: agent defaults/providers
        // and MCP servers. Every other page has one page header.
        Assert.Equal(InlineSettingsPageVisibilityBindings.Length + 1, pageHeaders.Length);
        Assert.All(pageHeaders, header =>
        {
            Assert.False(string.IsNullOrWhiteSpace(AttributeValue(header, "Heading")));
            Assert.False(string.IsNullOrWhiteSpace(AttributeValue(header, "Description")));
        });
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "Headless mode and ACP will use these same definitions in a later milestone.",
                StringComparison.Ordinal));

        foreach (var pageVisibility in InlineSettingsPageVisibilityBindings)
        {
            Assert.Contains(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "IsVisible"),
                    $"{{Binding {pageVisibility}}}",
                    StringComparison.Ordinal));
        }

        foreach (var extractedName in SettingsControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    extractedName,
                    StringComparison.Ordinal));
        }

        foreach (var passwordInput in PasswordInputNames)
        {
            var input = FindNamedElement(root, passwordInput);
            Assert.Equal("TextBox", input.Name.LocalName);
            Assert.Equal("●", AttributeValue(input, "PasswordChar"));
        }

        var undoNotice = FindNamedElement(root, "SavedScreenDeleteUndoNotice");
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.HasPending}",
            AttributeValue(undoNotice, "IsVisible"));
        Assert.Equal(
            "Polite",
            AttributeValue(undoNotice, "AutomationProperties.LiveSetting"));

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "RecoveryDataControlView", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "LocalArtifactControlView", StringComparison.Ordinal));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "DiagnosticsExportView", StringComparison.Ordinal));
    }

    [Fact]
    public void Appearance_settings_page_owns_layout_local_behavior_and_typed_seams()
    {
        var appearance = LoadSettingsPage("AppearanceSettingsPageView");
        var root = Assert.IsType<XElement>(appearance.Root);

        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        var content = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "StackPanel", StringComparison.Ordinal));
        // Sections are one step apart on the spacing scale. This page and Quick
        // Terminal used to say 22 and 20 for the same intent, which is the drift
        // the scale exists to stop; a literal pinned here would reintroduce it.
        Assert.Equal(
            "{DynamicResource ShellSpaceXl}",
            AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => string.Equals(element.Name.LocalName, "SettingsPageHeader", StringComparison.Ordinal));
        Assert.Equal("Appearance", AttributeValue(header, "Heading"));
        Assert.Equal(
            "Customize how the app looks — colour scheme, accent, and window chrome.",
            AttributeValue(header, "Description"));

        foreach (var controlName in AppearanceControlNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "Name"),
                    controlName,
                    StringComparison.Ordinal));
        }

        foreach (var (controlName, automationName) in new[]
                 {
                     ("AppearanceModeSystem", "Color mode System"),
                     ("AppearanceModeDark", "Color mode Dark"),
                     ("AppearanceModeLight", "Color mode Light"),
                     ("PlatformProfilePicker", "Platform profile"),
                     ("AccentModePicker", "Accent mode"),
                     ("CustomAccentText", "Custom accent colour"),
                     ("ApplicationTextScalePicker", "Application text size"),
                 })
        {
            Assert.Equal(
                automationName,
                AttributeValue(
                    FindNamedElement(root, controlName),
                    "AutomationProperties.Name"));
        }

        // The colour-mode tiles are the control: one exclusive group, so the
        // preview a user clicks is the same element that carries the choice.
        foreach (var tileName in new[]
                 {
                     "AppearanceModeSystem",
                     "AppearanceModeDark",
                     "AppearanceModeLight",
                 })
        {
            var tile = FindNamedElement(root, tileName);
            Assert.Equal("RadioButton", tile.Name.LocalName);
            Assert.Equal("AppearanceMode", AttributeValue(tile, "GroupName"));
            Assert.Equal("PresetCard", AttributeValue(tile, "Classes"));
        }

        var accentMode = FindNamedElement(root, "AccentModePicker");
        Assert.Equal(
            "OnAccentModeSelectionChanged",
            AttributeValue(accentMode, "SelectionChanged"));
        Assert.Equal(
            new[] { "Follow host", "GhostSHELL bronze", "Custom" },
            accentMode.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "ComboBoxItem", StringComparison.Ordinal))
                .Select(element => AttributeValue(element, "Content"))
                .ToArray());

        // Appearance is previewed as it is edited and committed only by the
        // section-specific Apply controls; the older generic Save label stays absent.
        Assert.DoesNotContain(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && (AttributeValue(element, "Content") ?? string.Empty)
                    .Contains("Save", StringComparison.Ordinal));

        foreach (var binding in new[] { "ThemeMode", "ThemeProfile", "ThemeTextScale" })
        {
            Assert.Contains(
                root.Descendants(),
                element => (AttributeValue(element, "Text") ?? string.Empty)
                    .Contains(binding, StringComparison.Ordinal));
        }

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class AppearanceSettingsPageView");
        Assert.Contains(
            "ApplicationAppearanceChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "TerminalAppearanceChanged?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);

        foreach (var status in new[]
                 {
                     "AppearanceValidationStatus",
                 })
        {
            var element = FindNamedElement(root, status);
            Assert.Equal(
                "Polite",
                AttributeValue(element, "AutomationProperties.LiveSetting"));
            Assert.False(string.IsNullOrWhiteSpace(
                AttributeValue(element, "AutomationProperties.Name")));
        }

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Terminal contrast status",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.LiveSetting"),
                    "Polite",
                    StringComparison.Ordinal));
        Assert.Contains(
            "UpdateCustomAccentAvailability();",
            codeBehind,
            StringComparison.Ordinal);
        foreach (var typedSeam in AppearancePageTypedSeams)
        {
            Assert.Contains(typedSeam, codeBehind, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveThemeAsync", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_terminal_settings_page_owns_layout_and_save_relay()
    {
        var quickTerminal = LoadSettingsPage("QuickTerminalSettingsPageView");
        var root = Assert.IsType<XElement>(quickTerminal.Root);

        Assert.Equal("Stretch", AttributeValue(root, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", AttributeValue(root, "VerticalContentAlignment"));

        var content = Assert.Single(
            root.Elements(),
            element => string.Equals(element.Name.LocalName, "StackPanel", StringComparison.Ordinal));
        // The gap between sections comes from the spacing scale, not from a number
        // written here. A literal is a gap the density and text-scale settings
        // cannot reach, and pinning one in a test is how it would stay that way.
        Assert.Equal(
            "{DynamicResource ShellSpaceXl}",
            AttributeValue(content, "Spacing"));
        Assert.Null(AttributeValue(content, "Margin"));
        Assert.Null(AttributeValue(content, "IsVisible"));

        var header = Assert.Single(
            content.Descendants(),
            element => string.Equals(element.Name.LocalName, "SettingsPageHeader", StringComparison.Ordinal));
        Assert.Equal("Quick Terminal", AttributeValue(header, "Heading"));
        Assert.Equal(
            "A global drop-down terminal that stays one keystroke away. Configure where it appears and how it behaves.",
            AttributeValue(header, "Description"));

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Content"),
                    "Save settings",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnSaveQuickTerminalSettingsClick",
                    StringComparison.Ordinal));

        foreach (var automationName in QuickTerminalAutomationNames)
        {
            Assert.Single(
                root.Descendants(),
                element => string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    automationName,
                    StringComparison.Ordinal));
        }

        var opacity = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "NumericUpDown"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal background opacity",
                    StringComparison.Ordinal));
        Assert.Equal("0", AttributeValue(opacity, "Minimum"));
        Assert.Equal("100", AttributeValue(opacity, "Maximum"));
        Assert.Equal("True", AttributeValue(opacity, "ClipValueToMinMax"));

        var height = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "NumericUpDown"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal panel height",
                    StringComparison.Ordinal));
        Assert.Equal("0", AttributeValue(height, "FormatString"));

        var display = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "ComboBox"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "Quick Terminal display",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding QuickTerminalSettingsEditor.MonitorOptions}",
            AttributeValue(display, "ItemsSource"));
        Assert.Equal(
            "{Binding QuickTerminalSettingsEditor.SelectedMonitorOption}",
            AttributeValue(display, "SelectedItem"));
        Assert.Contains(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "SettingRow"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Description"),
                    "Active window follows whichever app is in front and falls back to GhostSHELL where unsupported. GhostSHELL window follows this app; Primary always uses the OS primary display.",
                    StringComparison.Ordinal));

        var toggles = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ToggleSwitch", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, toggles.Length);
        Assert.Equal(
            new[]
            {
                "{Binding QuickTerminalSettingsEditor.IsTranslucent}",
                "{Binding QuickTerminalSettingsEditor.AnimateSlide}",
                "{Binding QuickTerminalSettingsEditor.ReduceMotion}",
                "{Binding QuickTerminalSettingsEditor.HideOnFocusLoss}",
                "{Binding QuickTerminalSettingsEditor.RestoreLastSession}",
                "{Binding QuickTerminalSettingsEditor.RestoreOnStart}",
            },
            toggles
                .Select(toggle => AttributeValue(toggle, "IsChecked"))
                .ToArray());

        Assert.Contains(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "AutomationProperties.LiveSetting"),
                "Polite",
                StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "AutomationProperties.Name"),
                    "{Binding QuickTerminalSettingsEditor.RegistrationStatus}",
                    StringComparison.Ordinal));

        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class QuickTerminalSettingsPageView");
        Assert.Contains(
            "SaveRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecordHotkeyRequested?.Invoke(sender, e);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SaveQuickTerminalSettingsAsync",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_page_header_is_a_passive_shared_component()
    {
        var header = LoadComponent("SettingsPageHeader");
        var root = Assert.IsType<XElement>(header.Root);

        Assert.Equal("Root", AttributeValue(root, "Name"));
        Assert.Equal("False", AttributeValue(root, "Focusable"));

        var textBlocks = root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TextBlock", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, textBlocks.Length);
        Assert.Contains(
            textBlocks,
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding Heading, ElementName=Root}",
                StringComparison.Ordinal));
        Assert.Contains(
            textBlocks,
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding Description, ElementName=Root}",
                StringComparison.Ordinal));
        Assert.All(
            textBlocks,
            element => Assert.StartsWith(
                "{DynamicResource ShellLineHeight",
                AttributeValue(element, "LineHeight"),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_view_forwards_interactions_and_exposes_typed_form_seams()
    {
        var codeBehind = ApplicationViews.FindUniqueCodeBehindSourceContaining(
            "public sealed partial class SettingsView");

        foreach (var interaction in ShellInteractions.Keys)
        {
            Assert.Contains($" {interaction};", codeBehind, StringComparison.Ordinal);
            Assert.Contains(
                $"{interaction}?.Invoke(sender, e);",
                codeBehind,
                StringComparison.Ordinal);
        }

        foreach (var typedSeam in TypedPresentationSeams)
        {
            Assert.Contains(typedSeam, codeBehind, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("FindControl<", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageProvider", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowViewModel", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(".Start()", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_uses_the_typed_settings_bridge_and_keeps_effect_ownership()
    {
        var mainWindowCode = ApplicationViews.FindPartialClassSources("MainWindow");

        foreach (var typedCall in MainWindowTypedCalls)
        {
            Assert.Contains(typedCall, mainWindowCode, StringComparison.Ordinal);
        }

        Assert.Contains(
            "MaterializeRoute<SettingsView>(",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("\"SettingsRouteTemplate\"", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("\"SettingsRouteHost\"", mainWindowCode, StringComparison.Ordinal);

        foreach (var extractedName in ExtractedControlNames)
        {
            Assert.DoesNotContain(
                $"FindControl<Control>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"FindControl<ComboBox>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"FindControl<TextBox>(\"{extractedName}\")",
                mainWindowCode,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "new AvaloniaDiagnosticsBundleDestinationPicker(this)",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("_recoveryDataControlViewModel.Start();", mainWindowCode);
        Assert.Contains("_localArtifactControlViewModel.Start();", mainWindowCode);
        Assert.Contains("ShowDialog<", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OnAccentModeSelectionChanged",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AccentModeSelectionChangedRequested",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_provider_editor_balances_rows_and_does_not_render_single_options_as_selectors()
    {
        var editor = LoadView("AiProviderProfileEditorDialog");

        Assert.Equal(
            "*,*",
            AttributeValue(FindNamedElement(editor.Root!, "IdentityFields"), "ColumnDefinitions"));
        Assert.Equal(
            "*,*",
            AttributeValue(FindNamedElement(editor.Root!, "ModelFields"), "ColumnDefinitions"));

        var providerSelector = FindNamedElement(editor.Root!, "ProviderSelector");
        Assert.Contains(
            providerSelector.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Text"),
                "{Binding Summary}",
                StringComparison.Ordinal));

        Assert.Equal(
            "{Binding HasMultipleAuthenticationOptions}",
            AttributeValue(
                FindNamedElement(editor.Root!, "AuthenticationSelector"),
                "IsVisible"));
        Assert.Equal(
            "{Binding HasSingleAuthenticationOption}",
            AttributeValue(
                FindNamedElement(editor.Root!, "AuthenticationValue"),
                "IsVisible"));
        Assert.Equal(
            "{Binding HasMultipleCredentialOptions}",
            AttributeValue(
                FindNamedElement(editor.Root!, "CredentialSelector"),
                "IsVisible"));
        Assert.Equal(
            "{Binding HasSingleCredentialOption}",
            AttributeValue(
                FindNamedElement(editor.Root!, "CredentialValue"),
                "IsVisible"));
    }

    private static readonly string[] InlineSettingsPageVisibilityBindings =
    [
        "IsWorkspaceSettingsVisible",
        "IsKeybindingSettingsVisible",
        "IsFilesSettingsVisible",
        "IsBrowserSettingsVisible",
        "IsTerminalSettingsVisible",
        "IsSecretsSettingsVisible",
        "IsAgentSettingsVisible",
        "IsDiagnosticsSettingsVisible",
        "IsAboutSettingsVisible",
    ];

    private static readonly string[] AppearanceControlNames =
    [
        "AccentModePicker",
        "AppearanceModeDark",
        "AppearanceModeLight",
        "AppearanceModeSystem",
        "ApplicationTextScalePicker",
        "CustomAccentText",
        "PlatformProfilePicker",
    ];

    private static readonly string[] QuickTerminalAutomationNames =
    [
        "Save Quick Terminal settings",
        "Quick Terminal global hotkey",
        "Record Quick Terminal global hotkey",
        "Quick Terminal display",
        "Quick Terminal panel height",
        "Quick Terminal background opacity",
        "Quick Terminal translucency",
        "Animate Quick Terminal",
        "Reduce Quick Terminal motion",
        "Quick Terminal animation duration",
        "Hide Quick Terminal on focus loss",
        "Keep Quick Terminal session",
    ];

    private static readonly string[] PasswordInputNames =
    [
        "SecretValueInput",
        "FileProviderSecretValueInput",
        "AiProviderSecretValueInput",
        "McpServerSecretValueInput",
    ];

    private static readonly string[] TypedPresentationSeams =
    [
        "ConfigureAppearanceControls(",
        "ApplyAppearance(",
        "CaptureAppearance()",
        "CaptureKeybindingPrefixOptions()",
        "CaptureConnectionSecretForm()",
        "CaptureFileProviderSecretForm()",
        "CaptureAiProviderSecretForm()",
        "CaptureMcpServerSecretForm()",
        "BindOperationalViewModels(",
        "FocusBackButton()",
        "FocusSavedScreenUndo()",
    ];

    private static readonly string[] AppearancePageTypedSeams =
    [
        "ConfigureAppearanceControls(",
        "ApplyAppearance(",
        "CaptureAppearance()",
    ];

    private static readonly string[] MainWindowTypedCalls =
    [
        "settings.ConfigureAppearanceControls(",
        "settings.BindOperationalViewModels(",
        "settings.ApplyAppearance(",
        "SettingsRoute.CaptureAppearance()",
        "SettingsRoute.CaptureKeybindingPrefixOptions()",
        "SettingsRoute.CaptureConnectionSecretForm()",
        "SettingsRoute.CaptureFileProviderSecretForm()",
        "SettingsRoute.CaptureAiProviderSecretForm()",
        "SettingsRoute.CaptureMcpServerSecretForm()",
        "FocusNavigator.FocusSettingsBackButton()",
        "FocusNavigator.FocusSavedScreenUndo()",
    ];

    private static readonly string[] SettingsControlNames =
    [
        "AiProviderSecretLabelInput",
        "AiProviderSecretProfilePicker",
        "AiProviderSecretValueInput",
        "DiagnosticsExportView",
        "FileProviderSecretKindPicker",
        "FileProviderSecretLabelInput",
        "FileProviderSecretValueInput",
        "KeybindingPrefixFailure",
        "KeybindingPrefixRepeatable",
        "KeybindingPrefixTimeout",
        "LocalArtifactControlView",
        "McpServerSecretTargetPicker",
        "McpServerSecretKindPicker",
        "McpServerSecretLabelInput",
        "McpServerSecretValueInput",
        "McpSettingsSection",
        "RecoveryDataControlView",
        "SavedScreenDeleteUndoNotice",
        "SecretConnectionPicker",
        "SecretFileProviderPicker",
        "SecretKindPicker",
        "SecretLabelInput",
        "SecretValueInput",
        "SettingsBackButton",
        "UndoDeletedSavedScreenButton",
    ];

    private static readonly string[] ExtractedControlNames =
    [
        .. AppearanceControlNames,
        .. SettingsControlNames,
    ];

    private static XElement FindNamedElement(XElement root, string name) =>
        Assert.Single(
            root.Descendants(),
            element => string.Equals(
                AttributeValue(element, "Name"),
                name,
                StringComparison.Ordinal));

    private static XDocument LoadView(string view) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            $"{view}.axaml"));

    private static XDocument LoadComponent(string component) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            $"{component}.axaml"));

    private static XDocument LoadSettingsPage(string page) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "SettingsPages",
            $"{page}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
