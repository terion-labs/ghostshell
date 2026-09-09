using System.Collections.ObjectModel;
using System.Globalization;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public abstract class ConnectionRuntimeAdapterBase : IConnectionRuntimeAdapter
{
    private readonly ConnectionSecretPreflight _secretPreflight;
    private readonly IConnectionCredentialBroker? _credentialBroker;

    protected ConnectionRuntimeAdapterBase(
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IConnectionCommandRunner commandRunner,
        IConnectionCredentialBroker? credentialBroker = null)
    {
        ArgumentNullException.ThrowIfNull(secretVault);
        _secretPreflight = new ConnectionSecretPreflight(secretVault);
        ExecutableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
        CommandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _credentialBroker = credentialBroker;
    }

    public abstract ConnectionKind Kind { get; }

    protected IConnectionExecutableLocator ExecutableLocator { get; }

    protected IConnectionCommandRunner CommandRunner { get; }

    public abstract ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    public virtual ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        PlanOpenAsync(profile, progress, cancellationToken);

    public abstract ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken);

    protected async ValueTask<ConnectionRuntimeResult<IReadOnlyList<ConnectionSecretRequirement>>>
        PreflightSecretsAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        Report(progress, ConnectionProgressStage.ResolvingCredentials);
        return await _secretPreflight.RunAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    protected async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PrepareCredentialLaunchAsync(
        ConnectionOpenPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SecretRequirements.Count == 0 || _credentialBroker is null)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(plan);
        }

        var result = await _credentialBroker.PrepareLaunchAsync(
                new ConnectionCredentialBrokerRequest(
                    plan.ConnectionId,
                    plan.Kind,
                    plan.Authentication,
                    plan.Launch,
                    plan.SecretRequirements),
                cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            ConnectionRuntimeResult<TerminalLaunchRequest>.Success success =>
                ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                    plan.WithPreparedSecretBroker(success.Value)),
            ConnectionRuntimeResult<TerminalLaunchRequest>.Failure failure =>
                ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(failure.Error),
            _ => throw new InvalidOperationException("The credential-broker result is invalid."),
        };
    }

    protected static IReadOnlyDictionary<string, string> PlainEnvironment(ConnectionProfile profile)
    {
        var values = profile.Startup.Environment
            .Where(variable => variable.Value is ConnectionEnvironmentValue.PlainText)
            .ToDictionary(
                variable => variable.Name,
                variable => ((ConnectionEnvironmentValue.PlainText)variable.Value).Value,
                StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, string>(values);
    }

    protected static TerminalConnectionMetadata ConnectionMetadata(
        ConnectionProfile profile,
        string? initialWorkingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var boundary = profile.Endpoint switch
        {
            ConnectionEndpoint.Local =>
                $"Local: {profile.Name}",
            ConnectionEndpoint.Ssh ssh =>
                $"SSH: {UserAt(ssh.Username)}{ssh.Host}:"
                + ssh.Port.ToString(CultureInfo.InvariantCulture),
            ConnectionEndpoint.Docker docker =>
                $"Docker: {docker.Context ?? "default"}/{docker.Container}",
            ConnectionEndpoint.Wsl wsl =>
                $"WSL: {UserAt(wsl.Username)}{wsl.Distribution}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.Endpoint.GetType(),
                "The connection endpoint kind is not supported."),
        };
        return new TerminalConnectionMetadata(
            boundary,
            initialWorkingDirectory ?? profile.Startup.Directory);
    }

    private static string UserAt(string? username) =>
        username is null ? string.Empty : $"{username}@";

    protected static ConnectionAuthenticationMode AuthenticationMode(
        ConnectionAuthentication authentication) => authentication switch
        {
            ConnectionAuthentication.None => ConnectionAuthenticationMode.None,
            ConnectionAuthentication.SshAgent => ConnectionAuthenticationMode.SshAgent,
            ConnectionAuthentication.Password => ConnectionAuthenticationMode.Password,
            ConnectionAuthentication.PrivateKey { PassphraseSecret: null } =>
                ConnectionAuthenticationMode.PrivateKey,
            ConnectionAuthentication.PrivateKey => ConnectionAuthenticationMode.PrivateKeyWithPassphrase,
            _ => throw new ArgumentOutOfRangeException(nameof(authentication), authentication, null),
        };

    protected static IReadOnlyList<ConnectionPlanWarning> Warnings(
        IReadOnlyList<ConnectionSecretRequirement> secretRequirements,
        params ConnectionPlanWarning[] additional)
    {
        var warnings = additional.ToList();
        if (secretRequirements.Count > 0)
        {
            warnings.Add(ConnectionPlanWarning.SecretBrokerRequired);
        }

        return Array.AsReadOnly(warnings.Distinct().ToArray());
    }

    protected static void Report(
        IProgress<ConnectionProgress>? progress,
        ConnectionProgressStage stage)
    {
        var update = stage switch
        {
            ConnectionProgressStage.ValidatingProfile =>
                new ConnectionProgress(stage, "connection_validating", "Validating connection profile."),
            ConnectionProgressStage.DetectingRuntime =>
                new ConnectionProgress(stage, "connection_detecting_runtime", "Detecting connection runtime."),
            ConnectionProgressStage.ResolvingCredentials =>
                new ConnectionProgress(stage, "connection_resolving_credentials", "Resolving scoped credentials."),
            ConnectionProgressStage.BuildingLaunchPlan =>
                new ConnectionProgress(stage, "connection_building_plan", "Building a structured launch plan."),
            ConnectionProgressStage.InspectingHostKey =>
                new ConnectionProgress(stage, "connection_inspecting_host_key", "Inspecting the remote SSH host key."),
            ConnectionProgressStage.Authenticating =>
                new ConnectionProgress(stage, "connection_authenticating", "Authenticating to the connection endpoint."),
            ConnectionProgressStage.ProbingEndpoint =>
                new ConnectionProgress(stage, "connection_probing_endpoint", "Testing the connection endpoint."),
            ConnectionProgressStage.Reconnecting =>
                new ConnectionProgress(stage, "connection_reconnecting", "Reconnecting to the endpoint."),
            ConnectionProgressStage.Completed =>
                new ConnectionProgress(stage, "connection_completed", "Connection operation completed."),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
        };
        progress?.Report(update);
    }

    protected static ConnectionRuntimeResult<T> Cancelled<T>() =>
        ConnectionRuntimeResult<T>.Fail(
            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled));

    protected static ConnectionRuntimeError MapProbeFailure(
        ConnectionProbeResult result,
        ConnectionKind kind) =>
        ConnectionProbeErrorMapper.Map(result, kind);

    protected async ValueTask<ConnectionProbeResult> RunProbeAsync(
        ConnectionProbeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ConnectionProbeResult(
                ConnectionProbeOutcome.Cancelled,
                null,
                string.Empty);
        }
    }

}
