using Asura.Application;
using Asura.Core;

namespace Asura.Terminal.Tests;

public sealed class GhosttyShellIntegrationLaunchAdapterTests : IDisposable
{
    private readonly string _resourceDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-shell-integration-{Guid.NewGuid():N}");

    public GhosttyShellIntegrationLaunchAdapterTests()
    {
        WriteResource("bash/ghostty.bash");
        WriteResource("bash/bash-preexec.sh");
        WriteResource("fish/vendor_conf.d/ghostty-shell-integration.fish");
        WriteResource("zsh/.zshenv");
        WriteResource("zsh/ghostty-integration");
    }

    [Fact]
    public void Packaged_bundle_resolves_shell_resources_outside_the_code_directory()
    {
        var executableDirectory = Path.Combine(
            _resourceDirectory,
            "Asura.app",
            "Contents",
            "MacOS");
        var packagedResources = Path.Combine(
            _resourceDirectory,
            "Asura.app",
            "Contents",
            "Resources",
            "ghostty");
        Directory.CreateDirectory(executableDirectory);
        Directory.CreateDirectory(packagedResources);

        var resolved = GhosttyShellIntegrationLaunchAdapter.ResolveResourceDirectory(
            executableDirectory);

        Assert.Equal(packagedResources, resolved);
    }

    [Fact]
    public void Development_output_resolves_adjacent_shell_resources()
    {
        var executableDirectory = Path.Combine(_resourceDirectory, "development");
        Directory.CreateDirectory(executableDirectory);

        var resolved = GhosttyShellIntegrationLaunchAdapter.ResolveResourceDirectory(
            executableDirectory);

        Assert.Equal(Path.Combine(executableDirectory, "ghostty"), resolved);
    }

    [Fact]
    public void Disabled_mode_leaves_the_process_launch_untouched()
    {
        var launch = Launch(
            "/bin/zsh",
            ["-l"],
            TerminalShellIntegrationMode.Disabled);

        var preparation = Adapter().Prepare(launch);

        Assert.Same(launch, preparation.Launch);
        Assert.Equal(
            GhosttyShellIntegrationPreparationStatus.Disabled,
            preparation.Status);
    }

    [Fact]
    public void Detect_zsh_temporarily_redirects_zdotdir_and_preserves_user_startup()
    {
        var launch = Launch(
            "/bin/zsh",
            ["-l"],
            TerminalShellIntegrationMode.Detect,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ZDOTDIR"] = "/user/zsh",
            });

        var preparation = Adapter().Prepare(launch);

