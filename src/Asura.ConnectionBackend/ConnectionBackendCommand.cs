using Asura.Redis;

namespace Asura.ConnectionBackend;

/// <summary>The same private IPC entry runs in the SDK guest or a Direct host child, before any desktop/profile initialization.</summary>
internal static class ConnectionBackendCommand
{
    internal const string Marker = "--asura-connection-backend";

    internal static async Task<int> RunAsync(string capability, string operationId)
    {
        try
        {
            switch (capability)
            {
                case "prepare": DatabaseWorkspaceScratch.Prepare(operationId); return 0;
                case "cleanup": await DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None).ConfigureAwait(false); return 0;
                case "database": return await DatabaseOperationWorker.RunChildAsync(privateWorkspace: true, workspaceOperationId: operationId).ConfigureAwait(false);
                case "redis":
                    using (DatabaseWorkspaceScratch.Acquire(operationId))
                    {
                        await RedisWorkspaceSession.RunChildAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                            new RedisPanelSessionFactory(), CancellationToken.None).ConfigureAwait(false);
                        return 0;
                    }
                case "http":
                    using (DatabaseWorkspaceScratch.Acquire(operationId))
                    {
                        await WorkspaceHttpProtocol.RunChildAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None).ConfigureAwait(false);
                        return 0;
                    }
                case "files":
                    using (DatabaseWorkspaceScratch.Acquire(operationId))
                    {
                        await FileWorkspaceChild.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None).ConfigureAwait(false);
                        return 0;
                    }
                default: return 64;
            }
        }
        catch { return 70; }
    }
}
