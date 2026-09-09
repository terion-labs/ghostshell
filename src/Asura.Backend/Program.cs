using Asura.ConnectionBackend;

namespace Asura.Backend;

/// <summary>An owned, UI-free operation process; stdin/stdout are private binary IPC.</summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => args.Length == 2
        ? ConnectionBackendCommand.RunAsync(args[0], args[1]) : Task.FromResult(64);
}
