using GhostShell.Core;

namespace GhostShell.App;

internal readonly record struct ApplicationKeyProfileSnapshot(
    KeymapProfile Profile,
    long Revision,
    string Name,
    CommandContext ActiveContexts);

internal delegate ValueTask<bool> ApplicationKeyReplay(
    IReadOnlyList<KeyStroke> strokes,
    CancellationToken cancellationToken);

internal sealed record ApplicationKeyPresentation(
    Func<CommandBinding, Task> ExecuteCommandAsync,
    Action<string> ShowHint,
    Action ClearHint,
    Action<string> SetError);

/// <summary>
/// Owns application-key sequence resolution, keymap revision changes, hint
/// expiry, and safe replay. The window maps framework key events into durable
/// key strokes and supplies the currently active terminal replay operation.
/// </summary>
internal sealed class ApplicationKeyController : IDisposable
{
    private readonly ApplicationKeyPresentation _presentation;
    private readonly CancellationTokenSource _lifetime;
    private readonly TimeProvider _timeProvider;
    private ApplicationKeySequenceResolver _resolver = new(BuiltInKeymaps.TmuxApplication);
    private KeymapProfileId _activeProfileId = BuiltInKeymaps.TmuxApplicationId;
    private long _activeProfileRevision;
    private CancellationTokenSource? _hintLifetime;
    private bool _disposed;

    public ApplicationKeyController(
        ApplicationKeyPresentation presentation,
        CancellationToken lifetime,
        TimeProvider? timeProvider = null)
    {
        _presentation = presentation;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ApplicationKeyResolution Resolve(
        KeyStroke stroke,
        ApplicationKeyProfileSnapshot profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Synchronize(profile);
        return _resolver.Resolve(
            stroke,
            profile.ActiveContexts,
            _timeProvider.GetUtcNow());
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resolver.Reset();
        ClearHint();
    }

    public async Task ApplyAsync(
        ApplicationKeyResolution resolution,
        ApplicationKeyProfileSnapshot profile,
        ApplicationKeyReplay? replay)
    {
        if (resolution.Kind == ApplicationKeyResolutionKind.Matched
            && resolution.Binding is { } binding)
        {
            ClearHint();
            await _presentation.ExecuteCommandAsync(binding);
            return;
        }

        if (resolution.Kind == ApplicationKeyResolutionKind.Pending
            && profile.Profile.Prefix is { } prefix)
        {
            ShowPendingHint(prefix, profile.Name, replay);
            return;
        }

        if (resolution.Kind == ApplicationKeyResolutionKind.Rejected
            && resolution.ShouldHandle)
        {
            ShowTimedHint(
                "That key is not bound after the application prefix.",
                TimeSpan.FromSeconds(2));
            return;
        }

        if (resolution.Kind is ApplicationKeyResolutionKind.PassedThrough
            or ApplicationKeyResolutionKind.Expired)
        {
            ClearHint();
            await ReplayAsync(replay, resolution.ReplayStrokes);
        }
    }

    private void Synchronize(ApplicationKeyProfileSnapshot profile)
    {
        if (profile.Profile.Id == _activeProfileId
            && profile.Revision == _activeProfileRevision)
        {
            return;
        }

        _resolver = new ApplicationKeySequenceResolver(profile.Profile);
        _activeProfileId = profile.Profile.Id;
        _activeProfileRevision = profile.Revision;
        ClearHint();
    }

    private void ShowTimedHint(string message, TimeSpan duration)
    {
        var cancellationToken = ReplaceHintLifetime();
        _presentation.ShowHint(message);
        _ = ClearHintAfterAsync(duration, cancellationToken);
    }

    private void ShowPendingHint(
        PrefixConfiguration prefix,
        string profileName,
        ApplicationKeyReplay? replay)
    {
        var cancellationToken = ReplaceHintLifetime();
        _presentation.ShowHint(
            $"{prefix.Stroke} — waiting for a command · {profileName}");
        _ = ExpireAsync(prefix.Timeout, replay, cancellationToken);
    }

    private CancellationToken ReplaceHintLifetime()
    {
        _hintLifetime?.Cancel();
        _hintLifetime?.Dispose();
        _hintLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _hintLifetime.Token;
    }

    private async Task ClearHintAfterAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, _timeProvider, cancellationToken);
            _presentation.ClearHint();
            _resolver.Reset();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExpireAsync(
        TimeSpan duration,
        ApplicationKeyReplay? replay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, _timeProvider, cancellationToken);
            var expiration = _resolver.Expire(
                _timeProvider.GetUtcNow() + TimeSpan.FromTicks(1));
            _presentation.ClearHint();
            if (expiration.Kind == ApplicationKeyResolutionKind.Expired)
            {
                await ReplayAsync(replay, expiration.ReplayStrokes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReplayAsync(
        ApplicationKeyReplay? replay,
        IReadOnlyList<KeyStroke>? strokes)
    {
        if (strokes is null || strokes.Count == 0)
        {
            return;
        }

        if (replay is null)
        {
            _presentation.SetError(
                "The application shortcut could not be passed through because no terminal is active.");
            return;
        }

        if (!await replay(strokes, _lifetime.Token))
        {
            _presentation.SetError(
                "The application shortcut could not be passed through safely.");
        }
    }

    private void ClearHint()
    {
        _hintLifetime?.Cancel();
        _hintLifetime?.Dispose();
        _hintLifetime = null;
        _presentation.ClearHint();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hintLifetime?.Cancel();
        _hintLifetime?.Dispose();
        _hintLifetime = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
