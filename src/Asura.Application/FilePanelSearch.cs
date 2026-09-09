using System.Runtime.CompilerServices;

namespace Asura.Application;

public static class FilePanelSearch
{
    public static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> FindAsync(
        IFilePanelClient client,
        FilePanelSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var result in FilePanelTree.EnumerateAsync(
            client,
            request.Location,
            request.Scope,
            request.ShowHidden,
            cancellationToken).ConfigureAwait(false))
        {
            if (!result.IsSuccess)
            {
                yield return result;
                yield break;
            }

            if (result.Value!.Name.Contains(
                    request.Query,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return result;
            }
        }
    }
}
