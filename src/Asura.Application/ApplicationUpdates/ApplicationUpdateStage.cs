namespace Asura.Application.ApplicationUpdates;

public enum ApplicationUpdateStage
{
    Unavailable,
    ManagedExternally,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}
