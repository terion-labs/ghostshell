using Asura.Application;

namespace Asura.App.Controls;

public sealed class TerminalSessionSnapshotEventArgs(SessionSnapshot snapshot) : EventArgs
{
    public SessionSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}
