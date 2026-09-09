using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class WslConnectionRuntimeAdapter : ConnectionRuntimeAdapterBase
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
    private readonly ConnectionRuntimeOptions _options;

    public WslConnectionRuntimeAdapter(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner,
        ConnectionRuntimeOptions options,
        IConnectionCredentialBroker? credentialBroker = null)
        : base(secretVault, executableLocator, commandRunner, credentialBroker)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override ConnectionKind Kind => ConnectionKind.Wsl;

    public override async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await BuildPlanAsync(profile, progress, cancellationToken).ConfigureAwait(false);
        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Success success)
        {
            result = await PrepareCredentialLaunchAsync(success.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Success)
        {
            Report(progress, ConnectionProgressStage.Completed);
        }

        return result;
    }

    public override async ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var planResult = await BuildPlanAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        if (planResult is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
        {
            return ConnectionRuntimeResult<ConnectionTestReport>.Fail(failure.Error);
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)planResult).Value;
        var endpoint = (ConnectionEndpoint.Wsl)profile.Endpoint;
        Report(progress, ConnectionProgressStage.ProbingEndpoint);
        var probe = await RunProbeAsync(
                new ConnectionProbeCommand(
                    plan.Launch.Executable!,
                    BuildProbeArguments(profile, endpoint),
                    ProbeTimeout),
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.Outcome != ConnectionProbeOutcome.Exited || probe.ExitCode != 0)
        {
            return ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                MapProbeFailure(probe, Kind));
        }

        Report(progress, ConnectionProgressStage.Completed);
        return ConnectionRuntimeResult<ConnectionTestReport>.Succeed(new ConnectionTestReport(
            profile.Id,
            Kind,
            ConnectionTestVerification.DistributionReachable,
            true));
    }

    private async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> BuildPlanAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Report(progress, ConnectionProgressStage.ValidatingProfile);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<ConnectionOpenPlan>();
        }

        if (profile.Endpoint is not ConnectionEndpoint.Wsl endpoint)
        {
            return Invalid();
        }

        if (_options.Platform != ConnectionHostPlatform.Windows)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.UnsupportedPlatform));
        }

        Report(progress, ConnectionProgressStage.DetectingRuntime);
        var executable = ExecutableLocator.Find("wsl.exe");
        if (executable is null)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.RuntimeMissing));
        }

        var secretResult = await PreflightSecretsAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        if (secretResult is ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Failure secretFailure)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(secretFailure.Error);
        }

        var requirements = ((ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>.Success)
            secretResult).Value;
        Report(progress, ConnectionProgressStage.BuildingLaunchPlan);
        try
        {
            var launch = new TerminalLaunchRequest(
                null,
                executable,
                BuildOpenArguments(profile, endpoint),
                BuildEnvironment(profile),
                connectionId: profile.Id,
                connectionMetadata: ConnectionMetadata(profile),
                initialCommand: profile.Startup.Command);
            var plan = new ConnectionOpenPlan(
                profile.Id,
                Kind,
                launch,
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.BoundedBackoff,
                requirements,
                Warnings(requirements));
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(plan);
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static IReadOnlyList<string> BuildOpenArguments(
        ConnectionProfile profile,
        ConnectionEndpoint.Wsl endpoint) =>
        Array.AsReadOnly(BuildPrefixArguments(profile, endpoint).ToArray());

    private static IReadOnlyList<string> BuildProbeArguments(
        ConnectionProfile profile,
        ConnectionEndpoint.Wsl endpoint)
    {
        var arguments = BuildPrefixArguments(profile, endpoint);
        arguments.Add("--exec");
        arguments.Add("/bin/true");
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static List<string> BuildPrefixArguments(
        ConnectionProfile profile,
        ConnectionEndpoint.Wsl endpoint)
    {
        var arguments = new List<string> { "--distribution", endpoint.Distribution };
        if (endpoint.Username is not null)
        {
            arguments.Add("--user");
            arguments.Add(endpoint.Username);
        }

        if (profile.Startup.Directory is not null)
        {
            arguments.Add("--cd");
            arguments.Add(profile.Startup.Directory);
        }

        return arguments;
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(ConnectionProfile profile)
    {
        var environment = PlainEnvironment(profile).ToDictionary(StringComparer.Ordinal);
        var forwardedNames = environment.Keys
            .Where(name => !string.Equals(name, "WSLENV", StringComparison.Ordinal))
            .ToArray();
        if (forwardedNames.Length == 0)
        {
            return new ReadOnlyDictionary<string, string>(environment);
        }

        environment.TryGetValue("WSLENV", out var existing);
        var segments = string.IsNullOrWhiteSpace(existing)
            ? forwardedNames
            : [existing, .. forwardedNames];
        environment["WSLENV"] = string.Join(':', segments);
        return new ReadOnlyDictionary<string, string>(environment);
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Invalid() =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile));
}
