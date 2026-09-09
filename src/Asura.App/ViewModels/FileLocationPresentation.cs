using Asura.Application;

namespace Asura.App.ViewModels;

internal static class FileLocationPresentation
{
    public static FilePanelLocation Parse(
        FileProviderProfileDescriptor profile,
        string input)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var value = input.Trim();
        return profile.Root.Address switch
        {
            FilePanelAddress.Hierarchical => new FilePanelLocation(
                profile.Id,
                profile.Root.Authority,
                new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
                    value.Trim('/').Length == 0
                        ? []
                        : value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                            .Select(segment => new FilePanelPathSegment(segment))))),
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot when value.Length == 0 =>
                new FilePanelLocation(
                    profile.Id,
                    profile.Root.Authority,
                    new FilePanelAddress.ContainerRoot()),
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot => new FilePanelLocation(
                profile.Id,
                profile.Root.Authority,
                new FilePanelAddress.ObjectKey(value)),
            _ => throw new ArgumentException("This provider uses an unsupported location format."),
        };
    }

    public static string Display(FilePanelLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical => hierarchical.Path.IsRoot
                ? "/"
                : "/" + string.Join('/', hierarchical.Path.Segments.Select(segment => segment.Value)),
            FilePanelAddress.ObjectKey value => value.Key,
            FilePanelAddress.ContainerRoot => string.Empty,
            _ => string.Empty,
        };
    }

    public static string ChildDisplay(
        FileProviderProfileDescriptor profile,
        string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return profile.Root.Address switch
        {
            FilePanelAddress.Hierarchical => $"/{name}",
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot => name,
            _ => name,
        };
    }

    public static FilePanelLocation Child(
        FilePanelLocation parent,
        string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return parent.Address switch
        {
            FilePanelAddress.Hierarchical =>
                parent.Child(new FilePanelPathSegment(name)),
            FilePanelAddress.ObjectKey value => new FilePanelLocation(
                parent.ProviderProfileId,
                parent.Authority,
                new FilePanelAddress.ObjectKey(
                    $"{value.Key.TrimEnd('/')}/{name}")),
            FilePanelAddress.ContainerRoot => new FilePanelLocation(
                parent.ProviderProfileId,
                parent.Authority,
                new FilePanelAddress.ObjectKey(name)),
            _ => throw new ArgumentException(
                "This destination cannot contain files or folders.",
                nameof(parent)),
        };
    }
}
