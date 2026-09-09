using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns file-provider definition projection, authoring, and optimistic persistence.
/// Runtime panels, transfers, and secret mutation remain shell-host concerns.
/// </summary>
public sealed class FileProviderSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IDefinitionCatalog _catalog;
    private readonly IFileProviderProfileRuntime? _runtime;
    private readonly Func<IReadOnlyList<FileProviderProfileDescriptor>> _liveProfiles;
    private readonly Func<IReadOnlyList<SecretMetadataViewModel>> _secretMetadata;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private DefinitionCatalogSnapshot _snapshot;
    private bool _disposed;

    public FileProviderSettingsViewModel(
        IDefinitionCatalog catalog,
        IFileProviderProfileRuntime? runtime,
        Func<IReadOnlyList<FileProviderProfileDescriptor>> liveProfiles,
        Func<IReadOnlyList<SecretMetadataViewModel>> secretMetadata,
        IUiThreadDispatcher dispatcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime;
        _liveProfiles = liveProfiles ?? throw new ArgumentNullException(nameof(liveProfiles));
        _secretMetadata = secretMetadata ?? throw new ArgumentNullException(nameof(secretMetadata));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshot = _catalog.Snapshot;
        _runtime?.ProfilesChanged += OnProfilesChanged;
        RefreshDefinitions();
    }

    public ObservableCollection<FileProviderProfileItemViewModel> Definitions { get; } = [];

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles => _liveProfiles();

    public void ApplyCatalog(DefinitionCatalogSnapshot snapshot)
    {
        ThrowIfDisposed();
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        RefreshDefinitions();
    }

    public FileProviderProfileEditorViewModel CreateEditor(
        FileProviderProfileId? profileId = null)
    {
        ThrowIfDisposed();
        var runtime = _runtime
            ?? throw new InvalidOperationException("The file-provider runtime is unavailable.");
        var connections = _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .ToArray();
        var secrets = _secretMetadata();
        if (profileId is null)
        {
            return new FileProviderProfileEditorViewModel(runtime, connections, secrets);
        }

        var stored = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId.Value)
            ?? throw new InvalidOperationException(
                "That file-provider profile no longer exists.");
        return new FileProviderProfileEditorViewModel(
            runtime,
            connections,
            secrets,
            stored.Value,
            stored.Revision);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveAsync(
        FileProviderProfileSaveRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        return _catalog.SaveFileProviderProfileAsync(
            request.Profile,
            request.ExpectedRevision,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime?.ProfilesChanged -= OnProfilesChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (!_disposed)
                    {
                        RefreshDefinitions();
                    }
                },
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RefreshDefinitions()
    {
        var liveIds = Profiles
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var diagnostics = (_runtime?.Diagnostics ?? [])
            .Where(item => item.ProfileId is not null)
            .GroupBy(item => item.ProfileId!.Value)
            .ToDictionary(item => item.Key, item => item.ToArray());
        ReplaceIfChanged(
            Definitions,
            [.. _snapshot.FileProviderProfiles
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => CreateDefinitionItem(item, liveIds, diagnostics))]);
        OnPropertyChanged(nameof(Definitions));
        OnPropertyChanged(nameof(Profiles));
    }

    private static FileProviderProfileItemViewModel CreateDefinitionItem(
        StoredDefinition<FileProviderProfile> item,
        IReadOnlySet<string> liveIds,
        IReadOnlyDictionary<
            FileProviderProfileId,
            FileProviderRuntimeDiagnostic[]> diagnostics)
    {
        diagnostics.TryGetValue(item.Value.Id, out var profileDiagnostics);
        var error = profileDiagnostics?.FirstOrDefault(diagnostic =>
            diagnostic.Severity == FileProviderRuntimeDiagnosticSeverity.Error);
        var warning = profileDiagnostics?.FirstOrDefault(diagnostic =>
            diagnostic.Severity == FileProviderRuntimeDiagnosticSeverity.Warning);
        var isLive = liveIds.Contains(item.Value.Id.Value);
        return new(
            item.Value.Id,
            item.Revision,
            item.Value.Name,
            KindLabel(item.Value.ProviderKind),
            Endpoint(item.Value.Configuration),
            error is not null ? "Unavailable" : isLive ? "Ready" : "Loading",
            error?.Message
                ?? warning?.Message
                ?? (isLive
                    ? "Adapter loaded; credentials resolve only when the provider is used."
                    : "Materializing the saved adapter…"),
            error is not null,
            warning is not null);
    }

    private static void ReplaceIfChanged(
        ObservableCollection<FileProviderProfileItemViewModel> target,
        IReadOnlyList<FileProviderProfileItemViewModel> values)
    {
        if (target.SequenceEqual(values))
        {
            return;
        }

        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static string Endpoint(FileProviderConfiguration configuration) =>
        configuration switch
        {
            FileProviderConfiguration.Local value => value.RootPath,
            FileProviderConfiguration.S3 value => value.ServiceUri is null
                ? $"s3://{value.BucketName} · {value.Region ?? "us-east-1"}"
                : $"{value.ServiceUri.Host} · {value.BucketName}",
            FileProviderConfiguration.Sftp value =>
                $"SSH connection {value.ConnectionId.Value} · {value.RemoteRoot}",
            FileProviderConfiguration.Ftp value =>
                $"{value.Security} · {value.Host}:{value.Port}{value.RemoteRoot}",
            FileProviderConfiguration.Smb value =>
                $"smb://{value.Server}/{value.Share}{value.RemoteRoot}",
            FileProviderConfiguration.WebDav value => value.BaseUri.AbsoluteUri,
            _ => "Unsupported provider",
        };

    private static string KindLabel(FileProviderKind kind) => kind switch
    {
        FileProviderKind.Local => "Local",
        FileProviderKind.S3 => "S3",
        FileProviderKind.Sftp => "SFTP",
        FileProviderKind.Ftp => "FTP/FTPS",
        FileProviderKind.Smb => "SMB",
        FileProviderKind.WebDav => "WebDAV",
        _ => kind.ToString().ToUpperInvariant(),
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
