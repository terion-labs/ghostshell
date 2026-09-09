namespace Asura.Application;

/// <summary>
/// A durable command-lifecycle event emitted by terminal shell integration.
/// Unlike viewport command boundaries, these events remain available after
/// their original row has scrolled out of view.
/// </summary>
public sealed record TerminalShellIntegrationEvent
{
    public TerminalShellIntegrationEvent(
        long Sequence,
        TerminalCommandBoundaryKind Kind,
        DateTimeOffset CapturedAtUtc,
        int? ExitCode = null)
    {
        if (Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        this.Sequence = Sequence;
        this.Kind = Kind;
        this.CapturedAtUtc = CapturedAtUtc;
        this.ExitCode = ExitCode;
    }

    public long Sequence { get; }

    public TerminalCommandBoundaryKind Kind { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public int? ExitCode { get; }
}
