namespace Asura.Application;

public enum PanelCloseOutcome
{
    GracefullyClosed,
    ConfirmationRequired,
    Cancelled,
    ForceTerminated,
    EngineFailed,
    AlreadyClosed,
}
