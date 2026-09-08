using System.Diagnostics;

namespace GhostShell.DatabaseBackend;

/// <summary>Owns the guest operation's launch and cleanup independently of its cancellation token.</summary>
internal sealed record DatabaseWorkspaceOperationLaunch(ProcessStartInfo StartInfo, Func<Task> CleanupAsync);
