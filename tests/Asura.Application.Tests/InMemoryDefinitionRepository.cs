using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

/// <summary>
/// Exercises catalog behavior without giving the tests SQLite's validation behavior for free.
/// The fake intentionally implements the repository's optimistic-revision contract.
/// </summary>
internal sealed class InMemoryDefinitionRepository<TDefinition> : IDefinitionRepository<TDefinition>
    where TDefinition : IDurableDefinition
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<DefinitionKey, StoredDefinition<TDefinition>> _definitions = [];
    private long _clockTick;

    public DefinitionStoreError? ListError { get; set; }

    public DefinitionStoreError? SaveError { get; set; }

    public int SaveAttempts { get; private set; }

    public int DeleteAttempts { get; private set; }

    public void Add(TDefinition definition, long revision = 1)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var now = NextTimestamp();
        _definitions.Add(definition.Key, new(definition, revision, now, now));
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> GetAsync(
        DefinitionKey key,
        CancellationToken cancellationToken)
    {
        var cancelled = Cancelled<StoredDefinition<TDefinition>>(cancellationToken);
        if (cancelled is not null)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (key.Kind != TDefinition.Kind)
        {
            return ValueTask.FromResult(Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The requested kind does not match this repository."));
        }

        return ValueTask.FromResult(_definitions.TryGetValue(key, out var stored)
            ? DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(stored)
            : Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.NotFound,
                "The requested definition does not exist."));
    }

    public ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        var cancelled = Cancelled<IReadOnlyList<StoredDefinition<TDefinition>>>(cancellationToken);
        if (cancelled is not null)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (ListError is not null)
        {
            return ValueTask.FromResult(
                DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>.Failure(ListError));
        }

        IReadOnlyList<StoredDefinition<TDefinition>> snapshot = [.. _definitions.Values
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Key.Value, StringComparer.Ordinal)];
        return ValueTask.FromResult(
            DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>.Success(snapshot));
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveAsync(
        TDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SaveAttempts++;

        var cancelled = Cancelled<StoredDefinition<TDefinition>>(cancellationToken);
        if (cancelled is not null)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (SaveError is not null)
        {
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(SaveError));
        }

        if (definition.Key.Kind != TDefinition.Kind)
        {
            return ValueTask.FromResult(Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The saved kind does not match this repository."));
        }

        if (expectedRevision is null)
        {
            return ValueTask.FromResult(Insert(definition));
        }

        return ValueTask.FromResult(Update(definition, expectedRevision.Value));
    }

    public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        DeleteAttempts++;
        var cancelled = Cancelled<Unit>(cancellationToken);
        if (cancelled is not null)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (key.Kind != TDefinition.Kind)
        {
            return ValueTask.FromResult(Failure<Unit>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The deleted kind does not match this repository."));
        }

        if (!_definitions.TryGetValue(key, out var current))
        {
            return ValueTask.FromResult(Failure<Unit>(
                DefinitionStoreErrorCode.NotFound,
                "The definition does not exist."));
        }

        if (current.Revision != expectedRevision)
        {
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Failure(new(
                DefinitionStoreErrorCode.RevisionConflict,
                "The definition changed before it could be deleted.",
                current.Revision)));
        }

        _definitions.Remove(key);
        return ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));
    }

    private DefinitionStoreResult<StoredDefinition<TDefinition>> Insert(TDefinition definition)
    {
        if (_definitions.TryGetValue(definition.Key, out var current))
        {
            return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(new(
                DefinitionStoreErrorCode.RevisionConflict,
                "A definition with this identity already exists.",
                current.Revision));
        }

        var now = NextTimestamp();
        var stored = new StoredDefinition<TDefinition>(definition, 1, now, now);
        _definitions.Add(definition.Key, stored);
        return DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(stored);
    }

    private DefinitionStoreResult<StoredDefinition<TDefinition>> Update(
        TDefinition definition,
        long expectedRevision)
    {
        if (!_definitions.TryGetValue(definition.Key, out var current))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.NotFound,
                "The definition does not exist.");
        }

        if (current.Revision != expectedRevision)
        {
            return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(new(
                DefinitionStoreErrorCode.RevisionConflict,
                "The definition changed before it could be saved.",
                current.Revision));
        }

        var stored = new StoredDefinition<TDefinition>(
            definition,
            current.Revision + 1,
            current.CreatedAt,
            NextTimestamp());
        _definitions[definition.Key] = stored;
        return DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(stored);
    }

    private DateTimeOffset NextTimestamp() => Epoch.AddTicks(_clockTick++);

    private static DefinitionStoreResult<T>? Cancelled<T>(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Failure<T>(DefinitionStoreErrorCode.Cancelled, "The operation was cancelled.")
            : null;

    private static DefinitionStoreResult<T> Failure<T>(
        DefinitionStoreErrorCode code,
        string message) =>
        DefinitionStoreResult<T>.Failure(new(code, message));
}
