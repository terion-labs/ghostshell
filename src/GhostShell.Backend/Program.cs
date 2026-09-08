using GhostShell.ConnectionBackend;

namespace GhostShell.Backend;

/// <summary>An owned, UI-free operation process; stdin/stdout are private binary IPC.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            return 64;
        }

        try
        {
            switch (args[0])
            {
                case "prepare": DatabaseWorkspaceScratch.Prepare(args[1]); return 0;
                case "cleanup": await DatabaseWorkspaceScratch.CleanupAsync(args[1], CancellationToken.None).ConfigureAwait(false); return 0;
                case "database": return await DatabaseOperationWorker.RunChildAsync(privateWorkspace: true, workspaceOperationId: args[1]).ConfigureAwait(false);
                default: return 64;
            }
        }
        catch { return 70; }
    }
}
