using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class LocalConnectionRuntimeAdapter : ConnectionRuntimeAdapterBase
{
    private static readonly IReadOnlyList<string> LoginShellArguments =
        Array.AsReadOnly(["-l"]);
    private readonly ConnectionRuntimeOptions _options;

    public LocalConnectionRuntimeAdapter(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner,
        ConnectionRuntimeOptions options,
        IConnectionCredentialBroker? credentialBroker = null)
        : base(secretVault, executableLocator, commandRunner, credentialBroker)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override ConnectionKind Kind => ConnectionKind.Local;

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
        var plan = await BuildPlanAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        if (plan is ConnectionRuntimeResult<ConnectionOpenPlan>.Success)
        {
            Report(progress, ConnectionProgressStage.Completed);
        }

        return plan switch
        {
            ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure =>
                ConnectionRuntimeResult<ConnectionTestReport>.Fail(failure.Error),
            ConnectionRuntimeResult<ConnectionOpenPlan>.Success =>
                ConnectionRuntimeResult<ConnectionTestReport>.Succeed(new ConnectionTestReport(
                    profile.Id,
                    Kind,
                    ConnectionTestVerification.RuntimeAvailable,
                    false)),
            _ => throw new InvalidOperationException("The connection plan result is invalid."),
        };
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

        if (profile.Endpoint is not ConnectionEndpoint.Local endpoint)
        {
            return Invalid();
        }

        var shellCandidate = endpoint.ShellPath ?? _options.DefaultShell;
        if (string.IsNullOrWhiteSpace(shellCandidate))
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.UnsupportedPlatform));
        }

        Report(progress, ConnectionProgressStage.DetectingRuntime);
        string? executable;
        try
        {
            executable = ExecutableLocator.Find(shellCandidate);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return Invalid();
        }

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
            var workingDirectory = profile.Startup.Directory
                ?? _options.UserProfileDirectory;
            var arguments = _options.Platform is
                ConnectionHostPlatform.MacOs or ConnectionHostPlatform.Linux
                    ? LoginShellArguments
                    : [];
            var launch = new TerminalLaunchRequest(
                workingDirectory,
                executable,
                arguments,
                PlainEnvironment(profile),
                connectionId: profile.Id,
                connectionMetadata: ConnectionMetadata(profile, workingDirectory),
                initialCommand: profile.Startup.Command);
            var plan = new ConnectionOpenPlan(
                profile.Id,
                Kind,
                launch,
                ConnectionAuthenticationMode.None,
                SshHostKeyPolicy.NotApplicable,
                ConnectionReconnectMode.NotApplicable,
                requirements,
                Warnings(requirements));
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(plan);
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Invalid() =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile));
}
