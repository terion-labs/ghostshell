using Asura.App.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class PanelAgentActivityHeadlessTests
{
    [Fact]
    public Task Only_the_exact_leased_panel_shows_the_activity_glow() =>
        RunHeadlessAsync(() =>
        {
            var leasedPanel = new PanelChrome
            {
                Title = "Leased",
                IsAgentActive = true,
                Content = new Border(),
            };
            var idlePanel = new PanelChrome
            {
                Title = "Idle",
                IsAgentActive = false,
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
                        leasedPanel,
                        idlePanel,
                    },
                },
            };
            Grid.SetColumn(idlePanel, 1);

            window.Show();
            window.UpdateLayout();

            var leasedGlow = FindGlow(leasedPanel);
            var idleGlow = FindGlow(idlePanel);
            var leasedSurface = Assert.Single(
                leasedPanel.GetVisualDescendants().OfType<SurfaceCard>());
            Assert.True(leasedGlow.IsEffectivelyVisible);
            Assert.False(idleGlow.IsEffectivelyVisible);
            Assert.Equal(leasedSurface.CornerRadius, leasedGlow.CornerRadius);
            Assert.Equal(new Thickness(0), leasedGlow.Margin);
            Assert.NotNull(leasedGlow.Background);
            Assert.Equal(4, leasedGlow.BoxShadow.Count);
            Assert.Equal(224, leasedGlow.BoxShadow[0].Blur);
            Assert.Equal(112, leasedGlow.BoxShadow[1].Blur);
            Assert.Equal(51.2, leasedGlow.BoxShadow[2].Blur);
            Assert.Equal(17.6, leasedGlow.BoxShadow[3].Blur);
            Assert.DoesNotContain(
                leasedPanel.GetVisualDescendants().OfType<SymbolIcon>(),
                icon => string.Equals(icon.Symbol.ToString(), "Bot", StringComparison.Ordinal));
            Assert.DoesNotContain(
                idlePanel.GetVisualDescendants().OfType<SymbolIcon>(),
                icon => string.Equals(icon.Symbol.ToString(), "Bot", StringComparison.Ordinal));

            window.Close();
            return Task.CompletedTask;
        });

    private static Border FindGlow(PanelChrome chrome) =>
        Assert.Single(
            chrome.GetVisualDescendants()
                .OfType<Border>(),
            border => border.Classes.Contains("PanelAgentGlow"));

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
