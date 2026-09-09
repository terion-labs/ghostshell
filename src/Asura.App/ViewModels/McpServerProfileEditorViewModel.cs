using System.Collections.ObjectModel;
using System.Net;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record McpServerTrustReviewEntry(
    string Label,
    string Value);

public enum McpServerCredentialReviewState
{
    Available,
    Missing,
    WrongScope,
}

public sealed record McpServerCredentialTrustReviewEntry(
    string BindingName,
    SecretRef Reference,
    string CredentialLabel,
    string CredentialKind,
    McpServerCredentialReviewState State)
{
    public string VariableName => BindingName;

    public string ReferenceValue => Reference.Value;

    public string MetadataSummary => State == McpServerCredentialReviewState.Missing
        ? "Credential metadata not found"
        : $"{CredentialLabel} · {CredentialKind}";

    public string StateSummary => State switch
    {
        McpServerCredentialReviewState.Available =>
            "Available to this MCP server profile",
        McpServerCredentialReviewState.Missing =>
            "Missing credential",
        McpServerCredentialReviewState.WrongScope =>
            "Wrong scope · unavailable to this MCP server profile",
        _ => throw new ArgumentOutOfRangeException(
            nameof(State),
            State,
            null),
    };

    public bool HasWarning => State != McpServerCredentialReviewState.Available;

    public string AutomationName =>
        $"{BindingName} vault binding: {MetadataSummary}; {StateSummary}";
}

public sealed record McpServerTrustReview
{
    public McpServerTrustReview(
        string serverName,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> changes,
        IReadOnlyList<McpServerTrustReviewEntry> arguments,
        IReadOnlyList<McpServerCredentialTrustReviewEntry> environment,
        IReadOnlyList<string> enabledTools)
        : this(
            serverName,
            McpServerTransportKind.Stdio,
            executable,
            workingDirectory,
            endpoint: string.Empty,
            transportSecurity: "Local process",
            changes,
            arguments,
            environment,
            httpHeaders: [],
            enabledTools)
    {
    }

    public McpServerTrustReview(
        string serverName,
        McpServerTransportKind transportKind,
        string executable,
        string workingDirectory,
        string endpoint,
        string transportSecurity,
        IReadOnlyList<string> changes,
        IReadOnlyList<McpServerTrustReviewEntry> arguments,
        IReadOnlyList<McpServerCredentialTrustReviewEntry> environment,
        IReadOnlyList<McpServerCredentialTrustReviewEntry> httpHeaders,
        IReadOnlyList<string> enabledTools)
    {
        ServerName = serverName;
        TransportKind = transportKind;
        Executable = executable;
        WorkingDirectory = workingDirectory;
        Endpoint = endpoint;
        TransportSecurity = transportSecurity;
        Changes = changes;
        Arguments = arguments;
        Environment = environment;
        HttpHeaders = httpHeaders;
        EnabledTools = enabledTools;
    }

    public string ServerName { get; }

    public McpServerTransportKind TransportKind { get; }

    public string Executable { get; }

    public string WorkingDirectory { get; }

    public string Endpoint { get; }

    public string TransportSecurity { get; }

    public IReadOnlyList<string> Changes { get; }

    public IReadOnlyList<McpServerTrustReviewEntry> Arguments { get; }

    public IReadOnlyList<McpServerCredentialTrustReviewEntry> Environment { get; }

    public IReadOnlyList<McpServerCredentialTrustReviewEntry> HttpHeaders { get; }

    public IReadOnlyList<string> EnabledTools { get; }

    public bool IsStdio => TransportKind == McpServerTransportKind.Stdio;

    public bool IsStreamableHttp =>
        TransportKind == McpServerTransportKind.StreamableHttp;

    public bool HasArguments => Arguments.Count > 0;

    public bool HasNoArguments => !HasArguments;

    public bool HasEnvironment => Environment.Count > 0;

    public bool HasNoEnvironment => !HasEnvironment;

    public bool HasHttpHeaders => HttpHeaders.Count > 0;

    public bool HasNoHttpHeaders => !HasHttpHeaders;

    public bool HasEnabledTools => EnabledTools.Count > 0;

    public bool HasNoEnabledTools => !HasEnabledTools;
}

