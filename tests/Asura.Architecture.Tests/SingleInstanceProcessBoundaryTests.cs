using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Asura.Architecture.Tests;

public sealed class SingleInstanceProcessBoundaryTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SecondaryProcessActivatesThePrimaryProcess()
    {
        await using var profile = TemporaryProfile.Create();
        var readyPath = Path.Combine(profile.DirectoryPath, "primary.ready");
        var activatedPath = Path.Combine(profile.DirectoryPath, "primary.activated");
        var stopPath = Path.Combine(profile.DirectoryPath, "primary.stop");
        using var primary = StartTestHost(
            "primary",
            profile.DirectoryPath,
            readyPath,
            activatedPath,
            stopPath);
        Process? secondary = null;

        try
        {
            await WaitForFileAsync(readyPath, ProcessTimeout);

            secondary = StartTestHost("activate", profile.DirectoryPath);
            Assert.NotEqual(primary.Id, secondary.Id);
            var secondaryResult = await WaitForExitAsync(secondary, ProcessTimeout);

            Assert.True(
                secondaryResult.ExitCode == 0,
                $"Secondary exit: {secondaryResult.ExitCode}\n{secondaryResult.StandardError}");
            await WaitForFileAsync(activatedPath, ProcessTimeout);
            Assert.Equal(
                primary.Id.ToString(CultureInfo.InvariantCulture),
                await File.ReadAllTextAsync(activatedPath));

            await File.WriteAllTextAsync(stopPath, "stop");
            var primaryResult = await WaitForExitAsync(primary, ProcessTimeout);
            Assert.True(
                primaryResult.ExitCode == 0,
                $"Primary exit: {primaryResult.ExitCode}\n{primaryResult.StandardError}");
        }
        finally
        {
            await File.WriteAllTextAsync(stopPath, "stop");
            if (secondary is not null)
            {
                await TerminateAsync(secondary);
                secondary.Dispose();
            }

            await TerminateAsync(primary);
        }
    }

    private static Process StartTestHost(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ResolveTestHostPath());
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("The single-instance test host did not start.");
    }

    private static async Task<ProcessResult> WaitForExitAsync(
        Process process,
        TimeSpan timeout)
    {
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var lifetime = new CancellationTokenSource(timeout);
        try
        {
            while (!File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for '{Path.GetFileName(path)}'.");
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // The native process handle observed the same exit race.
        }

        if (!process.HasExited)
        {
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        }
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredHost) ? "dotnet" : configuredHost;
    }

    private static string ResolveTestHostPath()
    {
        var path = typeof(SingleInstanceProcessBoundaryTests)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute =>
                string.Equals(
                    attribute.Key,
                    "SingleInstanceTestHostPath",
                    StringComparison.Ordinal))
            .Value;
        return File.Exists(path)
            ? path!
            : throw new FileNotFoundException(
                "The single-instance test host was not built.",
                path);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class TemporaryProfile : IAsyncDisposable
    {
        private TemporaryProfile(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static TemporaryProfile Create()
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "asura-single-instance-process-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TemporaryProfile(directoryPath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
