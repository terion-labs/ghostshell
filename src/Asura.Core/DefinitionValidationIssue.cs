namespace Asura.Core;

public sealed record DefinitionValidationIssue(
    DefinitionValidationCode Code,
    string Message,
    string? Target = null);
