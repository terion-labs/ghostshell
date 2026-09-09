namespace Asura.Core;

public sealed record ConnectionStartup
{
    public ConnectionStartup(
        string? directory = null,
        IReadOnlyList<ConnectionEnvironmentVariable>? environment = null,
        string? command = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        Command = NormalizeCommand(command);
        Environment = Array.AsReadOnly(environment?.ToArray() ?? []);

        var duplicate = Environment
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Environment variable '{duplicate.Key}' is defined more than once.",
                nameof(environment));
        }
    }

    public static ConnectionStartup Default { get; } = new();

    public string? Directory { get; }

    /// <summary>
    /// A single command typed into the terminal once the shell starts. It runs
    /// inside the interactive shell, so the shell stays open after it finishes.
    /// </summary>
    public string? Command { get; }

    public IReadOnlyList<ConnectionEnvironmentVariable> Environment { get; }

    private static string? NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var normalized = command.Trim();
        if (normalized.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                "A startup command must be a single line without control characters.",
                nameof(command));
        }

        return normalized;
    }
}
