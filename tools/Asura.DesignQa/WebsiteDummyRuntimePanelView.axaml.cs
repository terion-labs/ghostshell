using Asura.App.ViewModels;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Asura.DesignQa;

internal enum WebsiteDummyPanelContent
{
    Terminal,
    Browser,
}

internal sealed class WebsiteDummyRuntimePanelViewModel(
    PanelInstanceId id,
    PanelKind kind,
    string title,
    string kindLabel,
    WebsiteDummyPanelContent content)
    : RuntimePanelViewModel(id, kind, title, kindLabel)
{
    public string BrowserAddress => "https://example.com/";

    public string ConnectionDisplayName => "Local";

    public bool ShowsTerminal => content == WebsiteDummyPanelContent.Terminal;

    public bool ShowsBrowser => content == WebsiteDummyPanelContent.Browser;
}

internal sealed partial class WebsiteDummyRuntimePanelView : UserControl
{
    public WebsiteDummyRuntimePanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
