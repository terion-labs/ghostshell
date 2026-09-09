namespace Asura.Mcp;

internal sealed class McpSessionOptions
{
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public int MaxJsonDepth { get; init; } = 32;

    public int MaxJsonNodes { get; init; } = 16_384;

    public int MaxControlMessagesPerResponse { get; init; } = 32;

    public int MaxTools { get; init; } = 256;

    public int MaxToolListPages { get; init; } = 16;

    public int MaxToolSchemaBytes { get; init; } = 64 * 1024;

    public int MaxToolArgumentsBytes { get; init; } = 64 * 1024;

    public int MaxToolResultBytes { get; init; } = 512 * 1024;

    public int MaxToolContentItems { get; init; } = 128;

    public int MaxStderrBytes { get; init; } = 32 * 1024;

    public int MaxStderrLines { get; init; } = 256;

    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    internal void Validate()
    {
        if (InitializationTimeout <= TimeSpan.Zero
            || InitializationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(InitializationTimeout));
        }

        RequireRange(MaxMessageBytes, 1024, 16 * 1024 * 1024, nameof(MaxMessageBytes));
        RequireRange(MaxJsonDepth, 4, 128, nameof(MaxJsonDepth));
        RequireRange(MaxJsonNodes, 64, 1_000_000, nameof(MaxJsonNodes));
        RequireRange(
            MaxControlMessagesPerResponse,
            1,
            1024,
            nameof(MaxControlMessagesPerResponse));
        RequireRange(MaxTools, 1, 4096, nameof(MaxTools));
        RequireRange(MaxToolListPages, 1, 128, nameof(MaxToolListPages));
        RequireRange(MaxToolSchemaBytes, 128, MaxMessageBytes, nameof(MaxToolSchemaBytes));
        RequireRange(MaxToolArgumentsBytes, 2, MaxMessageBytes, nameof(MaxToolArgumentsBytes));
        RequireRange(MaxToolResultBytes, 128, MaxMessageBytes, nameof(MaxToolResultBytes));
        RequireRange(MaxToolContentItems, 1, 4096, nameof(MaxToolContentItems));
        RequireRange(MaxStderrBytes, 0, 1024 * 1024, nameof(MaxStderrBytes));
        RequireRange(MaxStderrLines, 0, 16_384, nameof(MaxStderrLines));
        if (ShutdownGracePeriod < TimeSpan.Zero
            || ShutdownGracePeriod > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownGracePeriod));
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
