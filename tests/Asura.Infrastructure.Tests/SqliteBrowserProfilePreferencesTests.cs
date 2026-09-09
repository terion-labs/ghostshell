using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteBrowserProfilePreferencesTests
{
    [Fact]
    public async Task FreshProfileUsesSharedBrowserStorage()
    {
        await using var temporary = TemporaryDatabase.Create();
        var preferences = new SqliteBrowserProfilePreferences(temporary.Database);

        await preferences.InitializeAsync(CancellationToken.None);

        Assert.Equal(BrowserProfileSharing.Shared, preferences.Current.Sharing);
    }

    [Fact]
    public async Task ChoiceIsLiveAndPersistsAcrossPreferenceInstances()
    {
        await using var temporary = TemporaryDatabase.Create();
        var preferences = new SqliteBrowserProfilePreferences(temporary.Database);
        await preferences.InitializeAsync(CancellationToken.None);
        var changed = 0;
        preferences.Changed += (_, _) => changed++;

        var selectedProfile = new Asura.Core.BrowserProfileId("browser.work");
        await preferences.ApplyAsync(
            new BrowserProfileSettings(
                BrowserProfileSharing.PerWorkspace,
                selectedProfile),
            CancellationToken.None);

        Assert.Equal(BrowserProfileSharing.PerWorkspace, preferences.Current.Sharing);
        Assert.Equal(1, changed);
        var restored = new SqliteBrowserProfilePreferences(temporary.Database);
        await restored.InitializeAsync(CancellationToken.None);
        Assert.Equal(BrowserProfileSharing.PerWorkspace, restored.Current.Sharing);
        Assert.Equal(selectedProfile, restored.Current.DefaultProfileId);
    }
}
