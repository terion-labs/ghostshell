using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Asura.App.Controls;

/// <summary>A dynamic status whose empty state is not an announcement.</summary>
internal sealed class LiveRegionTextBlock : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new LiveRegionPeer(this);

    private sealed class LiveRegionPeer(LiveRegionTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        // Avalonia 12.0.5's text peer ignores AutomationProperties.Name, while its
        // macOS bridge cannot convert an empty live announcement to NSString.
        // Keep the full status text; clearing it has no new message to announce.
        protected override AutomationLiveSetting GetLiveSettingCore() =>
            string.IsNullOrEmpty(base.GetNameCore()) ? AutomationLiveSetting.Off : base.GetLiveSettingCore();

        protected override string GetNameCore()
        {
            var text = base.GetNameCore();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            var statusName = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(statusName) ? "Status" : statusName;
        }
    }
}
