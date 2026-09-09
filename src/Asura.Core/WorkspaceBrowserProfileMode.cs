namespace Asura.Core;

/// <summary>
/// A durable workspace override for browser site data. Null on the workspace
/// means follow the application-wide browser setting.
/// </summary>
public enum WorkspaceBrowserProfileMode
{
    Shared,
    Isolated,
}
