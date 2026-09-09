using Asura.Core;

namespace Asura.Application;

public sealed record StoredDefinition<TDefinition>(
    TDefinition Value,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
    where TDefinition : IDurableDefinition;
