namespace Asura.Application;

internal static class WorkspaceGraphContractValidation
{
    public static void RequireId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A runtime identifier is required.", parameterName);
        }
    }
}