public sealed record McpServerTransportOption(
    McpServerTransportKind Kind,
    string Name,
    string Description);

/// <summary>
/// Carries the immutable profile reviewed by the user. Production callers
/// cannot construct or confirm this receipt outside the App assembly.
/// </summary>
public sealed record McpServerProfileSaveRequest
{
    internal McpServerProfileSaveRequest(
        McpServerProfile profile,
        long? expectedRevision,
        bool requiresTrustConfirmation,
        bool isTrustConfirmed,
        McpServerTrustReview trustReview)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ExpectedRevision = expectedRevision;
        RequiresTrustConfirmation = requiresTrustConfirmation;
        IsTrustConfirmed = isTrustConfirmed;
        TrustReview = trustReview ?? throw new ArgumentNullException(nameof(trustReview));
    }

    public McpServerProfile Profile { get; }

    public long? ExpectedRevision { get; }

    public bool RequiresTrustConfirmation { get; }

    public bool IsTrustConfirmed { get; }

    public McpServerTrustReview TrustReview { get; }

    public bool IsAuthorizedForSave =>
        !RequiresTrustConfirmation || IsTrustConfirmed;

    internal McpServerProfileSaveRequest ConfirmTrust() =>
        new(
            Profile,
            ExpectedRevision,
            RequiresTrustConfirmation,
            isTrustConfirmed: true,
            TrustReview);
}

public sealed class McpArgumentEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _value;

    internal McpArgumentEditorItemViewModel(int position, string value)
    {
        _position = position;
        _value = value;
    }

    public int Position
    {
        get => _position;
        internal set => SetProperty(ref _position, value);
    }

    public string AccessibleName => $"Argument {Position}";

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    internal void NotifyPositionChanged()
    {
        OnPropertyChanged(nameof(AccessibleName));
    }
}

public sealed class McpEnvironmentBindingEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _name;
    private string _secretReference;

    internal McpEnvironmentBindingEditorItemViewModel(
        int position,
        string name,
        string secretReference)
    {
        _position = position;
        _name = name;
        _secretReference = secretReference;
    }

    public int Position => _position;

    public string NameAccessibleName =>
        $"Environment binding {Position} variable name";

    public string SecretReferenceAccessibleName =>
        $"Environment binding {Position} secret reference";

    public string RemoveAccessibleName =>
        $"Remove environment binding {Position}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string SecretReference
    {
        get => _secretReference;
        set => SetProperty(ref _secretReference, value ?? string.Empty);
    }

    internal void UpdatePosition(int position)
    {
        if (!SetProperty(ref _position, position, nameof(Position)))
        {
            return;
        }

        OnPropertyChanged(nameof(NameAccessibleName));
        OnPropertyChanged(nameof(SecretReferenceAccessibleName));
        OnPropertyChanged(nameof(RemoveAccessibleName));
    }
}

public sealed class McpHttpHeaderBindingEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _name;
    private string _secretReference;

    internal McpHttpHeaderBindingEditorItemViewModel(
        int position,
        string name,
        string secretReference)
    {
        _position = position;
        _name = name;
        _secretReference = secretReference;
    }

    public int Position => _position;

    public string NameAccessibleName =>
        $"HTTP header binding {Position} name";

    public string SecretReferenceAccessibleName =>
        $"HTTP header binding {Position} secret reference";

    public string RemoveAccessibleName =>
        $"Remove HTTP header binding {Position}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string SecretReference
    {
        get => _secretReference;
        set => SetProperty(ref _secretReference, value ?? string.Empty);
    }

    internal void UpdatePosition(int position)
    {
        if (!SetProperty(ref _position, position, nameof(Position)))
        {
            return;
        }

        OnPropertyChanged(nameof(NameAccessibleName));
        OnPropertyChanged(nameof(SecretReferenceAccessibleName));
        OnPropertyChanged(nameof(RemoveAccessibleName));
    }
}

public sealed class McpEnabledToolEditorItemViewModel : ObservableObject
{
    private int _position;
    private string _name;

    internal McpEnabledToolEditorItemViewModel(int position, string name)
    {
        _position = position;
        _name = name;
    }

    public int Position => _position;

    public string NameAccessibleName => $"Enabled MCP tool {Position} name";

