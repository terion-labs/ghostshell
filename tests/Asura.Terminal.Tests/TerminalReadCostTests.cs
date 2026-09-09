using System.Diagnostics;
using System.Text;
using Asura.Application;
using Asura.Core;
using Asura.Terminal;

namespace Asura.Terminal.Tests;

/// <summary>
/// What a screen read costs, at the size a maximised terminal actually is.
///
/// Reads are synchronous — they take the session's lock and walk the grid —
/// so whatever they cost is paid on whichever thread asked, and the thread
/// that asks during a workspace switch is the one drawing the window. This
/// measures it rather than assuming, and prints the numbers so a regression
/// has something to be compared against.
/// </summary>
public sealed class TerminalReadCostTests
{
    [Fact]
    public async Task A_full_screen_read_is_measured_at_a_realistic_size()
    {
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var ptyFactory = new FakePortablePtyFactory();
        var factory = new GhosttyVtTerminalSessionFactory(ptyFactory);
        await using var session = (GhosttyVtTerminalSession)await factory.CreateAsync(
            SessionId.New(),
            new TerminalLaunchRequest(Environment.CurrentDirectory),
            default);

        // A maximised terminal on a large display, filled edge to edge: the
        // shape of the thing being walked, not an empty grid that walks fast.
        const int columns = 210;
        const int rows = 56;
        await session.ResizeAsync(
            new ViewportDescriptor(columns * 8, rows * 17, 2, columns, rows),
            default);
        var line = new string('m', columns - 1);
        var content = new StringBuilder();
        for (var row = 0; row < rows * 4; row++)
        {
            content.Append(line).Append("\r\n");
        }

        await ptyFactory.Connection.WriteOutputAsync(content.ToString());
        await WaitForFilledScreenAsync(session, columns);

        // Warm, so the numbers are the steady-state cost rather than first-call
        // JIT and lazy interop setup.
        for (var warmup = 0; warmup < 5; warmup++)
        {
            _ = await session.ReadScreenAsync(default);
            _ = await session.ReadRenderFrameAsync(default);
        }

        const int iterations = 20;
        var screenClock = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            _ = await session.ReadScreenAsync(default);
        }

        screenClock.Stop();
        var frameClock = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            _ = await session.ReadRenderFrameAsync(default);
        }

        frameClock.Stop();

        var screenMs = screenClock.Elapsed.TotalMilliseconds / iterations;
        var frameMs = frameClock.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine(
            $"[read-cost] {columns}x{rows}: ReadScreenAsync {screenMs:F2} ms, "
            + $"ReadRenderFrameAsync {frameMs:F2} ms");

        // Deliberately loose. This exists to produce the number and to catch an
        // order-of-magnitude regression, not to pin a machine's performance.
        Assert.True(
            screenMs < 100,
            $"A single screen read took {screenMs:F2} ms, which no thread should "
            + "be spending, least of all the one drawing the window.");
    }

    private static async Task WaitForFilledScreenAsync(
        GhosttyVtTerminalSession session,
        int columns)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await session.ReadRenderFrameAsync(default);
            if (frame.Columns >= columns
                && frame.Rows > 1
                && frame.Delta.Kind != TerminalRenderDamageKind.None)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException(
            "The terminal never reported a filled screen to measure.");
    }
}
