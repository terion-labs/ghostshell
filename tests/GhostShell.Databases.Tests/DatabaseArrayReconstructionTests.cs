using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using GhostShell.Application;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseArrayReconstructionTests
{
    [Fact]
    public void RankOneNonzeroBoundsRemainExactOnSupportedJitRuntime()
    {
        var type = Type.GetType("System.Int32[*]", throwOnError: true)!;
        var original = Array.CreateInstanceFromArrayType(type, [3], [-2]);
        original.SetValue(41, -2);
        original.SetValue(42, -1);
        original.SetValue(43, 0);
        var result = RoundTrip(original);
        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(-2, result.GetLowerBound(0));
        Assert.Equal(0, result.GetUpperBound(0));
        Assert.Equal(42, result.GetValue(-1));
    }

    [Fact]
    [SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional", Justification = "Provider array rank and bounds are part of the parameter identity being tested.")]
    public void NullableRectangularArraysKeepTypeRankAndValues()
    {
        int?[,] original = { { 1, null }, { int.MinValue, int.MaxValue } };
        var result = RoundTrip(original);
        Assert.Equal(original.GetType(), result.GetType());
        Assert.Equal(2, result.Rank);
        Assert.Equal(2, result.GetLength(0));
        Assert.Null(result.GetValue(0, 1));
        Assert.Equal(int.MaxValue, result.GetValue(1, 1));
    }

    [Fact]
    public void TextAndLargeIntegerParametersRetainExactClrTypes()
    {
        Array[] values = [new[] { "text", null, "🌐" }, new[] { Int128.MaxValue },
            new[] { UInt128.MaxValue }, new[] { BigInteger.Pow(10, 100) }, new decimal?[] { 1.25m, null }];
        foreach (var original in values)
        {
            var result = RoundTrip(original);
            Assert.Equal(original.GetType(), result.GetType());
            Assert.Equal(original.Cast<object?>(), result.Cast<object?>());
        }
    }

    [Fact]
    public void CanceledParameterReconstructionDoesNotReadEveryElement()
    {
        using var payload = new MemoryStream();
        DatabaseArrayContent.Write(payload, new int[1000], CancellationToken.None);
        payload.Position = 0;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DatabaseArrayReconstruction.Read(payload, canceled.Token));
        Assert.True(payload.Position < payload.Length);
    }

    private static Array RoundTrip(Array original)
    {
        using var payload = new MemoryStream();
        DatabaseArrayContent.Write(payload, original, CancellationToken.None);
        payload.Position = 0;
        return DatabaseArrayReconstruction.Read(payload, CancellationToken.None);
    }
}
