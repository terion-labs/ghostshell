using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Entry point for the short-lived connection helper and OpenSSH askpass child modes. It emits
/// only fixed diagnostics and never includes process arguments, secret handles, or secret values.
/// </summary>
public static class ConnectionCredentialProcessHost
{
    internal const string AskpassPipeEnvironment = "ASURA_SSH_ASKPASS_PIPE";
    internal const string AskpassTokenEnvironment = "ASURA_SSH_ASKPASS_TOKEN";
    internal const string AskpassRoleEnvironment = "ASURA_SSH_ASKPASS_ROLE";
    internal const string AskpassTimeoutEnvironment = "ASURA_SSH_ASKPASS_TIMEOUT_MS";
    private const int InvalidInvocationExitCode = 64;
    private const int CredentialUnavailableExitCode = 74;
    private const int ProcessFailureExitCode = 70;

    /// <summary>
    /// Identifies the two private, short-lived process modes that must run
    /// without loading the desktop UI or its CEF runtime. The marker is
    /// deliberately position-sensitive so a normal Chromium subprocess with
    /// unrelated later arguments remains a Chromium subprocess.
    /// </summary>
    public static bool IsPrivateHelperInvocation(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return IsPrivateHelperInvocation(
            arguments,
            Environment.GetEnvironmentVariable(AskpassPipeEnvironment)
                is not null);
    }

