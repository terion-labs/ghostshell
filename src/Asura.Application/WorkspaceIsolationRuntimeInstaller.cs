namespace Asura.Application;

/// <summary>
/// Starts the platform owner's supported installation flow for the workspace
/// isolation runtime. Installation itself remains visible and consented to by
/// the user.
/// </summary>
public interface IWorkspaceIsolationRuntimeInstaller
{
    string RuntimeDisplayName { get; }

    WorkspaceIsolationRuntimeInstallResult BeginInstallation();
}

public sealed record WorkspaceIsolationRuntimeInstallResult
{
    private WorkspaceIsolationRuntimeInstallResult(bool started, string? error)
    {
        Started = started;
        Error = error;
    }

    public bool Started { get; }

    public string? Error { get; }

    public static WorkspaceIsolationRuntimeInstallResult Success() => new(true, null);

    public static WorkspaceIsolationRuntimeInstallResult Failure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new(false, error);
    }
}
