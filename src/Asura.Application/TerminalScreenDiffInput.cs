namespace Asura.Application;

public sealed record TerminalScreenDiffInput
{
    public const int MaximumChangedRows = 200;

    public TerminalScreenDiffInput(long AfterContentRevision, int MaximumRowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(AfterContentRevision);
        if (MaximumRowCount is < 1 or > MaximumChangedRows)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRowCount));
        }

        this.AfterContentRevision = AfterContentRevision;
        this.MaximumRowCount = MaximumRowCount;
    }

    public long AfterContentRevision { get; }

    public int MaximumRowCount { get; }
}
