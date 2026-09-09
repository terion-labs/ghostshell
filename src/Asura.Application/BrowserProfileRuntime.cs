using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Identifies one exact browser partition. Durable profiles seal this
/// partition into encrypted application storage; private sessions discard it.
/// </summary>
public readonly record struct BrowserProfileSelection
{
    public BrowserProfileSelection(
        BrowserProfileId profileId,
        BrowserProfileKey partition)
    {
        _ = new BrowserProfileId(profileId.Value);
        ProfileId = profileId;
        Partition = partition;
    }

    public BrowserProfileId ProfileId { get; }

    public BrowserProfileKey Partition { get; }
}

/// <summary>
/// Immutable profile metadata captured by a browser panel. Catalog edits affect
/// only panels opened after the edit.
/// </summary>
public sealed record BrowserProfileBinding
{
    public BrowserProfileBinding(
        BrowserProfileSelection selection,
        BrowserProfileDefinition definition,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (selection.ProfileId != definition.Id)
        {
            throw new ArgumentException(
                "The browser profile selection does not match its definition.",
                nameof(selection));
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Selection = selection;
        Definition = definition;
        Revision = revision;
    }

    public BrowserProfileSelection Selection { get; }

    public BrowserProfileDefinition Definition { get; }

    public long Revision { get; }

    public static BrowserProfileBinding Legacy(BrowserProfileKey partition) => new(
        new BrowserProfileSelection(BuiltInBrowserProfiles.Default.Id, partition),
        BuiltInBrowserProfiles.Default,
        revision: 1);
}

public enum BrowserProfilePinFailure
{
    Missing,
    Disabled,
}

public sealed record BrowserProfilePinResult
{
    private BrowserProfilePinResult(
        BrowserProfileBinding? binding,
        BrowserProfilePinFailure? failure,
        string? message)
    {
        Binding = binding;
        Failure = failure;
        Message = message;
    }

    public BrowserProfileBinding? Binding { get; }

    public BrowserProfilePinFailure? Failure { get; }

    public string? Message { get; }

    public bool IsSuccess => Binding is not null;

    public static BrowserProfilePinResult Success(BrowserProfileBinding binding) =>
        new(binding ?? throw new ArgumentNullException(nameof(binding)), null, null);

    public static BrowserProfilePinResult Failed(
        BrowserProfilePinFailure failure,
        string message) =>
        new(null, failure, message);
}

/// <summary>
/// Resolves a named profile once and freezes its current catalog revision for a
/// panel lifetime. It never substitutes another profile.
/// </summary>
public sealed class CatalogBrowserProfileRuntime(IDefinitionCatalog catalog)
{
    private readonly IDefinitionCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    public BrowserProfilePinResult Pin(
        BrowserProfileId profileId,
        BrowserProfileKey partition)
    {
        var stored = _catalog.Snapshot.BrowserProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            return BrowserProfilePinResult.Failed(
                BrowserProfilePinFailure.Missing,
                "The selected browser profile no longer exists. Choose another profile in Browser settings, then reopen this panel.");
        }

        if (!stored.Value.IsEnabled)
        {
            return BrowserProfilePinResult.Failed(
                BrowserProfilePinFailure.Disabled,
                $"The browser profile '{stored.Value.Name}' is disabled. Enable it or choose another profile, then reopen this panel.");
        }

        return BrowserProfilePinResult.Success(new BrowserProfileBinding(
            new BrowserProfileSelection(profileId, partition),
            stored.Value,
            stored.Revision));
    }

    public BrowserProfilePinResult PinNewPanel(
        BrowserProfileId profileId,
        BrowserProfileKey legacyPartition,
        PanelInstanceId panelId)
    {
        var stored = _catalog.Snapshot.BrowserProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            return BrowserProfilePinResult.Failed(
                BrowserProfilePinFailure.Missing,
                "The selected browser profile no longer exists. Choose another profile in Browser settings, then reopen this panel.");
        }

        var partition = profileId == BuiltInBrowserProfiles.Default.Id
            ? legacyPartition
            : stored.Value.Persistence == BrowserProfilePersistence.PrivateSession
                ? BrowserProfileKey.ForSession($"{profileId.Value}:{panelId.Value}")
                : BrowserProfileKey.ForNamed(profileId.Value);
        return Pin(profileId, partition);
    }
}

[Flags]
public enum BrowserProfileDataCategory
{
    None = 0,
    Cookies = 1,
    HttpAuthentication = 2,
    AllWebContent = 4,
    AllEphemeralWebContent = AllWebContent,
}

public sealed record BrowserProfileClearRequest
{
    public BrowserProfileClearRequest(
        BrowserProfileSelection selection,
        long expectedRevision,
        BrowserProfileDataCategory categories)
    {
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        const BrowserProfileDataCategory supported =
            BrowserProfileDataCategory.Cookies
            | BrowserProfileDataCategory.HttpAuthentication
            | BrowserProfileDataCategory.AllWebContent;
        if (categories == BrowserProfileDataCategory.None
            || (categories & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(categories));
        }

        Selection = selection;
        ExpectedRevision = expectedRevision;
        Categories = categories;
    }

    public BrowserProfileSelection Selection { get; }

    public long ExpectedRevision { get; }

    public BrowserProfileDataCategory Categories { get; }
}

public sealed record BrowserProfileDataState(
    BrowserProfileSelection Selection,
    long Revision,
    int ActiveContexts,
    int ActiveLeases,
    long StoredBytes = 0)
{
    public bool HasData => ActiveContexts > 0 || StoredBytes > 0;

    public bool HasEphemeralData => ActiveContexts > 0;
}
