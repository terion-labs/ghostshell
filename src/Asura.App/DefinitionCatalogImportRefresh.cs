using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Refreshes the single catalog snapshot that drives every definition-backed desktop view.
/// </summary>
public sealed class DefinitionCatalogImportRefresh(IDefinitionCatalog catalog)
    : IDefinitionBundleImportRefresh
{
    private readonly IDefinitionCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async ValueTask<DefinitionStoreResult<Unit>> ReloadAsync(
        CancellationToken cancellationToken)
    {
        var result = await _catalog.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(result.Error!);
    }
}
