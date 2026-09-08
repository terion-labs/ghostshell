using System.Diagnostics;

namespace GhostShell.ConnectionBackend;

internal enum BackendExecutionLocation { Guest, HostDirect }

/// <summary>Owns the operation's launch and cleanup independently of its cancellation token.</summary>
internal sealed record DatabaseWorkspaceOperationLaunch(ProcessStartInfo StartInfo, Func<Task> CleanupAsync,
    CancellationToken Lifetime = default, BackendExecutionLocation Location = BackendExecutionLocation.Guest);
