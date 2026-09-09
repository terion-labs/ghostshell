namespace Asura.Application;

/// <summary>
/// Advisory state emitted explicitly by an interactive application running in a terminal.
/// The signal is untrusted observation only; it never carries or grants agent authority.
/// </summary>
public enum TerminalInteractiveStateKind
{
    IdleInput,
    Working,
    Streaming,
    Modal,
    InputRequired,
    ApprovalRequired,
}

public sealed record TerminalInteractiveStateSnapshot
{
    public TerminalInteractiveStateSnapshot(
        long Sequence,
        TerminalInteractiveStateKind Kind,
        DateTimeOffset ObservedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        TerminalInputRegion? InputRegion = null)
    {
        if (Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (ExpiresAtUtc <= ObservedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpiresAtUtc));
        }

        this.Sequence = Sequence;
        this.Kind = Kind;
        this.ObservedAtUtc = ObservedAtUtc;
        this.ExpiresAtUtc = ExpiresAtUtc;
        this.InputRegion = InputRegion;
    }

    public long Sequence { get; }

    public TerminalInteractiveStateKind Kind { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>
    /// Optional application-authored input range. Its absence means unknown,
    /// and its presence remains untrusted observation rather than authority.
    /// </summary>
    public TerminalInputRegion? InputRegion { get; init; }
}
