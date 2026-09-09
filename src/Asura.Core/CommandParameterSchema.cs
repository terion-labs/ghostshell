using System.Collections.Immutable;
using System.Globalization;

namespace Asura.Core;

public enum CommandParameterType
{
    String,
    Integer,
    Boolean,
    Choice,
}

public sealed record CommandParameter
{
    public CommandParameter(
        string name,
        CommandParameterType type,
        bool required = false,
        IEnumerable<string>? allowedValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var values = allowedValues?.ToImmutableArray() ?? [];
        if (type == CommandParameterType.Choice && values.IsEmpty)
        {
            throw new ArgumentException("A choice parameter requires at least one allowed value.", nameof(allowedValues));
        }

        if (type != CommandParameterType.Choice && !values.IsEmpty)
        {
            throw new ArgumentException("Only a choice parameter may define allowed values.", nameof(allowedValues));
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Allowed values cannot be empty.", nameof(allowedValues));
        }

        Name = name;
        Type = type;
        Required = required;
        AllowedValues = values;
    }

    public string Name { get; }

    public CommandParameterType Type { get; }

    public bool Required { get; }

    public ImmutableArray<string> AllowedValues { get; }

    internal bool Accepts(string value) => Type switch
    {
        CommandParameterType.String => true,
        CommandParameterType.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        CommandParameterType.Boolean => bool.TryParse(value, out _),
        CommandParameterType.Choice => AllowedValues.Contains(value, StringComparer.Ordinal),
        _ => false,
    };
}

public sealed record CommandParameterSchema
{
    public CommandParameterSchema(IEnumerable<CommandParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = parameters.ToImmutableArray();
        if (values.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("Command parameter names must be unique.", nameof(parameters));
        }

        Parameters = values;
    }

    public ImmutableArray<CommandParameter> Parameters { get; }

    public static CommandParameterSchema None { get; } = new([]);

    public ImmutableArray<string> Validate(IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var errors = ImmutableArray.CreateBuilder<string>();

        foreach (var parameter in Parameters)
        {
            if (!arguments.TryGetValue(parameter.Name, out var value))
            {
                if (parameter.Required)
                {
                    errors.Add($"Parameter '{parameter.Name}' is required.");
                }

                continue;
            }

            if (!parameter.Accepts(value))
            {
                errors.Add($"Parameter '{parameter.Name}' does not accept '{value}'.");
            }
        }

        var knownNames = Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var unknownName in arguments.Keys.Where(name => !knownNames.Contains(name)))
        {
            errors.Add($"Parameter '{unknownName}' is not defined.");
        }

        return errors.ToImmutable();
    }
}
