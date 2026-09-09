using Asura.Core;

namespace Asura.Application;

/// <summary>A dependent screen updated alongside the layout it references.</summary>
public sealed record ScreenRevisionUpdate(
    ScreenDefinition Definition,
    long ExpectedRevision);

/// <summary>
/// One definition inside an atomic graph write. A null expected revision means
/// the definition does not exist yet and must be inserted.
/// </summary>
public sealed record DefinitionGraphWrite(
    IDurableDefinition Definition,
    long? ExpectedRevision);

/// <summary>
/// Saves a layout together with the dependent screens reconciled to it, in one
/// transaction and one validation batch. Saved separately, either order is
/// rejected: the storage graph refuses a layout whose stored screens no longer
/// map every slot, and refuses a screen that maps slots its stored layout does
/// not yet have.
/// </summary>
public interface ILayoutGraphStore
{
    ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutWithScreensAsync(
        LayoutDefinition layout,
        long? expectedLayoutRevision,
        IReadOnlyList<ScreenRevisionUpdate> screens,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes a set of mutually dependent definitions as one transaction and one
    /// prospective validation batch. Returns null on success. Workspace autosave
    /// uses this to land a workspace together with the captured tab layouts its
    /// entries reference.
    /// </summary>
    ValueTask<DefinitionStoreError?> SaveGraphAsync(
        IReadOnlyList<DefinitionGraphWrite> writes,
        CancellationToken cancellationToken);
}
