using System.Reflection;
using Asura.Application;

namespace Asura.Application.Tests;

public sealed class TerminalSessionPortContractsTests
{
    [Fact]
    public void Terminal_session_aggregate_exposes_four_separate_capability_ports()
    {
        var aggregate = typeof(ITerminalPanelSession);

        Assert.True(typeof(ITerminalProcess).IsAssignableFrom(aggregate));
        Assert.True(typeof(ITerminalState).IsAssignableFrom(aggregate));
        Assert.True(typeof(ITerminalRendererAttachment).IsAssignableFrom(aggregate));
        Assert.True(typeof(ITerminalAutomation).IsAssignableFrom(aggregate));
        Assert.Equal(
            ["ReadScreenAsync", "WriteAsync"],
            OperationNames(aggregate));
    }

    [Fact]
    public void Terminal_ports_keep_process_state_renderer_and_automation_operations_cohesive()
    {
        Assert.True(typeof(IPanelSession).IsAssignableFrom(typeof(ITerminalProcess)));
        Assert.Equal(
            ["ResizeAsync", "WriteAsync"],
            OperationNames(typeof(ITerminalProcess)));
        Assert.Equal(
            [
                "ClearScrollbackAsync",
                "FindAsync",
                "FindRenderedHistoryAsync",
                "FindScrollbackAsync",
                "JumpToRenderedHistoryAsync",
                "ReadScreenAsync",
                "ReadScrollbackAsync",
                "ReadSelectionAsync",
                "ScrollViewportAsync",
                "UpdateSelectionAsync",
            ],
            OperationNames(typeof(ITerminalState)));
        // Reconfiguring a live surface belongs with attaching one: both act on the
        // renderer, not on the process behind it.
        Assert.Equal(
            [
                "AttachRendererAsync",
                "BlurAsync",
                "DetachRendererAsync",
                "FocusAsync",
                "UpdateRenderProfileAsync",
            ],
            OperationNames(typeof(ITerminalRendererAttachment)));
        Assert.Equal(
            [
                "EnterAsync",
                "InterruptAsync",
                "ObserveScreenAsync",
                "PasteAsync",
                "ReadScreenAsync",
                "ReadScreenDiffAsync",
                "SendChordAsync",
                "SendKeyAsync",
                "SendMouseAsync",
                "SendMouseAtContentRevisionAsync",
                "SendPhysicalKeyAsync",
                "SubmitTextAsync",
                "WaitForChangeAsync",
                "WaitForCommandFinishedAsync",
                "WaitForDelayAsync",
                "WaitForPromptReadyAsync",
                "WaitForStableAsync",
                "WaitForTextAsync",
                "WriteAsync",
            ],
            OperationNames(typeof(ITerminalAutomation)));

        var launch = Assert.Single(typeof(ITerminalProcess).GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(ITerminalProcess.Launch), launch.Name);
        Assert.Equal(typeof(TerminalLaunchRequest), launch.PropertyType);
    }

    [Fact]
    public void Terminal_ports_have_no_generic_execution_escape_hatch()
    {
        Type[] ports =
        [
            typeof(ITerminalProcess),
            typeof(ITerminalState),
            typeof(ITerminalRendererAttachment),
            typeof(ITerminalAutomation),
        ];

        foreach (var method in ports.SelectMany(port => port.GetMethods()))
        {
            Assert.DoesNotContain("Execute", method.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(object));
        }
    }

    private static string[] OperationNames(Type port) => [.. port
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName)
        .Select(method => method.Name)
        .Order(StringComparer.Ordinal)];
}
