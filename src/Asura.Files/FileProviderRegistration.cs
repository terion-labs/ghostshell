using Asura.Application;

namespace Asura.Files;

public sealed record FileProviderRegistration
{
    public FileProviderRegistration(
        string name,
        FileProviderFamily family,
        IFileProvider provider,
        FileLocation root)
        : this(
            name,
            family,
            provider,
            root,
            FilePanelCapability.None)
    {
    }

    public FileProviderRegistration(
        string name,
        FileProviderFamily family,
        IFileProvider provider,
        FileLocation root,
        FilePanelCapability governedMutationCapabilities,
        FileLocation? start = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(root);
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, null);
        }

        if (provider.ProfileId != root.ProviderProfileId)
        {
            throw new ArgumentException(
                "A registered provider and its root must use the same profile ID.",
                nameof(root));
        }

        const FilePanelCapability knownGovernedMutationCapabilities =
            FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedDelete
            | FilePanelCapability.GovernedRename
            | FilePanelCapability.GovernedCreateFile
            | FilePanelCapability.GovernedReplaceFile
            | FilePanelCapability.GovernedCopySource
            | FilePanelCapability.GovernedCopy;
        if ((governedMutationCapabilities & ~knownGovernedMutationCapabilities) != FilePanelCapability.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(governedMutationCapabilities),
                governedMutationCapabilities,
                "Only trusted governed-mutation capabilities may be registered here.");
        }

        if (governedMutationCapabilities.HasFlag(
                FilePanelCapability.GovernedCreateDirectory)
            && !provider.Capabilities.Supports(FileProviderCapability.CreateDirectory))
        {
            throw new ArgumentException(
                "Governed directory creation requires provider directory-creation support.",
                nameof(governedMutationCapabilities));
        }

        if (governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedDelete)
            && !provider.Capabilities.Supports(FileProviderCapability.Delete))
        {
            throw new ArgumentException(
                "Governed deletion requires provider deletion support.",
                nameof(governedMutationCapabilities));
        }

        if (governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedRename)
            && !provider.Capabilities.Supports(FileProviderCapability.Rename))
        {
            throw new ArgumentException(
                "Governed rename requires provider rename support.",
                nameof(governedMutationCapabilities));
        }

        var needsStreamingWrite = governedMutationCapabilities.HasFlag(
                FilePanelCapability.GovernedCreateFile)
            || governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedReplaceFile);
        if (needsStreamingWrite
            && !provider.Capabilities.Supports(FileProviderCapability.StreamingWrite))
        {
            throw new ArgumentException(
                "Governed text writes require provider streaming-write support.",
                nameof(governedMutationCapabilities));
        }

        if (governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedCopySource)
            && (!provider.Capabilities.Supports(FileProviderCapability.Stat)
                || !provider.Capabilities.Supports(FileProviderCapability.RangedRead)))
        {
            throw new ArgumentException(
                "A governed copy source requires provider stat and ranged-read support.",
                nameof(governedMutationCapabilities));
        }

        if (governedMutationCapabilities.HasFlag(FilePanelCapability.GovernedCopy)
            && !provider.Capabilities.Supports(FileProviderCapability.Copy))
        {
            throw new ArgumentException(
                "Governed copy requires provider copy support.",
                nameof(governedMutationCapabilities));
        }

        Name = name.Trim();
        Family = family;
        Provider = provider;
        Root = root;
        if (start is not null && start.ProviderProfileId != provider.ProfileId)
        {
            throw new ArgumentException(
                "A registered provider and its start location must use the same profile ID.",
                nameof(start));
        }

        Start = start ?? root;
        GovernedMutationCapabilities = governedMutationCapabilities;
    }

    public string Name { get; }

    public FileProviderFamily Family { get; }

    public IFileProvider Provider { get; }

    public FileLocation Root { get; }

    /// <summary>
    /// Where a panel opens on this provider. Defaults to the root; the local
    /// provider reaches the whole filesystem but opens at the user's home.
    /// </summary>
    public FileLocation Start { get; }

    /// <summary>
    /// Capabilities asserted only by trusted production composition after verifying that the
    /// provider confines mutations to its registered namespace and does not replay them.
    /// </summary>
    public FilePanelCapability GovernedMutationCapabilities { get; }
}
