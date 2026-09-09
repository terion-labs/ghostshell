namespace Asura.Application;

/// <summary>
/// An optional, application-authored viewport range where interactive input is
/// accepted. It is untrusted observation and never grants input authority.
/// </summary>
public sealed record TerminalInputRegion
{
    public const int MaximumCoordinate = 32_767;

    public TerminalInputRegion(int Row, int StartColumn, int EndColumnExclusive)
    {
        if (Row is < 0 or > MaximumCoordinate)
        {
            throw new ArgumentOutOfRangeException(nameof(Row));
        }

        if (StartColumn is < 0 or > MaximumCoordinate)
        {
            throw new ArgumentOutOfRangeException(nameof(StartColumn));
        }

        if (EndColumnExclusive <= StartColumn
            || EndColumnExclusive > MaximumCoordinate + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EndColumnExclusive));
        }

        this.Row = Row;
        this.StartColumn = StartColumn;
        this.EndColumnExclusive = EndColumnExclusive;
    }

    public int Row { get; }

    public int StartColumn { get; }

    public int EndColumnExclusive { get; }

    public bool Fits(int rows, int columns) =>
        Row < rows && StartColumn < columns && EndColumnExclusive <= columns;
}
