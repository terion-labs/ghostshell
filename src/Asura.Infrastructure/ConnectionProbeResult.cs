namespace Asura.Infrastructure;

public sealed record ConnectionProbeResult
{
    public ConnectionProbeResult(
        ConnectionProbeOutcome outcome,
        int? exitCode,
        string standardError,
        ConnectionProbeStartFailure startFailure = ConnectionProbeStartFailure.None)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "The probe outcome is invalid.");
        }

        if (!Enum.IsDefined(startFailure))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startFailure),
                startFailure,
                "The probe start failure is invalid.");
        }

        var validShape = outcome switch
        {
            ConnectionProbeOutcome.Exited =>
                exitCode is not null && startFailure == ConnectionProbeStartFailure.None,
            ConnectionProbeOutcome.StartFailed =>
                exitCode is null && startFailure != ConnectionProbeStartFailure.None,
            ConnectionProbeOutcome.TimedOut or ConnectionProbeOutcome.Cancelled =>
                exitCode is null && startFailure == ConnectionProbeStartFailure.None,
            _ => false,
        };
        if (!validShape)
        {
            throw new ArgumentException("Probe outcome details are inconsistent.");
        }

        Outcome = outcome;
        ExitCode = exitCode;
        StandardError = standardError;
        StartFailure = startFailure;
    }

    public ConnectionProbeOutcome Outcome { get; }

    public int? ExitCode { get; }

    public string StandardError { get; }

    public ConnectionProbeStartFailure StartFailure { get; }

    public static ConnectionProbeResult Success { get; } =
        new(ConnectionProbeOutcome.Exited, 0, string.Empty);
}
