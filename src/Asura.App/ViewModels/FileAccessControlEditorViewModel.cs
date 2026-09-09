using System.Collections.ObjectModel;

using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// Who can do what to one item, as something to look at and change.
///
/// Two models, one table. A filesystem has three parties — the owner, the group
/// and everyone — and an object store has as many as it has accounts; either
/// way the question is the same one, so the answer is one row per party with
/// what that party is allowed beside it. That is the shape people already read
/// this in, and it beats a grid of nine checkboxes for everything except the
/// execute bit, which gets a column of its own because a shell is exactly where
/// that bit matters.
/// </summary>
public sealed class FileAccessControlEditorViewModel : ObservableObject
{
    private readonly FilePanelAccessControl _original;
    private FilePanelPosixMode _mode;

    public FileAccessControlEditorViewModel(
        string itemName,
        string connectionName,
        FilePanelAccessControl accessControl,
        bool canEdit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(accessControl);
        _original = accessControl;
        _mode = accessControl.Mode ?? new FilePanelPosixMode(0);
        ItemName = itemName;
        CanEdit = canEdit;
        Summary = accessControl.Mode is not null
            ? OwnerAndGroup(accessControl, connectionName)
            : $"Access control list on {connectionName}";
        Rows = accessControl.Mode is not null
            ? [.. Enum.GetValues<FilePanelPosixWho>().Select(who =>
                new FileAccessRowViewModel(who, this))]
            : [.. accessControl.Grants.Select(grant =>
                new FileAccessRowViewModel(grant))];
    }

    public string ItemName { get; }

    public string Summary { get; }

    /// <summary>
    /// Whether this is a reading or an editing. A connection that grants the
    /// right to see its permissions need not grant the right to change them,
    /// and the refusal is better said here than after an Apply.
    /// </summary>
    public bool CanEdit { get; }

    public string ReadOnlyReason =>
        "This connection reports who has access but does not accept changes to it here.";

    public bool HasMode => _original.Mode is not null;

    /// <summary>One row per party, whichever model the connection answered in.</summary>
    public ObservableCollection<FileAccessRowViewModel> Rows { get; }

    /// <summary>
    /// The mode as it is written and as a listing shows it. Kept because the
    /// number is what anybody working at a shell actually wants to see, and
    /// because a table of privileges cannot express the odd combinations.
    /// </summary>
    public string ModeText => $"{_mode.Octal}  {_mode.Symbolic}";

    /// <summary>
    /// The change to send, or null when nothing was changed — an Apply that
    /// alters nothing should not be a write at all, least of all one that
    /// bumps a version somebody else is holding.
    /// </summary>
    public FilePanelSetAccessControlRequest? BuildRequest()
    {
        if (!CanEdit)
        {
            return null;
        }

        if (_original.Mode is { } before)
        {
            return before.Permissions == _mode.Permissions
                ? null
                : new FilePanelSetAccessControlRequest(
                    _original.Location,
                    mode: _mode,
                    version: _original.Version);
        }

        var grants = Rows
            .Where(row => row.Rights != FilePanelAccessRight.None)
            .Select(row => row.ToGrant())
            .ToArray();
        var unchanged = grants.Length == _original.Grants.Count
            && grants.Zip(_original.Grants).All(pair =>
                pair.First.Grantee == pair.Second.Grantee
                && pair.First.Rights == pair.Second.Rights);
        return unchanged
            ? null
            : new FilePanelSetAccessControlRequest(
                _original.Location,
                grants: grants,
                version: _original.Version);
    }

    internal bool Has(FilePanelPosixWho who, FilePanelPosixRight right) => _mode.Has(who, right);

    internal void Set(FilePanelPosixWho who, FilePanelPosixRight right, bool granted)
    {
        if (Has(who, right) == granted)
        {
            return;
        }

        _mode = _mode.With(who, right, granted);
        OnPropertyChanged(nameof(ModeText));
    }

    private static string OwnerAndGroup(FilePanelAccessControl accessControl, string connection)
    {
        // Named where the connection knows the names. A POSIX filesystem read
        // through .NET does not: the runtime exposes the bits and not the
        // account behind them, and a name guessed from this process would be
        // wrong for every file belonging to somebody else.
        var parties = new[] { accessControl.Owner, accessControl.Group }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return parties.Length == 0
            ? $"Permission bits on {connection}"
            : $"{string.Join(" · ", parties)} on {connection}";
    }
}

/// <summary>
/// One party and what it is allowed. The privileges offered are the ones a
/// person picks between rather than the bits underneath, and reading is what a
/// row means when it is left alone.
/// </summary>
public sealed class FileAccessRowViewModel : ObservableObject
{
    private static readonly (string Label, FilePanelAccessRight Rights)[] PosixPrivileges =
    [
        ("Read & Write", FilePanelAccessRight.Read | FilePanelAccessRight.Write),
        ("Read only", FilePanelAccessRight.Read),
        ("Write only", FilePanelAccessRight.Write),
        ("No access", FilePanelAccessRight.None),
    ];

