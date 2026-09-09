using System.Collections.ObjectModel;
using System.Text;

namespace Asura.Application;

public sealed record AgentApprovalArgument
{
    private const int MaximumNameBytes = 128;
    private const int MaximumValueBytes = 2 * 1024;

    // Trusted composers may need a larger display envelope because escaping
    // can expand one valid UTF-8 input byte to three ASCII bytes (for example
    // U+00AD -> "\u00AD"). The executable value remains bounded separately.
    internal const int MaximumEscapedValueBytes =
        3 * AgentMcpToolCallRequest.MaximumArgumentsBytes;

    public AgentApprovalArgument(string name, string displayValue, bool isSensitive = false)
    {
        Name = RequireDisplayText(name, MaximumNameBytes, nameof(name));
        DisplayValue = isSensitive
            ? "<secret reference>"
            : RequireDisplayText(displayValue, MaximumValueBytes, nameof(displayValue));
        IsSensitive = isSensitive;
    }

    internal AgentApprovalArgument(
        string name,
        string displayValue,
        int maximumDisplayBytes)
    {
        if (maximumDisplayBytes
            is < MaximumValueBytes or > MaximumEscapedValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDisplayBytes));
        }

        Name = RequireDisplayText(name, MaximumNameBytes, nameof(name));
        DisplayValue = RequireDisplayText(
            displayValue,
            maximumDisplayBytes,
            nameof(displayValue));
        IsSensitive = false;
    }

    public string Name { get; }

    public string DisplayValue { get; }

    public bool IsSensitive { get; }

    private static string RequireDisplayText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Approval text must be bounded and cannot contain control characters.",
                parameterName);
        }

        return string.Concat(value);
    }
}

public sealed record AgentApprovalPresentation
{
    public const int MaximumArgumentCount = 32;
    public const int MaximumWorkingDirectoryBytes = 4 * 1024;

    public AgentApprovalPresentation(
        string targetTitle,
        string? host,
        string? workingDirectory,
        IEnumerable<AgentApprovalArgument>? arguments = null)
    {
        TargetTitle = RequireText(targetTitle, 512, nameof(targetTitle));
        Host = CopyOptional(host, 512, nameof(host));
        WorkingDirectory = CopyOptional(
            workingDirectory,
            MaximumWorkingDirectoryBytes,
            nameof(workingDirectory));
        var copies = (arguments ?? [])
            .Select(argument => argument ?? throw new ArgumentException(
                "Approval arguments cannot contain null entries.",
                nameof(arguments)))
            .ToArray();
        if (copies.Length > MaximumArgumentCount)
        {
            throw new ArgumentException(
                $"An approval cannot contain more than {MaximumArgumentCount} material arguments.",
                nameof(arguments));
        }

        Arguments = new ReadOnlyCollection<AgentApprovalArgument>(copies);
    }

    public string TargetTitle { get; }

    public string? Host { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyList<AgentApprovalArgument> Arguments { get; }

    private static string RequireText(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Approval text must be bounded and cannot contain control characters.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static string? CopyOptional(
        string? value,
        int maximumBytes,
        string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : RequireText(value, maximumBytes, parameterName);
}
