using Asura.Core;

namespace Asura.Application;

public sealed record AppearancePreviewSnapshot(
    string? OwnerId,
    ThemePreference? Theme,
    TerminalRenderProfileSnapshot? TerminalRenderProfile)
{
    public static AppearancePreviewSnapshot Empty { get; } = new(null, null, null);

    public bool HasThemeDraft => Theme is not null;

    public bool HasTerminalDraft => TerminalRenderProfile is not null;

    public bool IsEmpty => !HasThemeDraft && !HasTerminalDraft;
}

public sealed record AppearancePreviewAcquisition(
    AppearancePreviewLease? Lease,
    string? Conflict)
{
    public bool IsSuccess => Lease is not null;
}

/// <summary>
/// The one process-wide appearance preview. Mutation is available only through
/// an owner lease, so a second window cannot overwrite or cancel another
/// window's draft.
/// </summary>
public sealed class AppearancePreviewCoordinator
{
    private readonly object _gate = new();
    private AppearancePreviewSnapshot _current = AppearancePreviewSnapshot.Empty;
    private Guid? _leaseToken;

    public event EventHandler? Changed;

    public AppearancePreviewSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public AppearancePreviewAcquisition TryAcquire(
        string ownerId,
        long? baselineThemeRevision,
        long? baselineTerminalRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        lock (_gate)
        {
            if (_leaseToken is not null)
            {
                return new(
                    null,
                    "Appearance is already being edited in another window. "
                    + "Apply or cancel that preview before editing here.");
            }

            var token = Guid.NewGuid();
            _leaseToken = token;
            _current = new(ownerId, null, null);
            return new(
                new AppearancePreviewLease(
                    this,
                    token,
                    ownerId,
                    baselineThemeRevision,
                    baselineTerminalRevision),
                null);
        }
    }

    internal bool PreviewTheme(Guid token, ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Mutate(token, current => current with { Theme = theme });
    }

    internal bool PreviewTerminal(Guid token, TerminalRenderProfileSnapshot renderProfile)
    {
        ArgumentNullException.ThrowIfNull(renderProfile);
        return Mutate(token, current => current with { TerminalRenderProfile = renderProfile });
    }

    internal bool ClearTheme(Guid token) =>
        Mutate(token, current => current with { Theme = null }, releaseWhenEmpty: true);

    internal bool ClearTerminal(Guid token) =>
        Mutate(
            token,
            current => current with { TerminalRenderProfile = null },
            releaseWhenEmpty: true);

    internal void Release(Guid token)
    {
        var changed = false;
        lock (_gate)
        {
            if (_leaseToken != token)
            {
                return;
            }

            _leaseToken = null;
            _current = AppearancePreviewSnapshot.Empty;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Mutate(
        Guid token,
        Func<AppearancePreviewSnapshot, AppearancePreviewSnapshot> mutation,
        bool releaseWhenEmpty = false)
    {
        AppearancePreviewSnapshot next;
        lock (_gate)
        {
            if (_leaseToken != token)
            {
                return false;
            }

            next = mutation(_current);
            if (releaseWhenEmpty && next.IsEmpty)
            {
                _leaseToken = null;
                next = AppearancePreviewSnapshot.Empty;
            }

            if (next == _current)
            {
                return true;
            }

            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

public sealed class AppearancePreviewLease : IDisposable
{
    private AppearancePreviewCoordinator? _owner;
    private readonly Guid _token;

    internal AppearancePreviewLease(
        AppearancePreviewCoordinator owner,
        Guid token,
        string ownerId,
        long? baselineThemeRevision,
        long? baselineTerminalRevision)
    {
        _owner = owner;
        _token = token;
        OwnerId = ownerId;
        BaselineThemeRevision = baselineThemeRevision;
        BaselineTerminalRevision = baselineTerminalRevision;
    }

    public string OwnerId { get; }

    public long? BaselineThemeRevision { get; private set; }

    public long? BaselineTerminalRevision { get; private set; }

    public bool IsActive => _owner is not null;

    public bool PreviewTheme(ThemePreference theme) =>
        _owner?.PreviewTheme(_token, theme) == true;

    public bool PreviewTerminal(TerminalRenderProfileSnapshot renderProfile) =>
        _owner?.PreviewTerminal(_token, renderProfile) == true;

    public bool AdvanceThemeBaseline(long? revision)
    {
        if (_owner is null)
        {
            return false;
        }

        BaselineThemeRevision = revision;
        return true;
    }

    public bool AdvanceTerminalBaseline(long? revision)
    {
        if (_owner is null)
        {
            return false;
        }

        BaselineTerminalRevision = revision;
        return true;
    }

    public bool ClearTheme()
    {
        var cleared = _owner?.ClearTheme(_token) == true;
        ReleaseIfCoordinatorReleased(cleared);
        return cleared;
    }

    public bool ClearTerminal()
    {
        var cleared = _owner?.ClearTerminal(_token) == true;
        ReleaseIfCoordinatorReleased(cleared);
        return cleared;
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(_token);
    }

    private void ReleaseIfCoordinatorReleased(bool mutationSucceeded)
    {
        if (mutationSucceeded && _owner?.Current.OwnerId is null)
        {
            _owner = null;
        }
    }
}
