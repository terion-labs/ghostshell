using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class GovernedAgentProgressTests
{
    [Fact]
    public void Progress_preserves_bounded_untrusted_model_content()
    {
        var progress = new GovernedAgentProgress(
            "Reviewed 12 of 20 hosts",
            percent: 60);

        Assert.Equal("Reviewed 12 of 20 hosts", progress.Message);
        Assert.Equal(60, progress.Percent);
        Assert.Equal(
            GovernedAgentProgress.UntrustedModelContentOrigin,
            progress.ContentOrigin);
        Assert.Equal("untrusted_model_progress", progress.ContentOrigin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00A0")]
    public void Progress_requires_a_nonblank_message(string? message)
    {
        var error = Assert.ThrowsAny<ArgumentException>(
            () => new GovernedAgentProgress(message!));

        Assert.Equal("message", error.ParamName);
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("first\u2028second")]
    [InlineData("first\u2029second")]
    public void Progress_rejects_multiline_text(string message)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentProgress(message));

        Assert.Equal("message", error.ParamName);
    }

    [Theory]
    [InlineData("bell\u0007")]
    [InlineData("hidden\u200Bformat")]
    public void Progress_rejects_unsafe_control_and_format_code_points(
        string message)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentProgress(message));

        Assert.Equal("message", error.ParamName);
    }

    [Fact]
    public void Progress_rejects_invalid_unicode()
    {
        var invalidMessages = new[]
        {
            string.Concat("invalid", '\uD800'),
            string.Concat("invalid", '\uDC00'),
        };

        foreach (var message in invalidMessages)
        {
            var error = Assert.Throws<ArgumentException>(
                () => new GovernedAgentProgress(message));

            Assert.Equal("message", error.ParamName);
        }
    }

    [Fact]
    public void Progress_accepts_exactly_512_utf8_bytes()
    {
        var message = new string('\u00E9', 256);

        var progress = new GovernedAgentProgress(message);

        Assert.Equal(message, progress.Message);
    }

    [Fact]
    public void Progress_rejects_more_than_512_utf8_bytes()
    {
        var message = string.Concat(new string('\u00E9', 256), "x");

        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentProgress(message));

        Assert.Equal("message", error.ParamName);
    }

    [Theory]
    [InlineData("token=literal-progress-secret")]
    [InlineData("authorization: bearer literal-progress-secret")]
    [InlineData("https://user:password@example.test")]
    public void Progress_rejects_likely_literal_secrets(string message)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentProgress(message));

        Assert.Equal("message", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(100)]
    public void Progress_accepts_optional_bounded_percent(int? percent)
    {
        var progress = new GovernedAgentProgress("Working", percent);

        Assert.Equal(percent, progress.Percent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Progress_rejects_percent_outside_zero_to_one_hundred(int percent)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new GovernedAgentProgress("Working", percent));

        Assert.Equal("percent", error.ParamName);
    }

    [Fact]
    public void Snapshot_exposes_optional_current_progress_at_the_run_boundary()
    {
        var progress = new GovernedAgentProgress("Checking services", 25);
        var snapshot = new GovernedAgentSnapshot(
            GovernedAgentState.StreamingProvider,
            RunId: null,
            ProviderId: null,
            Target: null,
            TargetTitle: "Target",
            ContextItems: [],
            Messages: [],
            EffectivePolicy: AgentPolicy.Default,
            ProvisionalAssistantText: string.Empty,
            Status: "Working",
            CurrentProgress: progress);

        Assert.Same(progress, snapshot.CurrentProgress);
    }

    [Fact]
    public void Report_progress_is_intrinsic_and_not_capability_catalogued()
    {
        Assert.Equal("agent.report_progress", IntrinsicAgentTools.ReportProgress);
        Assert.False(BuiltInAgentTools.Catalog.TryGet(
            IntrinsicAgentTools.ReportProgress,
            out _));
        Assert.DoesNotContain(
            BuiltInAgentTools.Catalog.Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.ReportProgress, StringComparison.Ordinal));
    }
}
