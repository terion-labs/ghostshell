namespace Asura.Core;

internal static class RuntimeId
{
    public static string NewValue() => Guid.CreateVersion7().ToString("N");

    public static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
