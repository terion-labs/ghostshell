using System.ComponentModel;
using System.Diagnostics;
using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Desktop;

internal sealed class WorkspaceIsolationRuntimeInstaller(
    WorkspaceIsolationRuntimeInstallation installation,
    Func<Uri, bool>? launch = null) : IWorkspaceIsolationRuntimeInstaller
{
    private readonly Func<Uri, bool> _launch = launch ?? Launch;

    public string RuntimeDisplayName => installation.RuntimeDisplayName;

    public WorkspaceIsolationRuntimeInstallResult BeginInstallation()
    {
        try
        {
            return _launch(installation.Address)
                ? WorkspaceIsolationRuntimeInstallResult.Success()
                : WorkspaceIsolationRuntimeInstallResult.Failure(
                    installation.OpenFailureMessage);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            return WorkspaceIsolationRuntimeInstallResult.Failure(
                installation.OpenFailureMessage);
        }
    }

    private static bool Launch(Uri address)
    {
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return process is not null;
    }
}
