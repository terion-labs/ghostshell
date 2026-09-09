using Asura.Terminal;

namespace Asura.Terminal.Tests;

public sealed class PortablePtyEnvironmentTests
{
    [Fact]
    public void Process_environment_advertises_the_supported_interactive_state_protocol()
    {
        var environment = PortaPtyFactory.CreateProcessEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LANG"] = "C",
                ["TERM"] = "inherited-terminal",
                ["COLORTERM"] = "inherited-color-mode",
                ["TERM_PROGRAM"] = "WarpTerminal",
                ["TERM_PROGRAM_VERSION"] = "0.2026.03.04.08.11.stable_03",
                ["ASURA_INTERACTIVE_STATE_PROTOCOL"] = "unsupported-version",
            });

        Assert.Equal("C", environment["LANG"]);
        Assert.Equal("xterm-256color", environment["TERM"]);
        Assert.Equal("truecolor", environment["COLORTERM"]);
        Assert.Equal("ghostty", environment["TERM_PROGRAM"]);
        Assert.False(environment.ContainsKey("TERM_PROGRAM_VERSION"));
        Assert.Equal(
            "terminal.interactive-state.v1",
            environment["ASURA_INTERACTIVE_STATE_PROTOCOL"]);
    }
}
