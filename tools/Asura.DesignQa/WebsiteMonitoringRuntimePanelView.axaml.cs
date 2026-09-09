using Asura.App.ViewModels;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Asura.DesignQa;

internal sealed class WebsiteMonitoringRuntimePanelViewModel(
    PanelInstanceId id,
    PanelKind kind,
    string title,
    string kindLabel)
    : RuntimePanelViewModel(id, kind, title, kindLabel)
{
    public bool ShowsStatistics => Kind == PanelKind.Statistics;

    public bool ShowsProcessMonitor => Kind == PanelKind.ProcessMonitor;

    public QaStatisticsPreview StatisticsPreview { get; } = new();

    public QaProcessMonitorPreview ProcessMonitorPreview { get; } = new();
}

internal sealed partial class WebsiteMonitoringRuntimePanelView : UserControl
{
    public WebsiteMonitoringRuntimePanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
