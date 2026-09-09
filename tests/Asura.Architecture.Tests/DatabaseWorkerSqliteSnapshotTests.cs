using Asura.ConnectionBackend;
using Asura.Databases;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class DatabaseWorkerSqliteSnapshotTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(268435457)]
    [InlineData(long.MaxValue)]
    public async Task InvalidImageLengthIsRejectedBeforeAllocationOrReadiness(long length)
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseWorkerSqliteSnapshot.ReceiveAsync(length, input, output, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(335544320, 0, 0, true)]
    [InlineData(335544319, 0, 0, false)]
    [InlineData(402653184, 67108864, 33554432, true)]
    [InlineData(402653184, 33554432, 67108864, true)]
    [InlineData(402653184, 67108865, 33554432, false)]
    public void HeadroomUsesExistingCeilingReserveAndDoesNotDoubleCountProcess(long available, long systemLoad, long workingSet, bool expected) =>
        Assert.Equal(expected, DatabaseWorkerSqliteSnapshot.HasHeadroom(DatabaseWorkerSqliteSnapshot.MaximumBytes,
            available, systemLoad, workingSet));

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task BodyMustMatchTheEntireDeclaredLength(int declared)
    {
        await using var input = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(input, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(input, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        input.Position = 0;
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseWorkerSqliteSnapshot.ReceiveAsync(declared, input, output, CancellationToken.None));
    }

    [Fact]
    public async Task ReceivedImageHasIndependentTokenAndIsReleasedByItsOwner()
    {
        await using var input = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(input, new byte[] { 1, 2 }, CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(input, new byte[] { 3, 4 }, CancellationToken.None);
        await DatabaseOperationProtocol.WriteFrameAsync(input, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        input.Position = 0;
        await using var output = new MemoryStream();
        var snapshot = await DatabaseWorkerSqliteSnapshot.ReceiveAsync(4, input, output, CancellationToken.None);
        var target = snapshot.ConnectionString;
        await using (var borrowed = SqliteInMemoryDatabases.OpenSnapshot(target)!)
        {
            var restored = new byte[4];
            await borrowed.ReadExactlyAsync(restored, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, restored);
        }
        snapshot.Dispose();
        Assert.Throws<InvalidOperationException>(() => SqliteInMemoryDatabases.OpenSnapshot(target));
    }

    [Fact]
    public async Task CanceledImageCannotBeRegistered()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseWorkerSqliteSnapshot.ReceiveAsync(4, input, output, cancellation.Token));
        Assert.Equal(0, output.Length);
    }
}
