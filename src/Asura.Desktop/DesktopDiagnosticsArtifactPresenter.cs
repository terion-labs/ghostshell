using System.Diagnostics;
using Asura.App;

namespace Asura.Desktop;

internal sealed class DesktopDiagnosticsArtifactPresenter : IDiagnosticsArtifactPresenter
{
    public DiagnosticsArtifactPresentationCapabilities Capabilities =>
        DiagnosticsArtifactPresentationCapabilities.Open
        | DiagnosticsArtifactPresentationCapabilities.Reveal;

    public ValueTask<DiagnosticsArtifactPresentationResult> PresentAsync(
        DiagnosticsGeneratedArtifact artifact,
        DiagnosticsArtifactPresentationAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(artifact.Locator))
        {
            return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Failed);
        }

        var path = Path.GetFullPath(artifact.Locator);
        if (!File.Exists(path))
        {
            return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Failed);
        }

        try
        {
            var startInfo = action switch
            {
                DiagnosticsArtifactPresentationAction.Open => new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                },
                DiagnosticsArtifactPresentationAction.Reveal => CreateRevealStartInfo(path),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };
            if (startInfo is null)
            {
                return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Unsupported);
            }

            _ = Process.Start(startInfo);
            return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Presented);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or FileNotFoundException)
        {
            return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Failed);
        }
    }

    private static ProcessStartInfo? CreateRevealStartInfo(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var info = DirectProcess("/usr/bin/open");
            info.ArgumentList.Add("-R");
            info.ArgumentList.Add(path);
            return info;
        }

        if (OperatingSystem.IsWindows())
        {
            var info = DirectProcess("explorer.exe");
            info.ArgumentList.Add($"/select,{path}");
            return info;
        }

        if (OperatingSystem.IsLinux())
        {
            var info = DirectProcess("xdg-open");
            info.ArgumentList.Add(Path.GetDirectoryName(path)!);
            return info;
        }

        return null;
    }

    private static ProcessStartInfo DirectProcess(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}
