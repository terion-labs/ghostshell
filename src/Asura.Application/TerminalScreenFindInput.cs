namespace Asura.Application;

public sealed record TerminalScreenFindInput
{
    public const int MaximumQueryLength = 512;
    public const int MaximumMatches = 64;

    public TerminalScreenFindInput(string Query, int MaximumMatchCount)
    {
        ArgumentNullException.ThrowIfNull(Query);
        if (Query.Length is 0 or > MaximumQueryLength)
        {
            throw new ArgumentException(
                $"A rendered-screen query must contain between 1 and {MaximumQueryLength} characters.",
                nameof(Query));
        }

        if (MaximumMatchCount is < 1 or > MaximumMatches)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMatchCount));
        }

        this.Query = Query;
        this.MaximumMatchCount = MaximumMatchCount;
    }

    public string Query { get; }

    public int MaximumMatchCount { get; }
}