    public string RemoveAccessibleName => $"Remove enabled MCP tool {Position}";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    internal void UpdatePosition(int position)
    {
        if (!SetProperty(ref _position, position, nameof(Position)))
        {
            return;
        }

        OnPropertyChanged(nameof(NameAccessibleName));
        OnPropertyChanged(nameof(RemoveAccessibleName));
    }
}

/// <summary>
/// Edits one stdio or Streamable HTTP MCP definition. Transport-specific
/// authority and vault references remain structurally separate.
/// </summary>
public sealed class McpServerProfileEditorViewModel : ObservableObject
{
    private static readonly IReadOnlyList<McpServerTransportOption>
        AvailableTransports =
        [
            new(
                McpServerTransportKind.Stdio,
                "Local process (stdio)",
                "Launch one fully qualified executable directly."),
            new(
                McpServerTransportKind.StreamableHttp,
                "Remote server (Streamable HTTP)",
                "Connect to one HTTPS endpoint; plaintext is limited to loopback."),
        ];

    private readonly McpServerProfileId _id;
    private readonly int _schemaVersion;
    private readonly McpServerProfile? _original;
    private readonly IReadOnlyList<SecretMetadataViewModel> _secrets;
    private string _name = string.Empty;
    private string _executable = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _endpoint = string.Empty;
    private McpServerTransportOption _selectedTransport = AvailableTransports[0];
    private bool _isEnabled = true;

