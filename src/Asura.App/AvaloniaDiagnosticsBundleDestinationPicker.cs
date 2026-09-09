using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Asura.App;

/// <summary>
/// Uses the platform save picker, then writes through a same-directory temporary file so a
/// cancelled or failed export never leaves a partially published diagnostics archive.
/// </summary>
public sealed class AvaloniaDiagnosticsBundleDestinationPicker(Window owner)
    : IDiagnosticsBundleDestinationPicker
{
    private static readonly FilePickerFileType BundleFileType = new("Asura diagnostics bundle")
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"],
        AppleUniformTypeIdentifiers = ["public.zip-archive"],
    };

    private readonly Window _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async ValueTask<IDiagnosticsBundleDestination?> PickAsync(string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        var selected = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Asura diagnostics",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "zip",
            FileTypeChoices = [BundleFileType],
            ShowOverwritePrompt = true,
        });
        if (selected is null)
        {
            return null;
        }

        var path = selected.TryGetLocalPath()
            ?? throw new NotSupportedException(
                "Diagnostics bundles must be exported to a local filesystem path.");
        return new LocalDiagnosticsBundleDestination(path);
    }
}

internal sealed class LocalDiagnosticsBundleDestination : IDiagnosticsBundleDestination
{
    private readonly string _targetPath;
    private readonly string _temporaryPath;
    private FileStream? _stream;
    private bool _completed;

    public LocalDiagnosticsBundleDestination(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        _targetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(_targetPath)
            ?? throw new ArgumentException("The diagnostics destination has no parent directory.", nameof(targetPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The diagnostics destination directory is unavailable.");
        }

        var fileName = Path.GetFileName(_targetPath);
        _temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(
            _temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        Artifact = new DiagnosticsGeneratedArtifact(fileName, _targetPath);
    }

    public DiagnosticsGeneratedArtifact Artifact { get; }

    public Stream Content => _stream
        ?? throw new ObjectDisposedException(nameof(LocalDiagnosticsBundleDestination));

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        var stream = _stream
            ?? throw new ObjectDisposedException(nameof(LocalDiagnosticsBundleDestination));
        cancellationToken.ThrowIfCancellationRequested();
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        await stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(_temporaryPath, _targetPath, overwrite: true);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        if (!_completed && File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
    }
}
