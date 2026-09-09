using System.Globalization;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal sealed record ConnectionCredentialSessionInvocation(
    ConnectionCredentialBrokerAccess Access,
    ConnectionKind Kind,
    ConnectionAuthenticationMode Authentication,
    TimeSpan ConnectTimeout,
    string Executable,
    IReadOnlyList<string> Arguments)
{
    public const string Marker = "--asura-connection-credential-session-v1";
    private const int MetadataArgumentCount = 9;

    public static TerminalLaunchRequest CreateHelperLaunch(
        SelfReentryLaunch selfReentry,
        ConnectionCredentialBrokerAccess access,
        ConnectionCredentialBrokerRequest request,
        TimeSpan connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(selfReentry);
        var arguments = new List<string>(
            selfReentry.PrefixArguments.Count + MetadataArgumentCount + request.Launch.Arguments.Count);
        arguments.AddRange(selfReentry.PrefixArguments);
        arguments.AddRange(
        [
            Marker,
            access.PipeName,
            access.TicketId,
            access.Token,
            access.ConnectionId.Value,
            ((int)request.Kind).ToString(CultureInfo.InvariantCulture),
            ((int)request.Authentication).ToString(CultureInfo.InvariantCulture),
            checked((int)connectTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            request.Launch.Executable!,
        ]);
        arguments.AddRange(request.Launch.Arguments);
        return new TerminalLaunchRequest(
            request.Launch.WorkingDirectory,
            selfReentry.Executable,
            arguments,
            request.Launch.Environment,
            request.Launch.RenderProfile,
            request.Launch.Keymap,
            request.ConnectionId,
            request.Launch.ConnectionMetadata,
            request.Launch.InitialCommand,
            multiplexerSession: request.Launch.MultiplexerSession);
    }

    public static ConnectionCredentialSessionInvocation? Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < MetadataArgumentCount
            || !string.Equals(arguments[0], Marker, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1])
            || string.IsNullOrWhiteSpace(arguments[2])
            || string.IsNullOrWhiteSpace(arguments[3])
            || string.IsNullOrWhiteSpace(arguments[4])
            || string.IsNullOrWhiteSpace(arguments[8])
            || !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var kindValue)
            || !int.TryParse(
                arguments[6],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var authenticationValue)
            || !int.TryParse(
                arguments[7],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var connectTimeoutMilliseconds)
            || connectTimeoutMilliseconds is <= 0 or > 60_000)
        {
            return null;
        }

        var kind = (ConnectionKind)kindValue;
        var authentication = (ConnectionAuthenticationMode)authenticationValue;
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(authentication))
        {
            return null;
        }

        try
        {
            var access = new ConnectionCredentialBrokerAccess(
                arguments[1],
                arguments[2],
                arguments[3],
                new ConnectionId(arguments[4]));
            return new ConnectionCredentialSessionInvocation(
                access,
                kind,
                authentication,
                TimeSpan.FromMilliseconds(connectTimeoutMilliseconds),
                arguments[8],
                Array.AsReadOnly(arguments.Skip(MetadataArgumentCount).ToArray()));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static ConnectionCredentialSessionInvocation? ParsePreparedLaunch(
        TerminalLaunchRequest launch,
        SelfReentryLaunch selfReentry)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(selfReentry);
        if (!string.Equals(launch.Executable, selfReentry.Executable, StringComparison.Ordinal)
            || launch.Arguments.Count < selfReentry.PrefixArguments.Count
            || !launch.Arguments
                .Take(selfReentry.PrefixArguments.Count)
                .SequenceEqual(selfReentry.PrefixArguments, StringComparer.Ordinal))
        {
            return null;
        }

        return Parse([.. launch.Arguments.Skip(selfReentry.PrefixArguments.Count)]);
    }

    public override string ToString() =>
        $"Credential session ({Kind}, {Authentication}, {Arguments.Count} executable arguments)";
}
