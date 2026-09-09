using System.Globalization;
using Asura.Desktop;

namespace Asura.SingleInstanceTestHost;

internal static class Program
{
    private static readonly TimeSpan ProcessLifetime = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var mode, var profileDirectory, ..])
        {
            return 64;
        }

        using var lifetime = new CancellationTokenSource(ProcessLifetime);
        return mode switch
        {
            "primary" when args is [_, _, var readyPath, var activatedPath, var stopPath] =>
                await RunPrimaryAsync(
                    profileDirectory,
                    readyPath,
                    activatedPath,
                    stopPath,
                    lifetime.Token),
            "activate" when args.Length == 2 =>
                await ActivateAsync(profileDirectory, lifetime.Token),
            _ => 64,
        };
    }

    private static async Task<int> RunPrimaryAsync(
        string profileDirectory,
        string readyPath,
        string activatedPath,
        string stopPath,
        CancellationToken cancellationToken)
    {
        var start = await SingleInstanceCoordinator.StartAsync(
            profileDirectory,
            cancellationToken);
        if (start is not SingleInstanceStartResult.Primary primary)
        {
            return start is SingleInstanceStartResult.Failure ? 70 : 71;
        }

        await using var coordinator = primary.Coordinator;
        coordinator.RegisterActivationHandler(() =>
            File.WriteAllText(
                activatedPath,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
        await File.WriteAllTextAsync(readyPath, "ready", cancellationToken);

        try
        {
            while (!File.Exists(stopPath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 72;
        }

        coordinator.StopAcceptingActivations();
        return 0;
    }

    private static async Task<int> ActivateAsync(
        string profileDirectory,
        CancellationToken cancellationToken)
    {
        var start = await SingleInstanceCoordinator.StartAsync(
            profileDirectory,
            cancellationToken);
        if (start is SingleInstanceStartResult.ExistingInstanceActivated)
        {
            return 0;
        }

        if (start is SingleInstanceStartResult.Primary primary)
        {
            await primary.Coordinator.DisposeAsync();
            return 73;
        }

        return 74;
    }
}
