using System.Text;

namespace Asura.Packaging;

internal static class NativeArtifactPath
{
    private const int MaximumPathCharacters = 240;
    private const int MaximumPathSegments = 32;

    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > MaximumPathCharacters
            || path[0] == '/'
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Native payload path {path} is not a safe relative path.");
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length > MaximumPathSegments
            || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Native payload path {path} is not a safe relative path.");
        }
    }

    public static void ValidatePortableUniqueness(IEnumerable<string> paths)
    {
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            Validate(path);
            if (!portablePaths.Add(path.Normalize(NormalizationForm.FormC)))
            {
                throw new InvalidDataException(
                    $"Native payload path {path} collides on a portable filesystem.");
            }
        }
    }
}
