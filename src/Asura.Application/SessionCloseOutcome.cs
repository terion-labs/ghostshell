namespace Asura.Application;

public enum SessionCloseOutcome
{
    GracefullyClosed,
    ConfirmationRequired,
    Cancelled,
    ForceTerminated,
    EngineFailed,
    AlreadyClosed,
}
