using System.Collections.ObjectModel;
using Asura.App;
using Asura.Application;
using Asura.Core;
using Asura.Docker;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

public sealed record FileProviderProfileItemViewModel(
    FileProviderProfileId Id,
    long Revision,
    string Name,
    string Kind,
    string Endpoint,
    string Status,
    string StatusDetail,
    bool HasError,
    bool HasWarning);

public sealed record AiProviderProfileItemViewModel(
    AiProviderProfileId Id,
    long Revision,
    string Name,
    string Kind,
    string Endpoint,
    string DefaultModel,
    int Order,
    string Status,
    string StatusDetail,
    bool IsEnabled,
    bool HasError,
    bool HasWarning,
    bool NeedsCredential);

public sealed class McpServerProfileItemViewModel : ObservableObject
{
    private long _revision;
    private string _name;
    private McpServerTransportKind _transportKind;
    private string _address;
    private int _argumentCount;
    private int _credentialBindingCount;
    private int _enabledToolCount;
    private string _status;
    private string _statusDetail;
    private bool _isEnabled;
    private bool _hasWarning;
    private bool _isTesting;
    private bool _canTest;
    private bool _isTrusted;

    public McpServerProfileItemViewModel(
        McpServerProfileId id,
        long revision,
        string name,
        McpServerTransportKind transportKind,
        string address,
        int argumentCount,
        int credentialBindingCount,
        int enabledToolCount,
        string status,
        string statusDetail,
        bool isEnabled,
        bool hasWarning,
        bool isTesting,
        bool canTest,
        bool isTrusted = true)
    {
        Id = id;
        _revision = revision;
        _name = name;
        _transportKind = transportKind;
        _address = address;
        _argumentCount = argumentCount;
        _credentialBindingCount = credentialBindingCount;
        _enabledToolCount = enabledToolCount;
        _status = status;
        _statusDetail = statusDetail;
        _isEnabled = isEnabled;
        _hasWarning = hasWarning;
        _isTesting = isTesting;
        _canTest = canTest;
        _isTrusted = isTrusted;
    }

    public McpServerProfileId Id { get; }

    public long Revision => _revision;

    public string Name => _name;

    public McpServerTransportKind TransportKind => _transportKind;

    public string Address => _address;

    public string Executable => IsStdio ? Address : string.Empty;

    public bool IsStdio => TransportKind == McpServerTransportKind.Stdio;

    public int ArgumentCount => _argumentCount;

    public int CredentialBindingCount => _credentialBindingCount;

    public int EnvironmentBindingCount => IsStdio ? CredentialBindingCount : 0;

    public int EnabledToolCount => _enabledToolCount;

    public string Status => _status;

    public string StatusDetail => _statusDetail;

    public bool IsEnabled => _isEnabled;

    public bool HasWarning => _hasWarning;

    public bool IsTesting => _isTesting;

    public bool CanTest => _canTest;

    public bool IsTrusted => _isTrusted;

    public string TransportSummary => TransportKind switch
    {
        McpServerTransportKind.Stdio => "Local stdio",
        McpServerTransportKind.StreamableHttp => "Streamable HTTP",
        _ => throw new ArgumentOutOfRangeException(
            nameof(TransportKind),
            TransportKind,
            null),
    };

    public string ArgumentSummary =>
        ArgumentCount == 1 ? "1 ordered arg" : $"{ArgumentCount} ordered args";

    public string EnvironmentBindingSummary =>
        EnvironmentBindingCount == 1
            ? "1 vault binding"
            : $"{EnvironmentBindingCount} vault bindings";

    public string CredentialBindingSummary =>
        CredentialBindingCount == 1
            ? "1 vault binding"
            : $"{CredentialBindingCount} vault bindings";

    public string EnabledToolSummary =>
        EnabledToolCount == 1
            ? "1 enabled tool"
            : $"{EnabledToolCount} enabled tools";

    public string TestActionLabel => IsTesting ? "Testing…" : "Test";

    internal void UpdateFrom(McpServerProfileItemViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Id != Id)
        {
            throw new ArgumentException(
                "Only the same MCP-server profile can update this settings row.",
                nameof(source));
        }

        _ = SetProperty(ref _revision, source.Revision, nameof(Revision));
        _ = SetProperty(ref _name, source.Name, nameof(Name));
        if (SetProperty(
                ref _transportKind,
                source.TransportKind,
                nameof(TransportKind)))
        {
            OnPropertyChanged(nameof(IsStdio));
            OnPropertyChanged(nameof(Executable));
            OnPropertyChanged(nameof(EnvironmentBindingCount));
            OnPropertyChanged(nameof(EnvironmentBindingSummary));
            OnPropertyChanged(nameof(TransportSummary));
            OnPropertyChanged(nameof(ArgumentSummary));
        }

        if (SetProperty(ref _address, source.Address, nameof(Address)))
        {
            OnPropertyChanged(nameof(Executable));
        }

        if (SetProperty(
                ref _argumentCount,
                source.ArgumentCount,
                nameof(ArgumentCount)))
        {
            OnPropertyChanged(nameof(ArgumentSummary));
        }

        if (SetProperty(
                ref _credentialBindingCount,
                source.CredentialBindingCount,
                nameof(CredentialBindingCount)))
        {
            OnPropertyChanged(nameof(EnvironmentBindingCount));
            OnPropertyChanged(nameof(EnvironmentBindingSummary));
            OnPropertyChanged(nameof(CredentialBindingSummary));
        }

        if (SetProperty(
                ref _enabledToolCount,
                source.EnabledToolCount,
                nameof(EnabledToolCount)))
        {
            OnPropertyChanged(nameof(EnabledToolSummary));
        }

        _ = SetProperty(ref _status, source.Status, nameof(Status));
        _ = SetProperty(
            ref _statusDetail,
            source.StatusDetail,
            nameof(StatusDetail));
        _ = SetProperty(ref _isEnabled, source.IsEnabled, nameof(IsEnabled));
        _ = SetProperty(ref _hasWarning, source.HasWarning, nameof(HasWarning));
        if (SetProperty(ref _isTesting, source.IsTesting, nameof(IsTesting)))
        {
            OnPropertyChanged(nameof(TestActionLabel));
        }

        _ = SetProperty(ref _canTest, source.CanTest, nameof(CanTest));
        _ = SetProperty(ref _isTrusted, source.IsTrusted, nameof(IsTrusted));
    }
}

public enum McpServerCredentialBindingKind
{
    EnvironmentVariable,
    HttpHeader,
}

public sealed record McpServerSecretTargetViewModel(
    McpServerProfileId ProfileId,
    string ServerName,
    McpServerCredentialBindingKind BindingKind,
    string BindingName,
    SecretRef Reference)
{
    public string BindingKindName => BindingKind switch
    {
        McpServerCredentialBindingKind.EnvironmentVariable => "Environment",
        McpServerCredentialBindingKind.HttpHeader => "HTTP header",
        _ => throw new ArgumentOutOfRangeException(
            nameof(BindingKind),
            BindingKind,
            null),
    };

    public string DisplayName =>
        $"{ServerName} · {BindingKindName} {BindingName} → {Reference.Value}";
}
