namespace Asura.Application;

public enum ConnectionReconnectState
{
    Idle,
    Waiting,
    Attempting,
    WaitingForSession,
    Connected,
    Exhausted,
    Cancelled,
}
