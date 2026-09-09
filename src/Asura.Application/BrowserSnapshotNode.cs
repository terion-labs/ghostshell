using System.Text;

namespace Asura.Application;

/// <summary>
/// One node in a bounded, pre-order DOM-derived accessibility snapshot.
/// Page-controlled text remains untrusted data.
/// </summary>
public sealed record BrowserSnapshotNode
{
    public const int MaximumDepth = 32;
    public const int MaximumRoleBytes = 64;
    public const int MaximumNameBytes = 256;

    private const BrowserSnapshotNodeState AllStates =
        BrowserSnapshotNodeState.Disabled
        | BrowserSnapshotNodeState.Checked
        | BrowserSnapshotNodeState.Selected
        | BrowserSnapshotNodeState.Expanded
        | BrowserSnapshotNodeState.Pressed
        | BrowserSnapshotNodeState.Required
        | BrowserSnapshotNodeState.ReadOnly;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public BrowserSnapshotNode(
        int depth,
        string role,
        string name,
        BrowserElementReference? reference = null,
        BrowserSnapshotNodeState states = BrowserSnapshotNodeState.None)
    {
        if (depth is < 0 or > MaximumDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        Role = CopyRole(role);
        Name = CopyName(name);
        if ((states & ~AllStates) != BrowserSnapshotNodeState.None)
        {
            throw new ArgumentOutOfRangeException(nameof(states));
        }

        Depth = depth;
        Reference = reference;
        States = states;
    }

    public int Depth { get; }

    public string Role { get; }

    public string Name { get; }

    public BrowserElementReference? Reference { get; }

    public BrowserSnapshotNodeState States { get; }

    private static string CopyRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (role.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_')
            || GetByteCount(role, nameof(role)) > MaximumRoleBytes)
        {
            throw new ArgumentException(
                $"A browser snapshot role must be a lowercase identifier of "
                + $"at most {MaximumRoleBytes} UTF-8 bytes.",
                nameof(role));
        }

        return string.Concat(role);
    }

    private static string CopyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0', StringComparison.Ordinal)
            || GetByteCount(name, nameof(name)) > MaximumNameBytes)
        {
            throw new ArgumentException(
                $"A browser snapshot name must be NUL-free and at most "
                + $"{MaximumNameBytes} UTF-8 bytes.",
                nameof(name));
        }

        return string.Concat(name);
    }

    private static int GetByteCount(
        string value,
        string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Browser snapshot text must contain valid Unicode.",
                parameterName,
                exception);
        }
    }
}