        Assert.True(preparation.IsApplied);
        Assert.Equal(TerminalShellIntegrationMode.Zsh, preparation.Shell);
        Assert.Equal("/bin/zsh", preparation.Launch.Executable);
        Assert.Equal(["-l"], preparation.Launch.Arguments);
        Assert.Equal("/user/zsh", preparation.Launch.Environment["GHOSTTY_ZSH_ZDOTDIR"]);
        Assert.Equal(
            Path.Combine(_resourceDirectory, "shell-integration", "zsh"),
            preparation.Launch.Environment["ZDOTDIR"]);
    }

    [Fact]
    public void Fish_prepends_the_pinned_vendor_directory_without_losing_xdg_paths()
    {
        var launch = Launch(
            "/usr/local/bin/fish",
            [],
            TerminalShellIntegrationMode.Detect,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["XDG_DATA_DIRS"] = "/opt/share:/usr/share",
            });

        var preparation = Adapter().Prepare(launch);
        var integrationDirectory = Path.Combine(
            _resourceDirectory,
            "shell-integration");

        Assert.True(preparation.IsApplied);
        Assert.Equal(TerminalShellIntegrationMode.Fish, preparation.Shell);
        Assert.Equal(
            $"{integrationDirectory}{Path.PathSeparator}/opt/share:/usr/share",
            preparation.Launch.Environment["XDG_DATA_DIRS"]);
        Assert.Equal(
            integrationDirectory,
            preparation.Launch.Environment["GHOSTTY_SHELL_INTEGRATION_XDG_DIR"]);
    }

    [Fact]
    public void Bash_bootstrap_preserves_profile_flags_rcfile_and_existing_environment()
    {
        var launch = Launch(
            "/usr/local/bin/bash",
            ["--noprofile", "--rcfile", "/user/bashrc", "-l"],
            TerminalShellIntegrationMode.Bash,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ENV"] = "/user/env",
                ["HISTFILE"] = "/user/history",
            },
            cursorBlink: false);

        var preparation = Adapter().Prepare(launch);

        Assert.True(preparation.IsApplied);
        Assert.Equal(["--posix", "-l"], preparation.Launch.Arguments);
        Assert.Equal("1 --noprofile", preparation.Launch.Environment["GHOSTTY_BASH_INJECT"]);
        Assert.Equal("/user/bashrc", preparation.Launch.Environment["GHOSTTY_BASH_RCFILE"]);
        Assert.Equal("/user/env", preparation.Launch.Environment["GHOSTTY_BASH_ENV"]);
        Assert.Equal("/user/history", preparation.Launch.Environment["HISTFILE"]);
        Assert.False(preparation.Launch.Environment.ContainsKey("GHOSTTY_BASH_UNEXPORT_HISTFILE"));
        Assert.Equal(
            "cursor:steady,path,title",
            preparation.Launch.Environment["GHOSTTY_SHELL_FEATURES"]);
        Assert.Equal(
            Path.Combine(_resourceDirectory, "shell-integration", "bash", "ghostty.bash"),
            preparation.Launch.Environment["ENV"]);
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("-ic")]
    [InlineData("--posix")]
    public void Bash_noninteractive_and_posix_launches_are_not_rewritten(string argument)
    {
        var launch = Launch(
            "/usr/local/bin/bash",
            [argument, "echo ignored"],
            TerminalShellIntegrationMode.Bash);

        var preparation = Adapter().Prepare(launch);

        Assert.Same(launch, preparation.Launch);
        Assert.Equal(
            GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
            preparation.Status);
    }

    [Theory]
    [InlineData(TerminalShellIntegrationMode.Elvish)]
    [InlineData(TerminalShellIntegrationMode.Nushell)]
    public void Unsupported_forced_shells_fail_explicitly(
        TerminalShellIntegrationMode mode)
    {
        var launch = Launch("/bin/sh", [], mode);

        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => Adapter().Prepare(launch));

        Assert.Contains(mode.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_of_an_unsupported_shell_falls_back_without_mutating_the_launch()
    {
        var launch = Launch(
            "/usr/bin/elvish",
            [],
            TerminalShellIntegrationMode.Detect);

        var preparation = Adapter().Prepare(launch);

        Assert.Same(launch, preparation.Launch);
        Assert.Equal(
            GhosttyShellIntegrationPreparationStatus.UnsupportedShell,
            preparation.Status);
        Assert.Equal(TerminalShellIntegrationMode.Elvish, preparation.Shell);
    }

    [Fact]
    public void Forced_shell_mode_never_rewrites_a_connection_transport_process()
    {
        var launch = Launch(
            "/usr/bin/ssh",
            ["example.invalid"],
            TerminalShellIntegrationMode.Zsh);

        var preparation = Adapter().Prepare(launch);

        Assert.Same(launch, preparation.Launch);
        Assert.Equal(
            GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
            preparation.Status);
        Assert.Equal(TerminalShellIntegrationMode.Zsh, preparation.Shell);
    }

    [Fact]
    public void Missing_resources_never_partially_mutate_the_launch()
    {
        var missing = Path.Combine(_resourceDirectory, "missing");
        var launch = Launch(
            "/bin/zsh",
            [],
            TerminalShellIntegrationMode.Zsh,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ZDOTDIR"] = "/user/zsh",
            });

        var preparation = new GhosttyShellIntegrationLaunchAdapter(missing)
            .Prepare(launch);

        Assert.Same(launch, preparation.Launch);
        Assert.Equal(
            GhosttyShellIntegrationPreparationStatus.ResourcesUnavailable,
            preparation.Status);
        Assert.Equal("/user/zsh", launch.Environment["ZDOTDIR"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_resourceDirectory))
        {
            Directory.Delete(_resourceDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private GhosttyShellIntegrationLaunchAdapter Adapter() => new(_resourceDirectory);

    private static TerminalLaunchRequest Launch(
        string executable,
        IReadOnlyList<string> arguments,
        TerminalShellIntegrationMode mode,
        IReadOnlyDictionary<string, string>? environment = null,
        bool cursorBlink = true) =>
        new(
            Environment.CurrentDirectory,
            executable,
            arguments,
            environment,
            new TerminalRenderProfileSnapshot(
                13,
                TerminalCursorStyle.Block,
                cursorBlink,
                10_000,
                TerminalPalette.AsuraDark,
                shellIntegration: mode));

    private void WriteResource(string relativePath)
    {
        var path = Path.Combine(
            _resourceDirectory,
            "shell-integration",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# pinned Ghostty shell-integration fixture\n");
    }
}
