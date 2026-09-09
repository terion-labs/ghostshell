namespace Asura.Application;

public interface IBrowserPanelSession :
    IPanelSession,
    IBrowserNavigation,
    IOriginConstrainedBrowserNavigation,
    IOriginConstrainedBrowserElementClick,
    IOriginConstrainedBrowserElementFill,
    IOriginConstrainedBrowserElementCheck,
    IOriginConstrainedBrowserAutomation,
    IBrowserDocumentReader,
    IBrowserWaitObservation,
    IBrowserRendererAttachment
{
}
