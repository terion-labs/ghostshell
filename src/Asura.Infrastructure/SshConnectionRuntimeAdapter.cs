using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class SshConnectionRuntimeAdapter : ConnectionRuntimeAdapterBase
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
    private readonly SshKnownHostStore? _knownHosts;

    public SshConnectionRuntimeAdapter(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner)
        : this(secretVault, executableLocator, commandRunner, null)
    {
    }

    public SshConnectionRuntimeAdapter(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner,
        SshKnownHostStore? knownHosts,
        IConnectionCredentialBroker? credentialBroker = null)
        : base(secretVault, executableLocator, commandRunner, credentialBroker)
    {
        _knownHosts = knownHosts;
    }

    public override ConnectionKind Kind => ConnectionKind.Ssh;

    public override async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
        => await PlanOpenAsync(profile, null, progress, cancellationToken).ConfigureAwait(false);

    public override async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await BuildPlanAsync(profile, multiplexerSession, progress, cancellationToken)
            .ConfigureAwait(false);
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
        var planResult = await BuildPlanAsync(profile, null, progress, cancellationToken)
            .ConfigureAwait(false);
        if (planResult is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
        {
            return ConnectionRuntimeResult<ConnectionTestReport>.Fail(failure.Error);
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)planResult).Value;
        if (plan.Authentication is ConnectionAuthenticationMode.Password
            or ConnectionAuthenticationMode.PrivateKey
            or ConnectionAuthenticationMode.PrivateKeyWithPassphrase)
        {
            Report(progress, ConnectionProgressStage.Completed);
            return ConnectionRuntimeResult<ConnectionTestReport>.Succeed(new ConnectionTestReport(
                profile.Id,
                Kind,
                ConnectionTestVerification.ConfigurationValidated,
                false));
        }

        Report(progress, ConnectionProgressStage.ProbingEndpoint);
        var endpoint = (ConnectionEndpoint.Ssh)profile.Endpoint;
        SshKnownHostBinding? knownHostBinding;
        try
        {
            knownHostBinding = await ResolveKnownHostBindingAsync(profile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<ConnectionTestReport>();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed));
        }
        var probe = await RunProbeAsync(
                new ConnectionProbeCommand(
                    plan.Launch.Executable!,
                    SshConnectionArguments.Probe(profile, endpoint, knownHostBinding),
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
            ConnectionTestVerification.EndpointAuthenticated,
            true));
    }

    private async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> BuildPlanAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Report(progress, ConnectionProgressStage.ValidatingProfile);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<ConnectionOpenPlan>();
        }

        if (profile.Endpoint is not ConnectionEndpoint.Ssh endpoint
            || !HasValidKeepAlive(profile.KeepAlive))
        {
            return Invalid();
        }

        Report(progress, ConnectionProgressStage.DetectingRuntime);
        var executable = ExecutableLocator.Find("ssh");
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
            var extraWarnings = new List<ConnectionPlanWarning>();
            if (profile.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore)
            {
                extraWarnings.Add(ConnectionPlanWarning.HostKeyVerificationDisabled);
            }

            if (profile.Startup.Environment.Count > 0)
            {
                extraWarnings.Add(ConnectionPlanWarning.RemoteEnvironmentRequiresServerAcceptance);
            }

            if (profile.Startup.Directory is not null)
            {
                extraWarnings.Add(ConnectionPlanWarning.SshStartupDirectoryRequiresPosixShell);
            }

            var launch = new TerminalLaunchRequest(
                null,
                executable,
                SshConnectionArguments.Open(
                    profile,
                    endpoint,
                    await ResolveKnownHostBindingAsync(profile, cancellationToken).ConfigureAwait(false),
                    multiplexerSession),
                PlainEnvironment(profile),
                connectionId: profile.Id,
                connectionMetadata: ConnectionMetadata(profile),
                initialCommand: profile.Startup.Command,
                multiplexerSession: multiplexerSession);
            var plan = new ConnectionOpenPlan(
                profile.Id,
                Kind,
                launch,
                AuthenticationMode(profile.Authentication),
                profile.HostKeyPolicy,
                ConnectionReconnectMode.BoundedBackoff,
                requirements,
                Warnings(requirements, [.. extraWarnings]));
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(plan);
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
        catch (OperationCanceledException)
        {
            return Cancelled<ConnectionOpenPlan>();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed));
        }
        catch (InvalidOperationException)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.AdapterUnavailable));
        }
    }

    private static bool HasValidKeepAlive(ConnectionKeepAlive keepAlive) =>
        !keepAlive.Enabled
        || keepAlive.Interval.TotalSeconds <= int.MaxValue;

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Invalid() =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile));

    private ValueTask<SshKnownHostBinding?> ResolveKnownHostBindingAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore)
        {
            return ValueTask.FromResult<SshKnownHostBinding?>(null);
        }

        if (_knownHosts is null)
        {
            throw new InvalidOperationException(
                "Verified SSH launch requires the per-connection known-host store.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<SshKnownHostBinding?>(_knownHosts.Binding(profile.Id));
    }
}
