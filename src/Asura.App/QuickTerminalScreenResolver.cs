using Asura.Core;

namespace Asura.App;

internal static class QuickTerminalScreenResolver
{
    public static TScreen? Resolve<TScreen>(
        TScreen? mainWindowScreen,
        TScreen? primaryScreen,
        TScreen? activeWindowScreen,
        QuickTerminalMonitorPolicy policy)
        where TScreen : class
    {
        var selected = policy switch
        {
            QuickTerminalMonitorPolicy.MainWindow => mainWindowScreen,
            QuickTerminalMonitorPolicy.Primary => primaryScreen,
            QuickTerminalMonitorPolicy.ActiveWindow => activeWindowScreen,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
        };

        return selected ?? mainWindowScreen ?? primaryScreen;
    }
}
