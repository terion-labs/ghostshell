using Asura.Application;
using Asura.Terminal;

namespace Asura.Terminal.Tests;

public sealed class TerminalInteractiveStateProtocolTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("idle_input", TerminalInteractiveStateKind.IdleInput)]
    [InlineData("working", TerminalInteractiveStateKind.Working)]
    [InlineData("streaming", TerminalInteractiveStateKind.Streaming)]
    [InlineData("modal", TerminalInteractiveStateKind.Modal)]
    [InlineData("input_required", TerminalInteractiveStateKind.InputRequired)]
    [InlineData("approval_required", TerminalInteractiveStateKind.ApprovalRequired)]
    public void Parses_bounded_expiring_application_state(
        string wireState,
        TerminalInteractiveStateKind expected)
    {
        var parsed = TerminalInteractiveStateProtocol.TryParse(
            $$"""{"sequence":12,"state":"{{wireState}}","ttl_ms":5000}""",
            afterSequence: 11,
            Now,
            out var update);

        Assert.True(parsed);
        Assert.Equal(12, update.Sequence);
        var snapshot = Assert.IsType<TerminalInteractiveStateSnapshot>(update.Snapshot);
        Assert.Equal(expected, snapshot.Kind);
        Assert.Equal(Now, snapshot.ObservedAtUtc);
        Assert.Equal(Now.AddSeconds(5), snapshot.ExpiresAtUtc);
    }

    [Fact]
    public void Clear_is_monotonic_and_carries_no_state()
    {
        Assert.True(TerminalInteractiveStateProtocol.TryParse(
            """{"sequence":14,"state":"clear"}""",
            afterSequence: 13,
            Now,
            out var update));

        Assert.Equal(14, update.Sequence);
        Assert.Null(update.Snapshot);
    }

    [Fact]
    public void Parses_an_explicit_half_open_input_region_without_inferring_one()
    {
        var parsed = TerminalInteractiveStateProtocol.TryParse(
            """
            {
              "sequence": 18,
              "state": "idle_input",
              "ttl_ms": 5000,
              "input_region": {
                "row": 23,
                "start_column": 4,
                "end_column_exclusive": 72
              }
            }
            """,
            afterSequence: 17,
            Now,
            out var update);

        Assert.True(parsed);
        var snapshot = Assert.IsType<TerminalInteractiveStateSnapshot>(update.Snapshot);
        Assert.Equal(new TerminalInputRegion(23, 4, 72), snapshot.InputRegion);
    }

    [Theory]
    [InlineData("""{"row":23,"start_column":4,"end_column":72}""")]
    [InlineData("""{"row":23,"start_column":72,"end_column_exclusive":4}""")]
    [InlineData("""{"row":23,"start_column":4,"end_column_exclusive":72,"label":"prompt"}""")]
    public void Rejects_ambiguous_or_invalid_input_regions(string inputRegion)
    {
        var payload = $$"""
            {"sequence":18,"state":"idle_input","ttl_ms":5000,"input_region":{{inputRegion}}}
            """;

        Assert.False(TerminalInteractiveStateProtocol.TryParse(
            payload,
            afterSequence: 17,
            Now,
            out _));
    }

    [Theory]
    [InlineData("""{"sequence":12,"state":"working","ttl_ms":5000}""", 12)]
    [InlineData("""{"sequence":12,"state":"working","ttl_ms":249}""", 11)]
    [InlineData("""{"sequence":12,"state":"working","ttl_ms":60001}""", 11)]
    [InlineData("""{"sequence":12,"state":"pretending","ttl_ms":5000}""", 11)]
    [InlineData("not json", 11)]
    public void Rejects_replayed_malformed_or_unbounded_state(
        string payload,
        long afterSequence)
    {
        Assert.False(TerminalInteractiveStateProtocol.TryParse(
            payload,
            afterSequence,
            Now,
            out _));
    }
}
