using System.Reflection;
using System.Xml.Linq;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Testing;

namespace GhostShell.App.Tests;

public sealed class McpServerProfileEditorViewModelTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void New_profile_keeps_direct_command_arguments_and_vault_references_separate()
    {
        var editor = NewEditor();
        editor.AddArgument();
        editor.Arguments[0].Value = "--stdio";
        editor.AddArgument();
        editor.Arguments[1].Value = "value with spaces";
        editor.AddEnvironmentBinding();
        editor.Environment[0].Name = "API_TOKEN";
        editor.Environment[0].SecretReference = "vault-token-ref";
        editor.AddEnabledTool();
        editor.EnabledTools[0].Name = "deploy.preview";

        var request = editor.CreateSaveRequest();

        var transport = Assert.IsType<McpServerTransport.Stdio>(
            request.Profile.Transport);
        Assert.Equal("/opt/tools/mcp-server", transport.Executable);
        Assert.Equal(["--stdio", "value with spaces"], transport.Arguments);
        var binding = Assert.Single(transport.Environment);
        Assert.Equal("API_TOKEN", binding.Name);
        Assert.Equal(new SecretRef("vault-token-ref"), binding.Reference);
        Assert.Equal(["deploy.preview"], request.Profile.EnabledTools);
        Assert.True(request.RequiresTrustConfirmation);
        Assert.False(request.IsTrustConfirmed);
        Assert.False(request.IsAuthorizedForSave);
        Assert.Equal("/opt/tools/mcp-server", request.TrustReview.Executable);
        Assert.Contains(
            request.TrustReview.Environment,
            item => string.Equals(item.VariableName, "API_TOKEN"
, StringComparison.Ordinal) && string.Equals(item.ReferenceValue, "vault-token-ref"
, StringComparison.Ordinal) && item.State == McpServerCredentialReviewState.Missing);
        Assert.DoesNotContain(
            request.TrustReview.Environment,
            item => item.MetadataSummary.Contains(
                "secret value",
                StringComparison.OrdinalIgnoreCase));

        var confirmed = request.ConfirmTrust();

        Assert.True(confirmed.IsTrustConfirmed);
        Assert.True(confirmed.IsAuthorizedForSave);
    }

    [Fact]
    public void New_remote_profile_keeps_endpoint_and_header_secret_references_separate()
    {
        var editor = NewRemoteEditor();
        editor.AddHttpHeaderBinding();
        editor.HttpHeaders[0].Name = "Authorization";
        editor.HttpHeaders[0].SecretReference = "vault-remote-token";
        editor.AddEnabledTool();
        editor.EnabledTools[0].Name = "remote.inspect";

        var request = editor.CreateSaveRequest();

        var transport = Assert.IsType<McpServerTransport.StreamableHttp>(
            request.Profile.Transport);
        Assert.Equal(
            new Uri("https://mcp.example.test/rpc"),
            transport.Endpoint);
        Assert.False(transport.AllowInsecureTransport);
        var header = Assert.Single(transport.Headers);
        Assert.Equal("Authorization", header.Name);
        Assert.Equal(new SecretRef("vault-remote-token"), header.Reference);
        Assert.True(request.RequiresTrustConfirmation);
        Assert.True(request.TrustReview.IsStreamableHttp);
        Assert.False(request.TrustReview.IsStdio);
        Assert.Equal(transport.Endpoint.AbsoluteUri, request.TrustReview.Endpoint);
        Assert.Equal(
            "vault-remote-token",
            Assert.Single(request.TrustReview.HttpHeaders).ReferenceValue);
    }

    [Theory]
    [InlineData("http://localhost:7070/mcp")]
    [InlineData("http://127.0.0.1:7070/mcp")]
    [InlineData("http://[::1]:7070/mcp")]
    public void Remote_profile_allows_plaintext_only_for_exact_loopback(
        string endpoint)
    {
        var editor = NewRemoteEditor();
        editor.Endpoint = endpoint;

        var request = editor.CreateSaveRequest();

        var transport = Assert.IsType<McpServerTransport.StreamableHttp>(
            request.Profile.Transport);
        Assert.True(transport.AllowInsecureTransport);
        Assert.Equal(
            "Plaintext loopback · explicitly acknowledged",
            request.TrustReview.TransportSecurity);
    }

    [Theory]
    [InlineData("http://mcp.example.test/rpc")]
    [InlineData("http://localhost.example.test/rpc")]
    [InlineData("ftp://mcp.example.test/rpc")]
    [InlineData("/relative/mcp")]
    [InlineData("https://user@mcp.example.test/rpc")]
    [InlineData("https://mcp.example.test/rpc#fragment")]
    public void Remote_profile_rejects_endpoints_outside_settings_policy(
        string endpoint)
    {
        var editor = NewRemoteEditor();
        editor.Endpoint = endpoint;

        Assert.Throws<ArgumentException>(editor.CreateSaveRequest);
    }

    [Fact]
    public void Existing_remote_profile_round_trips_without_authority_expansion()
    {
        var existing = RemoteProfile();
        var editor = new McpServerProfileEditorViewModel(
            existing,
            expectedRevision: 19);

        var request = editor.CreateSaveRequest();

        Assert.Equal(19, request.ExpectedRevision);
        Assert.True(editor.IsStreamableHttp);
        Assert.Equal(
            "https://mcp.example.test/rpc",
            editor.Endpoint);
        Assert.Equal("Authorization", Assert.Single(editor.HttpHeaders).Name);
        Assert.False(request.RequiresTrustConfirmation);
        Assert.Empty(request.TrustReview.Changes);
        Assert.Equal(existing.Id, request.Profile.Id);
        var savedTransport =
            Assert.IsType<McpServerTransport.StreamableHttp>(
                request.Profile.Transport);
        var existingTransport =
            Assert.IsType<McpServerTransport.StreamableHttp>(
                existing.Transport);
        Assert.Equal(existingTransport.Endpoint, savedTransport.Endpoint);
        Assert.Equal(existingTransport.Headers, savedTransport.Headers);
    }

    [Fact]
    public void Remote_endpoint_header_and_transport_changes_require_confirmation()
    {
        var mutations = new Action<McpServerProfileEditorViewModel>[]
        {
            editor => editor.Endpoint = "https://other.example.test/mcp",
            editor => editor.HttpHeaders[0].SecretReference = "replacement-ref",
            editor => editor.SelectedTransport = editor.TransportOptions.Single(
                option => option.Kind == McpServerTransportKind.Stdio),
        };

        foreach (var mutate in mutations)
        {
            var editor = new McpServerProfileEditorViewModel(
                RemoteProfile(),
                expectedRevision: 20)
            {
                Executable = "/opt/tools/mcp-server",
            };
            mutate(editor);

            var request = editor.CreateSaveRequest();

            Assert.True(request.RequiresTrustConfirmation);
            Assert.NotEmpty(request.TrustReview.Changes);
        }
    }

    [Fact]
    public void Remote_header_names_are_bounded_unique_and_non_reserved()
    {
        var editor = NewRemoteEditor();
        editor.AddHttpHeaderBinding();
        editor.HttpHeaders[0].Name = "Host";
        Assert.Throws<ArgumentException>(editor.CreateSaveRequest);

        editor.HttpHeaders[0].Name = "X-Token";
        editor.AddHttpHeaderBinding();
        editor.HttpHeaders[1].Name = "x-token";
        Assert.Throws<ArgumentException>(editor.CreateSaveRequest);
    }

    [Fact]
    public void Remote_header_rows_stop_at_the_profile_limit()
    {
        var editor = NewRemoteEditor();
        for (var index = 0;
             index < McpServerProfile.MaximumHttpHeaderCount;
             index++)
        {
            editor.AddHttpHeaderBinding();
        }

        var error = Assert.Throws<InvalidOperationException>(
            editor.AddHttpHeaderBinding);

        Assert.Contains(
            McpServerProfile.MaximumHttpHeaderCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Argument_reordering_changes_the_exact_argv_order_and_accessible_positions()
    {
        var editor = NewEditor();
        editor.AddArgument();
        editor.Arguments[0].Value = "first";
        editor.AddArgument();
        editor.Arguments[1].Value = "second";

        editor.MoveArgumentUp(editor.Arguments[1]);

        var request = editor.CreateSaveRequest();
        Assert.Equal(
            ["second", "first"],
            Assert.IsType<McpServerTransport.Stdio>(request.Profile.Transport)
                .Arguments);
        Assert.Equal("Argument 1", editor.Arguments[0].AccessibleName);
        Assert.Equal("Argument 2", editor.Arguments[1].AccessibleName);
    }

    [Fact]
    public void Repeated_environment_and_tool_rows_keep_unique_accessible_positions()
    {
        var editor = NewEditor();
        editor.AddEnvironmentBinding();
        editor.AddEnvironmentBinding();
        editor.AddEnvironmentBinding();
        editor.AddHttpHeaderBinding();
        editor.AddHttpHeaderBinding();
        editor.AddHttpHeaderBinding();
        editor.AddEnabledTool();
        editor.AddEnabledTool();
        editor.AddEnabledTool();

        editor.RemoveEnvironmentBinding(editor.Environment[0]);
        editor.RemoveHttpHeaderBinding(editor.HttpHeaders[1]);
        editor.RemoveEnabledTool(editor.EnabledTools[1]);

        Assert.Equal(
            [
                "Environment binding 1 variable name",
                "Environment binding 2 variable name",
            ],
            editor.Environment.Select(item => item.NameAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            [
                "Environment binding 1 secret reference",
                "Environment binding 2 secret reference",
            ],
            editor.Environment.Select(item => item.SecretReferenceAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            [
                "Remove environment binding 1",
                "Remove environment binding 2",
            ],
            editor.Environment.Select(item => item.RemoveAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            [
                "HTTP header binding 1 name",
                "HTTP header binding 2 name",
            ],
            editor.HttpHeaders.Select(item => item.NameAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            [
                "HTTP header binding 1 secret reference",
                "HTTP header binding 2 secret reference",
            ],
            editor.HttpHeaders.Select(item => item.SecretReferenceAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            [
                "Remove HTTP header binding 1",
                "Remove HTTP header binding 2",
            ],
            editor.HttpHeaders.Select(item => item.RemoveAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            ["Enabled MCP tool 1 name", "Enabled MCP tool 2 name"],
            editor.EnabledTools.Select(item => item.NameAccessibleName), StringComparer.Ordinal);
        Assert.Equal(
            ["Remove enabled MCP tool 1", "Remove enabled MCP tool 2"],
            editor.EnabledTools.Select(item => item.RemoveAccessibleName), StringComparer.Ordinal);
    }

    [Fact]
    public void Trust_review_shows_credential_metadata_and_scope_state()
    {
        var profile = Profile(enabledTools: ["read"]);
        var matching = Secret(
            Assert.IsType<McpServerTransport.Stdio>(profile.Transport)
                .Environment[0].Reference,
            "Deployment token",
            "ApiKey",
            new SecretScope(
                SecretScopeKind.McpServer,
                profile.Id.Value));
        var wrongScope = Secret(
            Assert.IsType<McpServerTransport.Stdio>(profile.Transport)
                .Environment[0].Reference,
            "Global token",
            "Password",
            SecretScope.Global);

        var availableReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [matching])
            .CreateSaveRequest()
            .TrustReview;
        var wrongScopeReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [wrongScope])
            .CreateSaveRequest()
            .TrustReview;
        var missingReview = new McpServerProfileEditorViewModel(
            profile,
            expectedRevision: 1,
            secrets: [])
            .CreateSaveRequest()
            .TrustReview;

        var available = Assert.Single(availableReview.Environment);
        Assert.Equal("Deployment token", available.CredentialLabel);
        Assert.Equal("ApiKey", available.CredentialKind);
        Assert.Equal(
            McpServerCredentialReviewState.Available,
            available.State);
        Assert.Equal(
            McpServerCredentialReviewState.WrongScope,
            Assert.Single(wrongScopeReview.Environment).State);
        Assert.Contains(
            "Global token",
            Assert.Single(wrongScopeReview.Environment).MetadataSummary,
            StringComparison.Ordinal);
        Assert.Equal(
            McpServerCredentialReviewState.Missing,
            Assert.Single(missingReview.Environment).State);
    }

    [Fact]
    public void Metadata_changes_and_allowlist_narrowing_do_not_require_expansion_confirmation()
    {
        var existing = Profile(enabledTools: ["read", "write"]);
        var editor = new McpServerProfileEditorViewModel(existing, expectedRevision: 7)
        {
            Name = "Renamed server",
            IsEnabled = false,
        };
        editor.RemoveEnabledTool(editor.EnabledTools.Single(item => string.Equals(item.Name, "write", StringComparison.Ordinal)));

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.False(request.RequiresTrustConfirmation);
        Assert.True(request.IsAuthorizedForSave);
        Assert.Empty(request.TrustReview.Changes);
        Assert.Equal(["read"], request.Profile.EnabledTools);
    }

    [Fact]
    public void Launch_changes_environment_changes_and_tool_expansion_require_confirmation()
    {
        var mutations = new Action<McpServerProfileEditorViewModel>[]
        {
            editor => editor.Executable = "/opt/tools/replacement-server",
            editor =>
            {
                editor.AddArgument();
                editor.Arguments[^1].Value = "--verbose";
            },
            editor => editor.WorkingDirectory = "/var/tmp/mcp",
            editor => editor.Environment[0].SecretReference = "replacement-ref",
            editor =>
            {
                editor.AddEnabledTool();
                editor.EnabledTools[^1].Name = "write";
            },
        };

        foreach (var mutate in mutations)
        {
            var editor = new McpServerProfileEditorViewModel(
                Profile(enabledTools: ["read"]),
                expectedRevision: 3);
            mutate(editor);

            var request = editor.CreateSaveRequest();

            Assert.True(request.RequiresTrustConfirmation);
            Assert.False(request.IsAuthorizedForSave);
            Assert.NotEmpty(request.TrustReview.Changes);
        }
    }

    [Fact]
    public void Enabling_a_previously_disabled_server_requires_confirmation()
    {
        var editor = new McpServerProfileEditorViewModel(
            Profile(enabledTools: ["read"], isEnabled: false),
            expectedRevision: 4)
        {
            IsEnabled = true,
        };

        var request = editor.CreateSaveRequest();

        Assert.True(request.RequiresTrustConfirmation);
        Assert.Contains(
            request.TrustReview.Changes,
            change => change.Contains("Enable this server", StringComparison.Ordinal));
    }

    [Fact]
    public void Imported_untrusted_server_requires_review_even_without_other_changes()
    {
        var editor = new McpServerProfileEditorViewModel(
            Profile(
                enabledTools: ["read"],
                isEnabled: false,
                isTrusted: false),
            expectedRevision: 5);

        var request = editor.CreateSaveRequest();

        Assert.True(request.RequiresTrustConfirmation);
        Assert.Contains(
            request.TrustReview.Changes,
            change => change.Contains(
                "Trust this imported server definition",
                StringComparison.Ordinal));
        Assert.True(request.Profile.IsTrusted);
        Assert.True(request.ConfirmTrust().IsAuthorizedForSave);
    }

    [Fact]
    public void Relative_launch_paths_are_rejected_before_review()
    {
        var editor = NewEditor();
        editor.Executable = "mcp-server";

        var executableError = Assert.Throws<ArgumentException>(
            editor.CreateSaveRequest);

        Assert.Contains(
            "fully qualified",
            executableError.Message,
            StringComparison.OrdinalIgnoreCase);

        editor.Executable = "/opt/tools/mcp-server";
        editor.WorkingDirectory = "relative/work";

        var directoryError = Assert.Throws<ArgumentException>(
            editor.CreateSaveRequest);

        Assert.Contains(
            "working directory",
            directoryError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_list_projection_reports_when_an_enabled_profile_has_no_tools()
    {
        var profile = Profile(enabledTools: []);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            9,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var item = MainWindowViewModel.ProjectMcpServerProfile(stored, []);

        Assert.Equal("No tools enabled", item.Status);
        Assert.Equal(
            "Choose at least one tool before enabling this server.",
            item.StatusDetail);
        Assert.True(item.HasWarning);
        Assert.Equal("1 ordered arg", item.ArgumentSummary);
        Assert.Equal("1 vault binding", item.EnvironmentBindingSummary);
        Assert.Equal("0 enabled tools", item.EnabledToolSummary);
    }

    [Fact]
    public void Server_list_projection_requires_a_matching_profile_scoped_credential()
    {
        var profile = Profile(enabledTools: ["read"]);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            4,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var wrongScope = new SecretMetadataViewModel(
            new SecretRef("vault-ref"),
            "Token",
            "ApiKey",
            "Global",
            "Never",
            "Never",
            SecretScope.Global,
            "None",
            0);

        var missing = MainWindowViewModel.ProjectMcpServerProfile(
            stored,
            [wrongScope]);
        var matching = MainWindowViewModel.ProjectMcpServerProfile(
            stored,
            [wrongScope with
            {
                SecretScope = new SecretScope(
                    SecretScopeKind.McpServer,
                    profile.Id.Value),
            }]);

        Assert.Equal("Credential missing", missing.Status);
        Assert.True(missing.HasWarning);
        Assert.Equal("Enabled for new runs", matching.Status);
        Assert.Equal("Ready.", matching.StatusDetail);
        Assert.False(matching.HasWarning);
        Assert.Equal("1 enabled tool", matching.EnabledToolSummary);
    }

    [Fact]
    public void Server_list_projection_reports_remote_transport_and_header_credentials()
    {
        var profile = RemoteProfile();
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            21,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var missing = MainWindowViewModel.ProjectMcpServerProfile(stored, []);
        var matching = MainWindowViewModel.ProjectMcpServerProfile(
            stored,
            [Secret(
                new SecretRef("vault-remote-token"),
                "Remote token",
                "ApiKey",
                new SecretScope(
                    SecretScopeKind.McpServer,
                    profile.Id.Value))]);

        Assert.Equal(McpServerTransportKind.StreamableHttp, missing.TransportKind);
        Assert.Equal(
            "https://mcp.example.test/rpc",
            missing.Address);
        Assert.Equal("Streamable HTTP", missing.TransportSummary);
        Assert.Equal("1 vault binding", missing.CredentialBindingSummary);
        Assert.Equal("Credential missing", missing.Status);
        Assert.Equal("Enabled for new runs", matching.Status);
        Assert.False(matching.HasWarning);
    }

    [Fact]
    public void Settings_offers_missing_remote_header_reference_as_a_vault_target()
    {
        var profile = RemoteProfile();
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles =
                [
                    new StoredDefinition<McpServerProfile>(
                        profile,
                        22,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch),
                ],
            };
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingProxy>(),
            catalog,
            DispatchProxy.Create<IConnectionRuntime, RejectingProxy>(),
            DispatchProxy.Create<ISecretVault, SecretVaultProxy>(),
            DispatchProxy.Create<IFilePanelClient, FilePanelProxy>(),
            DispatchProxy.Create<
                IFileTransferQueueClient,
                FileTransferQueueProxy>(),
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System));

        var target = Assert.Single(viewModel.McpServerSecretTargets);

        Assert.Equal(profile.Id, target.ProfileId);
        Assert.Equal(
            McpServerCredentialBindingKind.HttpHeader,
            target.BindingKind);
        Assert.Equal("Authorization", target.BindingName);
        Assert.Equal(new SecretRef("vault-remote-token"), target.Reference);
        Assert.Contains("HTTP header", target.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("secret value", target.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_fills_the_exact_remote_header_secret_reference()
    {
        var profile = RemoteProfile();
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles =
                [
                    new StoredDefinition<McpServerProfile>(
                        profile,
                        23,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch),
                ],
            };
        var vault = DispatchProxy.Create<ISecretVault, SecretVaultProxy>();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingProxy>(),
            catalog,
            DispatchProxy.Create<IConnectionRuntime, RejectingProxy>(),
            vault,
            DispatchProxy.Create<IFilePanelClient, FilePanelProxy>(),
            DispatchProxy.Create<
                IFileTransferQueueClient,
                FileTransferQueueProxy>(),
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System));
        var target = Assert.Single(viewModel.McpServerSecretTargets);

        var created = await viewModel.CreateMcpServerSecretAsync(
            target,
            "Remote token",
            SecretKind.ApiKey,
            "not-returned-to-settings",
            CancellationToken.None);

        Assert.True(created);
        var createRequest = Assert.IsType<CreateSecretRequest>(
            ((SecretVaultProxy)(object)vault).CreateRequest);
        Assert.Equal(target.Reference, createRequest.Reference);
        Assert.Equal(
            new SecretScope(SecretScopeKind.McpServer, profile.Id.Value),
            createRequest.Scope);
        Assert.Equal(
            SecretUseKind.UserManagement,
            createRequest.Purpose.Kind);
        Assert.Empty(viewModel.McpServerSecretTargets);
    }

    [Fact]
    public void Editor_and_trust_review_are_keyboard_and_screen_reader_accessible()
    {
        XNamespace view = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "McpServerProfileEditorDialog.axaml"));
        var confirmation = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "McpServerTrustConfirmationDialog.axaml"));

        Assert.Equal("OnOpened", editor.Root?.Attribute("Opened")?.Value);
        Assert.NotNull(editor.Descendants(view + "TextBox").Single(element => string.Equals(element.Attribute(x + "Name")?.Value, "McpServerNameInput", StringComparison.Ordinal)));
        Assert.NotNull(editor.Descendants(view + "ComboBox").Single(element => string.Equals(element.Attribute(x + "Name")?.Value, "McpServerTransportPicker"
, StringComparison.Ordinal) && string.Equals(element.Attribute("ItemsSource")?.Value
, "{Binding TransportOptions}"
, StringComparison.Ordinal) && string.Equals(element.Attribute("SelectedItem")?.Value
, "{Binding SelectedTransport}", StringComparison.Ordinal)));
        Assert.Contains(
            editor.Descendants(view + "TextBox"),
            element => string.Equals(element.Attribute("AutomationProperties.Name")?.Value
, "MCP Streamable HTTP endpoint", StringComparison.Ordinal));
        Assert.Contains(
            editor.Descendants(view + "ItemsControl"),
            element => string.Equals(element.Attribute("ItemsSource")?.Value
, "{Binding HttpHeaders}", StringComparison.Ordinal));
        var validationError = Assert.Single(
            editor.Descendants(XName.Get("LiveRegionTextBlock", "using:GhostShell.App.Controls")),
            element => string.Equals(element.Attribute(x + "Name")?.Value, "ValidationError", StringComparison.Ordinal));
        Assert.Equal("True", validationError.Attribute("Focusable")?.Value);
        Assert.Equal("Assertive", validationError.Attribute("AutomationProperties.LiveSetting")?.Value);
        Assert.Equal("MCP server validation error", validationError.Attribute("AutomationProperties.Name")?.Value);
        // The sentence lives on the executable field's Hint, which LabeledField
        // renders below the control; the guarantee is that it is stated, not
        // which element states it.
        Assert.Contains(
            editor.Descendants(),
            element =>
                (element.Attribute("Text")?.Value ?? element.Attribute("Hint")?.Value)
                    ?.Contains("not a shell command", StringComparison.OrdinalIgnoreCase)
                == true);
        foreach (var iconButton in editor
                     .Descendants(view + "Button")
                     .Where(button => button.Descendants(view + "SymbolIcon").Any()))
        {
            Assert.False(string.IsNullOrWhiteSpace(
                iconButton.Attribute("ToolTip.Tip")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(
                iconButton.Attribute("AutomationProperties.Name")?.Value));
        }

        var editorCancel = Assert.Single(
            editor.Descendants(view + "Button"),
            button => string.Equals(button.Attribute("Content")?.Value, "Cancel", StringComparison.Ordinal));
        Assert.Equal("True", editorCancel.Attribute("IsCancel")?.Value);
        var editorSave = Assert.Single(
            editor.Descendants(view + "Button"),
            button => string.Equals(button.Attribute("Content")?.Value, "Review and save", StringComparison.Ordinal));
        Assert.Equal("True", editorSave.Attribute("IsDefault")?.Value);

        Assert.Equal("OnOpened", confirmation.Root?.Attribute("Opened")?.Value);
        var acknowledgement = Assert.Single(
            confirmation.Descendants(view + "CheckBox"),
            element => string.Equals(element.Attribute(x + "Name")?.Value, "Acknowledgement", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(
            acknowledgement.Attribute("AutomationProperties.Name")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(
            acknowledgement.Attribute("AutomationProperties.HelpText")?.Value));
        var confirm = Assert.Single(
            confirmation.Descendants(view + "Button"),
            button => string.Equals(button.Attribute(x + "Name")?.Value, "ConfirmButton", StringComparison.Ordinal));
        Assert.Equal("False", confirm.Attribute("IsEnabled")?.Value);
        Assert.Equal("True", confirm.Attribute("IsDefault")?.Value);
        Assert.Contains(
            confirmation.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "Credential values are never shown",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            confirmation.Descendants(),
            element => element.Attribute("Text")?.Value?.Contains(
                "Header values are never shown",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Settings_surface_has_navigation_empty_state_health_and_credential_controls()
    {
        XNamespace view = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var ownedSection = ApplicationViews.FindUniqueNamedElement(
            "McpSettingsSection");
        var mainWindow = ownedSection.Owner.Document;
        var section = ownedSection.Element;

        Assert.Equal(view + "StackPanel", section.Name);
        Assert.Equal(
            "{Binding IsAgentSettingsVisible}",
            section.Ancestors().Single(element => string.Equals(element.Attribute(x + "Name")?.Value, "AiSettingsPage", StringComparison.Ordinal))
                .Attribute("IsVisible")?.Value);
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            navigationItem => string.Equals(navigationItem.Name.LocalName, "ShellNavigationItem"
, StringComparison.Ordinal) && string.Equals(navigationItem.Attribute("Click")?.Value, "OnMcpSettingsClick", StringComparison.Ordinal));
        Assert.Contains(
            section.Descendants(),
            element => string.Equals(element.Name.LocalName, "StateOverlay"
, StringComparison.Ordinal)
                && string.Equals(element.Attribute("Kind")?.Value, "Empty", StringComparison.Ordinal)
                && string.Equals(element.Attribute("Heading")?.Value, "No MCP server configured", StringComparison.Ordinal));
        Assert.Contains(
            section.Descendants(view + "Button"),
            button => string.Equals(button.Attribute("Click")?.Value
, "OnTestMcpServerClick"
, StringComparison.Ordinal) && string.Equals(button.Attribute("IsEnabled")?.Value
, "{Binding CanTest}"
, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(
                    button.Attribute(
                        "AutomationProperties.Name")?.Value));
        Assert.DoesNotContain(
            section.Descendants(view + "TextBlock"),
            element => element.Attribute("Text")?.Value?.Contains(
                "No process is started",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(section.Descendants(view + "ComboBox").Single(element => string.Equals(element.Attribute(x + "Name")?.Value, "McpServerSecretTargetPicker", StringComparison.Ordinal)));
        Assert.NotNull(section.Descendants(view + "TextBox").Single(element => string.Equals(element.Attribute(x + "Name")?.Value, "McpServerSecretValueInput"
, StringComparison.Ordinal) && string.Equals(element.Attribute("PasswordChar")?.Value, "●", StringComparison.Ordinal)));
        Assert.All(
            section.Descendants(view + "Button").Where(button =>
                button.Descendants(view + "SymbolIcon").Any()),
            button =>
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    button.Attribute("ToolTip.Tip")?.Value));
                Assert.False(string.IsNullOrWhiteSpace(
                    button.Attribute("AutomationProperties.Name")?.Value));
            });
    }

    [Fact]
    public async Task Settings_test_preserves_row_identity_and_reports_bounded_counts()
    {
        var profile = Profile(
            enabledTools: ["read"],
            isEnabled: false,
            hasEnvironment: false);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            11,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        var diagnosticsProxy =
            (McpDiagnosticsProxy)(object)diagnostics;
        diagnosticsProxy.Result = new McpServerTestResult.Success(
            new McpServerTestReport(
                profile.Id,
                stored.Revision,
                discoveredToolCount: 2,
                enabledToolCount: 1,
                DateTimeOffset.UnixEpoch));
        var filePanel = DispatchProxy.Create<
            IFilePanelClient,
            FilePanelProxy>();
        var transfers = DispatchProxy.Create<
            IFileTransferQueueClient,
            FileTransferQueueProxy>();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            DispatchProxy.Create<
                ISecretVault,
                SecretVaultProxy>(),
            filePanel,
            transfers,
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics);
        var item = Assert.Single(
            viewModel.McpServerDefinitions);

        await viewModel.TestMcpServerAsync(
            item,
            CancellationToken.None);

        var tested = Assert.Single(
            viewModel.McpServerDefinitions);
        Assert.Same(item, tested);
        Assert.Equal("Last test passed", tested.Status);
        Assert.Contains(
            "Found 2 tools",
            tested.StatusDetail,
            StringComparison.Ordinal);
        Assert.True(tested.CanTest);
        Assert.False(tested.IsTesting);
        Assert.Equal(1, diagnosticsProxy.CallCount);
        Assert.Equal(
            stored.Revision,
            diagnosticsProxy.Request!.ExpectedRevision);
        Assert.Equal(
            ActorKind.Human,
            diagnosticsProxy.Context!.Actor.Kind);
        Assert.Equal(
            stored.Revision,
            diagnosticsProxy.Context.ExpectedRevision);
    }

    [Fact]
    public void Settings_projects_correlated_live_diagnostic_state()
    {
        var profile = Profile(
            enabledTools: ["read"],
            hasEnvironment: false);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            14,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        ((McpDiagnosticsProxy)(object)diagnostics).SnapshotValue =
            new McpServerDiagnosticsSnapshot(
                [
                    new McpServerDiagnosticSummary(
                        profile.Id,
                        stored.Revision,
                        "0123456789abcdef",
                        McpServerSessionKind.AgentRun,
                        McpServerLifecycleState.Starting,
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.UnixEpoch,
                        [
                            new McpServerDiagnosticEvent(
                                DateTimeOffset.UnixEpoch,
                                McpServerLifecycleState.Starting,
                                "mcp_starting",
                                "Starting the MCP server and negotiating its protocol."),
                        ]),
                ],
                cleanupUncertain: false,
                cleanupUncertainAtUtc: null);
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, RejectingProxy>(),
            catalog,
            DispatchProxy.Create<IConnectionRuntime, RejectingProxy>(),
            DispatchProxy.Create<ISecretVault, SecretVaultProxy>(),
            DispatchProxy.Create<IFilePanelClient, FilePanelProxy>(),
            DispatchProxy.Create<
                IFileTransferQueueClient,
                FileTransferQueueProxy>(),
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics);

        var item = Assert.Single(viewModel.McpServerDefinitions);
        Assert.Equal("Starting", item.Status);
        Assert.Contains("01234567", item.StatusDetail, StringComparison.Ordinal);
        Assert.True(viewModel.HasMcpDiagnosticHistory);
    }

    [Fact]
    public async Task Settings_test_keeps_the_same_row_while_diagnostics_are_pending()
    {
        var profile = Profile(
            enabledTools: ["read"],
            hasEnvironment: false);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            12,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        var diagnosticsProxy =
            (McpDiagnosticsProxy)(object)diagnostics;
        diagnosticsProxy.PendingResult = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var filePanel = DispatchProxy.Create<
            IFilePanelClient,
            FilePanelProxy>();
        var transfers = DispatchProxy.Create<
            IFileTransferQueueClient,
            FileTransferQueueProxy>();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            DispatchProxy.Create<
                ISecretVault,
                SecretVaultProxy>(),
            filePanel,
            transfers,
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics);
        var original = Assert.Single(viewModel.McpServerDefinitions);

        var testTask = viewModel.TestMcpServerAsync(
                original,
                CancellationToken.None)
            .AsTask();

        var pending = Assert.Single(viewModel.McpServerDefinitions);
        Assert.Same(original, pending);
        Assert.True(pending.IsTesting);
        Assert.Equal("Testing…", pending.TestActionLabel);

        diagnosticsProxy.PendingResult.SetResult(
            new McpServerTestResult.Success(
                new McpServerTestReport(
                    profile.Id,
                    stored.Revision,
                    discoveredToolCount: 1,
                    enabledToolCount: 1,
                    DateTimeOffset.UnixEpoch)));
        await testTask;

        Assert.Same(
            original,
            Assert.Single(viewModel.McpServerDefinitions));
        Assert.False(original.IsTesting);
        Assert.Equal("Last test passed", original.Status);
    }

    [Fact]
    public async Task ReplacingAnMcpCredentialInvalidatesOnlyMcpSessions()
    {
        var mcpReference = new SecretRef("vault-mcp-token");
        var mcpScope = new SecretScope(
            SecretScopeKind.McpServer,
            "mcp.local-tools");
        var profile = new McpServerProfile(
            new McpServerProfileId("mcp.local-tools"),
            McpServerProfile.CurrentSchemaVersion,
            "Local tools",
            new McpServerTransport.Stdio(
                "/opt/tools/mcp-server",
                ["--stdio"],
                "/var/lib/mcp",
                [new McpServerEnvironmentVariable(
                    "API_TOKEN",
                    mcpReference)]),
            ["read"]);
        var stored = new StoredDefinition<McpServerProfile>(
            profile,
            13,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = DispatchProxy.Create<
            IDefinitionCatalog,
            CatalogProxy>();
        ((CatalogProxy)(object)catalog).SnapshotValue =
            DefinitionCatalogSnapshot.Empty with
            {
                McpServerProfiles = [stored],
            };
        var vault = DispatchProxy.Create<
            ISecretVault,
            SecretVaultProxy>();
        ((SecretVaultProxy)(object)vault).Metadata =
        [
            new SecretMetadata(
                mcpReference,
                "MCP token",
                SecretKind.ApiKey,
                mcpScope,
                SecretVaultPersistenceKind.MemoryOnly,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
        ];
        var invalidator = DispatchProxy.Create<
            IMcpCredentialSessionInvalidator,
            McpCredentialInvalidatorProxy>();
        var diagnostics = DispatchProxy.Create<
            IMcpServerDiagnostics,
            McpDiagnosticsProxy>();
        ((McpDiagnosticsProxy)(object)diagnostics).Result =
            new McpServerTestResult.Success(
                new McpServerTestReport(
                    profile.Id,
                    stored.Revision,
                    discoveredToolCount: 1,
                    enabledToolCount: 1,
                    DateTimeOffset.UnixEpoch));
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<
                ISessionHostClient,
                RejectingProxy>(),
            catalog,
            DispatchProxy.Create<
                IConnectionRuntime,
                RejectingProxy>(),
            vault,
            DispatchProxy.Create<
                IFilePanelClient,
                FilePanelProxy>(),
            DispatchProxy.Create<
                IFileTransferQueueClient,
                FileTransferQueueProxy>(),
            new TerminalStartupCommandDispatcher(
                new UnusedAuditStore(),
                TimeProvider.System),
            mcpServerDiagnostics: diagnostics,
            mcpCredentialSessionInvalidator: invalidator);
        viewModel.ShowSettings(SettingsPage.Mcp);
        var mcpSecret = Secret(
            mcpReference,
            "MCP token",
            "ApiKey",
            mcpScope);
        var globalSecret = Secret(
            new SecretRef("vault-global-token"),
            "Global token",
            "ApiKey",
            SecretScope.Global);
        using var firstReplacement =
            SecretMaterial.CopyFrom("first"u8);
        using var secondReplacement =
            SecretMaterial.CopyFrom("second"u8);

        var row = Assert.Single(viewModel.McpServerDefinitions);
        await viewModel.TestMcpServerAsync(
            row,
            CancellationToken.None);
        Assert.Equal("Last test passed", row.Status);

        Assert.True(await viewModel.ReplaceSecretAsync(
            mcpSecret,
            firstReplacement,
            CancellationToken.None));
        Assert.True(await viewModel.ReplaceSecretAsync(
            globalSecret,
            secondReplacement,
            CancellationToken.None));

        var invalidatorProxy =
            (McpCredentialInvalidatorProxy)(object)invalidator;
        Assert.Equal([mcpReference], invalidatorProxy.References);
        Assert.Same(
            row,
            Assert.Single(viewModel.McpServerDefinitions));
        Assert.Equal(
            "Enabled for new runs",
            row.Status);
        Assert.DoesNotContain(
            "test passed",
            row.StatusDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            2,
            ((SecretVaultProxy)(object)vault).ReplaceCount);
    }

    private static McpServerProfileEditorViewModel NewEditor() =>
        new()
        {
            Name = "Local tools",
            Executable = "/opt/tools/mcp-server",
            WorkingDirectory = "/var/lib/mcp",
        };

    private static McpServerProfileEditorViewModel NewRemoteEditor()
    {
        var editor = new McpServerProfileEditorViewModel
        {
            Name = "Remote tools",
            Endpoint = "https://mcp.example.test/rpc",
        };
        editor.SelectedTransport = editor.TransportOptions.Single(option =>
            option.Kind == McpServerTransportKind.StreamableHttp);
        return editor;
    }

    private static McpServerProfile Profile(
        IReadOnlyList<string> enabledTools,
        bool isEnabled = true,
        bool hasEnvironment = true,
        bool isTrusted = true) =>
        new(
            new McpServerProfileId("mcp.local-tools"),
            McpServerProfile.CurrentSchemaVersion,
            "Local tools",
            new McpServerTransport.Stdio(
                "/opt/tools/mcp-server",
                ["--stdio"],
                "/var/lib/mcp",
                hasEnvironment
                    ? [new McpServerEnvironmentVariable(
                        "API_TOKEN",
                        new SecretRef("vault-ref"))]
                    : []),
            enabledTools,
            isEnabled,
            isTrusted);

    private static McpServerProfile RemoteProfile() =>
        new(
            new McpServerProfileId("mcp.remote-tools"),
            McpServerProfile.CurrentSchemaVersion,
            "Remote tools",
            new McpServerTransport.StreamableHttp(
                new Uri("https://mcp.example.test/rpc"),
                [
                    new McpServerHttpHeader(
                        "Authorization",
                        new SecretRef("vault-remote-token")),
                ]),
            ["remote.inspect"]);

    private static SecretMetadataViewModel Secret(
        SecretRef reference,
        string label,
        string kind,
        SecretScope scope) =>
        new(
            reference,
            label,
            kind,
            scope.Kind.ToString(),
            "Never",
            "Never",
            scope,
            "MCP server",
            1);

    public class CatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot SnapshotValue { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Snapshot" => SnapshotValue,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class McpDiagnosticsProxy : DispatchProxy
    {
        public McpServerDiagnosticsSnapshot SnapshotValue { get; set; } =
            new([], cleanupUncertain: false, cleanupUncertainAtUtc: null);

        public McpServerTestResult Result { get; set; } =
            new McpServerTestResult.Failure(
                new McpServerTestError(
                    "mcp_test_failed",
                    "Test failure.",
                    retryable: false));

        public TaskCompletionSource<McpServerTestResult>? PendingResult
        {
            get;
            set;
        }

        public int CallCount { get; private set; }

        public McpServerTestRequest? Request { get; private set; }

        public OperationContext? Context { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (string.Equals(
                    targetMethod?.Name,
                    "get_Snapshot",
                    StringComparison.Ordinal))
            {
                return SnapshotValue;
            }

            if (targetMethod?.Name is "add_Changed" or "remove_Changed")
            {
                return null;
            }

            if (string.Equals(targetMethod?.Name
, nameof(IMcpServerDiagnostics.TestAsync)
, StringComparison.Ordinal) && arguments is
                [
                    McpServerTestRequest request,
                    OperationContext context,
                    CancellationToken cancellationToken,
                ])
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                Request = request;
                Context = context;
                return PendingResult is null
                    ? ValueTask.FromResult(Result)
                    : new ValueTask<McpServerTestResult>(
                        PendingResult.Task);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class SecretVaultProxy : DispatchProxy
    {
        public IReadOnlyList<SecretMetadata> Metadata { get; set; } = [];

        public CreateSecretRequest? CreateRequest { get; private set; }

        public int ReplaceCount { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (string.Equals(targetMethod?.Name, nameof(ISecretVault.CreateAsync)
, StringComparison.Ordinal) && arguments is
                [
                    CreateSecretRequest createRequest,
                    SecretMaterial,
                    CancellationToken,
                ])
            {
                CreateRequest = createRequest;
                var metadata = new SecretMetadata(
                    createRequest.Reference,
                    createRequest.Label,
                    createRequest.Kind,
                    createRequest.Scope,
                    SecretVaultPersistenceKind.MemoryOnly,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch);
                Metadata = [metadata];
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(metadata));
            }

            if (string.Equals(targetMethod?.Name, nameof(ISecretVault.ReplaceAsync)
, StringComparison.Ordinal) && arguments is
                [
                    ReplaceSecretRequest request,
                    SecretMaterial,
                    CancellationToken,
                ])
            {
                ReplaceCount++;
                return ValueTask.FromResult(
                    SecretVaultResult<SecretMetadata>.Succeed(
                        new SecretMetadata(
                            request.Reference,
                            "Updated credential",
                            SecretKind.ApiKey,
                            request.Scope,
                            SecretVaultPersistenceKind.MemoryOnly,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch)));
            }

            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.ListMetadata,
                    "test",
                    "test_available",
                    "Test vault is available."),
                nameof(ISecretVault.ListMetadataAsync) =>
                    ValueTask.FromResult(
                        SecretVaultResult<
                            IReadOnlyList<SecretMetadata>>.Succeed(
                                Metadata)),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
        }
    }

    public class McpCredentialInvalidatorProxy : DispatchProxy
    {
        public List<SecretRef> References { get; } = [];

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            if (string.Equals(targetMethod?.Name
, nameof(
                        IMcpCredentialSessionInvalidator
                            .InvalidateAsync)
, StringComparison.Ordinal) && arguments is [SecretRef reference])
            {
                References.Add(reference);
                return ValueTask.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class FilePanelProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Profiles" =>
                    Array.Empty<FileProviderProfileDescriptor>(),
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class FileTransferQueueProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Transfers" =>
                    Array.Empty<FilePanelTransferSnapshot>(),
                "add_TransfersChanged"
                    or "remove_TransfersChanged" => null,
                _ => throw new NotSupportedException(
                    targetMethod?.Name),
            };
    }

    public class RejectingProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class UnusedAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

}
