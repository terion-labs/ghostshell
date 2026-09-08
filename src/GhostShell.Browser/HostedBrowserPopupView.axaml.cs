using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.Browser;

internal sealed partial class HostedBrowserPopupView : UserControl
{
    public HostedBrowserPopupView(Control browserContent)
    {
        InitializeComponent();
        BrowserContent.Content = browserContent;
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CloseRequested;

    public void SetAddress(string url)
    {
        var address = BrowserAddress.TryParse(url, out var parsed) ? parsed : BrowserAddress.Blank;
        OriginText.Text = "Popup · " + (address == BrowserAddress.Blank
            ? "about:blank"
            : address.Value.GetLeftPart(UriPartial.Authority));
        ToolTip.SetTip(OriginText, address.Value.AbsoluteUri);
    }
}
