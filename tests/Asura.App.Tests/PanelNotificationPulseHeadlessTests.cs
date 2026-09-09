using Asura.App.Controls;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class PanelNotificationPulseHeadlessTests
{
    [Fact]
    public Task Only_the_exact_notifying_panel_shows_the_transient_overlay() =>
        RunHeadlessAsync(() =>
        {
            var pulsingPanel = new PanelChrome
            {
                Title = "Notifying",
                IsNotificationPulseActive = true,
                Content = new Border(),
            };
            var idlePanel = new PanelChrome
            {
                Title = "Idle",
                Content = new Border(),
            };
            var window = new Window
            {
                Width = 800,
                Height = 500,
                Content = new Grid
                {
                    ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
                    Children =
                    {
                        pulsingPanel,
                        idlePanel,
                    },
                },
            };
            Grid.SetColumn(idlePanel, 1);

            window.Show();
            window.UpdateLayout();

            Assert.True(FindPulse(pulsingPanel).IsEffectivelyVisible);
            Assert.False(FindPulse(idlePanel).IsEffectivelyVisible);

            pulsingPanel.IsNotificationPulseActive = false;
            window.UpdateLayout();

            Assert.False(FindPulse(pulsingPanel).IsEffectivelyVisible);
            window.Close();
            return Task.CompletedTask;
        });

    private static Border FindPulse(PanelChrome chrome) =>
        Assert.Single(
            chrome.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("PanelNotificationPulse"));

    private static async Task RunHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
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
