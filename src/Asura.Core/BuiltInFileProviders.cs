namespace Asura.Core;

/// <summary>
/// Identifies runtime-owned file providers that definitions may reference without
/// storing a user-managed provider profile.
/// </summary>
public static class BuiltInFileProviders
{
    public static FileProviderProfileId HomeId { get; } = new("builtin.files.home");

    public static bool IsIntrinsic(FileProviderProfileId id) => id == HomeId;
}
