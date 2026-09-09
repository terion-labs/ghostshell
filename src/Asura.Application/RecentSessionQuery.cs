using Asura.Core;

namespace Asura.Application;

public sealed record RecentSessionQuery
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 1_000;

    public RecentSessionQuery(
        int limit = DefaultLimit,
        DefinitionKind? sourceKind = null)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"A recent-session query must request between 1 and {MaximumLimit} records.");
        }

        if (sourceKind is { } kind && string.IsNullOrWhiteSpace(kind.Value))
        {
            throw new ArgumentException(
                "A source-definition kind cannot be empty.",
                nameof(sourceKind));
        }

        Limit = limit;
        SourceKind = sourceKind;
    }

    public int Limit { get; }

    public DefinitionKind? SourceKind { get; }
}