    private static readonly (string Label, FilePanelAccessRight Rights)[] GrantPrivileges =
    [
        ("Full control", FilePanelAccessRight.FullControl),
        ("Read & Write", FilePanelAccessRight.Read | FilePanelAccessRight.Write),
        ("Read only", FilePanelAccessRight.Read),
        ("Write only", FilePanelAccessRight.Write),
        ("No access", FilePanelAccessRight.None),
    ];

    private readonly FileAccessControlEditorViewModel? _mode;
    private readonly FilePanelPosixWho _who;
    private readonly FilePanelGrantee? _grantee;
    private readonly (string Label, FilePanelAccessRight Rights)[] _privileges;
    private string _privilege;
    private FilePanelAccessRight _rights;

    /// <summary>A party of a filesystem: the owner, the group, or everyone.</summary>
    internal FileAccessRowViewModel(
        FilePanelPosixWho who,
        FileAccessControlEditorViewModel mode)
    {
        _mode = mode;
        _who = who;
        _privileges = PosixPrivileges;
        _rights =
            (mode.Has(who, FilePanelPosixRight.Read) ? FilePanelAccessRight.Read : FilePanelAccessRight.None)
            | (mode.Has(who, FilePanelPosixRight.Write) ? FilePanelAccessRight.Write : FilePanelAccessRight.None);
        _privilege = Nearest(_rights, _privileges);
        Name = who switch
        {
            FilePanelPosixWho.Owner => "Owner",
            FilePanelPosixWho.Group => "Group",
            _ => "Everyone",
        };
        Detail = string.Empty;
        ShowsExecute = true;
    }

    /// <summary>A party of an object store: an account, or a well-known group.</summary>
    public FileAccessRowViewModel(FilePanelAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grantee = grant.Grantee;
        _privileges = GrantPrivileges;
        _rights = grant.Rights;
        _privilege = Nearest(grant.Rights, _privileges);
        Name = grant.Grantee.Label;
        // What the connection calls this party underneath the readable name,
        // where the two differ — a canonical account id means nothing on its
        // own, and without it two accounts named alike cannot be told apart.
        var detail = grant.Grantee.DisplayName is not null && grant.Grantee.Id is not null
            ? grant.Grantee.Id
            : grant.Grantee.Kind.ToString();
        Detail = string.Equals(detail, Name, StringComparison.Ordinal)
            ? string.Empty
            : detail;
    }

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public IReadOnlyList<string> Choices => [.. _privileges.Select(option => option.Label)];

    public string Privilege
    {
        get => _privilege;
        set
        {
            if (!SetProperty(ref _privilege, value))
            {
                return;
            }

            _rights = _privileges.FirstOrDefault(option => string.Equals(option.Label, value, StringComparison.Ordinal)).Rights;
            if (_mode is not null)
            {
                _mode.Set(
                    _who,
                    FilePanelPosixRight.Read,
                    _rights.HasFlag(FilePanelAccessRight.Read));
                _mode.Set(
                    _who,
                    FilePanelPosixRight.Write,
                    _rights.HasFlag(FilePanelAccessRight.Write));
            }

            OnPropertyChanged(nameof(PrivilegeLabel));
        }
    }

    public string PrivilegeLabel => $"What {Name} is allowed";

    /// <summary>
    /// Only a filesystem has one. Finder hides it and a shell cannot: the
    /// difference between a script that runs and one that does not is this bit.
    /// </summary>
    public bool ShowsExecute { get; }

    public bool CanRun
    {
        get => _mode?.Has(_who, FilePanelPosixRight.Execute) == true;
        set
        {
            if (_mode is null || CanRun == value)
            {
                return;
            }

            _mode.Set(_who, FilePanelPosixRight.Execute, value);
            OnPropertyChanged();
        }
    }

    internal FilePanelAccessRight Rights => _rights;

    internal FilePanelAccessGrant ToGrant() => new(
        _grantee ?? throw new InvalidOperationException(
            "A filesystem's parties are written back as a mode, not as grants."),
        _rights);

    /// <summary>
    /// A grant the choices cannot express exactly — read plus the right to
    /// change the list, say — shows as the smallest one that covers it, and
    /// keeps what it actually has until somebody picks something else. Leaving
    /// a row alone must never quietly take a permission away.
    /// </summary>
    private static string Nearest(
        FilePanelAccessRight rights,
        (string Label, FilePanelAccessRight Rights)[] privileges)
    {
        for (var index = privileges.Length - 1; index >= 0; index--)
        {
            if ((rights & ~privileges[index].Rights) == FilePanelAccessRight.None
                && (privileges[index].Rights != FilePanelAccessRight.None
                    || rights == FilePanelAccessRight.None))
            {
                return privileges[index].Label;
            }
        }

        return privileges[0].Label;
    }
}
