using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Application;

public static class FilePanelWatch
{
    public static async IAsyncEnumerable<FilePanelResult<FilePanelChange>> ObserveAsync(
        IFilePanelClient client,
        FilePanelWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var baseline = await CaptureAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess)
        {
            yield return FilePanelResult<FilePanelChange>.Failure(baseline.Error!);
            yield break;
        }

        yield return Changed(request, FilePanelChangeKind.Synchronized);
        while (true)
        {
            await Task.Delay(request.Interval, cancellationToken).ConfigureAwait(false);
            var current = await CaptureAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess)
            {
                yield return FilePanelResult<FilePanelChange>.Failure(current.Error!);
                if (current.Error?.Retryable != true)
                {
                    yield break;
                }

                continue;
            }

            if (!baseline.Value!.AsSpan().SequenceEqual(current.Value!))
            {
                baseline = current;
                yield return Changed(request, FilePanelChangeKind.Changed);
            }
        }
    }

    private static async ValueTask<FilePanelResult<byte[]>>
        CaptureAsync(
            IFilePanelClient client,
            FilePanelWatchRequest request,
            CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var result in FilePanelTree.EnumerateAsync(
            client,
            request.Location,
            request.Scope,
            request.ShowHidden,
            cancellationToken).ConfigureAwait(false))
        {
            if (!result.IsSuccess)
            {
                return FilePanelResult<byte[]>.Failure(
                    result.Error!);
            }

            var entry = result.Value!;
            Append(entry.Location.ToString());
            Append(entry.Kind.ToString());
            Append(entry.Size?.ToString(CultureInfo.InvariantCulture));
            Append(entry.LastModifiedAt?.ToString("O", CultureInfo.InvariantCulture));
            Append(entry.IsHidden ? "1" : "0");
        }

        // Enumeration-order changes may request an extra refresh, but no complete
        // directory/subtree snapshot remains resident between observations.
        return FilePanelResult<byte[]>.Success(digest.GetHashAndReset());

        void Append(string? value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
    }

    private static FilePanelResult<FilePanelChange> Changed(
        FilePanelWatchRequest request,
        FilePanelChangeKind kind) =>
        FilePanelResult<FilePanelChange>.Success(new FilePanelChange(request.Location, kind));

}
