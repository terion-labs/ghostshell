namespace Asura.Core;

/// <summary>
/// Maps one host path into the Linux environment owned by an isolated workspace.
/// Paths remain platform-neutral durable text here; the selected platform provider
/// performs native normalization and existence checks before booting the isolate.
/// </summary>
public sealed record WorkspaceIsolationMountDefinition(
    string HostPath,
    string GuestPath,
    bool IsReadOnly);