    public McpServerProfileEditorViewModel(
        McpServerProfile? existing = null,
        long? expectedRevision = null,
        IReadOnlyList<SecretMetadataViewModel>? secrets = null)
    {
        if (existing is null && expectedRevision is not null)
        {
            throw new ArgumentException(
                "A revision can be supplied only when editing an MCP server.",
                nameof(expectedRevision));
        }

        _original = existing;
        _secrets = secrets?.ToArray() ?? [];
        _id = existing?.Id ?? McpServerProfileId.New();
        _schemaVersion = existing?.SchemaVersion ?? McpServerProfile.CurrentSchemaVersion;
        ExpectedRevision = expectedRevision;
        if (existing is null)
        {
            return;
        }

        _name = existing.Name;
        _selectedTransport = AvailableTransports.Single(option =>
            option.Kind == existing.Transport.Kind);
        _isEnabled = existing.IsEnabled;
        if (existing.Transport is McpServerTransport.Stdio stdio)
        {
            _executable = stdio.Executable;
            _workingDirectory = stdio.WorkingDirectory ?? string.Empty;
            foreach (var argument in stdio.Arguments)
            {
                Arguments.Add(new McpArgumentEditorItemViewModel(
                    Arguments.Count + 1,
                    argument));
            }

            foreach (var binding in stdio.Environment)
            {
                Environment.Add(new McpEnvironmentBindingEditorItemViewModel(
                    Environment.Count + 1,
                    binding.Name,
                    binding.Reference.Value));
            }
        }
        else if (existing.Transport is McpServerTransport.StreamableHttp http)
        {
            _endpoint = http.Endpoint.AbsoluteUri;
            foreach (var header in http.Headers)
            {
                HttpHeaders.Add(new McpHttpHeaderBindingEditorItemViewModel(
                    HttpHeaders.Count + 1,
                    header.Name,
                    header.Reference.Value));
            }
        }

        foreach (var tool in existing.EnabledTools)
        {
            EnabledTools.Add(new McpEnabledToolEditorItemViewModel(
                EnabledTools.Count + 1,
                tool));
        }
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit MCP server" : "New MCP server";

    public string ProfileId => _id.Value;

    public IReadOnlyList<McpServerTransportOption> TransportOptions =>
        AvailableTransports;

    public McpServerTransportOption SelectedTransport
    {
        get => _selectedTransport;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedTransport, value))
            {
                OnPropertyChanged(nameof(IsStdio));
                OnPropertyChanged(nameof(IsStreamableHttp));
            }
        }
    }

    public bool IsStdio =>
        SelectedTransport.Kind == McpServerTransportKind.Stdio;

    public bool IsStreamableHttp =>
        SelectedTransport.Kind == McpServerTransportKind.StreamableHttp;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Executable
    {
        get => _executable;
        set => SetProperty(ref _executable, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value ?? string.Empty);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ObservableCollection<McpArgumentEditorItemViewModel> Arguments { get; } = [];

    public ObservableCollection<McpEnvironmentBindingEditorItemViewModel> Environment { get; } = [];

    public ObservableCollection<McpHttpHeaderBindingEditorItemViewModel> HttpHeaders { get; } = [];

    public ObservableCollection<McpEnabledToolEditorItemViewModel> EnabledTools { get; } = [];

    public void AddArgument()
    {
        EnsureRoom(Arguments.Count, McpServerProfile.MaximumArgumentCount, "arguments");
        Arguments.Add(new McpArgumentEditorItemViewModel(Arguments.Count + 1, string.Empty));
    }

    public void RemoveArgument(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (Arguments.Remove(argument))
        {
            RefreshArgumentPositions();
        }
    }

    public void MoveArgumentUp(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var index = Arguments.IndexOf(argument);
        if (index > 0)
        {
            Arguments.Move(index, index - 1);
            RefreshArgumentPositions();
        }
    }

    public void MoveArgumentDown(McpArgumentEditorItemViewModel argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var index = Arguments.IndexOf(argument);
        if (index >= 0 && index < Arguments.Count - 1)
        {
            Arguments.Move(index, index + 1);
            RefreshArgumentPositions();
        }
    }

    public void AddEnvironmentBinding()
    {
        EnsureRoom(
            Environment.Count,
            McpServerProfile.MaximumEnvironmentVariableCount,
            "environment bindings");
        Environment.Add(new McpEnvironmentBindingEditorItemViewModel(
            Environment.Count + 1,
            NextEnvironmentName(),
            SecretRef.New().Value));
    }

    public void RemoveEnvironmentBinding(McpEnvironmentBindingEditorItemViewModel binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (Environment.Remove(binding))
        {
            RefreshEnvironmentPositions();
        }
    }

    public void AddHttpHeaderBinding()
    {
        EnsureRoom(
            HttpHeaders.Count,
            McpServerProfile.MaximumHttpHeaderCount,
            "HTTP header bindings");
        HttpHeaders.Add(new McpHttpHeaderBindingEditorItemViewModel(
            HttpHeaders.Count + 1,
            NextHttpHeaderName(),
            SecretRef.New().Value));
    }

    public void RemoveHttpHeaderBinding(
        McpHttpHeaderBindingEditorItemViewModel binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (HttpHeaders.Remove(binding))
        {
            RefreshHttpHeaderPositions();
        }
    }

    public void AddEnabledTool()
    {
        EnsureRoom(
            EnabledTools.Count,
            McpServerProfile.MaximumEnabledToolCount,
            "enabled tools");
        EnabledTools.Add(new McpEnabledToolEditorItemViewModel(
            EnabledTools.Count + 1,
            string.Empty));
    }

    public void RemoveEnabledTool(McpEnabledToolEditorItemViewModel tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (EnabledTools.Remove(tool))
        {
            RefreshEnabledToolPositions();
        }
    }

    public McpServerProfileSaveRequest CreateSaveRequest()
    {
        McpServerTransport transport = SelectedTransport.Kind switch
        {
            McpServerTransportKind.Stdio => CreateStdioTransport(),
            McpServerTransportKind.StreamableHttp =>
                CreateStreamableHttpTransport(),
            _ => throw new InvalidOperationException(
                "The selected MCP transport is unavailable."),
        };
        var profile = new McpServerProfile(
            _id,
            _schemaVersion,
            Required(Name, "Server name"),
            transport,
            [.. EnabledTools.Select(tool => Required(tool.Name, "Enabled tool name"))],
            IsEnabled);
        var review = CreateTrustReview(profile);
        return new McpServerProfileSaveRequest(
            profile,
            ExpectedRevision,
            review.Changes.Count > 0,
            isTrustConfirmed: false,
            review);
    }

    private McpServerTransport.Stdio CreateStdioTransport()
    {
        var executable = Required(Executable, "Executable");
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException(
                "Executable must be a fully qualified local path.");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
            ? null
            : Required(WorkingDirectory, "Working directory");
        if (workingDirectory is not null
            && !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "Working directory must be a fully qualified local path.");
        }

        return new McpServerTransport.Stdio(
            executable,
            [.. Arguments.Select(argument => argument.Value)],
            workingDirectory,
            [.. Environment.Select(binding => new McpServerEnvironmentVariable(
                Required(binding.Name, "Environment variable name"),
                new SecretRef(Required(
                    binding.SecretReference,
                    "Environment secret reference"))))]);
    }

    private McpServerTransport.StreamableHttp CreateStreamableHttpTransport()
    {
        var endpointText = Required(Endpoint, "Streamable HTTP endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException(
                "Streamable HTTP endpoint must be an absolute HTTPS URI.");
        }

        var isPlaintext = string.Equals(
            endpoint.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);
        if (isPlaintext && !IsLoopback(endpoint))
        {
            throw new ArgumentException(
                "Streamable HTTP endpoint must use HTTPS; plaintext HTTP is allowed only for an exact loopback host.");
        }

        return new McpServerTransport.StreamableHttp(
            endpoint,
            [.. HttpHeaders.Select(binding => new McpServerHttpHeader(
                Required(binding.Name, "HTTP header name"),
                new SecretRef(Required(
                    binding.SecretReference,
                    "HTTP header secret reference"))))],
            allowInsecureTransport: isPlaintext);
    }

    private McpServerTrustReview CreateTrustReview(McpServerProfile profile)
    {
        var changes = new List<string>();
        if (_original is null)
        {
            changes.Add(profile.Transport.Kind == McpServerTransportKind.Stdio
                ? "Add a new local MCP server process"
                : "Add a new remote Streamable HTTP MCP server");
        }
        else
        {
            if (!_original.IsTrusted)
            {
                changes.Add("Trust this imported server definition");
            }

            if (profile.Transport.Kind != _original.Transport.Kind)
            {
                changes.Add("Change the MCP server transport");
            }
            else if (profile.Transport is McpServerTransport.Stdio stdio
                     && _original.Transport is McpServerTransport.Stdio originalStdio)
            {
                AddStdioAuthorityChanges(changes, stdio, originalStdio);
            }
            else if (profile.Transport is McpServerTransport.StreamableHttp http
                     && _original.Transport is McpServerTransport.StreamableHttp originalHttp)
            {
                AddHttpAuthorityChanges(changes, http, originalHttp);
            }

            if (profile.IsEnabled && !_original.IsEnabled)
            {
                changes.Add("Enable this server");
            }

            var addedTools = profile.EnabledTools
                .Except(_original.EnabledTools, StringComparer.Ordinal)
                .ToArray();
            if (addedTools.Length > 0)
            {
                changes.Add(addedTools.Length == 1
                    ? $"Enable MCP tool “{addedTools[0]}”"
                    : $"Enable {addedTools.Length} additional MCP tools");
            }
        }

        var stdioTransport = profile.Transport as McpServerTransport.Stdio;
        var httpTransport = profile.Transport as McpServerTransport.StreamableHttp;
        return new McpServerTrustReview(
            profile.Name,
            profile.Transport.Kind,
            stdioTransport?.Executable ?? string.Empty,
            stdioTransport?.WorkingDirectory ?? "Executable directory",
            httpTransport?.Endpoint.AbsoluteUri ?? string.Empty,
            httpTransport is null
                ? "Local process"
                : httpTransport.AllowInsecureTransport
                    ? "Plaintext loopback · explicitly acknowledged"
                    : "HTTPS · TLS required",
            changes.AsReadOnly(),
            [.. (stdioTransport?.Arguments ?? [])
                .Select((argument, index) => new McpServerTrustReviewEntry(
                    $"Argument {index + 1}",
                    argument.Length == 0 ? "(empty argument)" : argument))],
            [.. (stdioTransport?.Environment ?? [])
                .Select(binding => CreateCredentialTrustReviewEntry(
                    profile.Id,
                    binding.Name,
                    binding.Reference))],
            [.. (httpTransport?.Headers ?? [])
                .Select(binding => CreateCredentialTrustReviewEntry(
                    profile.Id,
                    binding.Name,
                    binding.Reference))],
            [.. profile.EnabledTools]);
    }

    private static void AddStdioAuthorityChanges(
        ICollection<string> changes,
        McpServerTransport.Stdio transport,
        McpServerTransport.Stdio original)
    {
        if (!string.Equals(
                transport.Executable,
                original.Executable,
                StringComparison.Ordinal))
        {
            changes.Add("Change the executable");
        }

        if (!transport.Arguments.SequenceEqual(
                original.Arguments,
                StringComparer.Ordinal))
        {
            changes.Add("Change the ordered argument list");
        }

        if (!string.Equals(
                transport.WorkingDirectory,
                original.WorkingDirectory,
                StringComparison.Ordinal))
        {
            changes.Add("Change the working directory");
        }

        if (!transport.Environment.SequenceEqual(original.Environment))
        {
            changes.Add("Change environment-to-vault bindings");
        }
    }

    private static void AddHttpAuthorityChanges(
        ICollection<string> changes,
        McpServerTransport.StreamableHttp transport,
        McpServerTransport.StreamableHttp original)
    {
        if (transport.Endpoint != original.Endpoint
            || transport.AllowInsecureTransport != original.AllowInsecureTransport)
        {
            changes.Add("Change the remote endpoint");
        }

        if (!transport.Headers.SequenceEqual(original.Headers))
        {
            changes.Add("Change HTTP-header-to-vault bindings");
        }
    }

    private McpServerCredentialTrustReviewEntry
        CreateCredentialTrustReviewEntry(
            McpServerProfileId profileId,
            string bindingName,
            SecretRef reference)
    {
        var matchingReference = _secrets
            .Where(secret => secret.Reference == reference)
            .ToArray();
        var available = matchingReference.FirstOrDefault(secret =>
            secret.SecretScope.Kind == SecretScopeKind.McpServer
            && string.Equals(
                secret.SecretScope.OwnerId,
                profileId.Value,
                StringComparison.Ordinal));
        if (available is not null)
        {
            return new McpServerCredentialTrustReviewEntry(
                bindingName,
                reference,
                available.Label,
                available.Kind,
                McpServerCredentialReviewState.Available);
        }

        var wrongScope = matchingReference.FirstOrDefault();
        return wrongScope is null
            ? new McpServerCredentialTrustReviewEntry(
                bindingName,
                reference,
                "Not found",
                "Unavailable",
                McpServerCredentialReviewState.Missing)
            : new McpServerCredentialTrustReviewEntry(
                bindingName,
                reference,
                wrongScope.Label,
                wrongScope.Kind,
                McpServerCredentialReviewState.WrongScope);
    }

    private string NextEnvironmentName()
    {
        const string baseName = "MCP_SECRET";
        var existing = Environment
            .Select(binding => binding.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix <= McpServerProfile.MaximumEnvironmentVariableCount; suffix++)
        {
            var candidate = $"{baseName}_{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No environment binding name is available.");
    }

    private string NextHttpHeaderName()
    {
        var existing = HttpHeaders
            .Select(binding => binding.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains("Authorization"))
        {
            return "Authorization";
        }

        const string baseName = "X-MCP-Secret";
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2;
             suffix <= McpServerProfile.MaximumHttpHeaderCount;
             suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No HTTP header binding name is available.");
    }

    private static bool IsLoopback(Uri endpoint)
    {
        if (string.Equals(
                endpoint.Host,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(endpoint.Host, out var address)
            && IPAddress.IsLoopback(address);
    }

    private void RefreshArgumentPositions()
    {
        for (var index = 0; index < Arguments.Count; index++)
        {
            Arguments[index].Position = index + 1;
            Arguments[index].NotifyPositionChanged();
        }
    }

    private void RefreshEnvironmentPositions()
    {
        for (var index = 0; index < Environment.Count; index++)
        {
            Environment[index].UpdatePosition(index + 1);
        }
    }

    private void RefreshHttpHeaderPositions()
    {
        for (var index = 0; index < HttpHeaders.Count; index++)
        {
            HttpHeaders[index].UpdatePosition(index + 1);
        }
    }

    private void RefreshEnabledToolPositions()
    {
        for (var index = 0; index < EnabledTools.Count; index++)
        {
            EnabledTools[index].UpdatePosition(index + 1);
        }
    }

    private static void EnsureRoom(int count, int maximum, string itemName)
    {
        if (count >= maximum)
        {
            throw new InvalidOperationException(
                $"An MCP server cannot define more than {maximum} {itemName}.");
        }
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
