using Asura.Terminal;
using Porta.Pty;

namespace Asura.Terminal.Tests;

public sealed class PortaPtyConnectionTests
{
    [Fact]
    public async Task Exit_before_subscription_is_observed_without_waiting_for_another_event()
    {
        using var connection = new PortaPtyConnection(new AlreadyExitedPty());

        var completion = connection.WaitForExitAsync(CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        await completion;
        Assert.True(connection.TryGetExitCode(out var exitCode));
        Assert.Equal(0, exitCode);
    }

    private sealed class AlreadyExitedPty : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add { }
            remove { }
        }

        public Stream ReaderStream => Stream.Null;
        public Stream WriterStream => Stream.Null;
        public int Pid => 1;
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
    }
}
