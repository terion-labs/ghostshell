using System.Globalization;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Encodes OpenSSH policy as individual argv entries and keeps endpoint values behind an option boundary.
/// </summary>
internal static class SshConnectionArguments
{
    public static IReadOnlyList<string> Open(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding = null,
        TerminalMultiplexerSession? multiplexerSession = null)
    {
        var arguments = Common(profile, endpoint, knownHostBinding);
        AddOption(arguments, "ConnectTimeout", "12");
        foreach (var variable in profile.Startup.Environment)
        {
            AddOption(arguments, "SendEnv", variable.Name);
        }

        arguments.Add("-tt");
        AddDestination(arguments, endpoint);
        if (multiplexerSession is not null)
        {
            arguments.Add(BuildMultiplexerCommand(profile.Startup.Directory, multiplexerSession));
        }
        else if (profile.Startup.Directory is { } directory)
        {
            arguments.Add(BuildRemoteLoginCommand(directory));
        }

        return Array.AsReadOnly(arguments.ToArray());
    }

    public static IReadOnlyList<string> Probe(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding = null)
    {
        var arguments = Common(profile, endpoint, knownHostBinding);
        AddOption(arguments, "BatchMode", "yes");
        AddOption(arguments, "ConnectTimeout", "10");
        AddDestination(arguments, endpoint);
        arguments.Add("true");
        return Array.AsReadOnly(arguments.ToArray());
    }

    private static List<string> Common(
        ConnectionProfile profile,
        ConnectionEndpoint.Ssh endpoint,
        SshKnownHostBinding? knownHostBinding)
    {
        var arguments = new List<string>
        {
            "-p",
            endpoint.Port.ToString(CultureInfo.InvariantCulture),
        };
        AddOption(arguments, "StrictHostKeyChecking", profile.HostKeyPolicy switch
        {
            SshHostKeyPolicy.Strict => "yes",
            SshHostKeyPolicy.AcceptNew => "accept-new",
            SshHostKeyPolicy.InsecureIgnore => "no",
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.HostKeyPolicy,
                "SSH host-key policy is invalid."),
        });
        if (profile.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore)
        {
            AddOption(arguments, "UserKnownHostsFile", NullKnownHostsPath());
        }
        else if (knownHostBinding is not null)
        {
            AddOption(
                arguments,
                "UserKnownHostsFile",
                QuoteOpenSshOptionValue(knownHostBinding.FilePath));
            AddOption(arguments, "GlobalKnownHostsFile", NullKnownHostsPath());
            AddOption(arguments, "HostKeyAlias", knownHostBinding.Alias);
        }

        if (profile.KeepAlive.Enabled)
        {
            var seconds = (int)Math.Ceiling(profile.KeepAlive.Interval.TotalSeconds);
            AddOption(arguments, "ServerAliveInterval", seconds.ToString(CultureInfo.InvariantCulture));
            AddOption(
                arguments,
                "ServerAliveCountMax",
                profile.KeepAlive.MaximumFailures.ToString(CultureInfo.InvariantCulture));
        }

