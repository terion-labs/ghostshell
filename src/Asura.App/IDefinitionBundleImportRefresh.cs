using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Reloads presentation-visible definitions after an atomic import has committed.
/// </summary>
public interface IDefinitionBundleImportRefresh
{
    ValueTask<DefinitionStoreResult<Unit>> ReloadAsync(CancellationToken cancellationToken);
}
