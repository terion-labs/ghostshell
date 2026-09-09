using Asura.Core;
using Avalonia.Input;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using CoreKeyModifiers = Asura.Core.KeyModifiers;

namespace Asura.App;

internal enum ApplicationKeyResolutionKind
{
    NotHandled,
    Pending,
    Matched,
    Rejected,
    PassedThrough,
    Expired,
}

internal readonly record struct ApplicationKeyResolution(
    ApplicationKeyResolutionKind Kind,
    CommandBinding? Binding = null,
    bool ShouldHandle = false,
    IReadOnlyList<KeyStroke>? ReplayStrokes = null)
{
    public static ApplicationKeyResolution NotHandled() => new(
        ApplicationKeyResolutionKind.NotHandled);

    public static ApplicationKeyResolution Pending() => new(
        ApplicationKeyResolutionKind.Pending,
        ShouldHandle: true);

    public static ApplicationKeyResolution Matched(CommandBinding binding) => new(
        ApplicationKeyResolutionKind.Matched,
        binding,
        ShouldHandle: true);

    public static ApplicationKeyResolution Rejected(bool shouldHandle) => new(
        ApplicationKeyResolutionKind.Rejected,
        ShouldHandle: shouldHandle);

    public static ApplicationKeyResolution PassedThrough(params KeyStroke[] strokes) => new(
        ApplicationKeyResolutionKind.PassedThrough,
        ShouldHandle: true,
        ReplayStrokes: Array.AsReadOnly(strokes));

    public static ApplicationKeyResolution Expired(IReadOnlyList<KeyStroke>? replayStrokes) => new(
        ApplicationKeyResolutionKind.Expired,
        ShouldHandle: false,
        ReplayStrokes: replayStrokes);
}

/// <summary>
/// Resolves one application-layer keymap. The resolver owns prefix timing only;
/// command validation and execution remain in <see cref="ApplicationCommandRouter"/>.
/// </summary>
internal sealed class ApplicationKeySequenceResolver
{
    private readonly KeymapProfile _profile;
    private readonly PrefixConfiguration? _prefix;
    private DateTimeOffset? _prefixStartedAt;
    private DateTimeOffset? _repeatStartedAt;

    public ApplicationKeySequenceResolver(KeymapProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Layer != KeymapLayer.Application)
        {
            throw new ArgumentException("The keymap must be an application-layer profile.", nameof(profile));
        }

        _profile = profile;
        _prefix = profile.Prefix;
    }

    public ApplicationKeyResolution Resolve(
        KeyStroke stroke,
        CommandContext activeContexts,
        DateTimeOffset timestamp)
    {
        if (_prefix is { } prefix && _prefixStartedAt is { } prefixStartedAt)
        {
            if (timestamp - prefixStartedAt <= prefix.Timeout)
            {
                _prefixStartedAt = null;
                var binding = FindBinding(stroke, activeContexts);
                if (binding is not null)
                {
                    ArmRepeat(timestamp);
                    return ApplicationKeyResolution.Matched(binding);
                }

                _repeatStartedAt = null;
                return prefix.FailedSequenceBehavior == FailedSequenceBehavior.PassThrough
                    ? ApplicationKeyResolution.PassedThrough(prefix.Stroke, stroke)
                    : ApplicationKeyResolution.Rejected(shouldHandle: true);
            }

            _prefixStartedAt = null;
            if (prefix.FailedSequenceBehavior == FailedSequenceBehavior.PassThrough)
            {
                _repeatStartedAt = null;
                return ApplicationKeyResolution.PassedThrough(prefix.Stroke, stroke);
            }
        }

        if (_prefix is { } repeatPrefix
            && repeatPrefix.Repeatable
            && _repeatStartedAt is { } repeatStartedAt
            && timestamp - repeatStartedAt <= repeatPrefix.Timeout)
        {
            var repeatedBinding = FindBinding(stroke, activeContexts);
            if (repeatedBinding is not null)
            {
                ArmRepeat(timestamp);
                return ApplicationKeyResolution.Matched(repeatedBinding);
            }

            _repeatStartedAt = null;
        }

        if (_prefix is { } candidatePrefix
            && stroke == candidatePrefix.Stroke
            && HasApplicablePrefixedBinding(activeContexts))
        {
            _prefixStartedAt = timestamp;
            return ApplicationKeyResolution.Pending();
        }

        var directBinding = FindDirectBinding(stroke, activeContexts);
        if (directBinding is not null)
        {
            _repeatStartedAt = null;
            return ApplicationKeyResolution.Matched(directBinding);
        }

        return ApplicationKeyResolution.NotHandled();
    }

    public void Reset()
    {
        _prefixStartedAt = null;
        _repeatStartedAt = null;
    }

    public DateTimeOffset? PendingDeadline => _prefixStartedAt is { } startedAt
        && _prefix is { } prefix
            ? startedAt + prefix.Timeout
            : null;

    public ApplicationKeyResolution Expire(DateTimeOffset timestamp)
    {
        if (_prefixStartedAt is not { } startedAt
            || _prefix is not { } prefix
            || timestamp - startedAt <= prefix.Timeout)
        {
            return ApplicationKeyResolution.NotHandled();
        }

        _prefixStartedAt = null;
        _repeatStartedAt = null;
        return ApplicationKeyResolution.Expired(
            prefix.FailedSequenceBehavior == FailedSequenceBehavior.PassThrough
                ? Array.AsReadOnly([prefix.Stroke])
                : null);
    }

    private CommandBinding? FindBinding(KeyStroke suffix, CommandContext activeContexts) =>
        _prefix is { } prefix
            ? _profile.Bindings.FirstOrDefault(binding =>
                IsApplicable(binding, activeContexts)
                && binding.Sequence.Count == 2
                && binding.Sequence[0] == prefix.Stroke
                && binding.Sequence[1] == suffix)
            : null;

    private CommandBinding? FindDirectBinding(
        KeyStroke stroke,
        CommandContext activeContexts) => _profile.Bindings.FirstOrDefault(binding =>
            IsApplicable(binding, activeContexts)
            && binding.Sequence.Count == 1
            && binding.Sequence[0] == stroke);

    private bool HasApplicablePrefixedBinding(CommandContext activeContexts) =>
        _prefix is { } prefix
        && _profile.Bindings.Any(binding =>
            IsApplicable(binding, activeContexts)
            && binding.Sequence.Count == 2
            && binding.Sequence[0] == prefix.Stroke);

    private static bool IsApplicable(CommandBinding binding, CommandContext activeContexts) =>
        (binding.Contexts & CommandContext.Global) != CommandContext.None || (binding.Contexts & activeContexts) != CommandContext.None;

    private void ArmRepeat(DateTimeOffset timestamp) =>
        _repeatStartedAt = _prefix?.Repeatable == true ? timestamp : null;
}

