using System.Collections.ObjectModel;

namespace Asura.Mcp;

internal sealed class McpStdioServerLaunch
{
    private readonly Dictionary<string, string> _environment;

    public McpStdioServerLaunch(
        string executable,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "The MCP executable must be an absolute path without null characters.",
                nameof(executable));
        }

        Executable = executable;
        Arguments = Array.AsReadOnly((arguments ?? [])
            .Select(argument =>
            {
                ArgumentNullException.ThrowIfNull(argument);
                if (argument.IndexOf('\0') >= 0)
                {
                    throw new ArgumentException(
                        "MCP arguments cannot contain null characters.",
                        nameof(arguments));
                }

                return argument;
            })
            .ToArray());

        if (workingDirectory is not null
            && (!Path.IsPathFullyQualified(workingDirectory)
                || workingDirectory.IndexOf('\0') >= 0))
        {
            throw new ArgumentException(
                "The MCP working directory must be an absolute path without null characters.",
                nameof(workingDirectory));
        }

        WorkingDirectory = workingDirectory
            ?? Path.GetDirectoryName(executable)
            ?? Path.GetPathRoot(executable)
            ?? throw new ArgumentException(
                "The MCP executable does not have a usable directory.",
                nameof(executable));
        _environment = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var pair in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Contains('=', StringComparison.Ordinal)
                || pair.Key.IndexOf('\0') >= 0
                || pair.Value is null
                || pair.Value.IndexOf('\0') >= 0)
            {
                throw new ArgumentException(
                    "MCP environment entries must be resolved non-null name/value pairs.",
                    nameof(environment));
            }

            _environment.Add(pair.Key, pair.Value);
        }

        Environment = new ReadOnlyDictionary<string, string>(_environment);
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    /// <summary>
    /// Exact already-resolved values supplied to the child. This boundary does
    /// not know about secret references and does not inherit ambient variables.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    public override string ToString() => "MCP stdio server launch";

    internal void ForgetEnvironment() => _environment.Clear();
}
