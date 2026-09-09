using Microsoft.SqlServer.TDS;
using Microsoft.SqlServer.TDS.PreLogin;

namespace Asura.Architecture.Tests;

public sealed class SqlClientTdsFramingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FragmentedValidFramesAreCompleteBeforeTokenParsing(int fragmentBytes)
    {
        var token = new TDSPreLoginToken(new Version(16, 0, 1000), TDSPreLoginTokenEncryptionType.Off)
        {
            ThreadID = 1234,
            ClientTraceID = [.. Enumerable.Range(0, 16).Select(value => (byte)value)],
            ActivityID = [.. Enumerable.Range(16, 20).Select(value => (byte)value)],
        };
        using var bytes = new MemoryStream();
        using (var output = new TDSStream(bytes, leaveInnerStreamOpen: true))
        {
            // Force both packet boundaries and short underlying reads.
            output.PacketSize = 32;
            new TDSMessage(TDSMessageType.PreLogin, token).Deflate(output);
        }
        using var fragments = new FragmentedReadStream(bytes.ToArray(), fragmentBytes);
        using var input = new TDSStream(fragments);
        var message = new TDSMessage();
        var complete = false;
        // Packet headers are continuable too. Match the endpoint's inflation
        // loop, while bounding attempts by the finite serialized fixture size.
        for (var attempt = 0; attempt < bytes.Length && !complete; attempt++)
        {
            complete = message.InflateClientRequest(input);
        }
        Assert.True(complete);
        var actual = Assert.IsType<TDSPreLoginToken>(Assert.Single(message));
        Assert.Equal(token.Version, actual.Version);
        Assert.Equal(token.ThreadID, actual.ThreadID);
        Assert.Equal(token.ClientTraceID, actual.ClientTraceID);
        Assert.Equal(token.ActivityID, actual.ActivityID);
        Assert.True(message.PacketStatuses.Count > 1);
    }

    private sealed class FragmentedReadStream(byte[] bytes, int fragmentBytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, fragmentBytes));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, fragmentBytes)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task TruncatedHeaderFailsWithoutAnInflationSpinAsync(int availableBytes)
    {
        using var serialized = new MemoryStream();
        using (var output = new TDSStream(serialized, leaveInnerStreamOpen: true))
        {
            output.PacketSize = 32;
            new TDSMessage(TDSMessageType.PreLogin,
                new TDSPreLoginToken(new Version(16, 0), TDSPreLoginTokenEncryptionType.Off)).Deflate(output);
        }
        var truncated = serialized.ToArray()[..availableBytes];
        var parse = Task.Run(() =>
        {
            using var fragments = new FragmentedReadStream(truncated, 1);
            using var input = new TDSStream(fragments);
            new TDSMessage().InflateClientRequest(input);
        });
        await Assert.ThrowsAsync<EndOfStreamException>(() => parse.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
