using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record BrowserProfileLaunchOption(
    BrowserProfileId Id,
    string Name,
    BrowserProfilePersistence Persistence)
{
    public string Detail => Persistence switch
    {
        BrowserProfilePersistence.DurableMetadata =>
            "Encrypted session restored between runs",
        BrowserProfilePersistence.PrivateSession =>
            "Private to this new browser panel",
        _ => "Unsupported browser policy",
    };
}

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<BrowserProfileLaunchOption> _browserPanelProfileOptions = [];
    private BrowserProfileLaunchOption? _selectedBrowserPanelProfile;

    public IReadOnlyList<BrowserProfileLaunchOption> BrowserPanelProfileOptions
    {
        get => _browserPanelProfileOptions;
        private set => SetProperty(ref _browserPanelProfileOptions, value);
    }

    public BrowserProfileLaunchOption? SelectedBrowserPanelProfile
    {
        get => _selectedBrowserPanelProfile;
        set => SetProperty(ref _selectedBrowserPanelProfile, value);
    }

    private void RefreshBrowserPanelProfileOptions(
        DefinitionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previousId = SelectedBrowserPanelProfile?.Id;
        var options = snapshot.BrowserProfiles
            .Where(item => item.Value.IsEnabled)
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new BrowserProfileLaunchOption(
                item.Value.Id,
                item.Value.Name,
                item.Value.Persistence))
            .ToArray();
        BrowserPanelProfileOptions = options;

        var preferredId = previousId
            ?? _browserProfilePreferences.Current.DefaultProfileId
            ?? BuiltInBrowserProfiles.Default.Id;
        SelectedBrowserPanelProfile = options.FirstOrDefault(item => item.Id == preferredId);
    }
}
