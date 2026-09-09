using Asura.Application;

namespace Asura.Files;

/// <summary>
/// Who can do what to one item, as a provider reads it.
///
/// It carries no location: the caller asked about one, and knows which. The
/// value types inside come from the application layer because they are the same
/// facts either side of the seam — a mode is nine bits wherever it is read, and
/// translating it into a provider-shaped copy and back would only add a place
/// for the two to disagree.
/// </summary>
public sealed record FileAccessControl
{
    public FileAccessControl(
        FilePanelPosixMode? mode = null,
        string? owner = null,
        string? group = null,
        IReadOnlyList<FilePanelAccessGrant>? grants = null,
        string? version = null)
    {
        Mode = mode;
        Owner = owner;
        Group = group;
        Grants = grants ?? [];
        Version = version;
    }

    public FilePanelPosixMode? Mode { get; }

    /// <summary>What the provider calls the owning account, where it knows.</summary>
    public string? Owner { get; }

    public string? Group { get; }

    public IReadOnlyList<FilePanelAccessGrant> Grants { get; }

    /// <summary>
    /// What the provider had when this was read, handed back when it is written
    /// so a change made elsewhere in between is refused rather than overwritten.
    /// </summary>
    public string? Version { get; }
}

public sealed record FileAccessControlRequest(FileLocation Location);

public sealed record FileSetAccessControlRequest
{
    public FileSetAccessControlRequest(
        FileLocation location,
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

    public FileLocation Location { get; }

    public FilePanelPosixMode? Mode { get; }

    public IReadOnlyList<FilePanelAccessGrant>? Grants { get; }

    public string? Version { get; }
}