    internal static bool IsPrivateHelperInvocation(
        IReadOnlyList<string> arguments,
        bool hasAskpassPipe)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return hasAskpassPipe
            || arguments.Count > 0
            && string.Equals(
                arguments[0],
                ConnectionCredentialSessionInvocation.Marker,
                StringComparison.Ordinal);
    }

    public static async ValueTask<int?> TryRunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Environment.GetEnvironmentVariable(AskpassPipeEnvironment) is not null)
        {
            return await RunAskpassAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        var invocation = ConnectionCredentialSessionInvocation.Parse(arguments);
        if (invocation is null)
        {
            return arguments.Count > 0
                && string.Equals(
                    arguments[0],
                    ConnectionCredentialSessionInvocation.Marker,
                    StringComparison.Ordinal)
                ? InvalidInvocationExitCode
                : null;
        }

        return await RunSessionAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<int> RunSessionAsync(
        ConnectionCredentialSessionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var claimResult = await ConnectionCredentialBrokerClient.ClaimAsync(
                invocation.Access,
                invocation.ConnectTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (claimResult is not ConnectionCredentialClaimResult.Success success)
        {
            await SecretSafeDiagnosticProjection.WriteStandardErrorAsync(
                    "connection-credential.claim.failed",
                    SecretSafeDiagnosticKind.Unexpected)
                .ConfigureAwait(false);
            return CredentialUnavailableExitCode;
        }

        using var claim = success.Claim;
        EphemeralPrivateKeyFile? privateKeyFile = null;
        ConnectionCredentialAskpassServer? askpass = null;
        var secretEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in claim.TakeEnvironment())
            {
                using (entry.Material)
                {
                    if (entry.EnvironmentVariableName is null
                        || !IsEnvironmentName(entry.EnvironmentVariableName)
                        || !secretEnvironment.TryAdd(
                            entry.EnvironmentVariableName,
                            DecodeEnvironmentValue(entry.Material)))
                    {
                        await SecretSafeDiagnosticProjection.WriteStandardErrorAsync(
                                "connection-credential.invalid",
                                SecretSafeDiagnosticKind.Unexpected)
                            .ConfigureAwait(false);
                        return CredentialUnavailableExitCode;
                    }
                }
            }

            switch (invocation.Authentication)
            {
                case ConnectionAuthenticationMode.Password:
                    {
                        var password = claim.Take(ConnectionSecretRole.Password);
                        if (password is null)
                        {
                            return CredentialUnavailableExitCode;
                        }

                        try
                        {
                            askpass = ConnectionCredentialAskpassServer.Create(
                                password,
                                ConnectionCredentialAskpassRole.Password);
                        }
                        catch
                        {
                            password.Dispose();
                            throw;
                        }

                        break;
                    }
                case ConnectionAuthenticationMode.PrivateKey:
                case ConnectionAuthenticationMode.PrivateKeyWithPassphrase:
                    {
                        using var privateKey = claim.Take(ConnectionSecretRole.PrivateKey);
                        if (privateKey is null)
                        {
                            return CredentialUnavailableExitCode;
                        }

                        privateKeyFile = await EphemeralPrivateKeyFile.CreateAsync(
                                privateKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (invocation.Authentication == ConnectionAuthenticationMode.PrivateKeyWithPassphrase)
                        {
                            var passphrase = claim.Take(ConnectionSecretRole.PrivateKeyPassphrase);
                            if (passphrase is null)
                            {
                                return CredentialUnavailableExitCode;
                            }

                            try
                            {
                                askpass = ConnectionCredentialAskpassServer.Create(
                                    passphrase,
                                    ConnectionCredentialAskpassRole.PrivateKeyPassphrase);
                            }
                            catch
                            {
                                passphrase.Dispose();
                                throw;
                            }
                        }

                        break;
                    }
                case ConnectionAuthenticationMode.None:
                case ConnectionAuthenticationMode.SshAgent:
                    break;
                default:
                    return InvalidInvocationExitCode;
            }

            if (claim.Entries.Count > 0)
            {
                return CredentialUnavailableExitCode;
            }

            var startInfo = BuildStartInfo(
                invocation,
                secretEnvironment,
                privateKeyFile?.Path,
                askpass?.Access);
            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                process = null;
            }
            finally
            {
                ClearSensitiveEnvironment(startInfo, secretEnvironment.Keys);
                secretEnvironment.Clear();
            }

            if (process is null)
            {
                await SecretSafeDiagnosticProjection.WriteStandardErrorAsync(
                        "connection-credential.process-start.failed",
                        SecretSafeDiagnosticKind.Unexpected)
                    .ConfigureAwait(false);
                return ProcessFailureExitCode;
            }

            using (process)
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    return process.ExitCode;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CredentialUnavailableExitCode;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            await SecretSafeDiagnosticProjection.WriteStandardErrorAsync(
                    "connection-credential.prepare.failed",
                    SecretSafeDiagnosticKind.Unexpected)
                .ConfigureAwait(false);
            return CredentialUnavailableExitCode;
        }
        finally
        {
            foreach (var name in secretEnvironment.Keys.ToArray())
            {
                secretEnvironment[name] = string.Empty;
            }

            secretEnvironment.Clear();
            if (askpass is not null)
            {
                await askpass.DisposeAsync().ConfigureAwait(false);
            }

            if (privateKeyFile is not null)
            {
                await privateKeyFile.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(
        ConnectionCredentialSessionInvocation invocation,
        IReadOnlyDictionary<string, string> secretEnvironment,
        string? privateKeyPath,
        ConnectionCredentialAskpassAccess? askpass,
        SelfReentryLaunch? selfReentry = null)
    {
        var arguments = invocation.Arguments.ToList();
        if (privateKeyPath is not null)
        {
            if (invocation.Kind != ConnectionKind.Ssh)
            {
                throw new InvalidOperationException("Only SSH connections accept a private key.");
            }

            var destinationBoundary = arguments.IndexOf("--");
            if (destinationBoundary < 0)
            {
                throw new InvalidDataException("The SSH launch has no destination boundary.");
            }

            arguments.InsertRange(
                destinationBoundary,
                ["-o", "IdentityAgent=none", "-i", privateKeyPath]);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in secretEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        if (invocation.Kind == ConnectionKind.Wsl)
        {
            AddWslEnvironmentForwarding(startInfo, secretEnvironment.Keys);
        }

        if (askpass is not null)
        {
            selfReentry ??= SelfReentryLaunch.Detect();

            startInfo.Environment["SSH_ASKPASS"] = selfReentry.AskpassExecutable;
            foreach (var (name, value) in selfReentry.AskpassEnvironment)
            {
                startInfo.Environment[name] = value;
            }

            startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            startInfo.Environment["DISPLAY"] = "asura-askpass";
            startInfo.Environment[AskpassPipeEnvironment] = askpass.PipeName;
            startInfo.Environment[AskpassTokenEnvironment] = askpass.Token;
            startInfo.Environment[AskpassRoleEnvironment] = askpass.Role.ToString();
            startInfo.Environment[AskpassTimeoutEnvironment] = "10000";
        }

        return startInfo;
    }

    private static async ValueTask<int> RunAskpassAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var pipe = Environment.GetEnvironmentVariable(AskpassPipeEnvironment);
        var token = Environment.GetEnvironmentVariable(AskpassTokenEnvironment);
        var roleText = Environment.GetEnvironmentVariable(AskpassRoleEnvironment);
        var timeoutText = Environment.GetEnvironmentVariable(AskpassTimeoutEnvironment);
        if (arguments.Count != 1
            || string.IsNullOrWhiteSpace(pipe)
            || string.IsNullOrWhiteSpace(token)
            || !Enum.TryParse<ConnectionCredentialAskpassRole>(roleText, out var role)
            || !Enum.IsDefined(role)
            || !int.TryParse(timeoutText, System.Globalization.CultureInfo.InvariantCulture, out var timeoutMilliseconds) || timeoutMilliseconds is <= 0 or > 60_000
            || !PromptMatches(role, arguments[0]))
        {
            return InvalidInvocationExitCode;
        }

        var material = await ConnectionCredentialAskpassServer.ClaimAsync(
                new ConnectionCredentialAskpassAccess(pipe, token, role),
                TimeSpan.FromMilliseconds(timeoutMilliseconds),
                cancellationToken)
            .ConfigureAwait(false);
        if (material is null)
        {
            return CredentialUnavailableExitCode;
        }

        using (material)
        {
            var value = new byte[material.Length + 1];
            try
            {
                material.CopyTo(value);
                value[^1] = (byte)'\n';
                var output = Console.OpenStandardOutput();
                await output.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private static string DecodeEnvironmentValue(SecretMaterial material)
    {
        const int maximumEnvironmentValueBytes = 32 * 1024;
        if (material.Length > maximumEnvironmentValueBytes)
        {
            throw new InvalidDataException("A connection environment credential is too large.");
        }

        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            if (bytes.AsSpan().Contains((byte)'\0'))
            {
                throw new InvalidDataException("A connection environment credential is invalid.");
            }

            return new UTF8Encoding(false, true).GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsEnvironmentName(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsAsciiLetter(value[0])))
        {
            return false;
        }

        return value.Skip(1).All(character =>
            character == '_' || char.IsAsciiLetterOrDigit(character));
    }

    private static void AddWslEnvironmentForwarding(
        ProcessStartInfo startInfo,
        IEnumerable<string> names)
    {
        startInfo.Environment.TryGetValue("WSLENV", out var existing);
        var segments = string.IsNullOrWhiteSpace(existing)
            ? new List<string>()
            : [.. existing.Split(':', StringSplitOptions.RemoveEmptyEntries)];
        var forwarded = segments
            .Select(segment => segment.Split('/', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in names.Where(name => !string.Equals(name, "WSLENV", StringComparison.Ordinal)))
        {
            if (forwarded.Add(name))
            {
                segments.Add(name);
            }
        }

        if (segments.Count > 0)
        {
            startInfo.Environment["WSLENV"] = string.Join(':', segments);
        }
    }

    internal static bool PromptMatches(ConnectionCredentialAskpassRole role, string prompt) =>
        Enum.IsDefined(role)
        && !string.IsNullOrWhiteSpace(prompt)
        && prompt.Length <= 4 * 1024
        && prompt.AsSpan().IndexOfAny('\0', '\r', '\n') < 0;

    private static void ClearSensitiveEnvironment(
        ProcessStartInfo startInfo,
        IEnumerable<string> secretNames)
    {
        foreach (var name in secretNames)
        {
            startInfo.Environment[name] = string.Empty;
        }

        startInfo.Environment.Remove(AskpassTokenEnvironment);
        startInfo.Environment.Remove(AskpassPipeEnvironment);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }
    }

}
