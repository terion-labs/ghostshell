using Asura.App.Views;
using Avalonia.Input;

namespace Asura.App.Tests;

public sealed class MainWindowKeyboardPolicyTests
{
    [Fact]
    public void New_terminal_reuses_any_existing_runtime_regardless_of_visible_route()
    {
        Assert.Equal(
            NewTerminalTarget.ExistingRuntimeWorkspace,
            MainWindow.ResolveNewTerminalTarget(hasRuntimeWorkspace: true));
        Assert.Equal(
            NewTerminalTarget.DefaultConnectionWorkspace,
            MainWindow.ResolveNewTerminalTarget(hasRuntimeWorkspace: false));
    }

    [Theory]
    [InlineData(KeyModifiers.None, false)]
    [InlineData(KeyModifiers.Control, true)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, false)]
    public void Global_gestures_require_the_exact_modifier_set(
        KeyModifiers actualModifiers,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.IsExactGlobalGesture(
                Key.K,
                actualModifiers,
                Key.K,
                KeyModifiers.Control));
    }

    [Fact]
    public void Global_gestures_require_the_expected_key()
    {
        Assert.False(MainWindow.IsExactGlobalGesture(
            Key.T,
            KeyModifiers.Control,
            Key.K,
            KeyModifiers.Control));
    }
}
