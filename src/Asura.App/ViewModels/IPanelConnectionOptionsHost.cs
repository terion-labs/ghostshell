namespace Asura.App.ViewModels;

/// <summary>
/// Supplies terminal connection choices to panel views hosted by either the
/// main shell or Quick Terminal without reflecting over a window DataContext.
/// </summary>
public interface IPanelConnectionOptionsHost
{
    IEnumerable<PanelConnectionOptionViewModel> PanelConnectionOptions { get; }
}
