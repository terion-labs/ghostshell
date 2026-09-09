using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record AiProviderProfileSaveRequest(
    AiProviderProfile Profile,
    long? ExpectedRevision);

public sealed record AiProviderSecretOption(
    SecretRef? Reference,
    string Label,
    string Kind,
    bool IsAvailable)
{
    public string DisplayName => Reference is null
        ? Label
        : IsAvailable
            ? $"{Label} · {Kind}"
            : $"Missing · {Reference.Value.Value}";
}

public sealed record AiProviderIdentityOption(
    AiProviderKind Kind,
    string DisplayName,
    string Category,
    bool IsRuntimeSupported)
{
    public string Availability => IsRuntimeSupported
        ? Category
        : $"{Category} · Coming soon";

    public string Summary => $"{DisplayName} · {Availability}";
}

public enum AiProviderEditorAuthenticationMode
{
    ApiKey,
    NoAuthentication,
    OAuthBrowser,
    OAuthDevice,
    AwsCredentialChain,
}

public sealed record AiProviderAuthenticationOption(
    AiProviderEditorAuthenticationMode Mode,
    string Label,
    string Detail);

public sealed record AiProviderAuthenticationLaunch(
    Uri AuthorizationUri,
    Task Completion);

public sealed class AiProviderProfileEditorViewModel : ObservableObject
{
    private readonly IAiProviderProfileRuntime _runtime;
    private readonly IAiProviderAuthenticationRuntime? _authenticationRuntime;
    private readonly AiProviderProfileId _id;
    private readonly SecretRef _pendingSecretReference = SecretRef.New();
    private readonly int _schemaVersion;
    private string _name = string.Empty;
    private AiProviderKind _kind = AiProviderKind.OpenAi;
    private string _endpoint = AiProviderProfile
        .DefaultEndpoint(AiProviderKind.OpenAi)
        .AbsoluteUri;
    private string _defaultModel = "gpt-5.6-terra";
    private int _order;
    private bool _isEnabled = true;
    private AiProviderSecretOption? _selectedCredential;
    private IReadOnlyList<AiProviderAuthenticationOption> _authenticationOptions = [];
    private AiProviderAuthenticationOption? _selectedAuthentication;
    private SecretRef? _oauthSessionReference;
    private CancellationTokenSource? _authenticationAttempt;
    private bool _isAuthenticating;
    private string _authenticationStatus = "Choose how Asura authenticates this provider.";
    private string _deviceCode = string.Empty;
    private string _authenticationUri = string.Empty;
    private bool _isTesting;
    private string _testStatus = "Not tested";
    private string _testDetail =
        "Tests use the saved credential to load the provider's model list.";
    private IReadOnlyList<AiProviderModelDescriptor> _models = [];
    private AiProviderProfile? _modelCatalogProfile;
    private readonly Func<CreateSecretRequest, SecretMaterial, CancellationToken, ValueTask<SecretVaultResult<SecretMetadata>>>? _storeCredential;
    private string _apiKeyValue = string.Empty;
    private string _credentialStatus = string.Empty;
    private bool _isStoringCredential;

