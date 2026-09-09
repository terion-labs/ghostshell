using Asura.Core;

namespace Asura.Application;

public interface IDefinitionRepository<TDefinition>
    where TDefinition : IDurableDefinition
{
    ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> GetAsync(
        DefinitionKey key,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>> ListAsync(
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveAsync(
        TDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken);
}
