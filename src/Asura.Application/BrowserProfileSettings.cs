using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Chooses whether ordinary browser panels share one durable application
/// profile or use the durable workspace identity as their partition key.
/// </summary>
public enum BrowserProfileSharing
{
    Shared,
    PerWorkspace,
}

public sealed record BrowserProfileSettings
{
    public BrowserProfileSettings(
        BrowserProfileSharing sharing,
        BrowserProfileId? defaultProfileId = null)
    {
        if (!Enum.IsDefined(sharing))
        {
            throw new ArgumentOutOfRangeException(nameof(sharing));
        }

        Sharing = sharing;
        DefaultProfileId = defaultProfileId;
    }

    public static BrowserProfileSettings Default { get; } = new(
        BrowserProfileSharing.Shared);

    public BrowserProfileSharing Sharing { get; }

    /// <summary>
    /// Null preserves the legacy built-in shared/per-workspace behavior.
    /// A concrete id selects that named profile for new panels.
    /// </summary>
    public BrowserProfileId? DefaultProfileId { get; }
}

/// <summary>
/// The live browser-profile preference. Changes affect browsers created after
/// the change; an open browser keeps the request context it started with.
/// </summary>
public interface IBrowserProfilePreferences
{
    BrowserProfileSettings Current { get; }

    event EventHandler? Changed;

    ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken);
}

/// <summary>
/// A logical Chromium context partition. The CEF host adds the network route
/// when it resolves the durable state identity.
/// </summary>
public readonly record struct BrowserProfileKey
{
    private const int MaximumIdentityLength = 256;

    public BrowserProfileKey(BrowserProfileKind kind, string identity)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == BrowserProfileKind.Global)
        {
            if (!string.IsNullOrEmpty(identity))
            {
                throw new ArgumentException(
                    "The global browser profile has no secondary identity.",
                    nameof(identity));
            }
        }
        else if (string.IsNullOrWhiteSpace(identity)
            || identity.Length > MaximumIdentityLength)
        {
            throw new ArgumentException(
                $"A scoped browser profile identity must contain 1 to {MaximumIdentityLength} characters.",
                nameof(identity));
        }

        Kind = kind;
        Identity = identity;
    }

    public BrowserProfileKind Kind { get; }

    public string Identity { get; }

    public static BrowserProfileKey Global { get; } = new(
        BrowserProfileKind.Global,
        string.Empty);

    public static BrowserProfileKey ForWorkspace(string identity) => new(
        BrowserProfileKind.Workspace,
        identity);

    public static BrowserProfileKey ForWebApp(string identity) => new(
        BrowserProfileKind.WebApp,
        identity);

    public static BrowserProfileKey ForNamed(string identity) => new(
        BrowserProfileKind.Named,
        identity);

    public static BrowserProfileKey ForSession(string identity) => new(
        BrowserProfileKind.Session,
        identity);
}

public enum BrowserProfileKind
{
    Global,
    Workspace,
    WebApp,
    Named,
    Session,
}

public enum BrowserProfileClearStatus
{
    Cleared,
    InUse,
    RevisionMismatch,
    Cancelled,
    Failed,
}

public sealed record BrowserProfileClearResult
{
    public BrowserProfileClearResult(
        BrowserProfileClearStatus status,
        long clearedBytes,
        string message)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (clearedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clearedBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Status = status;
        ClearedBytes = clearedBytes;
        Message = message;
    }

    public BrowserProfileClearStatus Status { get; }

    public long ClearedBytes { get; }

    public string Message { get; }
}

/// <summary>
/// Inspects and clears one exact browser profile. Durable state is encrypted at
/// rest and materialized only into the browser runtime directory while CEF owns
/// the profile.
/// </summary>
public interface IBrowserProfileDataControl
{
    BrowserProfileDataState ReadState(
        BrowserProfileSelection selection,
        long expectedRevision);

    ValueTask<BrowserProfileClearResult> ClearAsync(
        BrowserProfileClearRequest request,
        CancellationToken cancellationToken);
}

public sealed class InMemoryBrowserProfilePreferences : IBrowserProfilePreferences
{
    private BrowserProfileSettings _current = BrowserProfileSettings.Default;

    public BrowserProfileSettings Current => _current;

    public event EventHandler? Changed;

    public ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}
