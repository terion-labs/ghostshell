namespace Asura.Core;

/// <summary>
/// Declares which runtime-panel experiences a durable connection target can launch.
/// Runtime health is intentionally separate: capabilities describe the target type,
/// while the application projection decides whether that particular target is ready.
/// </summary>
public interface IPanelLaunchCapabilitySource
{
    PanelLaunchCapabilities PanelLaunchCapabilities { get; }
}

public sealed class PanelLaunchCapabilities
{
    private readonly PanelKind[] _supportedPanels;

    public PanelLaunchCapabilities(
        PanelKind defaultPanel,
        params PanelKind[] supportedPanels)
    {
        ArgumentNullException.ThrowIfNull(supportedPanels);
        if (supportedPanels.Length == 0)
        {
            throw new ArgumentException(
                "At least one launchable panel is required.",
                nameof(supportedPanels));
        }

        if (supportedPanels.Any(panel => panel is PanelKind.Browser or PanelKind.Placeholder))
        {
            throw new ArgumentException(
                "Browser and placeholder panels are not connection-backed.",
                nameof(supportedPanels));
        }

        if (supportedPanels.Distinct().Count() != supportedPanels.Length)
        {
            throw new ArgumentException(
                "Launchable panels must be unique.",
                nameof(supportedPanels));
        }

        if (!supportedPanels.Contains(defaultPanel))
        {
            throw new ArgumentException(
                "The default panel must be launchable.",
                nameof(defaultPanel));
        }

        DefaultPanel = defaultPanel;
        _supportedPanels = [.. supportedPanels];
    }

    public PanelKind DefaultPanel { get; }

    public IReadOnlyList<PanelKind> SupportedPanels => _supportedPanels;

    public bool Supports(PanelKind panel) => _supportedPanels.Contains(panel);
}

public static class BuiltInPanelLaunchCapabilities
{
    public static PanelLaunchCapabilities ShellAndFiles { get; } = new(
        PanelKind.Terminal,
        PanelKind.Terminal,
        PanelKind.FileViewer,
        PanelKind.Statistics,
        PanelKind.ProcessMonitor,
        PanelKind.Docker,
        PanelKind.Git);

    public static PanelLaunchCapabilities ShellAndMonitoring { get; } = new(
        PanelKind.Terminal,
        PanelKind.Terminal,
        PanelKind.Statistics,
        PanelKind.ProcessMonitor);

    public static PanelLaunchCapabilities FilesOnly { get; } = new(
        PanelKind.FileViewer,
        PanelKind.FileViewer);
}
