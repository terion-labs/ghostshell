using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.RuntimePanels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class TerminalRuntimePanelContinuityBadgeHeadlessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Badge_reflects_the_terminal_runtime_state(bool isContinuityActive) =>
        RunHeadlessAsync(() =>
        {
            var view = new TerminalRuntimePanelView
            {
                DataContext = new TerminalHeaderState(isContinuityActive),
                IsVisible = true,
            };
            var window = new Window
            {
                Width = 800,
                Height = 500,
                Content = view,
            };
            window.Show();
            window.UpdateLayout();

            var visibleBadges = view.GetVisualDescendants()
                .OfType<SymbolIcon>()
                .Where(icon => string.Equals(AutomationProperties.GetName(icon), "Continuity enabled", StringComparison.Ordinal))
                .Where(icon => icon.IsEffectivelyVisible)
                .ToArray();
            if (isContinuityActive)
            {
                var badge = Assert.Single(visibleBadges);
                Assert.Equal("Agents", badge.Symbol.ToString());
                var titleLine = Assert.Single(
                    view.GetVisualDescendants().OfType<PanelTitleLine>());
                var title = Assert.IsType<TextBlock>(titleLine.Children[0]);
                var titleTop = Assert.NotNull(title.TranslatePoint(default, titleLine));
                var badgeTop = Assert.NotNull(badge.TranslatePoint(default, titleLine));
                Assert.InRange(
                    Math.Abs(titleLine.Bounds.Height - title.Bounds.Height),
                    0,
                    0.25);
                Assert.InRange(Math.Abs(titleTop.Y), 0, 0.25);
                var titleBaseline = title.Padding.Top
                    + title.TextLayout.Baseline
                    + title.BaselineOffset;
                var baselineDifference = badgeTop.Y + badge.Bounds.Height
                    - titleTop.Y - titleBaseline;
                Assert.True(
                    baselineDifference is >= 0.75 and <= 1.25,
                    $"title={title.Bounds} baseline={titleBaseline} titleTop={titleTop} "
                    + $"badge={badge.Bounds} badgeTop={badgeTop} line={titleLine.Bounds} "
                    + $"difference={baselineDifference}");

                var glowLayers = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(border => border.Classes.Contains("ContinuityGlow"))
                    .OrderByDescending(border => border.Bounds.Width)
                    .ToArray();
                Assert.Equal(6, glowLayers.Length);
                Assert.True(glowLayers[^1].Bounds.Width > badge.Bounds.Width);
                Assert.True(glowLayers[^1].Bounds.Height > badge.Bounds.Height);
                for (var index = 1; index < glowLayers.Length; index++)
                {
                    Assert.True(glowLayers[index - 1].Bounds.Width > glowLayers[index].Bounds.Width);
                    Assert.True(glowLayers[index - 1].Bounds.Height > glowLayers[index].Bounds.Height);
                }
                Assert.DoesNotContain(
                    badge.GetVisualAncestors(),
                    ancestor => ancestor is TextBlock);
            }
            else
            {
                Assert.Empty(visibleBadges);
            }

            window.Close();
            return Task.CompletedTask;
        });

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

    private sealed record TerminalHeaderState(bool IsContinuityActive)
        : ITerminalContinuityState
    {
        public bool IsVisibleInLayout => true;
    }
}