    public AiProviderProfileEditorViewModel(
        IAiProviderProfileRuntime runtime,
        IReadOnlyList<SecretMetadataViewModel> secrets,
        AiProviderProfile? existing = null,
        long? expectedRevision = null,
        int suggestedOrder = 0,
        IAiProviderAuthenticationRuntime? authenticationRuntime = null,
        Func<CreateSecretRequest, SecretMaterial, CancellationToken, ValueTask<SecretVaultResult<SecretMetadata>>>? storeCredential = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _authenticationRuntime = authenticationRuntime;
        ArgumentNullException.ThrowIfNull(secrets);
        ExpectedRevision = expectedRevision;
        _storeCredential = storeCredential;
        _id = existing?.Id ?? AiProviderProfileId.New();
        _schemaVersion = existing?.SchemaVersion ?? AiProviderProfile.CurrentSchemaVersion;
        ProviderKinds = Enum.GetValues<AiProviderKind>();
        ProviderOptions = [.. AiProviderCatalog.Definitions
            .Select(definition => new AiProviderIdentityOption(
                definition.Identity,
                definition.DisplayName,
                definition.Category.ToString(),
                definition.IsRuntimeSupported))];
        SecretOptions = BuildSecretOptions(secrets, existing);
        _selectedCredential = SecretOptions[0];
        _order = suggestedOrder;

        if (existing is null)
        {
            RebuildAuthenticationOptions(DefaultAuthenticationMode(_kind));
            return;
        }

        _name = existing.Name;
        _kind = existing.ProviderKind;
        _endpoint = existing.Endpoint.AbsoluteUri;
        _defaultModel = existing.DefaultModel;
        _order = existing.Order;
        _isEnabled = existing.IsEnabled;
        _modelCatalogProfile = existing;
        _models = [.. existing.DiscoveredModelIds
            .Select(id => new AiProviderModelDescriptor(id, id))];
        var existingMode = existing.Authentication switch
        {
            AiProviderAuthentication.None =>
                AiProviderEditorAuthenticationMode.NoAuthentication,
            AiProviderAuthentication.ApiKey =>
                AiProviderEditorAuthenticationMode.ApiKey,
            AiProviderAuthentication.OAuth { Flow: AiProviderOAuthFlow.Browser } =>
                AiProviderEditorAuthenticationMode.OAuthBrowser,
            AiProviderAuthentication.OAuth =>
                AiProviderEditorAuthenticationMode.OAuthDevice,
            AiProviderAuthentication.AwsCredentialChain =>
                AiProviderEditorAuthenticationMode.AwsCredentialChain,
            _ => throw new ArgumentOutOfRangeException(nameof(existing)),
        };
        if (existing.Authentication is AiProviderAuthentication.ApiKey apiKey)
        {
            _selectedCredential = SecretOptions.First(option =>
                option.Reference == apiKey.Secret);
        }

        if (existing.Authentication is AiProviderAuthentication.OAuth oauth)
        {
            _oauthSessionReference = oauth.Session;
            _authenticationStatus = "OAuth session is stored in the OS vault.";
        }

        RebuildAuthenticationOptions(existingMode);
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit AI provider" : "New AI provider";

    public string ProfileId => _id.Value;

    public IReadOnlyList<AiProviderKind> ProviderKinds { get; }

    public IReadOnlyList<AiProviderIdentityOption> ProviderOptions { get; }

    public IReadOnlyList<AiProviderSecretOption> SecretOptions { get; private set; }

    public bool HasSingleCredentialOption => SecretOptions.Count == 1;

    public bool HasMultipleCredentialOptions => SecretOptions.Count > 1;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public AiProviderKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value))
            {
                return;
            }

            Endpoint = AiProviderProfile.DefaultEndpoint(value).AbsoluteUri;
            DefaultModel = value switch
            {
                AiProviderKind.Anthropic => "claude-sonnet-5",
                AiProviderKind.OpenAi => "gpt-5.6-terra",
                AiProviderKind.Google => "gemini-3.1-pro-preview",
                AiProviderKind.XAi => "grok-4.6",
                AiProviderKind.DeepSeek => "deepseek-v4-pro",
                AiProviderKind.MoonshotAi => "kimi-k3",
                AiProviderKind.OpenRouter => "openai/gpt-5.6-terra",
                AiProviderKind.GitHubCopilot => "gpt-5.6-terra",
                AiProviderKind.Bedrock =>
                    "anthropic.claude-sonnet-4-5-20250929-v1:0",
                AiProviderKind.Ollama => "llama3.2",
                AiProviderKind.OpenAiCompatible => "local-model",
                _ => string.Empty,
            };
            _oauthSessionReference = null;
            RebuildAuthenticationOptions(DefaultAuthenticationMode(value));
            OnPropertyChanged(nameof(CanDisableAuthentication));
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(ProviderProtocol));
            OnPropertyChanged(nameof(ProviderCategory));
            OnPropertyChanged(nameof(IsProviderRuntimeSupported));
            OnPropertyChanged(nameof(ProviderAvailability));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(IsInteractiveAuthenticationAvailable));
            OnPropertyChanged(nameof(CanAuthenticate));
        }
    }

    public AiProviderIdentityOption SelectedProvider
    {
        get => ProviderOptions.Single(option => option.Kind == Kind);
        set => Kind = value.Kind;
    }

    public string ProviderProtocol => AiProviderCatalog.Get(Kind).Protocol.ToString();

    public string ProviderCategory => AiProviderCatalog.Get(Kind).Category.ToString();

    public bool IsProviderRuntimeSupported => AiProviderCatalog.Get(Kind).IsRuntimeSupported;

    public string ProviderAvailability => IsProviderRuntimeSupported
        ? "This provider is available."
        : "This provider is cataloged, but its native runtime is not implemented yet. Saving and testing are disabled.";

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public string DefaultModel
    {
        get => _defaultModel;
        set => SetProperty(ref _defaultModel, value);
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool UseNoAuthentication
    {
        get => SelectedAuthentication?.Mode
            == AiProviderEditorAuthenticationMode.NoAuthentication;
        set => SelectAuthenticationMode(
            value
                ? AiProviderEditorAuthenticationMode.NoAuthentication
                : AiProviderEditorAuthenticationMode.ApiKey);
    }

    public bool CanDisableAuthentication => AiProviderCatalog.Get(Kind)
        .AuthenticationMethods
        .HasFlag(AiProviderAuthenticationMethod.NoAuthentication);

    public bool UsesCredential => SelectedAuthentication?.Mode
        == AiProviderEditorAuthenticationMode.ApiKey;

    public IReadOnlyList<AiProviderAuthenticationOption> AuthenticationOptions
    {
        get => _authenticationOptions;
        private set
        {
            if (SetProperty(ref _authenticationOptions, value))
            {
                OnPropertyChanged(nameof(HasSingleAuthenticationOption));
                OnPropertyChanged(nameof(HasMultipleAuthenticationOptions));
            }
        }
    }

    public bool HasSingleAuthenticationOption => AuthenticationOptions.Count == 1;

    public bool HasMultipleAuthenticationOptions => AuthenticationOptions.Count > 1;

    public AiProviderAuthenticationOption? SelectedAuthentication
    {
        get => _selectedAuthentication;
        set
        {
            if (!SetProperty(ref _selectedAuthentication, value))
            {
                return;
            }

            CancelAuthenticationAttempt();
            if (UsesInteractiveAuthentication)
            {
                Endpoint = AiProviderProfile.DefaultEndpoint(Kind).AbsoluteUri;
            }

            OnPropertyChanged(nameof(UseNoAuthentication));
            OnPropertyChanged(nameof(UsesCredential));
            OnPropertyChanged(nameof(UsesInteractiveAuthentication));
            OnPropertyChanged(nameof(IsEndpointEditable));
            OnPropertyChanged(nameof(EndpointPolicy));
            OnPropertyChanged(nameof(IsInteractiveAuthenticationAvailable));
            OnPropertyChanged(nameof(CanAuthenticate));
            OnPropertyChanged(nameof(AuthenticationButtonLabel));
            RefreshAuthenticationAvailability();
        }
    }

    public bool UsesInteractiveAuthentication => SelectedAuthentication?.Mode
        is AiProviderEditorAuthenticationMode.OAuthBrowser
        or AiProviderEditorAuthenticationMode.OAuthDevice;

    public bool IsEndpointEditable => !UsesInteractiveAuthentication;

    public string EndpointPolicy => UsesInteractiveAuthentication
        ? "OAuth request destinations are pinned to the provider's official endpoint."
        : "HTTPS is required except for an exact loopback endpoint. Redirects, embedded credentials, queries, and fragments are rejected.";

    public bool IsInteractiveAuthenticationAvailable =>
        UsesInteractiveAuthentication
        && ResolveAuthenticationAvailability().IsAvailable;

    public bool CanAuthenticate => UsesInteractiveAuthentication
        && IsProviderRuntimeSupported
        && _authenticationRuntime is not null
        && IsInteractiveAuthenticationAvailable
        && !IsAuthenticating;

    public string AuthenticationButtonLabel => SelectedAuthentication?.Mode
        == AiProviderEditorAuthenticationMode.OAuthBrowser
            ? "Connect in browser"
            : "Connect with device code";

    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        private set
        {
            if (SetProperty(ref _isAuthenticating, value))
            {
                OnPropertyChanged(nameof(CanAuthenticate));
            }
        }
    }

    public string AuthenticationStatus
    {
        get => _authenticationStatus;
        private set => SetProperty(ref _authenticationStatus, value);
    }

    public string DeviceCode
    {
        get => _deviceCode;
        private set
        {
            if (SetProperty(ref _deviceCode, value))
            {
                OnPropertyChanged(nameof(HasDeviceCode));
            }
        }
    }

    public bool HasDeviceCode => DeviceCode.Length > 0;

    public string AuthenticationUri
    {
        get => _authenticationUri;
        private set => SetProperty(ref _authenticationUri, value);
    }

    public AiProviderSecretOption? SelectedCredential
    {
        get => _selectedCredential;
        set
        {
            // Rebuilding the picker briefly writes null through its two-way binding.
            if (value is not null)
            {
                SetProperty(ref _selectedCredential, value);
            }
        }
    }

    public string ApiKeyValue
    {
        get => _apiKeyValue;
        set
        {
            if (SetProperty(ref _apiKeyValue, value))
            {
                OnPropertyChanged(nameof(CanStoreCredential));
            }
        }
    }

    public string CredentialStatus
    {
        get => _credentialStatus;
        private set => SetProperty(ref _credentialStatus, value);
    }

    public bool IsStoringCredential
    {
        get => _isStoringCredential;
        private set
        {
            if (SetProperty(ref _isStoringCredential, value))
            {
                OnPropertyChanged(nameof(CanTest));
                OnPropertyChanged(nameof(CanStoreCredential));
            }
        }
    }

    public bool CanStoreCredential => _storeCredential is not null
        && !IsStoringCredential && !IsTesting && ApiKeyValue.Length > 0;

    public async Task<bool> StoreApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!UsesCredential || !CanStoreCredential)
        {
            CredentialStatus = "Enter an API key before storing it.";
            return false;
        }

        IsStoringCredential = true;
        try
        {
            var request = new CreateSecretRequest(SecretRef.New(), $"{Required(Name, "Provider name")} API key",
                SecretKind.ApiKey, new SecretScope(SecretScopeKind.AiProvider, _id.Value),
                new SecretUsePurpose(SecretUseKind.UserManagement, _id.Value));
            using var material = SecretMaterial.TakeOwnership(Encoding.UTF8.GetBytes(ApiKeyValue));
            ApiKeyValue = string.Empty;
            var result = await _storeCredential!(request, material, cancellationToken);
            if (result is not SecretVaultResult<SecretMetadata>.Success success)
            {
                CredentialStatus = "The API key could not be stored. Check that the system keychain is available and try again.";
                return false;
            }

            // Create a fresh entry, never overwrite a key another saved profile may still use.
            var option = new AiProviderSecretOption(success.Value.Reference, success.Value.Label, "API key", true);
            SecretOptions = [.. SecretOptions, option];
            _selectedCredential = option;
            OnPropertyChanged(nameof(SecretOptions));
            OnPropertyChanged(nameof(SelectedCredential));
            OnPropertyChanged(nameof(HasSingleCredentialOption));
            OnPropertyChanged(nameof(HasMultipleCredentialOptions));
            CredentialStatus = "API key stored in the system keychain and selected for this provider.";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CredentialStatus = "API-key storage was cancelled.";
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CredentialStatus = "The API key could not be stored. Enter a provider name and key, then try again.";
            return false;
        }
        finally
        {
            ApiKeyValue = string.Empty;
            IsStoringCredential = false;
        }
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(CanTest));
                OnPropertyChanged(nameof(CanStoreCredential));
            }
        }
    }

    public bool CanTest => IsProviderRuntimeSupported && !IsTesting && !IsStoringCredential;

    public string TestStatus
    {
        get => _testStatus;
        private set => SetProperty(ref _testStatus, value);
    }

    public string TestDetail
    {
        get => _testDetail;
        private set => SetProperty(ref _testDetail, value);
    }

    public IReadOnlyList<AiProviderModelDescriptor> Models
    {
        get => _models;
        private set => SetProperty(ref _models, value);
    }

    public AiProviderProfileSaveRequest CreateSaveRequest()
    {
        if (UsesCredential && ApiKeyValue.Length > 0)
        {
            throw new ArgumentException("Store the entered API key before saving the provider.");
        }

        return new(BuildProfile(), ExpectedRevision);
    }

    public async Task<AiProviderProfileSaveRequest?> PrepareSaveAsync(CancellationToken cancellationToken)
    {
        if (IsTesting || IsStoringCredential
            || (UsesCredential && ApiKeyValue.Length > 0 && !await StoreApiKeyAsync(cancellationToken)))
        {
            return null;
        }

        return CreateSaveRequest();
    }

    public async ValueTask<AiProviderAuthenticationLaunch?> BeginAuthenticationAsync(
        CancellationToken cancellationToken)
    {
        if (_authenticationRuntime is null || !UsesInteractiveAuthentication)
        {
            throw new InvalidOperationException(
                "Interactive authentication is unavailable for this provider.");
        }

        var availability = ResolveAuthenticationAvailability();
        if (!availability.IsAvailable)
        {
            AuthenticationStatus = availability.Message;
            return null;
        }

        if (IsAuthenticating)
        {
            throw new InvalidOperationException("Authentication is already in progress.");
        }

        var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _authenticationAttempt = attempt;
        IsAuthenticating = true;
        DeviceCode = string.Empty;
        AuthenticationUri = string.Empty;
        AuthenticationStatus = "Starting authentication…";
        try
        {
            if (SelectedAuthentication!.Mode
                == AiProviderEditorAuthenticationMode.OAuthBrowser)
            {
                var authorization = await _authenticationRuntime.StartBrowserAsync(
                    _id,
                    attempt.Token);
                if (!ReferenceEquals(_authenticationAttempt, attempt))
                {
                    CompleteAuthenticationAttempt(attempt, status: null);
                    return null;
                }

                AuthenticationUri = authorization.AuthorizationUri.AbsoluteUri;
                AuthenticationStatus = "Complete authentication in the browser.";
                return new AiProviderAuthenticationLaunch(
                    authorization.AuthorizationUri,
                    ObserveAuthenticationAsync(authorization.Completion, attempt));
            }

            var device = await _authenticationRuntime.StartDeviceAsync(
                _id,
                Kind,
                attempt.Token);
            if (!ReferenceEquals(_authenticationAttempt, attempt))
            {
                CompleteAuthenticationAttempt(attempt, status: null);
                return null;
            }

            DeviceCode = device.UserCode;
            AuthenticationUri = device.VerificationUri.AbsoluteUri;
            AuthenticationStatus =
                "Enter the device code in the browser, then return here.";
            return new AiProviderAuthenticationLaunch(
                device.VerificationUri,
                ObserveAuthenticationAsync(device.Completion, attempt));
        }
        catch (OperationCanceledException)
        {
            CompleteAuthenticationAttempt(
                attempt,
                "Authentication was cancelled.");
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CompleteAuthenticationAttempt(
                attempt,
                "Authentication could not be started.");
            return null;
        }
    }

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting || IsStoringCredential)
        {
            return;
        }

        if (!IsProviderRuntimeSupported)
        {
            TestStatus = "Provider unavailable";
            TestDetail = ProviderAvailability;
            return;
        }

        if (UsesCredential && ApiKeyValue.Length > 0 && !await StoreApiKeyAsync(cancellationToken))
        {
            return;
        }

        AiProviderProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            TestStatus = "Validation failed";
            TestDetail = exception.Message;
            return;
        }

        IsTesting = true;
        TestStatus = "Testing provider";
        TestDetail = "Loading the provider's model list…";
        try
        {
            var result = await _runtime.TestAsync(profile, cancellationToken);
            if (result.Models.Count > 0)
            {
                // Discovery can succeed even when the chosen default model is unavailable.
                _modelCatalogProfile = profile;
                Models = result.Models;
            }
            TestStatus = !result.IsSuccess
                ? "Test failed"
                : string.Equals(result.Code, "ai_provider_test_configuration_valid"
, StringComparison.Ordinal) ? "Configuration valid"
                    : "Provider connected";
            TestDetail = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatus = "Test cancelled";
            TestDetail = "The provider test was cancelled.";
        }
        catch (Exception)
        {
            TestStatus = "Test failed";
            TestDetail = "The provider test could not be completed.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private AiProviderProfile BuildProfile()
    {
        if (!IsProviderRuntimeSupported)
        {
            throw new ArgumentException(ProviderAvailability);
        }

        if (!Uri.TryCreate(Required(Endpoint, "Endpoint"), UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URI.");
        }

        if (UseNoAuthentication && !CanDisableAuthentication)
        {
            throw new ArgumentException(
                "Only an OpenAI-compatible loopback provider can disable authentication.");
        }

        var authentication = SelectedAuthentication?.Mode switch
        {
            AiProviderEditorAuthenticationMode.NoAuthentication =>
                (AiProviderAuthentication)new AiProviderAuthentication.None(),
            AiProviderEditorAuthenticationMode.ApiKey =>
                new AiProviderAuthentication.ApiKey(
                    SelectedCredential?.Reference ?? _pendingSecretReference),
            AiProviderEditorAuthenticationMode.OAuthBrowser =>
                new AiProviderAuthentication.OAuth(
                    RequireOAuthSession(),
                    AiProviderOAuthFlow.Browser),
            AiProviderEditorAuthenticationMode.OAuthDevice =>
                new AiProviderAuthentication.OAuth(
                    RequireOAuthSession(),
                    AiProviderOAuthFlow.Device),
            AiProviderEditorAuthenticationMode.AwsCredentialChain =>
                new AiProviderAuthentication.AwsCredentialChain(),
            _ => throw new ArgumentException("Choose an authentication method."),
        };
        return new AiProviderProfile(
            _id,
            _schemaVersion,
            Required(Name, "Provider name"),
            Kind,
            endpoint,
            authentication,
            Required(DefaultModel, "Default model"),
            Order,
            IsEnabled,
            discoveredModelIds: _modelCatalogProfile is { } tested
                && tested.ProviderKind == Kind
                && string.Equals(tested.Endpoint.AbsoluteUri,
                    endpoint.AbsoluteUri.EndsWith('/') ? endpoint.AbsoluteUri : endpoint.AbsoluteUri + "/",
                    StringComparison.Ordinal)
                && tested.Authentication == authentication
                    ? Models.Select(model => model.Id).ToArray()
                    : []);
    }

    private async Task ObserveAuthenticationAsync(
        Task<AiProviderAuthenticationResult> completion,
        CancellationTokenSource attempt)
    {
        try
        {
            var result = await completion;
            if (!ReferenceEquals(_authenticationAttempt, attempt))
            {
                return;
            }

            if (result.Succeeded && result.Session is { } session)
            {
                _oauthSessionReference = session;
                AuthenticationStatus = "Connected. The token session is stored in the OS vault.";
            }
            else
            {
                AuthenticationStatus = SafeAuthenticationMessage(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_authenticationAttempt, attempt))
            {
                AuthenticationStatus = "Authentication was cancelled.";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ReferenceEquals(_authenticationAttempt, attempt))
            {
                AuthenticationStatus = "Authentication failed.";
            }
        }
        finally
        {
            CompleteAuthenticationAttempt(attempt, status: null);
        }
    }

    private void CancelAuthenticationAttempt()
    {
        var attempt = _authenticationAttempt;
        if (attempt is null)
        {
            return;
        }

        _authenticationAttempt = null;
        attempt.Cancel();
        IsAuthenticating = false;
        DeviceCode = string.Empty;
        AuthenticationUri = string.Empty;
    }

    private void CompleteAuthenticationAttempt(
        CancellationTokenSource attempt,
        string? status)
    {
        if (ReferenceEquals(_authenticationAttempt, attempt))
        {
            _authenticationAttempt = null;
            if (status is not null)
            {
                AuthenticationStatus = status;
            }

            IsAuthenticating = false;
        }

        attempt.Dispose();
    }

    private static string SafeAuthenticationMessage(
        AiProviderAuthenticationResult result) => result.StableCode switch
        {
            "ai_provider_authentication_cancelled" => "Authentication was cancelled.",
            "ai_provider_authentication_denied" => "Authentication was denied.",
            "ai_provider_device_code_expired" => "The device code expired.",
            "ai_provider_oauth_token_exchange_rejected" =>
                "OpenAI rejected the OAuth token exchange. Start authentication again.",
            "ai_provider_oauth_token_exchange_unavailable" =>
                "OpenAI's OAuth token exchange is temporarily unavailable.",
            "ai_provider_oauth_token_exchange_invalid_response" =>
                "OpenAI returned an invalid OAuth token response.",
            "ai_provider_oauth_token_response_missing_access_token" =>
                "OpenAI's OAuth response did not include a valid access token.",
            "ai_provider_oauth_token_response_missing_refresh_token" =>
                "OpenAI's OAuth response did not include a refresh token.",
            "ai_provider_oauth_token_response_invalid_expiry" =>
                "OpenAI's OAuth response included an invalid token expiry.",
            "ai_provider_oauth_session_store_failed" =>
                "The OAuth session could not be stored in the OS vault.",
            _ => "Authentication failed.",
        };

    private AiProviderAuthenticationAvailability ResolveAuthenticationAvailability()
    {
        if (!UsesInteractiveAuthentication)
        {
            return AiProviderAuthenticationAvailability.Available;
        }

        if (_authenticationRuntime is null)
        {
            return new AiProviderAuthenticationAvailability(
                false,
                "ai_provider_authentication_runtime_unavailable",
                "Interactive authentication is unavailable in this build.");
        }

        var flow = SelectedAuthentication!.Mode
            == AiProviderEditorAuthenticationMode.OAuthBrowser
                ? AiProviderOAuthFlow.Browser
                : AiProviderOAuthFlow.Device;
        return _authenticationRuntime.GetAvailability(Kind, flow);
    }

    private void RefreshAuthenticationAvailability()
    {
        if (!UsesInteractiveAuthentication || _oauthSessionReference is not null)
        {
            return;
        }

        var availability = ResolveAuthenticationAvailability();
        AuthenticationStatus = availability.IsAvailable
            ? "Ready to start interactive authentication."
            : availability.Message;
    }

    private SecretRef RequireOAuthSession() =>
        _oauthSessionReference
        ?? throw new ArgumentException(
            "Connect this provider before saving its OAuth authentication method.");

    private void RebuildAuthenticationOptions(
        AiProviderEditorAuthenticationMode preferred)
    {
        var methods = AiProviderCatalog.Get(Kind).AuthenticationMethods;
        var options = new List<AiProviderAuthenticationOption>();
        AddAuthenticationOption(
            options,
            methods,
            AiProviderAuthenticationMethod.ApiKey,
            AiProviderEditorAuthenticationMode.ApiKey,
            "API key",
            "Use an API key stored in the system keychain.");
        AddAuthenticationOption(
            options,
            methods,
            AiProviderAuthenticationMethod.OAuthBrowser,
            AiProviderEditorAuthenticationMode.OAuthBrowser,
            "Browser OAuth",
            "Open a PKCE browser flow and keep refreshable tokens in the OS vault.");
        AddAuthenticationOption(
            options,
            methods,
            AiProviderAuthenticationMethod.OAuthDevice,
            AiProviderEditorAuthenticationMode.OAuthDevice,
            "Device authorization",
            "Enter a short code in the provider browser flow.");
        AddAuthenticationOption(
            options,
            methods,
            AiProviderAuthenticationMethod.NoAuthentication,
            AiProviderEditorAuthenticationMode.NoAuthentication,
            "No authentication",
            "Allowed only for exact loopback endpoints.");
        AddAuthenticationOption(
            options,
            methods,
            AiProviderAuthenticationMethod.AwsCredentialChain,
            AiProviderEditorAuthenticationMode.AwsCredentialChain,
            "AWS credential chain",
            "Typed configuration only; Bedrock requests fail closed until SigV4 is available.");
        AuthenticationOptions = options.AsReadOnly();
        SelectedAuthentication = options.FirstOrDefault(option => option.Mode == preferred)
            ?? options.FirstOrDefault();
    }

    private void SelectAuthenticationMode(AiProviderEditorAuthenticationMode mode)
    {
        var option = AuthenticationOptions.FirstOrDefault(item => item.Mode == mode);
        if (option is null)
        {
            if (mode == AiProviderEditorAuthenticationMode.ApiKey)
            {
                return;
            }

            throw new ArgumentException(
                "The selected provider does not support this authentication method.");
        }

        SelectedAuthentication = option;
    }

    private static void AddAuthenticationOption(
        ICollection<AiProviderAuthenticationOption> options,
        AiProviderAuthenticationMethod available,
        AiProviderAuthenticationMethod required,
        AiProviderEditorAuthenticationMode mode,
        string label,
        string detail)
    {
        if (available.HasFlag(required))
        {
            options.Add(new AiProviderAuthenticationOption(mode, label, detail));
        }
    }

    private static AiProviderEditorAuthenticationMode DefaultAuthenticationMode(
        AiProviderKind kind) => kind switch
        {
            AiProviderKind.GitHubCopilot =>
                AiProviderEditorAuthenticationMode.OAuthDevice,
            AiProviderKind.Bedrock =>
                AiProviderEditorAuthenticationMode.AwsCredentialChain,
            AiProviderKind.Ollama or AiProviderKind.OpenAiCompatible =>
                AiProviderEditorAuthenticationMode.NoAuthentication,
            _ => AiProviderEditorAuthenticationMode.ApiKey,
        };

    private IReadOnlyList<AiProviderSecretOption> BuildSecretOptions(
        IReadOnlyList<SecretMetadataViewModel> secrets,
        AiProviderProfile? existing)
    {
        var options = new List<AiProviderSecretOption>
        {
            new(
                null,
                existing is null
                    ? "Add an API key below"
                    : "Add a new API key below",
                "API key",
                true),
        };
        options.AddRange(secrets
            .Where(item => item.SecretScope.Kind == SecretScopeKind.AiProvider
                && string.Equals(item.SecretScope.OwnerId, _id.Value, StringComparison.Ordinal))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AiProviderSecretOption(
                item.Reference,
                item.Label,
                item.Kind,
                true)));
        if (existing?.Authentication is AiProviderAuthentication.ApiKey apiKey
            && options.All(item => item.Reference != apiKey.Secret))
        {
            options.Add(new AiProviderSecretOption(
                apiKey.Secret,
                "Missing credential",
                "Unavailable",
                false));
        }

        return options.AsReadOnly();
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} is required.");
        }

        return value.Trim();
    }
}
