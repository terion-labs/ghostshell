using Asura.Application;

namespace Asura.Application.Tests;

/// <summary>
/// Nine bits, in the two notations everyone who works with them uses. The
/// numbers are the whole point: a permissions dialog that shows "755" and means
/// something else is worse than one that shows nothing.
/// </summary>
public sealed class FilePanelPosixModeTests
{
    [Theory]
    [InlineData(0b111_101_101, "755", "rwxr-xr-x")]
    [InlineData(0b110_100_100, "644", "rw-r--r--")]
    [InlineData(0b111_000_000, "700", "rwx------")]
    [InlineData(0, "000", "---------")]
    public void A_mode_reads_the_same_as_a_listing_reads_it(
        int value,
        string octal,
        string symbolic)
    {
        var mode = new FilePanelPosixMode(value);

        Assert.Equal(octal, mode.Octal);
        Assert.Equal(symbolic, mode.Symbolic);
    }

    /// <summary>
    /// And each bit belongs to exactly one party and one right. An off-by-one
    /// in the shift is the kind of mistake that silently grants the world write
    /// access to somebody's home directory.
    /// </summary>
    [Fact]
    public void Every_bit_belongs_to_one_party_and_one_right()
    {
        var everything = new FilePanelPosixMode(0b111_111_111);

        foreach (var who in Enum.GetValues<FilePanelPosixWho>())
        {
            foreach (var right in Enum.GetValues<FilePanelPosixRight>())
            {
                Assert.True(everything.Has(who, right));
                var without = everything.With(who, right, granted: false);
                Assert.False(without.Has(who, right));
                // And nothing else moved.
                Assert.Equal(1, CountDifferences(everything, without));
            }
        }
    }

    [Fact]
    public void Setting_a_bit_that_is_already_set_changes_nothing()
    {
        var mode = new FilePanelPosixMode(0b110_100_100);

        Assert.Equal(
            mode.Permissions,
            mode.With(FilePanelPosixWho.Owner, FilePanelPosixRight.Read, granted: true)
                .Permissions);
    }

    /// <summary>
    /// The bits above the nine — setuid, setgid, sticky — are carried but never
    /// shown. A dialog that dropped them would be quietly disarming a binary
    /// somebody deliberately armed.
    /// </summary>
    [Fact]
    public void The_bits_above_the_nine_are_carried_rather_than_shown()
    {
        var sticky = new FilePanelPosixMode(0b001_111_101_101);

        Assert.Equal("755", sticky.Octal);
        Assert.Equal(0b111_101_101, sticky.Permissions);
        Assert.Equal(0b001_111_101_101, sticky.Value);
        Assert.Equal(
            0b001_111_101_100,
            sticky.With(FilePanelPosixWho.Other, FilePanelPosixRight.Execute, granted: false).Value);
    }

    [Theory]
    [InlineData("755", 0b111_101_101)]
    [InlineData("0644", 0b110_100_100)]
    [InlineData(" 600 ", 0b110_000_000)]
    public void A_mode_can_be_typed_the_way_it_is_written(string text, int value)
    {
        Assert.True(FilePanelPosixMode.TryParseOctal(text, out var mode));
        Assert.Equal(value, mode!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rwx")]
    [InlineData("999")]
    [InlineData("77777")]
    [InlineData(null)]
    public void And_what_is_not_a_mode_is_refused_rather_than_guessed(string? text)
    {
        Assert.False(FilePanelPosixMode.TryParseOctal(text, out var mode));
        Assert.Null(mode);
    }

    private static int CountDifferences(FilePanelPosixMode left, FilePanelPosixMode right) =>
        System.Numerics.BitOperations.PopCount(
            (uint)(left.Permissions ^ right.Permissions));
}
