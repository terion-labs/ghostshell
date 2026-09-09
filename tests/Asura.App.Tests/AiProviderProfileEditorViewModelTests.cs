using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.Application;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class AiProviderProfileEditorViewModelTests
{
    [Fact]
    public async Task Inline_key_is_masked_and_stays_selected_when_the_live_picker_rebuilds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(async () =>
            {
                using var runtime = new StubRuntime();
                var editor = new AiProviderProfileEditorViewModel(runtime, [],
                    storeCredential: (request, _, _) => ValueTask.FromResult(Stored(request)))
                { Name = "Gateway" };
                var dialog = new AiProviderProfileEditorDialog(editor);
                dialog.Show();
                try
                {
                    var input = dialog.FindControl<TextBox>("ApiKeyInput");
                    Assert.NotNull(input);
                    Assert.NotEqual(default, input.PasswordChar);
                    input.Text = "synthetic-key";
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Equal("synthetic-key", editor.ApiKeyValue);
                    Assert.True(await editor.StoreApiKeyAsync(timeout.Token));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    dialog.UpdateLayout();
                    Assert.Empty(input.Text!);
                    var selected = editor.SelectedCredential;
                    Assert.NotNull(selected);
                    Assert.True(selected.IsAvailable);
                    Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(),
                        picker => Equals(picker.SelectedItem, selected));
                    editor.ApiKeyValue = "discarded-synthetic-key";
                }
                finally { dialog.Close(); }
                Assert.Empty(editor.ApiKeyValue);
                return true;
            }, timeout.Token));
        }
        finally { await session.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_and_save_store_a_new_key_before_using_the_draft(bool test)
    {
        using var runtime = new StubRuntime();
        CreateSecretRequest? stored = null;
        SecretMaterial? capturedMaterial = null;
        var editor = new AiProviderProfileEditorViewModel(runtime, [], storeCredential: (request, material, token) =>
        {
            token.ThrowIfCancellationRequested();
            stored = request;
            capturedMaterial = material;
            var bytes = new byte[material.Length];
            material.CopyTo(bytes);
            Assert.Equal("synthetic-inline-key", System.Text.Encoding.UTF8.GetString(bytes));
            return ValueTask.FromResult(Stored(request));
        })
        { Name = "Gateway", ApiKeyValue = "synthetic-inline-key" };

        AiProviderProfile? profile;
        if (test)
        {
            await editor.TestAsync(CancellationToken.None);
            profile = runtime.LastProfile;
        }
        else
        {
            profile = (await editor.PrepareSaveAsync(CancellationToken.None))?.Profile;
        }

        Assert.NotNull(profile);
        Assert.NotNull(stored);
        Assert.Equal(SecretKind.ApiKey, stored.Kind);
        Assert.Equal(new SecretScope(SecretScopeKind.AiProvider, editor.ProfileId), stored.Scope);
        Assert.Equal(new SecretUsePurpose(SecretUseKind.UserManagement, editor.ProfileId), stored.Purpose);
        Assert.Equal(stored.Reference, Assert.IsType<AiProviderAuthentication.ApiKey>(profile.Authentication).Secret);
        Assert.Equal(stored.Reference, editor.SelectedCredential?.Reference);
        Assert.True(editor.SelectedCredential?.IsAvailable);
        Assert.Empty(editor.ApiKeyValue);
        Assert.True(capturedMaterial?.IsDisposed);
        Assert.DoesNotContain("synthetic-inline-key", profile.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_storage_clears_input_without_testing_or_changing_the_selected_key(bool throws)
    {
        using var runtime = new StubRuntime();
        var reference = new SecretRef("existing-provider-key");
        var profile = Profile(new AiProviderAuthentication.ApiKey(reference), AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"));
        var editor = new AiProviderProfileEditorViewModel(runtime, [], profile, storeCredential: (_, _, _) =>
            throws
                ? throw new InvalidOperationException("synthetic-secret-must-not-appear")
                : ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                    SecretVaultError.Create(SecretVaultErrorCode.AccessDenied))))
        { ApiKeyValue = "synthetic-secret-must-not-appear" };

        Assert.Null(await editor.PrepareSaveAsync(CancellationToken.None));
        editor.ApiKeyValue = "synthetic-secret-must-not-appear";
        await editor.TestAsync(CancellationToken.None);

        Assert.Null(runtime.LastProfile);
        Assert.Equal(reference, editor.SelectedCredential?.Reference);
        Assert.Empty(editor.ApiKeyValue);
        Assert.False(editor.IsStoringCredential);
        Assert.Contains("could not be stored", editor.CredentialStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-must-not-appear", editor.CredentialStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_stored_key_has_a_fresh_reference_and_null_binding_updates_preserve_selection()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [],
            storeCredential: (request, _, _) => ValueTask.FromResult(Stored(request)))
        { Name = "Gateway", ApiKeyValue = "first-synthetic-key" };
        Assert.True(await editor.StoreApiKeyAsync(CancellationToken.None));
        var first = editor.SelectedCredential;
        editor.ApiKeyValue = "second-synthetic-key";
        Assert.True(await editor.StoreApiKeyAsync(CancellationToken.None));
        var second = editor.SelectedCredential;
        editor.SelectedCredential = null;

        Assert.NotEqual(first?.Reference, second?.Reference);
        Assert.Equal(second, editor.SelectedCredential);
        Assert.Contains(first, editor.SecretOptions);
        Assert.Contains(second, editor.SecretOptions);
    }

    [Fact]
    public async Task Pending_storage_blocks_duplicate_save_and_test_and_cancellation_clears_input()
    {
        using var runtime = new StubRuntime();
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<SecretVaultResult<SecretMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var editor = new AiProviderProfileEditorViewModel(runtime, [], storeCredential: (_, _, token) =>
        {
            calls++;
            return new ValueTask<SecretVaultResult<SecretMetadata>>(pending.Task.WaitAsync(token));
        })
        { Name = "Gateway", ApiKeyValue = "synthetic-key" };
        var storing = editor.StoreApiKeyAsync(cancellation.Token);
        Assert.True(editor.IsStoringCredential);
        Assert.False(editor.CanStoreCredential);
        Assert.False(editor.CanTest);
        Assert.Null(await editor.PrepareSaveAsync(CancellationToken.None));
        await editor.TestAsync(CancellationToken.None);
        cancellation.Cancel();

        Assert.False(await storing);
        Assert.Equal(1, calls);
        Assert.Null(runtime.LastProfile);
        Assert.Empty(editor.ApiKeyValue);
        Assert.False(editor.IsStoringCredential);
        Assert.Contains("cancelled", editor.CredentialStatus, StringComparison.Ordinal);
    }

    private static SecretVaultResult<SecretMetadata> Stored(CreateSecretRequest request) =>
        SecretVaultResult<SecretMetadata>.Succeed(new SecretMetadata(request.Reference, request.Label,
            request.Kind, request.Scope, SecretVaultPersistenceKind.OsProtectedPersistent,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    [Fact]
    public void New_openai_profile_uses_an_opaque_repairable_credential_slot()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            suggestedOrder: 3)
        {
            Name = "OpenAI",
        };

        var request = editor.CreateSaveRequest();

        Assert.Null(request.ExpectedRevision);
        Assert.Equal(AiProviderKind.OpenAi, request.Profile.ProviderKind);
        Assert.Equal(3, request.Profile.Order);
        Assert.Equal(new Uri("https://api.openai.com/v1/"), request.Profile.Endpoint);
        var authentication = Assert.IsType<AiProviderAuthentication.ApiKey>(
            request.Profile.Authentication);
        Assert.False(string.IsNullOrWhiteSpace(authentication.Secret.Value));
        Assert.DoesNotContain(
            authentication.Secret.Value,
            request.Profile.Name,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compatible_loopback_profile_can_explicitly_disable_authentication()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://127.0.0.1:11434/v1/",
            DefaultModel = "local-model",
            UseNoAuthentication = true,
        };

        var request = editor.CreateSaveRequest();

        Assert.IsType<AiProviderAuthentication.None>(request.Profile.Authentication);
    }

    [Fact]
    public void Remote_profile_cannot_disable_authentication()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Gateway",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "https://gateway.example.test/v1/",
            DefaultModel = "model",
            UseNoAuthentication = true,
        };

        var exception = Assert.Throws<ArgumentException>(editor.CreateSaveRequest);

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_existing_credential_reference_is_preserved_for_repair()
    {
        using var runtime = new StubRuntime();
        var reference = new SecretRef("missing-provider-key");
        var profile = Profile(
            new AiProviderAuthentication.ApiKey(reference),
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"));
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            profile,
            expectedRevision: 7);

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(
            reference,
            Assert.IsType<AiProviderAuthentication.ApiKey>(
                request.Profile.Authentication).Secret);
        Assert.Contains(
            editor.SecretOptions,
            option => option.Reference == reference && !option.IsAvailable);
    }

    [Fact]
    public async Task Test_projects_bounded_runtime_result_and_models()
    {
        using var runtime = new StubRuntime
        {
            Result = new AiProviderTestResult(
                true,
                "ai_provider_test_succeeded",
                "Connected.",
                [new AiProviderModelDescriptor("model", "Model")]),
        };
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost:11434/v1/",
            DefaultModel = "model",
            UseNoAuthentication = true,
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Provider connected", editor.TestStatus);
        Assert.Equal("Connected.", editor.TestDetail);
        Assert.Equal("model", Assert.Single(editor.Models).Id);
        Assert.NotNull(runtime.LastProfile);
        var saved = editor.CreateSaveRequest().Profile;
        Assert.Equal(["model"], saved.DiscoveredModelIds);
        var reopened = new AiProviderProfileEditorViewModel(runtime, [], saved);
        Assert.Equal(["model"], reopened.CreateSaveRequest().Profile.DiscoveredModelIds);
        reopened.DefaultModel = "another-model";
        Assert.Equal(["model"], reopened.CreateSaveRequest().Profile.DiscoveredModelIds);
        reopened.Endpoint = "http://localhost:11435/v1/";
        Assert.Empty(reopened.CreateSaveRequest().Profile.DiscoveredModelIds);
    }

    [Fact]
    public async Task Discovery_remains_available_when_the_default_model_needs_correction()
    {
        using var runtime = new StubRuntime
        {
            Result = new AiProviderTestResult(false, "ai_provider_model_unavailable", "Choose a returned model.",
                [new AiProviderModelDescriptor("actual-model", "Actual model")]),
        };
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost:11434/v1",
            DefaultModel = "wrong-model",
            UseNoAuthentication = true,
        };
        await editor.TestAsync(CancellationToken.None);
        editor.DefaultModel = "actual-model";
        Assert.Equal(["actual-model"], editor.CreateSaveRequest().Profile.DiscoveredModelIds);
    }

    [Fact]
    public async Task Configuration_only_test_does_not_claim_a_live_provider_connection()
    {
        using var runtime = new StubRuntime
        {
            Result = new AiProviderTestResult(
                true,
                "ai_provider_test_configuration_valid",
                "The configured OAuth session is readable.",
                [new AiProviderModelDescriptor("model", "Model")]),
        };
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Name = "Local",
            Kind = AiProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost:11434/v1/",
            DefaultModel = "model",
            UseNoAuthentication = true,
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Configuration valid", editor.TestStatus);
        Assert.DoesNotContain("connected", editor.TestStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_scoped_secret_is_the_only_reusable_option()
    {
        using var runtime = new StubRuntime();
        var own = Secret(
            new SecretRef("own-key"),
            new SecretScope(SecretScopeKind.AiProvider, "provider"));
        var other = Secret(
            new SecretRef("other-key"),
            new SecretScope(SecretScopeKind.AiProvider, "other-provider"));
        var profile = Profile(
            new AiProviderAuthentication.ApiKey(own.Reference),
            AiProviderKind.OpenAi,
            new Uri("https://api.openai.com/v1/"));

        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [own, other],
            profile,
            expectedRevision: 1);

        Assert.Contains(editor.SecretOptions, option => option.Reference == own.Reference);
        Assert.DoesNotContain(editor.SecretOptions, option => option.Reference == other.Reference);
    }

    [Fact]
    public void Provider_catalog_drives_display_defaults_and_authentication_choices()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, []);

        Assert.Equal(AiProviderCatalog.Definitions.Count, editor.ProviderOptions.Count);
        Assert.Contains(editor.ProviderOptions, option =>
            option.Kind == AiProviderKind.MoonshotAi
            && string.Equals(option.DisplayName, "Moonshot AI", StringComparison.Ordinal));

        editor.Kind = AiProviderKind.GitHubCopilot;

        Assert.Equal("gpt-5.6-terra", editor.DefaultModel);
        Assert.Equal(AiProviderProtocol.GitHubCopilot.ToString(), editor.ProviderProtocol);
        Assert.Equal(
            AiProviderEditorAuthenticationMode.OAuthDevice,
            editor.SelectedAuthentication!.Mode);
        Assert.Single(editor.AuthenticationOptions);

        editor.Kind = AiProviderKind.Bedrock;
        Assert.Equal(
            AiProviderEditorAuthenticationMode.AwsCredentialChain,
            editor.SelectedAuthentication!.Mode);
    }

    [Fact]
    public void Editor_distinguishes_single_values_from_selectable_options()
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, []);

        Assert.True(editor.HasMultipleAuthenticationOptions);
        Assert.False(editor.HasSingleAuthenticationOption);
        Assert.True(editor.HasSingleCredentialOption);
        Assert.False(editor.HasMultipleCredentialOptions);

        editor.Kind = AiProviderKind.GitHubCopilot;

        Assert.True(editor.HasSingleAuthenticationOption);
        Assert.False(editor.HasMultipleAuthenticationOptions);
    }

    [Fact]
    public async Task Changing_authentication_method_cancels_the_old_attempt_and_enables_device_flow()
    {
        var completion = new TaskCompletionSource<AiProviderAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime
        {
            Completion = completion.Task,
        };
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication);
        editor.SelectedAuthentication = editor.AuthenticationOptions.Single(option =>
            option.Mode == AiProviderEditorAuthenticationMode.OAuthBrowser);

        var browserLaunch = Assert.IsType<AiProviderAuthenticationLaunch>(
            await editor.BeginAuthenticationAsync(CancellationToken.None));

        Assert.True(editor.IsAuthenticating);
        Assert.False(editor.CanAuthenticate);
        editor.SelectedAuthentication = editor.AuthenticationOptions.Single(option =>
            option.Mode == AiProviderEditorAuthenticationMode.OAuthDevice);

        Assert.True(authentication.LastCancellationToken.IsCancellationRequested);
        Assert.False(editor.IsAuthenticating);
        Assert.True(editor.CanAuthenticate);
        Assert.Equal(
            "Ready to start interactive authentication.",
            editor.AuthenticationStatus);

        completion.SetResult(AiProviderAuthenticationResult.Failure(
            "ai_provider_authentication_denied",
            "The old attempt failed."));
        await browserLaunch.Completion;

        Assert.True(editor.CanAuthenticate);
        Assert.Equal(
            "Ready to start interactive authentication.",
            editor.AuthenticationStatus);
    }

    [Fact]
    public async Task Completed_oauth_flow_saves_only_the_vault_session_reference()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime();
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication)
        {
            Name = "OpenAI OAuth",
        };
        editor.SelectedAuthentication = editor.AuthenticationOptions.Single(option =>
            option.Mode == AiProviderEditorAuthenticationMode.OAuthBrowser);

        var launch = Assert.IsType<AiProviderAuthenticationLaunch>(
            await editor.BeginAuthenticationAsync(CancellationToken.None));
        await launch.Completion;
        var request = editor.CreateSaveRequest();

        Assert.Equal(new Uri("https://auth.example.test/authorize"), launch.AuthorizationUri);
        var oauth = Assert.IsType<AiProviderAuthentication.OAuth>(
            request.Profile.Authentication);
        Assert.Equal(authentication.Session, oauth.Session);
        Assert.Equal(AiProviderOAuthFlow.Browser, oauth.Flow);
        Assert.DoesNotContain(
            "raw-access-token",
            System.Text.Json.JsonSerializer.Serialize(request.Profile),
            StringComparison.Ordinal);
        Assert.Equal(
            "Connected. The token session is stored in the OS vault.",
            editor.AuthenticationStatus);
    }

    [Fact]
    public async Task Token_exchange_failure_is_presented_with_a_safe_actionable_reason()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime
        {
            Completion = Task.FromResult(AiProviderAuthenticationResult.Failure(
                "ai_provider_oauth_token_exchange_invalid_response",
                "provider detail must not be displayed")),
        };
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication);
        editor.SelectedAuthentication = editor.AuthenticationOptions.Single(option =>
            option.Mode == AiProviderEditorAuthenticationMode.OAuthDevice);

        var launch = Assert.IsType<AiProviderAuthenticationLaunch>(
            await editor.BeginAuthenticationAsync(CancellationToken.None));
        await launch.Completion;

        Assert.False(editor.IsAuthenticating);
        Assert.Equal(
            "OpenAI returned an invalid OAuth token response.",
            editor.AuthenticationStatus);
        Assert.DoesNotContain("provider detail", editor.AuthenticationStatus);
    }

    [Fact]
    public async Task Authentication_start_failure_is_normalized_without_exception_or_detail_leakage()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime
        {
            StartFailure = new InvalidOperationException("sensitive-provider-detail"),
        };
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication)
        {
            Kind = AiProviderKind.GitHubCopilot,
            Name = "Copilot",
        };

        var launch = await editor.BeginAuthenticationAsync(CancellationToken.None);

        Assert.Null(launch);
        Assert.False(editor.IsAuthenticating);
        Assert.Equal("Authentication could not be started.", editor.AuthenticationStatus);
        Assert.DoesNotContain(
            "sensitive-provider-detail",
            editor.AuthenticationStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_Asura_GitHub_client_id_disables_connect_with_precise_reason()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime
        {
            Availability = new AiProviderAuthenticationAvailability(
                false,
                "ai_provider_github_client_id_unavailable",
                "GitHub device authorization requires "
                + "ASURA_GITHUB_OAUTH_CLIENT_ID."),
        };
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication)
        {
            Kind = AiProviderKind.GitHubCopilot,
        };

        var launch = await editor.BeginAuthenticationAsync(CancellationToken.None);

        Assert.False(editor.IsInteractiveAuthenticationAvailable);
        Assert.False(editor.CanAuthenticate);
        Assert.Null(launch);
        Assert.Equal(0, authentication.StartCount);
        Assert.Contains(
            "ASURA_GITHUB_OAUTH_CLIENT_ID",
            editor.AuthenticationStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OAuth_editor_pins_and_locks_the_provider_endpoint()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime();
        var profile = new AiProviderProfile(
            new AiProviderProfileId("copilot-oauth-endpoint"),
            AiProviderProfile.CurrentSchemaVersion,
            "Copilot",
            AiProviderKind.GitHubCopilot,
            new Uri("https://attacker.example.test/steal/"),
            new AiProviderAuthentication.OAuth(
                new SecretRef("copilot-oauth-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.3-codex",
            order: 0);

        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            profile,
            expectedRevision: 1,
            authenticationRuntime: authentication);

        Assert.False(editor.IsEndpointEditable);
        Assert.Contains("pinned", editor.EndpointPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot).AbsoluteUri,
            editor.Endpoint);
        Assert.Equal(
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            editor.CreateSaveRequest().Profile.Endpoint);
    }

    [Fact]
    public async Task Unexpected_authentication_completion_fault_is_normalized()
    {
        using var runtime = new StubRuntime();
        using var authentication = new StubAuthenticationRuntime
        {
            Completion = Task.FromException<AiProviderAuthenticationResult>(
                new InvalidOperationException("sensitive-provider-detail")),
        };
        var editor = new AiProviderProfileEditorViewModel(
            runtime,
            [],
            authenticationRuntime: authentication)
        {
            Name = "OpenAI OAuth",
        };
        editor.SelectedAuthentication = editor.AuthenticationOptions.Single(option =>
            option.Mode == AiProviderEditorAuthenticationMode.OAuthBrowser);

        var launch = Assert.IsType<AiProviderAuthenticationLaunch>(
            await editor.BeginAuthenticationAsync(CancellationToken.None));
        await launch.Completion;

        Assert.False(editor.IsAuthenticating);
        Assert.Equal("Authentication failed.", editor.AuthenticationStatus);
    }

    [Theory]
    [InlineData(AiProviderKind.Google)]
    [InlineData(AiProviderKind.Bedrock)]
    public async Task Cataloged_native_protocol_without_runtime_is_visibly_fail_closed(
        AiProviderKind kind)
    {
        using var runtime = new StubRuntime();
        var editor = new AiProviderProfileEditorViewModel(runtime, [])
        {
            Kind = kind,
            Name = kind.ToString(),
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.False(editor.IsProviderRuntimeSupported);
        Assert.False(editor.CanTest);
        Assert.Contains("not implemented", editor.ProviderAvailability);
        Assert.Equal("Provider unavailable", editor.TestStatus);
        Assert.Null(runtime.LastProfile);
        Assert.Throws<ArgumentException>(editor.CreateSaveRequest);
    }

    private static AiProviderProfile Profile(
        AiProviderAuthentication authentication,
        AiProviderKind kind,
        Uri endpoint) =>
        new(
            new AiProviderProfileId("provider"),
            AiProviderProfile.CurrentSchemaVersion,
            "Provider",
            kind,
            endpoint,
            authentication,
            "model",
            order: 0);

    private static SecretMetadataViewModel Secret(SecretRef reference, SecretScope scope) =>
        new(
            reference,
            "API key",
            "ApiKey",
            scope.Kind.ToString(),
            "Today",
            "Never",
            scope,
            "No saved definition dependencies",
            0);

    private sealed class StubRuntime : IAiProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged;

        public IReadOnlyList<AiProviderProfileDescriptor> Profiles { get; set; } = [];

        public IReadOnlyList<AiProviderRuntimeDiagnostic> Diagnostics { get; set; } = [];

        public AiProviderTestResult Result { get; set; } = new(
            false,
            "ai_provider_unavailable",
            "Unavailable.",
            [],
            AiProviderRuntimeErrorCode.ProviderUnavailable);

        public AiProviderProfile? LastProfile { get; private set; }

        public ValueTask<AiProviderTestResult> TestAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProfile = profile;
            return ValueTask.FromResult(Result);
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubAuthenticationRuntime : IAiProviderAuthenticationRuntime
    {
        public SecretRef Session { get; } = new("oauth-session-reference");

        public Exception? StartFailure { get; init; }

        public Task<AiProviderAuthenticationResult>? Completion { get; init; }

        public AiProviderAuthenticationAvailability Availability { get; init; } =
            AiProviderAuthenticationAvailability.Available;

        public int StartCount { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public AiProviderAuthenticationAvailability GetAvailability(
            AiProviderKind provider,
            AiProviderOAuthFlow flow) => Availability;

        public ValueTask<AiProviderBrowserAuthorization> StartBrowserAsync(
            AiProviderProfileId profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCancellationToken = cancellationToken;
            StartCount++;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            return ValueTask.FromResult(new AiProviderBrowserAuthorization(
                new Uri("https://auth.example.test/authorize"),
                Completion
                ?? Task.FromResult(AiProviderAuthenticationResult.Success(Session))));
        }

        public ValueTask<AiProviderDeviceAuthorization> StartDeviceAsync(
            AiProviderProfileId profileId,
            AiProviderKind provider,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCancellationToken = cancellationToken;
            StartCount++;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            return ValueTask.FromResult(new AiProviderDeviceAuthorization(
                new Uri("https://auth.example.test/device"),
                "TEST-CODE",
                TimeSpan.FromSeconds(5),
                DateTimeOffset.UtcNow.AddMinutes(5),
                Completion
                ?? Task.FromResult(AiProviderAuthenticationResult.Success(Session))));
        }

        public void Dispose()
        {
        }
    }
}
