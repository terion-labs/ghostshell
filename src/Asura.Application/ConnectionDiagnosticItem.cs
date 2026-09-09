namespace Asura.Application;

public sealed record ConnectionDiagnosticItem
{
    public ConnectionDiagnosticItem(
        ConnectionDiagnosticStage stage,
        ConnectionDiagnosticStatus status,
        string stableCode,
        string message)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Stage = stage;
        Status = status;
        StableCode = stableCode;
        Message = message;
    }

    public ConnectionDiagnosticStage Stage { get; }

    public ConnectionDiagnosticStatus Status { get; }

    public string StableCode { get; }

    public string Message { get; }
}
