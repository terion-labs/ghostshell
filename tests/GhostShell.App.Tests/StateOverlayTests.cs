using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Common;
using GhostShell.App.Controls;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class StateOverlayTests
{
    [Fact]
    public void Every_state_has_a_complete_non_colour_presentation()
    {
        var states = Enum.GetValues<StateOverlayKind>();

        Assert.Equal(13, states.Length);
        Assert.Equal(
            states.Length,
            states
                .Select(StateOverlayPresentation.For)
                .Select(state => state.StateLabel)
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var kind in states)
        {
            var presentation = StateOverlayPresentation.For(kind);
            var overlay = new StateOverlay
            {
                Kind = kind,
                Heading = $"{presentation.StateLabel} heading",
                Body = "Recovery guidance",
            };

            Assert.Equal(presentation.Glyph, overlay.EffectiveGlyph);
            Assert.Equal(presentation.Tone, overlay.PresentationTone);
            Assert.Equal(presentation.LiveSetting, overlay.AnnouncementMode);
            Assert.Contains(presentation.StateLabel, overlay.AccessibleStatus, StringComparison.Ordinal);
            Assert.Contains("Recovery guidance", overlay.AccessibleStatus, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_screen_can_override_the_glyph_without_changing_state_semantics()
    {
        var overlay = new StateOverlay
        {
            Kind = StateOverlayKind.PermissionRequired,
            Glyph = Symbol.Key,
        };

        Assert.Equal(Symbol.Key, overlay.EffectiveGlyph);
        Assert.Equal(SurfaceTone.Warning, overlay.PresentationTone);
        Assert.Equal(AutomationLiveSetting.Assertive, overlay.AnnouncementMode);
    }

    [Fact]
    public Task Changing_state_templates_never_announces_an_empty_accessible_name() =>
        RunHeadlessAsync(async () =>
        {
            var emptyAnnouncements = new List<string>();
            using var subscription = AutomationProperties.NameProperty.Changed.AddClassHandler<Control>(
                (control, _) =>
                {
                    if (AutomationProperties.GetLiveSetting(control) != AutomationLiveSetting.Off
                        && string.IsNullOrEmpty(AutomationProperties.GetName(control)))
                    {
                        emptyAnnouncements.Add(control.GetType().Name);
                    }
                });
            var overlay = new StateOverlay { Kind = StateOverlayKind.Loading, Heading = "Opening browser" };
            var window = new Window { Content = overlay };
            try
            {
                window.Show();
                window.UpdateLayout();
                foreach (var kind in Enum.GetValues<StateOverlayKind>())
                {
                    overlay.Kind = kind;
                    window.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }

                window.Content = null;
                Assert.Empty(emptyAnnouncements);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task Primary_action_raises_the_shared_event_and_receives_requested_focus() =>
        RunHeadlessAsync(async () =>
        {
            var requested = 0;
            var overlay = new StateOverlay
            {
                Kind = StateOverlayKind.Retry,
                Heading = "Connection interrupted",
                Body = "The draft is retained.",
                ActionLabel = "Retry",
                FocusTarget = StateOverlayFocusTarget.PrimaryAction,
            };
            overlay.ActionRequested += (_, _) => requested++;
            var window = new Window
            {
                Width = 600,
                Height = 400,
                Content = overlay,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(
                    () => { },
                    DispatcherPriority.Background);

                var action = Assert.Single(
                    overlay.GetVisualDescendants().OfType<Button>(),
                    button => button.Content as string == "Retry");
                Assert.True(action.IsFocused);

                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(1, requested);
                var liveRegion = Assert.Single(
                    overlay.GetVisualDescendants().OfType<Control>(),
                    control => AutomationProperties.GetName(control) == overlay.AccessibleStatus);
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(liveRegion));
            }
            finally
            {
                window.Close();
            }
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
}
