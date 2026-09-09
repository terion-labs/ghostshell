using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Terminal.Tests;

public sealed class TerminalLaunchRequestTests
{
    [Fact]
    public void Launch_collections_are_defensive_read_only_snapshots()
    {
        List<string> arguments = ["-l"];
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["LANG"] = "en_US.UTF-8",
        };

        var launch = new TerminalLaunchRequest("/tmp", "/bin/zsh", arguments, environment);
        arguments[0] = "--changed";
        environment["LANG"] = "changed";

        Assert.Equal(["-l"], launch.Arguments);
        Assert.Equal("en_US.UTF-8", launch.Environment["LANG"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)launch.Arguments).Add("--mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)launch.Environment).Add("BAD", "value"));
    }

    [Fact]
    public void Arguments_require_an_explicit_executable()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new TerminalLaunchRequest("/tmp", arguments: ["-l"]));

        Assert.Equal("arguments", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NAME=VALUE")]
    public void Environment_names_must_be_valid_process_environment_keys(string name)
    {
        Assert.Throws<ArgumentException>(() => new TerminalLaunchRequest(
            "/tmp",
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "value" }));
    }

    [Fact]
    public void Environment_values_cannot_be_null()
    {
        Assert.Throws<ArgumentException>(() => new TerminalLaunchRequest(
            "/tmp",
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["NAME"] = null! }));
    }

    [Theory]
    [InlineData("working-directory")]
    [InlineData("executable")]
    [InlineData("argument")]
    [InlineData("environment-name")]
    [InlineData("environment-value")]
    public void Launch_values_reject_nul_characters(string field)
    {
        const string invalid = "value\0hidden";

        Assert.Throws<ArgumentException>(() => field switch
        {
            "working-directory" => new TerminalLaunchRequest(invalid),
            "executable" => new TerminalLaunchRequest("/tmp", invalid),
            "argument" => new TerminalLaunchRequest("/tmp", "/bin/sh", [invalid]),
            "environment-name" => new TerminalLaunchRequest(
                "/tmp",
                environment: new Dictionary<string, string>(StringComparer.Ordinal) { [invalid] = "value" }),
            "environment-value" => new TerminalLaunchRequest(
                "/tmp",
                environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["NAME"] = invalid }),
            _ => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void Render_snapshot_copies_the_supported_terminal_profile_values()
    {
        var profile = new TerminalProfile(
            new TerminalProfileId("test"),
            "Test",
            "JetBrains Mono",
            15,
            1.2,
            TerminalCursorStyle.Underline,
            cursorBlink: false,
            42_000,
            TerminalPalette.AsuraDark,
            BuiltInKeymaps.MacOsTerminalId);

        var snapshot = TerminalRenderProfileSnapshot.FromProfile(profile);

        Assert.Equal(15, snapshot.FontSize);
        Assert.Equal(TerminalCursorStyle.Underline, snapshot.CursorStyle);
        Assert.False(snapshot.CursorBlink);
        Assert.Equal(42_000, snapshot.ScrollbackLines);
        Assert.Equal(profile.Palette.Name, snapshot.Palette.Name);
        Assert.Equal(profile.Palette.Foreground, snapshot.Palette.Foreground);
        Assert.Equal(profile.Palette.Background, snapshot.Palette.Background);
        Assert.Equal(profile.Palette.Cursor, snapshot.Palette.Cursor);
        Assert.Equal(profile.Palette.SelectionBackground, snapshot.Palette.SelectionBackground);
        Assert.Equal(profile.Palette.AnsiColors, snapshot.Palette.AnsiColors);
        Assert.NotSame(profile.Palette, snapshot.Palette);
    }

    [Fact]
    public void Launch_request_round_trips_without_reintroducing_mutable_collections()
    {
        var keymap = TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal);
        var connectionId = new ConnectionId("round-trip");
        var launch = new TerminalLaunchRequest(
            "/tmp",
            "/bin/zsh",
            ["-l"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["LANG"] = "C" },
            new TerminalRenderProfileSnapshot(
                13,
                TerminalCursorStyle.Block,
                cursorBlink: true,
                1_000,
                TerminalPalette.AsuraDark),
            keymap,
            connectionId,
            new TerminalConnectionMetadata(
                "SSH: deploy@example.test:22",
                "/srv/start"));

        var json = JsonSerializer.Serialize(launch);
        var restored = JsonSerializer.Deserialize<TerminalLaunchRequest>(json);

        Assert.NotNull(restored);
        Assert.Equal(launch.WorkingDirectory, restored.WorkingDirectory);
        Assert.Equal(launch.Executable, restored.Executable);
        Assert.Equal(launch.Arguments, restored.Arguments);
        Assert.Equal(launch.Environment, restored.Environment);
        Assert.Equal(connectionId, restored.ConnectionId);
        Assert.Equal(launch.ConnectionMetadata, restored.ConnectionMetadata);
        Assert.Equal(launch.RenderProfile!.FontSize, restored.RenderProfile!.FontSize);
        Assert.Equal(keymap.Id, restored.Keymap!.Id);
        Assert.Equal(
            keymap.Bindings.Select(binding => binding.CommandId),
            restored.Keymap.Bindings.Select(binding => binding.CommandId));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)restored.Arguments).Add("--mutated"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CommandBinding>)restored.Keymap.Bindings).Add(
                BuiltInKeymaps.LinuxTerminal.Bindings[0]));
    }

    [Fact]
    public void Keymap_snapshot_defensively_copies_identity_bindings_and_prefix()
    {
        var bindings = BuiltInKeymaps.LinuxTerminal.Bindings.ToList();
        var prefix = new PrefixConfiguration(
            new KeyStroke("X", KeyModifiers.Control),
            TimeSpan.FromSeconds(1),
            repeatable: false,
            FailedSequenceBehavior.PassThrough);
        var snapshot = new TerminalKeymapSnapshot(
            new KeymapProfileId("snapshot-map"),
            "Snapshot map",
            bindings,
            prefix);
        bindings.Clear();

        Assert.Equal(new KeymapProfileId("snapshot-map"), snapshot.Id);
        Assert.Equal(BuiltInKeymaps.LinuxTerminal.Bindings.Count, snapshot.Bindings.Count);
        Assert.NotSame(BuiltInKeymaps.LinuxTerminal.Bindings[0], snapshot.Bindings[0]);
        Assert.Equal(prefix, snapshot.Prefix);
        Assert.NotSame(prefix, snapshot.Prefix);
    }

    [Fact]
    public void Application_keymaps_cannot_become_terminal_launch_snapshots()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.TmuxApplication));

        Assert.Equal("profile", error.ParamName);
    }
}
