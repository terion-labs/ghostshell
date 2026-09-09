namespace Asura.Application;

public sealed record PanelSessionEvent(
    long Sequence,
    SessionLifecycle Lifecycle,
    SessionHealth Health,
    DateTimeOffset TimestampUtc,
    string Detail);
