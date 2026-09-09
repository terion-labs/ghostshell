namespace Asura.Core;

public sealed record DefinitionValidationResult
{
    public DefinitionValidationResult(IReadOnlyList<DefinitionValidationIssue>? issues = null)
    {
        Issues = Array.AsReadOnly(issues?.ToArray() ?? []);
    }

    public static DefinitionValidationResult Valid { get; } = new();

    public IReadOnlyList<DefinitionValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}
