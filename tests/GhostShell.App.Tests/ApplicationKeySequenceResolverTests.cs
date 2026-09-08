using Avalonia.Input;
using GhostShell.App.ViewModels;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using CoreKeyModifiers = GhostShell.Core.KeyModifiers;

namespace GhostShell.App.Tests;

public sealed class ApplicationKeySequenceResolverTests
{
    [Fact]
    public void EveryDeclaredApplicationBindingResolvesWithItsArgumentsIntact()
    {
        var resolver = new ApplicationKeySequenceResolver(BuiltInKeymaps.TmuxApplication);
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var binding in BuiltInKeymaps.TmuxApplication.Bindings)
        {
            resolver.Reset();

            ApplicationKeyResolution match;
            if (binding.Sequence.Count == 1)
            {
                match = resolver.Resolve(binding.Sequence[0], binding.Contexts, timestamp);
            }
            else
            {
                var prefix = resolver.Resolve(binding.Sequence[0], binding.Contexts, timestamp);
                Assert.Equal(ApplicationKeyResolutionKind.Pending, prefix.Kind);
                match = resolver.Resolve(
                    binding.Sequence[1],
                    binding.Contexts,
                    timestamp.AddMilliseconds(10));
            }

            Assert.Equal(ApplicationKeyResolutionKind.Matched, match.Kind);
            Assert.Same(binding, match.Binding);
            Assert.Equal(binding.Arguments, match.Binding!.Arguments);
        }
    }

    [Fact]
    public void TimedOutPrefixDoesNotConsumeTheFollowingTerminalKey()
    {
        var resolver = new ApplicationKeySequenceResolver(BuiltInKeymaps.TmuxApplication);
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(
            new KeyStroke("B", CoreKeyModifiers.Control),
            CommandContext.Workspace,
            timestamp);
        var result = resolver.Resolve(
            new KeyStroke("C"),
            CommandContext.Workspace,
            timestamp.AddMilliseconds(751));

        Assert.Equal(ApplicationKeyResolutionKind.NotHandled, result.Kind);
        Assert.False(result.ShouldHandle);
    }

    [Fact]
    public void UnknownSuffixUsesTheProfilesDiscardAndHintPolicy()
    {
        var resolver = new ApplicationKeySequenceResolver(BuiltInKeymaps.TmuxApplication);
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(
            new KeyStroke("B", CoreKeyModifiers.Control),
            CommandContext.Workspace,
            timestamp);
        var result = resolver.Resolve(
            new KeyStroke("Q"),
            CommandContext.Workspace,
            timestamp.AddMilliseconds(10));

        Assert.Equal(ApplicationKeyResolutionKind.Rejected, result.Kind);
        Assert.True(result.ShouldHandle);
    }

    [Fact]
    public void PassThroughProfileConsumesAndReplaysThePrefixAndUnknownSuffix()
    {
        var prefix = new KeyStroke("B", CoreKeyModifiers.Control);
        var resolver = new ApplicationKeySequenceResolver(PassThroughProfile(prefix));
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(prefix, CommandContext.Workspace, timestamp);
        var result = resolver.Resolve(
            new KeyStroke("Q"),
            CommandContext.Workspace,
            timestamp.AddMilliseconds(10));

        Assert.Equal(ApplicationKeyResolutionKind.PassedThrough, result.Kind);
        Assert.True(result.ShouldHandle);
        Assert.Equal([prefix, new KeyStroke("Q")], result.ReplayStrokes);
    }

    [Fact]
    public void PassThroughProfileReplaysThePrefixWhenItExpiresWithoutASuffix()
    {
        var prefix = new KeyStroke("B", CoreKeyModifiers.Control);
        var resolver = new ApplicationKeySequenceResolver(PassThroughProfile(prefix));
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(prefix, CommandContext.Workspace, timestamp);
        var beforeDeadline = resolver.Expire(timestamp.AddMilliseconds(750));
        var expired = resolver.Expire(timestamp.AddMilliseconds(751));
        var repeatedExpiration = resolver.Expire(timestamp.AddSeconds(2));

        Assert.Equal(ApplicationKeyResolutionKind.NotHandled, beforeDeadline.Kind);
        Assert.Equal(ApplicationKeyResolutionKind.Expired, expired.Kind);
        Assert.Equal([prefix], expired.ReplayStrokes);
        Assert.Equal(ApplicationKeyResolutionKind.NotHandled, repeatedExpiration.Kind);
    }

    [Fact]
    public void LateSuffixIsConsumedAndReplayedAfterAPassThroughPrefix()
    {
        var prefix = new KeyStroke("B", CoreKeyModifiers.Control);
        var suffix = new KeyStroke("Q");
        var resolver = new ApplicationKeySequenceResolver(PassThroughProfile(prefix));
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(prefix, CommandContext.Workspace, timestamp);
        var result = resolver.Resolve(
            suffix,
            CommandContext.Workspace,
            timestamp.AddMilliseconds(751));

        Assert.Equal(ApplicationKeyResolutionKind.PassedThrough, result.Kind);
        Assert.True(result.ShouldHandle);
        Assert.Equal([prefix, suffix], result.ReplayStrokes);
    }

    [Fact]
    public void RepeatableProfileAcceptsAnotherBoundSuffixWithinTheTimeout()
    {
        var resolver = new ApplicationKeySequenceResolver(BuiltInKeymaps.TmuxApplication);
        var timestamp = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        _ = resolver.Resolve(
            new KeyStroke("B", CoreKeyModifiers.Control),
            CommandContext.Tab,
            timestamp);
        var first = resolver.Resolve(
            new KeyStroke("N"),
            CommandContext.Tab,
            timestamp.AddMilliseconds(10));
        var repeated = resolver.Resolve(
            new KeyStroke("P"),
            CommandContext.Tab,
            timestamp.AddMilliseconds(20));

        Assert.Equal(BuiltInCommands.NextTab, first.Binding?.CommandId);
        Assert.Equal(BuiltInCommands.PreviousTab, repeated.Binding?.CommandId);
    }

    [Fact]
    public void PrefixIsNotCapturedWhenNoBindingAppliesToTheActiveContext()
    {
        var resolver = new ApplicationKeySequenceResolver(BuiltInKeymaps.TmuxApplication);

        var result = resolver.Resolve(
            new KeyStroke("B", CoreKeyModifiers.Control),
            CommandContext.Browser,
            DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(ApplicationKeyResolutionKind.NotHandled, result.Kind);
    }

    [Fact]
    public void CustomDirectApplicationBindingResolvesWithoutThePrefix()
    {
        var direct = new CommandBinding(
            BuiltInCommands.NewTab,
            KeySequence.Of(new KeyStroke(
                "T",
                CoreKeyModifiers.Control | CoreKeyModifiers.Shift)),
            CommandContext.Workspace);
        var profile = new KeymapProfile(
            new KeymapProfileId("test.application.direct"),
            "Direct application binding",
            KeymapLayer.Application,
            [direct],
            BuiltInKeymaps.TmuxApplication.Prefix,
            BuiltInKeymaps.TmuxApplicationId);
        var resolver = new ApplicationKeySequenceResolver(profile);

        var result = resolver.Resolve(
            direct.Sequence[0],
            CommandContext.Workspace,
            DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(ApplicationKeyResolutionKind.Matched, result.Kind);
        Assert.Same(direct, result.Binding);
        Assert.True(result.ShouldHandle);
    }

    [Fact]
    public void PrefixlessApplicationProfileStillResolvesDirectBindings()
    {
        var direct = new CommandBinding(
            BuiltInCommands.NewTab,
            KeySequence.Of(new KeyStroke(
                "T",
                CoreKeyModifiers.Control | CoreKeyModifiers.Shift)),
            CommandContext.Workspace);
        var profile = new KeymapProfile(
            new KeymapProfileId("test.application.prefixless"),
            "Prefixless application binding",
            KeymapLayer.Application,
            [direct]);
        var resolver = new ApplicationKeySequenceResolver(profile);

        var result = resolver.Resolve(
            direct.Sequence[0],
            CommandContext.Workspace,
            DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(ApplicationKeyResolutionKind.Matched, result.Kind);
        Assert.Same(direct, result.Binding);
        Assert.True(result.ShouldHandle);
    }

    [Theory]
    [InlineData(Key.D5, AvaloniaKeyModifiers.Shift, null, "%", CoreKeyModifiers.None)]
    [InlineData(Key.D7, AvaloniaKeyModifiers.Shift, null, "&", CoreKeyModifiers.None)]
    [InlineData(Key.OemQuotes, AvaloniaKeyModifiers.Shift, null, "\"", CoreKeyModifiers.None)]
    [InlineData(Key.OemComma, AvaloniaKeyModifiers.None, null, ",", CoreKeyModifiers.None)]
    [InlineData(Key.OemOpenBrackets, AvaloniaKeyModifiers.None, null, "[", CoreKeyModifiers.None)]
    [InlineData(Key.OemPlus, AvaloniaKeyModifiers.Meta | AvaloniaKeyModifiers.Shift, "+", "+", CoreKeyModifiers.Meta)]
    [InlineData(Key.OemPlus, AvaloniaKeyModifiers.Meta, null, "OEMPLUS", CoreKeyModifiers.Meta)]
    [InlineData(Key.OemMinus, AvaloniaKeyModifiers.Meta, "-", "-", CoreKeyModifiers.Meta)]
    [InlineData(Key.OemPeriod, AvaloniaKeyModifiers.Meta, null, "OEMPERIOD", CoreKeyModifiers.Meta)]
    [InlineData(Key.OemCloseBrackets, AvaloniaKeyModifiers.Meta, null, "OEMCLOSEBRACKETS", CoreKeyModifiers.Meta)]
    [InlineData(Key.OemTilde, AvaloniaKeyModifiers.Meta, null, "OEM3", CoreKeyModifiers.Meta)]
    [InlineData(Key.Left, AvaloniaKeyModifiers.None, null, "ARROWLEFT", CoreKeyModifiers.None)]
    [InlineData(Key.D3, AvaloniaKeyModifiers.None, null, "3", CoreKeyModifiers.None)]
    [InlineData(Key.B, AvaloniaKeyModifiers.Control, null, "B", CoreKeyModifiers.Control)]
    [InlineData(Key.T, AvaloniaKeyModifiers.Meta, null, "T", CoreKeyModifiers.Meta)]
    public void AvaloniaKeysMapToDurableKeyStrokeNames(
        Key key,
        AvaloniaKeyModifiers modifiers,
        string? symbol,
        string expectedKey,
        CoreKeyModifiers expectedModifiers)
    {
        var stroke = ApplicationKeyStrokeMapper.Map(key, modifiers, symbol);

        Assert.Equal(expectedKey, stroke.Key);
        Assert.Equal(expectedModifiers, stroke.Modifiers);
    }

    private static KeymapProfile PassThroughProfile(KeyStroke prefix)
    {
        var binding = new CommandBinding(
            BuiltInCommands.NewTab,
            KeySequence.Of(prefix, new KeyStroke("C")),
            CommandContext.Workspace);
        return new KeymapProfile(
            new KeymapProfileId("test.application.pass-through"),
            "Pass-through application",
            KeymapLayer.Application,
            [binding],
            new PrefixConfiguration(
                prefix,
                TimeSpan.FromMilliseconds(750),
                repeatable: false,
                FailedSequenceBehavior.PassThrough));
    }
}

public sealed class ApplicationKeyControllerTests
{
    [Fact]
    public async Task Matched_binding_executes_once_and_reports_handling()
    {
        CommandBinding? executed = null;
        using var controller = CreateController(binding =>
        {
            executed = binding;
            return Task.CompletedTask;
        });
        var binding = new CommandBinding(
            BuiltInCommands.NewTab,
            KeySequence.Of(new KeyStroke("T", CoreKeyModifiers.Control)),
            CommandContext.Workspace);
        var profile = DirectProfile(binding);

        var handling = controller.Resolve(binding.Sequence[0], Snapshot(profile));
        await controller.ApplyAsync(handling, Snapshot(profile), replay: null);

        Assert.NotEqual(ApplicationKeyResolutionKind.NotHandled, handling.Kind);
        Assert.True(handling.ShouldHandle);
        Assert.Same(binding, executed);
    }

    [Fact]
    public async Task Pass_through_without_an_active_terminal_reports_the_boundary_error()
    {
        string? error = null;
        using var controller = CreateController(
            _ => Task.CompletedTask,
            setError: message => error = message);
        var prefix = new KeyStroke("B", CoreKeyModifiers.Control);
        var profile = PassThroughProfile(prefix, TimeSpan.FromSeconds(1));

        await controller.ApplyAsync(controller.Resolve(prefix, Snapshot(profile)), Snapshot(profile), replay: null);
        var handling = controller.Resolve(new KeyStroke("Q"), Snapshot(profile));
        await controller.ApplyAsync(handling, Snapshot(profile), replay: null);

        Assert.NotEqual(ApplicationKeyResolutionKind.NotHandled, handling.Kind);
        Assert.True(handling.ShouldHandle);
        Assert.Contains("no terminal is active", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposal_cancels_pending_expiry_and_replay()
    {
        var replayCount = 0;
        var prefix = new KeyStroke("B", CoreKeyModifiers.Control);
        var profile = PassThroughProfile(prefix, TimeSpan.FromMilliseconds(25));
        var controller = CreateController(_ => Task.CompletedTask);

        await controller.ApplyAsync(
            controller.Resolve(prefix, Snapshot(profile)),
            Snapshot(profile),
            (_, _) =>
            {
                replayCount++;
                return ValueTask.FromResult(true);
            });
        controller.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, replayCount);
    }

    private static ApplicationKeyController CreateController(
        Func<CommandBinding, Task> execute,
        Action<string>? setError = null) => new(
        new ApplicationKeyPresentation(
            execute,
            _ => { },
            () => { },
            setError ?? (_ => { })),
        CancellationToken.None);

    private static ApplicationKeyProfileSnapshot Snapshot(KeymapProfile profile) =>
        new(profile, Revision: 1, profile.Name, CommandContext.Workspace);

    private static KeymapProfile DirectProfile(CommandBinding binding) => new(
        new KeymapProfileId("test.application.direct.controller"),
        "Direct controller",
        KeymapLayer.Application,
        [binding],
        prefix: null);

    private static KeymapProfile PassThroughProfile(
        KeyStroke prefix,
        TimeSpan timeout)
    {
        var binding = new CommandBinding(
            BuiltInCommands.NewTab,
            KeySequence.Of(prefix, new KeyStroke("C")),
            CommandContext.Workspace);
        return new KeymapProfile(
            new KeymapProfileId("test.application.pass-through.controller"),
            "Pass-through controller",
            KeymapLayer.Application,
            [binding],
            new PrefixConfiguration(
                prefix,
                timeout,
                repeatable: false,
                FailedSequenceBehavior.PassThrough));
    }
}

public sealed class ApplicationCommandRouterTests
{
    [Fact]
    public void EveryDeclaredTmuxBindingRoutesThroughTheCommandRegistry()
    {
        foreach (var binding in BuiltInKeymaps.TmuxApplication.Bindings)
        {
            var result = ApplicationCommandRouter.Route(
                binding.CommandId,
                binding.Arguments,
                binding.Contexts);

            Assert.True(result.IsSuccess, $"{binding.Sequence}: {result.Error}");
        }
    }

    [Fact]
    public void BindingArgumentsBecomeTypedExecutionParameters()
    {
        var bindings = BuiltInKeymaps.TmuxApplication.Bindings;

        var horizontal = Route(bindings.Single(binding =>
            binding.CommandId == BuiltInCommands.SplitPanel
            && string.Equals(binding.Arguments["orientation"], "left-right", StringComparison.Ordinal)));
        var vertical = Route(bindings.Single(binding =>
            binding.CommandId == BuiltInCommands.SplitPanel
            && string.Equals(binding.Arguments["orientation"], "top-bottom", StringComparison.Ordinal)));
        var focus = Route(bindings.Single(binding =>
            binding.CommandId == BuiltInCommands.FocusPanel
            && string.Equals(binding.Arguments["direction"], "down", StringComparison.Ordinal)));
        var position = Route(bindings.Single(binding =>
            binding.CommandId == BuiltInCommands.SelectTab
            && string.Equals(binding.Arguments["position"], "9", StringComparison.Ordinal)));
        var workspace = Route(bindings.Single(binding =>
            binding.CommandId == BuiltInCommands.SelectWorkspace
            && string.Equals(binding.Arguments["position"], "8", StringComparison.Ordinal)));

        Assert.Equal(PanelSplitOrientation.LeftRight, horizontal.SplitOrientation);
        Assert.Equal(PanelSplitOrientation.TopBottom, vertical.SplitOrientation);
        Assert.Equal(PanelFocusDirection.Down, focus.FocusDirection);
        Assert.Equal(9, position.TabPosition);
        Assert.Equal(8, workspace.WorkspacePosition);
    }

    [Fact]
    public void RegistryRejectsMissingRequiredParameters()
    {
        var result = ApplicationCommandRouter.Route(
            BuiltInCommands.SplitPanel,
            new Dictionary<string, string>(StringComparer.Ordinal),
            CommandContext.Panel);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid arguments", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tab.move-left", -1)]
    [InlineData("tab.move-right", 1)]
    public void TabMoveCommandsRouteToOneTypedMoveAction(
        string commandId,
        int expectedOffset)
    {
        var result = ApplicationCommandRouter.Route(
            new CommandId(commandId),
            new Dictionary<string, string>(StringComparer.Ordinal),
            CommandContext.Tab);

        var action = Assert.IsType<ApplicationCommandAction>(result.Action);
        Assert.Equal(ApplicationCommandActionKind.MoveTab, action.Kind);
        Assert.Equal(expectedOffset, action.TabOffset);
    }

    [Fact]
    public void TabMoveCommandsAreUnavailableWithoutATabContext()
    {
        var result = ApplicationCommandRouter.Route(
            BuiltInCommands.MoveTabLeft,
            new Dictionary<string, string>(StringComparer.Ordinal),
            CommandContext.Workspace);

        Assert.False(result.IsSuccess);
        Assert.Contains("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationCommandAction Route(CommandBinding binding)
    {
        var result = ApplicationCommandRouter.Route(
            binding.CommandId,
            binding.Arguments,
            binding.Contexts);
        return Assert.IsType<ApplicationCommandAction>(result.Action);
    }
}
