using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Owns the live adapter set derived from durable file-provider definitions. The application
/// sees only typed health information; SDK clients and credential material stay behind this seam.
/// </summary>
public interface IFileProviderProfileRuntime : IDisposable
{
    event EventHandler? ProfilesChanged;

    IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics { get; }

    ValueTask<FileProviderTestResult> TestAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken);

    ValueTask ReloadAsync(CancellationToken cancellationToken);
}

public enum FileProviderRuntimeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record FileProviderRuntimeDiagnostic(
    FileProviderProfileId? ProfileId,
    FileProviderRuntimeDiagnosticSeverity Severity,
    string Code,
    string Message);

public sealed record FileProviderTestResult(
    bool IsSuccess,
    string Code,
    string Message,
    FileProviderProfileDescriptor? Descriptor = null,
    FilePanelErrorCode? ErrorCode = null);
