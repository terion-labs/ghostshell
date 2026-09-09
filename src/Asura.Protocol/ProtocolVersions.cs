namespace Asura.Protocol;

public static class ProtocolVersions
{
    public const int Current = 1;

    public static IReadOnlyList<int> Supported { get; } = [Current];
}
