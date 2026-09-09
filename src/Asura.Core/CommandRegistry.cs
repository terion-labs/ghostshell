using System.Collections.Immutable;

namespace Asura.Core;

public sealed class CommandRegistry
{
    private readonly ImmutableDictionary<CommandId, CommandDefinition> _commands;
    private readonly ImmutableArray<CommandDefinition> _definitions;

    public CommandRegistry(IEnumerable<CommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var builder = ImmutableDictionary.CreateBuilder<CommandId, CommandDefinition>();

        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new ArgumentException(
                    "The command sequence cannot contain null entries.",
                    nameof(commands));
            }

            if (!builder.TryAdd(command.Id, command))
            {
                throw new ArgumentException($"Command '{command.Id}' is registered more than once.", nameof(commands));
            }
        }

        _commands = builder.ToImmutable();
        _definitions = [.. _commands.Values];
    }

    public IReadOnlyCollection<CommandDefinition> Commands => _definitions;

    public bool Contains(CommandId id) => _commands.ContainsKey(id);

    public bool TryGet(CommandId id, out CommandDefinition? command) => _commands.TryGetValue(id, out command);

    public IReadOnlyList<CommandDefinition> Search(string query)
    {
        query ??= string.Empty;
        return [.. _commands.Values
            .Where(command => command.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase)
                || command.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || command.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(command => command.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
