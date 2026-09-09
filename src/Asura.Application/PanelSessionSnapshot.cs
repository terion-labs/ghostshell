namespace Asura.Application;

public sealed record PanelSessionSnapshot(
    SessionLifecycle Lifecycle,
    SessionHealth Health,
    bool HasActiveWork,
    string StatusDetail,
    SessionFailure? Failure = null);
