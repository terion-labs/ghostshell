using Asura.Application;

namespace Asura.Browser;

internal sealed record NativeBrowserSnapshot(
    IReadOnlyList<NativeBrowserSnapshotNode> Nodes,
    bool IsTruncated);

internal sealed record NativeBrowserSnapshotNode(
    int Depth,
    string Role,
    string Name,
    BrowserSnapshotNodeState States,
    NativeBrowserElementHandle? Handle);

internal sealed record NativeBrowserElementHandle
{
    public const int MaximumNonceLength = 64;
    public const int MaximumTokenLength = 64;
    public const long MaximumMutationEpoch = 9_007_199_254_740_991;

    public NativeBrowserElementHandle(
        string snapshotNonce,
        string elementToken,
        long mutationEpoch)
    {
        SnapshotNonce = RequireIdentifier(
            snapshotNonce,
            MaximumNonceLength,
            nameof(snapshotNonce));
        ElementToken = RequireIdentifier(
            elementToken,
            MaximumTokenLength,
            nameof(elementToken));
        if (mutationEpoch is < 0 or > MaximumMutationEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(mutationEpoch));
        }

        MutationEpoch = mutationEpoch;
    }

    public string SnapshotNonce { get; }

    public string ElementToken { get; }

    public long MutationEpoch { get; }

    private static string RequireIdentifier(
        string value,
        int maximumLength,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "An embedded browser element handle must contain one bounded "
                + "URL-safe ASCII identifier.",
                parameterName);
        }

        return string.Concat(value);
    }
}

internal sealed record NativeBrowserSnapshotResult
{
    private NativeBrowserSnapshotResult(
        NativeBrowserSnapshot? value,
        NativeBrowserSnapshotFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public NativeBrowserSnapshot? Value { get; }

    public NativeBrowserSnapshotFailure? Failure { get; }

    public bool IsSuccess => Value is not null;

    public static NativeBrowserSnapshotResult Success(
        NativeBrowserSnapshot value) =>
        new(
            value ?? throw new ArgumentNullException(nameof(value)),
            failure: null);

    public static NativeBrowserSnapshotResult Invalid() =>
        new(value: null, NativeBrowserSnapshotFailure.Invalid);

    public static NativeBrowserSnapshotResult Unavailable() =>
        new(value: null, NativeBrowserSnapshotFailure.Unavailable);
}

internal enum NativeBrowserSnapshotFailure
{
    Invalid,
    Unavailable,
}