        AddAuthentication(arguments, profile.Authentication);
        return arguments;
    }

    private static void AddAuthentication(
        List<string> arguments,
        ConnectionAuthentication authentication)
    {
        switch (authentication)
        {
            case ConnectionAuthentication.None:
                // "None" means Asura has no app-managed credential. OpenSSH still applies
                // the user's system configuration, identity files, platform credential store,
                // and agent. Publish any file-backed identity it successfully uses so SDK-backed
                // SSH channels can reuse the same signing capability.
                AddOption(arguments, "AddKeysToAgent", "yes");
                return;
            case ConnectionAuthentication.SshAgent:
                AddOption(arguments, "PreferredAuthentications", "publickey");
                AddOption(arguments, "PubkeyAuthentication", "yes");
                AddOption(arguments, "PasswordAuthentication", "no");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                // Keep the system OpenSSH process as the authority for configured identities
                // and platform credential stores. Once OpenSSH authenticates, expose that same
                // signing identity through the agent so in-process SSH consumers (for example
                // SFTP) can reuse it without reading private-key material.
                AddOption(arguments, "AddKeysToAgent", "yes");
                return;
            case ConnectionAuthentication.Password:
                AddOption(arguments, "PreferredAuthentications", "password");
                AddOption(arguments, "PubkeyAuthentication", "no");
                AddOption(arguments, "PasswordAuthentication", "yes");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                AddOption(arguments, "NumberOfPasswordPrompts", "1");
                AddOption(arguments, "BatchMode", "no");
                return;
            case ConnectionAuthentication.PrivateKey:
                AddOption(arguments, "PreferredAuthentications", "publickey");
                AddOption(arguments, "PubkeyAuthentication", "yes");
                AddOption(arguments, "PasswordAuthentication", "no");
                AddOption(arguments, "KbdInteractiveAuthentication", "no");
                AddOption(arguments, "IdentitiesOnly", "yes");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(authentication), authentication, null);
        }
    }

    private static void AddDestination(
        List<string> arguments,
        ConnectionEndpoint.Ssh endpoint)
    {
        if (endpoint.Username is not null)
        {
            arguments.Add("-l");
            arguments.Add(endpoint.Username);
        }

        arguments.Add("--");
        arguments.Add(endpoint.Host);
    }

    private static void AddOption(List<string> arguments, string name, string value)
    {
        arguments.Add("-o");
        arguments.Add($"{name}={value}");
    }

    private static string QuoteOpenSshOptionValue(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("An OpenSSH option value cannot contain a line break or null character.");
        }

        var normalized = OperatingSystem.IsWindows()
            ? value.Replace('\\', '/')
            : value;
        normalized = normalized.Replace("%", "%%", StringComparison.Ordinal);
        if (!normalized.Any(character =>
                char.IsWhiteSpace(character) || character is '"' or '\\' or '#'))
        {
            return normalized;
        }

        return $"\"{normalized
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildRemoteLoginCommand(string directory)
    {
        if (directory.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "An SSH startup directory cannot contain a null character.",
                nameof(directory));
        }

        // OpenSSH joins the remote command into one string for the account's login shell.
        // The directory is passed as /bin/sh's positional parameter rather than interpolated
        // into its script, so whitespace, quotes, newlines, substitutions, and separators stay
        // data. The plan carries an explicit POSIX-target warning because Windows OpenSSH and
        // non-POSIX login shells do not guarantee this command contract.
        const string script = "cd \"$1\" && exec \"${SHELL:-/bin/sh}\" -l";
        var directoryArgument = directory.StartsWith('-')
            ? $"./{directory}"
            : directory;
        return $"exec /bin/sh -c {QuotePosixShellWord(script)} asura-startup {QuotePosixShellWord(directoryArgument)}";
    }

    private static string BuildMultiplexerCommand(
        string? directory,
        TerminalMultiplexerSession session)
    {
        if (session.Mode != TerminalMultiplexingMode.Automatic)
        {
            throw new ArgumentOutOfRangeException(nameof(session), session.Mode, null);
        }

        // An established identity may belong to Screen from an older Asura
        // build. Resume the backend that owns it; only a new identity may choose
        // tmux first and fall back to Screen when tmux is unavailable.
        var multiplexerScript = session.IsEstablished
            ? EstablishedMultiplexerScript()
            : NewMultiplexerScript(directory is not null);
        var sessionName = QuotePosixShellWord(session.SessionName);
        if (directory is null)
        {
            return $"exec /bin/sh -c {QuotePosixShellWord(multiplexerScript)} "
                + $"asura-multiplexer {sessionName}";
        }

        if (directory.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "An SSH startup directory cannot contain a null character.",
                nameof(directory));
        }

        var directoryArgument = directory.StartsWith('-')
            ? $"./{directory}"
            : directory;
        return $"exec /bin/sh -c {QuotePosixShellWord(multiplexerScript)} asura-multiplexer "
            + $"{sessionName} {QuotePosixShellWord(directoryArgument)}";
    }

    private static string EstablishedMultiplexerScript() =>
        "if command -v tmux >/dev/null 2>&1 "
        + "&& tmux -L asura has-session -t \"$1\" 2>/dev/null; then "
        + "tmux -L asura set-option -t \"$1\" status off >/dev/null 2>&1; "
        + "tmux -L asura set-option -t \"$1\" mouse on >/dev/null 2>&1; "
        + "exec tmux -L asura -u -2 attach-session -d -t \"$1\"; fi; "
        + "if command -v screen >/dev/null 2>&1; then "
        + "exec screen -A -U -D -r \"$1\"; fi; "
        + "printf \"%s\\n\" \"Asura could not find the managed tmux or Screen session.\" >&2; "
        + "exit 1";

    private static string NewMultiplexerScript(bool hasDirectory)
    {
        var tmuxDirectory = hasDirectory ? " -c \"$2\"" : string.Empty;
        var screenDirectory = hasDirectory ? "cd \"$2\" || exit $?; " : string.Empty;
        return "if command -v tmux >/dev/null 2>&1; then "
            + "if tmux -L asura has-session -t \"$1\" 2>/dev/null; then "
            + "tmux -L asura set-option -t \"$1\" status off >/dev/null 2>&1; "
            + "tmux -L asura set-option -t \"$1\" mouse on >/dev/null 2>&1; "
            + "exec tmux -L asura -u -2 attach-session -d -t \"$1\"; fi; "
            + "tmux -L asura -u -2 start-server \\; "
            + "set-option -s default-terminal tmux-256color \\; "
            + "set-option -s terminal-features \"xterm*:RGB\" \\; "
            + $"new-session -d -s \"$1\"{tmuxDirectory} \\; "
            + "set-option -t \"$1\" status off || exit $?; "
            + "tmux -L asura set-option -t \"$1\" mouse on >/dev/null 2>&1; "
            + "exec tmux -L asura -u -2 attach-session -d -t \"$1\"; fi; "
            + "if command -v screen >/dev/null 2>&1; then "
            + screenDirectory
            + "exec screen -A -U -D -RR -S \"$1\"; fi; "
            + "printf \"%s\\n\" \"Asura terminal continuity requires tmux or GNU Screen.\" >&2; "
            + "exit 127";
    }

    private static string QuotePosixShellWord(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string NullKnownHostsPath() =>
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
}
