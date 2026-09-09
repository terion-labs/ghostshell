using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class DockerConnectionRuntimeAdapter : ConnectionRuntimeAdapterBase
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    public DockerConnectionRuntimeAdapter(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner,
        IConnectionCredentialBroker? credentialBroker = null)
        : base(secretVault, executableLocator, commandRunner, credentialBroker)
    {
    }

    public override ConnectionKind Kind => ConnectionKind.Docker;

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
        var endpoint = (ConnectionEndpoint.Docker)profile.Endpoint;
        Report(progress, ConnectionProgressStage.ProbingEndpoint);
        var probe = await RunProbeAsync(
                new ConnectionProbeCommand(
                    plan.Launch.Executable!,
                    BuildProbeArguments(endpoint),
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
            ConnectionTestVerification.ContainerReachable,
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

        if (profile.Endpoint is not ConnectionEndpoint.Docker endpoint)
        {
            return Invalid();
        }

        Report(progress, ConnectionProgressStage.DetectingRuntime);
        var executable = ExecutableLocator.Find("docker");
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
                PlainEnvironment(profile),
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
        ConnectionEndpoint.Docker endpoint)
    {
        var arguments = Prefix(endpoint);
        arguments.Add("exec");
        arguments.Add("--interactive");
        arguments.Add("--tty");
        if (profile.Startup.Directory is not null)
        {
            arguments.Add("--workdir");
            arguments.Add(profile.Startup.Directory);
        }

        foreach (var variable in profile.Startup.Environment)
        {
            arguments.Add("--env");
            arguments.Add(variable.Name);
        }

        arguments.Add("--");
        arguments.Add(endpoint.Container);
        arguments.Add("/bin/sh");
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static IReadOnlyList<string> BuildProbeArguments(ConnectionEndpoint.Docker endpoint)
    {
        var arguments = Prefix(endpoint);
        arguments.Add("exec");
        arguments.Add("--");
        arguments.Add(endpoint.Container);
        arguments.Add("/bin/true");
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static List<string> Prefix(ConnectionEndpoint.Docker endpoint)
    {
        var arguments = new List<string>();
        if (endpoint.Context is not null)
        {
            arguments.Add("--context");
            arguments.Add(endpoint.Context);
        }

        return arguments;
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Invalid() =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile));
}
