using System.Globalization;

namespace Asura.Application;

/// <summary>
/// Who a permission is granted to.
///
/// Two families of system are being described with one vocabulary. A POSIX
/// filesystem knows three parties and nothing else: the file's owner, its
/// group, and everyone. An object store knows named accounts and a handful of
/// well-known groups. The kinds below are the union, and a connection uses the
/// ones its own model has.
/// </summary>
public enum FilePanelGranteeKind
{
    /// <summary>The account the item belongs to.</summary>
    Owner,

    /// <summary>The group it belongs to.</summary>
    Group,

    /// <summary>Anyone at all, signed in or not.</summary>
    Everyone,

    /// <summary>Anyone the service recognises as signed in.</summary>
    AuthenticatedUsers,

    /// <summary>The service's own log writer.</summary>
    LogDelivery,

    /// <summary>One named account.</summary>
    User,
}

/// <summary>
/// What is granted. Read and Write are the two every system has; the two about
/// the permissions themselves, and the everything of FullControl, come from the
/// object stores.
/// </summary>
[Flags]
public enum FilePanelAccessRight
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,

    /// <summary>Read the permissions, not the item.</summary>
    ReadAcl = 1 << 2,

    /// <summary>Change them.</summary>
    WriteAcl = 1 << 3,

    FullControl = Read | Write | ReadAcl | WriteAcl,
}

public sealed record FilePanelGrantee
{
    public FilePanelGrantee(
        FilePanelGranteeKind kind,
        string? id = null,
        string? displayName = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        if (kind == FilePanelGranteeKind.User)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
        }

        Kind = kind;
        Id = id;
        DisplayName = displayName;
    }

    public FilePanelGranteeKind Kind { get; }

    /// <summary>
    /// What the connection calls this party — a canonical account id, a numeric
    /// uid. Absent for the well-known groups, which need no name.
    /// </summary>
    public string? Id { get; }

    /// <summary>A readable name for it, where the connection knows one.</summary>
    public string? DisplayName { get; }

    /// <summary>What to show in a list of who has access.</summary>
    public string Label => DisplayName
        ?? (Kind switch
        {
            FilePanelGranteeKind.Owner => "Owner",
            FilePanelGranteeKind.Group => "Group",
            FilePanelGranteeKind.Everyone => "Everyone",
            FilePanelGranteeKind.AuthenticatedUsers => "Authenticated users",
            FilePanelGranteeKind.LogDelivery => "Log delivery",
            _ => null,
        })
        ?? Id
        ?? Kind.ToString();
}

public sealed record FilePanelAccessGrant(
    FilePanelGrantee Grantee,
    FilePanelAccessRight Rights);

/// <summary>
/// The nine permission bits, held as the number they are written as. Anything
/// above them — setuid, sticky — is preserved but not offered for editing:
/// showing a checkbox for a bit whose meaning depends on the platform invites
/// exactly the mistake it looks harmless to make.
/// </summary>
public sealed record FilePanelPosixMode
{
    public const int PermissionMask = 0x1FF;

    public FilePanelPosixMode(int value)
    {
        if (value is < 0 or > 0xFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }

        Value = value;
    }

    public int Value { get; }

    public int Permissions => Value & PermissionMask;

    /// <summary>The mode as it is written down and typed: "644", "755".</summary>
    public string Octal => Convert
        .ToString(Permissions, 8)
        .PadLeft(3, '0');

    /// <summary>And as a listing shows it: "rw-r--r--".</summary>
    public string Symbolic
    {
        get
        {
            Span<char> text = stackalloc char[9];
            const string order = "rwx";
            for (var index = 0; index < 9; index++)
            {
                var bit = 1 << (8 - index);
                text[index] = (Permissions & bit) != 0 ? order[index % 3] : '-';
            }

            return new string(text);
        }
    }

    public bool Has(FilePanelPosixWho who, FilePanelPosixRight right) =>
        (Permissions & Bit(who, right)) != 0;

    public FilePanelPosixMode With(FilePanelPosixWho who, FilePanelPosixRight right, bool granted)
    {
        var bit = Bit(who, right);
        return new FilePanelPosixMode(granted ? Value | bit : Value & ~bit);
    }

    public static bool TryParseOctal(string? text, out FilePanelPosixMode? mode)
    {
        mode = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var value = Convert.ToInt32(text.Trim(), 8);
            if (value is < 0 or > 0xFFF)
            {
                return false;
            }

            mode = new FilePanelPosixMode(value);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
            or ArgumentException)
        {
            return false;
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Octal} ({Symbolic})");

    private static int Bit(FilePanelPosixWho who, FilePanelPosixRight right)
    {
        if (!Enum.IsDefined(who))
        {
            throw new ArgumentOutOfRangeException(nameof(who), who, null);
        }

        if (!Enum.IsDefined(right))
        {
            throw new ArgumentOutOfRangeException(nameof(right), right, null);
        }

        var shift = ((2 - (int)who) * 3) + (2 - (int)right);
        return 1 << shift;
    }
}

public enum FilePanelPosixWho
{
    Owner,
    Group,
    Other,
}

public enum FilePanelPosixRight
{
    Read,
    Write,
    Execute,
}

/// <summary>
/// Who can do what to one item, as the connection describes it.
///
/// Both halves are optional and a connection fills in the one it has. A POSIX
/// filesystem answers with a mode and the names of the owner and group; an
/// object store answers with a list of grants. Neither is translated into the
/// other, because the translation would be a lie in both directions.
/// </summary>
public sealed record FilePanelAccessControl
{
    public FilePanelAccessControl(
        FilePanelLocation location,
        FilePanelPosixMode? mode = null,
        string? owner = null,
        string? group = null,
        IReadOnlyList<FilePanelAccessGrant>? grants = null,
        string? version = null)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Mode = mode;
        Owner = owner;
        Group = group;
        Grants = grants ?? [];
        Version = version;
    }

    public FilePanelLocation Location { get; }

    public FilePanelPosixMode? Mode { get; }

    public string? Owner { get; }

    public string? Group { get; }

    public IReadOnlyList<FilePanelAccessGrant> Grants { get; }

    /// <summary>
    /// What the connection had when this was read, to be handed back when it is
    /// written so a change made elsewhere in between is refused rather than
    /// overwritten.
    /// </summary>
    public string? Version { get; }

    public bool HasMode => Mode is not null;

    public bool HasGrants => Grants.Count > 0;
}

public sealed record FilePanelAccessControlRequest(FilePanelLocation Location);

/// <summary>
/// A change to who can do what. Exactly one of the two halves is sent: the
/// connection that answered with a mode is asked for a mode, and the one that
/// answered with grants is asked for grants.
/// </summary>
public sealed record FilePanelSetAccessControlRequest
{
    public FilePanelSetAccessControlRequest(
        FilePanelLocation location,
        FilePanelPosixMode? mode = null,
        IReadOnlyList<FilePanelAccessGrant>? grants = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (mode is null == grants is null)
        {
            throw new ArgumentException(
                "A change to access control sets either a mode or a list of grants.",
                nameof(mode));
        }

        Location = location;
        Mode = mode;
        Grants = grants;
        Version = version;
    }

    public FilePanelLocation Location { get; }

    public FilePanelPosixMode? Mode { get; }

    public IReadOnlyList<FilePanelAccessGrant>? Grants { get; }

    public string? Version { get; }
}
