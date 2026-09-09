using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class QuickTerminalSettingsRepositoryTests
{
    [Fact]
    public async Task Quick_terminal_settings_survive_database_restart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<QuickTerminalSettings>(
            temporary.Database,
            TimeProvider.System);
        var settings = new QuickTerminalSettings(
            new QuickTerminalSettingsId("operator"),
            "Operator Quick Terminal",
            new KeyStroke("K", KeyModifiers.Control | KeyModifiers.Alt),
            QuickTerminalMonitorPolicy.ActiveWindow,
            0.48,
            0.74,
            animateSlide: true,
            animationDurationMilliseconds: 240,
            reduceMotion: false,
            restoreLastSession: false,
            hideOnFocusLoss: false);

        var saved = await repository.SaveAsync(settings, null, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        await temporary.ReopenAsync();
        repository = new SqliteDefinitionRepository<QuickTerminalSettings>(
            temporary.Database,
            TimeProvider.System);
        var loaded = await repository.GetAsync(settings.Key, CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(settings, loaded.Value!.Value);
        Assert.Equal(1, loaded.Value.Revision);
    }
}
