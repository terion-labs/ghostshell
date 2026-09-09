using Asura.Application;
using Asura.Core;

namespace Asura.App.Controls;

internal enum TerminalKeyResolutionKind
{
    NotHandled,
    Pending,
    Matched,
    Rejected,
    PassedThrough,
    Expired,
}

internal readonly record struct TerminalKeyResolution(
    TerminalKeyResolutionKind Kind,
    CommandBinding? Binding = null,
    bool ShouldHandle = false,
    IReadOnlyList<KeyStroke>? ReplayStrokes = null);

/// <summary>
/// Resolves the immutable keymap captured by one terminal launch. Sequence state belongs to
/// the surface, so replacing a surface or a launch snapshot always clears pending input.
/// </summary>
internal sealed class ManagedTerminalKeymapResolver
{
    private static readonly TimeSpan DefaultSequenceTimeout = TimeSpan.FromMilliseconds(750);
    private readonly TerminalKeymapSnapshot _keymap;
    private readonly List<KeyStroke> _pending = [];
    private DateTimeOffset? _pendingStartedAt;
    private DateTimeOffset? _repeatStartedAt;

    public ManagedTerminalKeymapResolver(TerminalKeymapSnapshot keymap)
    {
        _keymap = keymap ?? throw new ArgumentNullException(nameof(keymap));
    }

    public TerminalKeyResolution Resolve(KeyStroke stroke, DateTimeOffset timestamp)
    {
        if (_pending.Count > 0)
        {
            _pending.Add(stroke);
            var continuation = ResolvePending(timestamp);
            if (continuation.Kind != TerminalKeyResolutionKind.NotHandled)
            {
                return continuation;
            }

            var buffered = _pending.ToArray();
            ResetPending();
            _repeatStartedAt = null;
            return _keymap.Prefix?.FailedSequenceBehavior
                    == FailedSequenceBehavior.PassThrough
                ? new TerminalKeyResolution(
                    TerminalKeyResolutionKind.PassedThrough,
                    ShouldHandle: true,
                    ReplayStrokes: buffered)
                : new TerminalKeyResolution(
                    TerminalKeyResolutionKind.Rejected,
                    ShouldHandle: true);
        }

        if (_keymap.Prefix is { Repeatable: true } repeatablePrefix
            && _repeatStartedAt is { } repeatStartedAt)
        {
            _repeatStartedAt = null;
            if (timestamp - repeatStartedAt <= repeatablePrefix.Timeout)
            {
                var repeatedBinding = FindRepeatedBinding(repeatablePrefix.Stroke, stroke);
                if (repeatedBinding is not null)
                {
                    _repeatStartedAt = timestamp;
                    return new TerminalKeyResolution(
                        TerminalKeyResolutionKind.Matched,
                        repeatedBinding,
                        ShouldHandle: true);
                }
            }
        }

        var direct = FindExact([stroke]);
        if (direct is not null)
        {
            return new TerminalKeyResolution(
                TerminalKeyResolutionKind.Matched,
                direct,
                ShouldHandle: true);
        }

        if (!HasContinuation([stroke]))
        {
            return new TerminalKeyResolution(TerminalKeyResolutionKind.NotHandled);
        }

        _pending.Add(stroke);
        _pendingStartedAt = timestamp;
        return new TerminalKeyResolution(
            TerminalKeyResolutionKind.Pending,
            ShouldHandle: true);
    }

    public void Reset()
    {
        ResetPending();
        _repeatStartedAt = null;
    }

    public DateTimeOffset? PendingDeadline => _pendingStartedAt is { } startedAt
        ? startedAt + SequenceTimeout
        : null;

    public TimeSpan SequenceTimeout => _keymap.Prefix?.Timeout ?? DefaultSequenceTimeout;

    public TerminalKeyResolution Expire(DateTimeOffset timestamp)
    {
        if (!PendingExpired(timestamp))
        {
            return new TerminalKeyResolution(TerminalKeyResolutionKind.NotHandled);
        }

        var buffered = _pending.ToArray();
        ResetPending();
        return _keymap.Prefix?.FailedSequenceBehavior
                == FailedSequenceBehavior.PassThrough
            ? new TerminalKeyResolution(
                TerminalKeyResolutionKind.Expired,
                ShouldHandle: true,
                ReplayStrokes: buffered)
            : new TerminalKeyResolution(
                TerminalKeyResolutionKind.Expired,
                ShouldHandle: true);
    }

    private TerminalKeyResolution ResolvePending(DateTimeOffset timestamp)
    {
        var exact = FindExact(_pending);
        if (exact is not null)
        {
            var matchedPrefix = _keymap.Prefix is { } prefix
                && _pending.Count > 1
                && _pending[0] == prefix.Stroke;
            ResetPending();
            _repeatStartedAt = matchedPrefix && _keymap.Prefix?.Repeatable == true
                ? timestamp
                : null;
            return new TerminalKeyResolution(
                TerminalKeyResolutionKind.Matched,
                exact,
                ShouldHandle: true);
        }

        if (!HasContinuation(_pending))
        {
            return new TerminalKeyResolution(TerminalKeyResolutionKind.NotHandled);
        }

        _pendingStartedAt = timestamp;
        return new TerminalKeyResolution(
            TerminalKeyResolutionKind.Pending,
            ShouldHandle: true);
    }

    private CommandBinding? FindExact(IReadOnlyList<KeyStroke> strokes) =>
        ApplicableBindings().FirstOrDefault(binding =>
            binding.Sequence.Count == strokes.Count
            && binding.Sequence.Strokes.SequenceEqual(strokes));

    private bool HasContinuation(IReadOnlyList<KeyStroke> strokes) =>
        ApplicableBindings().Any(binding =>
            binding.Sequence.Count > strokes.Count
            && binding.Sequence.Strokes.Take(strokes.Count).SequenceEqual(strokes));

    private CommandBinding? FindRepeatedBinding(KeyStroke prefix, KeyStroke suffix) =>
        ApplicableBindings().FirstOrDefault(binding =>
            binding.Sequence.Count == 2
            && binding.Sequence[0] == prefix
            && binding.Sequence[1] == suffix);

    private IEnumerable<CommandBinding> ApplicableBindings() => _keymap.Bindings.Where(binding =>
        (binding.Contexts & (CommandContext.Global | CommandContext.Terminal)) != CommandContext.None);

    private bool PendingExpired(DateTimeOffset timestamp) =>
        _pendingStartedAt is { } startedAt
        && timestamp - startedAt > (_keymap.Prefix?.Timeout ?? DefaultSequenceTimeout);

    private void ResetPending()
    {
        _pending.Clear();
        _pendingStartedAt = null;
    }
}
