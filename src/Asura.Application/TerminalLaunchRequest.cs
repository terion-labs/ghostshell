using System.Collections.ObjectModel;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A validated, immutable description of the process and connection identity
/// hosted by a terminal session.
/// </summary>
public sealed record TerminalLaunchRequest
{
    private static readonly IReadOnlyList<string> EmptyArguments = Array.AsReadOnly(Array.Empty<string>());
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    public TerminalLaunchRequest(
        string? workingDirectory,
        string? executable = null,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TerminalRenderProfileSnapshot? renderProfile = null,
        TerminalKeymapSnapshot? keymap = null,
        ConnectionId? connectionId = null,
        TerminalConnectionMetadata? connectionMetadata = null,
        string? initialCommand = null,
        TerminalShellActivityFallback shellActivityFallback =
            TerminalShellActivityFallback.None,
        TerminalMultiplexerSession? multiplexerSession = null)
    {
        ValidateText(workingDirectory, nameof(workingDirectory));
        ValidateText(executable, nameof(executable));
        if (initialCommand is not null
            && (string.IsNullOrWhiteSpace(initialCommand)
                || initialCommand.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "An initial command must be a single non-empty line without control characters.",
                nameof(initialCommand));
        }
        TerminalConnectionMetadata.ValidateConnectionId(
            connectionId,
            nameof(connectionId));
        if (!Enum.IsDefined(shellActivityFallback))
        {
            throw new ArgumentOutOfRangeException(
                nameof(shellActivityFallback),
                shellActivityFallback,
                "The terminal shell-activity fallback is not recognized.");
        }

        if (executable is not null && string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("An explicit terminal executable cannot be empty.", nameof(executable));
        }

        var argumentSnapshot = SnapshotArguments(arguments);
        if (executable is null && argumentSnapshot.Count > 0)
        {
            throw new ArgumentException(
                "Terminal arguments require an explicit executable.",
                nameof(arguments));
        }

        WorkingDirectory = workingDirectory;
        Executable = executable;
        Arguments = argumentSnapshot;
        Environment = SnapshotEnvironment(environment);
        RenderProfile = renderProfile;
        Keymap = keymap;
        ConnectionId = connectionId;
        ConnectionMetadata = connectionMetadata;
        InitialCommand = initialCommand;
        ShellActivityFallback = shellActivityFallback;
        MultiplexerSession = multiplexerSession;
    }

    public string? WorkingDirectory { get; }

    public string? Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public TerminalRenderProfileSnapshot? RenderProfile { get; }

    public TerminalKeymapSnapshot? Keymap { get; }

    /// <summary>
    /// Identifies the durable connection that produced this launch, when one exists.
    /// </summary>
    public ConnectionId? ConnectionId { get; }

    /// <summary>
    /// Bounded non-secret connection and initial-directory presentation produced by
    /// the trusted connection adapter. It is never derived from model output.
    /// </summary>
    public TerminalConnectionMetadata? ConnectionMetadata { get; }

    /// <summary>
    /// A single line the session types into the terminal (followed by Enter)
    /// once the shell starts. It executes inside the interactive shell, so the
    /// shell remains open after it finishes.
    /// </summary>
    public string? InitialCommand { get; }

    public TerminalShellActivityFallback ShellActivityFallback { get; }

    public TerminalMultiplexerSession? MultiplexerSession { get; }

    public TerminalLaunchRequest WithPresentationProfiles(
        TerminalRenderProfileSnapshot? renderProfile,
        TerminalKeymapSnapshot? keymap) =>
        new(
            WorkingDirectory,
            Executable,
            Arguments,
            Environment,
            renderProfile,
            keymap,
            ConnectionId,
            ConnectionMetadata,
            InitialCommand,
            ShellActivityFallback,
            MultiplexerSession);

    public TerminalLaunchRequest WithShellActivityFallback(
        TerminalShellActivityFallback fallback) =>
        new(
            WorkingDirectory,
            Executable,
            Arguments,
            Environment,
            RenderProfile,
            Keymap,
            ConnectionId,
            ConnectionMetadata,
            InitialCommand,
            fallback,
            MultiplexerSession);

    private static IReadOnlyList<string> SnapshotArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return EmptyArguments;
        }

        var snapshot = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index]
                ?? throw new ArgumentException("Terminal arguments cannot contain null values.", nameof(arguments));
            ValidateText(argument, nameof(arguments));
            snapshot[index] = argument;
        }

        return Array.AsReadOnly(snapshot);
    }

    private static IReadOnlyDictionary<string, string> SnapshotEnvironment(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
        {
            return EmptyEnvironment;
        }

        var snapshot = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        foreach (var (name, value) in environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Environment variable names cannot be empty or contain '='.",
                    nameof(environment));
            }

            ValidateText(name, nameof(environment));
            if (value is null)
            {
                throw new ArgumentException("Environment variable values cannot be null.", nameof(environment));
            }

            ValidateText(value, nameof(environment));
            snapshot.Add(name, value);
        }

        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static void ValidateText(string? value, string parameterName)
    {
        if (value?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Terminal launch values cannot contain NUL characters.", parameterName);
        }
    }
}
