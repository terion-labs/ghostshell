using Asura.App.Views.RuntimePanels;

namespace Asura.App.ViewModels;

/// <summary>
/// What was copied or cut, waiting to be pasted somewhere else.
///
/// It is one thing for the whole window rather than one per panel, because
/// copying in one panel and pasting in another is the point of it. The
/// operating system's clipboard is not used: what is held here is a set of
/// locations on some connection, which means nothing outside this shell.
/// </summary>
public sealed class FileTransferClipboard
{
    private FilePanelTransferPayload? _payload;

    /// <summary>Raised when something is put on the clipboard or taken off it.</summary>
    public event EventHandler? Changed;

    public FilePanelTransferPayload? Payload
    {
        get => _payload;
        set
        {
            if (ReferenceEquals(_payload, value))
            {
                return;
            }

            _payload = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasContent => _payload is not null;
}
