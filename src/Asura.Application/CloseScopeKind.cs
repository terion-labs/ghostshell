namespace Asura.Application;

/// <summary>
/// What a close is being asked to end.
///
/// A window holds several workspaces at once, so "this workspace" had to become
/// sayable. Without it the only word wide enough to close one was Window, and
/// reaching for it took every other workspace's sessions down as well.
/// </summary>
public enum CloseScopeKind
{
    Panel,
    Tab,
    Workspace,
    Window,
    Session,
}
