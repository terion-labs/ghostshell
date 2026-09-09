using Velopack;

namespace Asura.Updates;

public static class VelopackStartup
{
    /// <summary>
    /// Handles Velopack install hooks before any application framework or
    /// private helper dispatch. Downloaded updates are applied only after the
    /// user explicitly asks the running app to restart.
    /// </summary>
    public static void Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();
    }
}
