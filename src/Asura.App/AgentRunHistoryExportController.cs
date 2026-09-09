using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App;

public static class AgentRunHistoryExportController
{
    public const string SuggestedFileName = "asura-agent-history.json";

    public static async ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>>
        ExportAsync(
            AgentChatViewModel agent,
            string destinationPath,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var path = Path.GetFullPath(destinationPath.Trim());
        if (!Path.HasExtension(path))
        {
            path += ".json";
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException(
                "An agent history export directory is required.",
                nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".asura-agent-history-{Guid.NewGuid():N}.tmp");
        try
        {
            AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt> result;
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                result = await agent.ExportHistoryAsync(destination, cancellationToken);
                if (result.IsSuccess)
                {
                    await destination.FlushAsync(cancellationToken);
                }
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            agent.ReportHistoryExportPublicationFailure();
            return Failure(
                AgentSessionCheckpointStoreErrorCode.Cancelled,
                "Agent history export was cancelled.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            agent.ReportHistoryExportPublicationFailure();
            return Failure(
                AgentSessionCheckpointStoreErrorCode.StorageUnavailable,
                "Agent history export could not be written.");
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    agent.ReportHistoryExportPublicationFailure(cleanupUncertain: true);
                }
            }
        }
    }

    private static AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>
        Failure(AgentSessionCheckpointStoreErrorCode code, string message) =>
        AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>.Failure(
            new AgentSessionCheckpointStoreError(code, message));
}
