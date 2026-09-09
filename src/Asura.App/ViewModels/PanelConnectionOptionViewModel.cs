using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// One target shown by the shared runtime-panel connection selector.
///
/// Execution-backed panels select a durable connection. File Viewer selects a
/// provider profile because its target may be SSH/SFTP, FTP, SMB, WebDAV, S3,
/// or a filesystem root rather than a terminal-capable endpoint.
/// </summary>
public sealed record PanelConnectionOptionViewModel(
    PanelConnectionOptionViewModel.Target Selection,
    string Name,
    string Kind,
    string Detail,
    bool CanOpen)
{
    public abstract record Target
    {
        private Target()
        {
        }

        public sealed record Connection(ConnectionId Id) : Target;

        public sealed record FileProvider(FileProviderProfileId Id) : Target;

        public sealed record Database(DatabaseConnectionProfileId Id) : Target;
    }
}