internal static class ApplicationKeyStrokeMapper
{
    public static KeyStroke Map(
        Key key,
        AvaloniaKeyModifiers modifiers,
        string? keySymbol = null)
    {
        var mappedModifiers = MapModifiers(modifiers);
        if (TryMapSemanticCharacter(key, modifiers, keySymbol, out var character))
        {
            return new KeyStroke(character, mappedModifiers & ~CoreKeyModifiers.Shift);
        }

        return new KeyStroke(MapKeyName(key), mappedModifiers);
    }

    private static CoreKeyModifiers MapModifiers(AvaloniaKeyModifiers modifiers)
    {
        var mapped = CoreKeyModifiers.None;
        if ((modifiers & AvaloniaKeyModifiers.Control) != AvaloniaKeyModifiers.None)
        {
            mapped |= CoreKeyModifiers.Control;
        }

        if ((modifiers & AvaloniaKeyModifiers.Alt) != AvaloniaKeyModifiers.None)
        {
            mapped |= CoreKeyModifiers.Alt;
        }

        if ((modifiers & AvaloniaKeyModifiers.Shift) != AvaloniaKeyModifiers.None)
        {
            mapped |= CoreKeyModifiers.Shift;
        }

        if ((modifiers & AvaloniaKeyModifiers.Meta) != AvaloniaKeyModifiers.None)
        {
            mapped |= CoreKeyModifiers.Meta;
        }

        return mapped;
    }

    private static bool TryMapSemanticCharacter(
        Key key,
        AvaloniaKeyModifiers modifiers,
        string? keySymbol,
        out string character)
    {
        if (keySymbol is ['%' or '"' or '&' or ',' or '[' or '+' or '-'])
        {
            character = keySymbol;
            return true;
        }

        var shifted = (modifiers & AvaloniaKeyModifiers.Shift) != AvaloniaKeyModifiers.None;
        character = (key.ToString(), shifted) switch
        {
            ("D5", true) => "%",
            ("D7", true) => "&",
            ("OemQuotes", true) => "\"",
            ("OemComma", false) => ",",
            ("OemOpenBrackets" or "Oem4", false) => "[",
            _ => string.Empty,
        };
        return character.Length > 0;
    }

    private static string MapKeyName(Key key)
    {
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1]))
        {
            return name[1].ToString();
        }

        const string numPadPrefix = "NumPad";
        if (name.StartsWith(numPadPrefix, StringComparison.Ordinal)
            && name.Length == numPadPrefix.Length + 1
            && char.IsAsciiDigit(name[^1]))
        {
            return name[^1].ToString();
        }

        return key switch
        {
            Key.Left => "ARROWLEFT",
            Key.Right => "ARROWRIGHT",
            Key.Up => "ARROWUP",
            Key.Down => "ARROWDOWN",
            Key.Return or Key.Enter => "ENTER",
            Key.Back => "BACKSPACE",
            Key.Add => "+",
            Key.OemMinus or Key.Subtract => "-",
            _ => name,
        };
    }
}
