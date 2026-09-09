using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

[Collection("native global hotkey")]
public sealed class MacOsGlobalHotkeyServiceTests
{
    private static readonly KeyStroke IsolatedGesture = new(
        "GRAVE",
        KeyModifiers.Control
        | KeyModifiers.Alt
        | KeyModifiers.Shift
        | KeyModifiers.Meta);

    [Theory]
    [InlineData("GRAVE", KeyModifiers.Meta, 50U, 0x100U)]
    [InlineData("T", KeyModifiers.Meta, 17U, 0x100U)]
    [InlineData("K", KeyModifiers.Control | KeyModifiers.Alt, 40U, 0x1800U)]
    [InlineData(
        "F12",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta,
        111U,
        0x1B00U)]
    public void Gesture_mapping_uses_macOS_virtual_keys_and_modifier_masks(
        string key,
        KeyModifiers modifiers,
        uint expectedVirtualKey,
        uint expectedModifiers)
    {
        Assert.True(MacOsGlobalHotkeyService.TryMapGesture(
            new KeyStroke(key, modifiers),
            out var actual));
        Assert.Equal(expectedVirtualKey, actual.VirtualKey);
        Assert.Equal(expectedModifiers, actual.Modifiers);
    }

    [Fact]
    public void Gesture_mapping_rejects_modifierless_and_unknown_keys()
    {
        Assert.False(MacOsGlobalHotkeyService.TryMapGesture(
            new KeyStroke("T"),
            out _));
        Assert.False(MacOsGlobalHotkeyService.TryMapGesture(
            new KeyStroke("NOTAKEY", KeyModifiers.Meta),
            out _));
    }

    [Fact]
    public void Command_letter_can_be_registered_as_a_native_global_hotkey()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var service = new MacOsGlobalHotkeyService();

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(
            service.Register(new KeyStroke("T", KeyModifiers.Meta)));
    }

    [Fact]
    public void Duplicate_primary_registration_reports_conflict_and_release_allows_retry()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var first = new MacOsGlobalHotkeyService();
        using var second = new MacOsGlobalHotkeyService();

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(first.Register(IsolatedGesture));
        var conflict = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            second.Register(IsolatedGesture));
        Assert.Equal(GlobalHotkeyRegistrationErrorCode.Conflict, conflict.Error.Code);

        first.Unregister();
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(second.Register(IsolatedGesture));
    }

}
