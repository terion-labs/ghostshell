using Asura.App.Views;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class MainWindowAppearanceTests
{
    [Fact]
    public void Platform_profile_picker_includes_every_durable_profile()
    {
        Assert.Equal(
            Enum.GetValues<PlatformProfile>(),
            MainWindow.AppearancePlatformProfiles);
        Assert.Contains(
            PlatformProfile.Custom,
            MainWindow.AppearancePlatformProfiles);
    }

    [Fact]
    public async Task Appearance_refresh_reapplies_the_quick_terminal_backdrop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                () =>
                {
                    var window = new QuickTerminalWindow
                    {
                        TransparencyLevelHint =
                        [
                            WindowTransparencyLevel.AcrylicBlur,
                            WindowTransparencyLevel.Blur,
                        ],
                    };

                    App.RefreshWindowBackdrop(window);

                    Assert.Equal(
                        [WindowTransparencyLevel.Transparent],
                        window.TransparencyLevelHint);
                    return Task.FromResult(true);
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
