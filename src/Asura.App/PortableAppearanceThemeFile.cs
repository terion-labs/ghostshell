using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Core;

namespace Asura.App;

public sealed record PortableAppearanceTheme(
    int FormatVersion,
    PortableApplicationAppearance Application,
    TerminalPalette? TerminalPalette)
{
    public const int CurrentFormatVersion = 1;

    public static PortableAppearanceTheme Create(
        ThemePreference theme,
        TerminalPalette? terminalPalette = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(
            CurrentFormatVersion,
            PortableApplicationAppearance.From(theme),
            terminalPalette);
    }

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new JsonException(
                $"Appearance theme format {FormatVersion} is not supported.");
        }

        ArgumentNullException.ThrowIfNull(Application);
        if (!Enum.IsDefined(Application.Appearance)
            || !Enum.IsDefined(Application.PlatformProfile)
            || !Enum.IsDefined(Application.AccentKind)
            || !Enum.IsDefined(Application.Density)
            || !Enum.IsDefined(Application.TabStripPlacement)
            || !Enum.IsDefined(Application.WorkspacePanelPlacement))
        {
            throw new JsonException("The appearance theme contains an undefined option.");
        }

        _ = Application.ApplyTo(ThemePreference.Default);
        if (TerminalPalette is { AnsiColors.Count: not 16 })
        {
            throw new JsonException("A terminal palette must contain exactly 16 ANSI colors.");
        }
    }
}

public sealed record PortableApplicationAppearance(
    AppearanceMode Appearance,
    PlatformProfile PlatformProfile,
    AccentPreferenceKind AccentKind,
    RgbColor? CustomAccent,
    double? TextScaleOverride,
    InterfaceDensity Density,
    bool ShowTabBar,
    bool ShowWorkspacesPanel,
    TabStripPlacement TabStripPlacement,
    WorkspacePanelPlacement WorkspacePanelPlacement,
    bool IsTranslucent,
    int BackdropOpacityPercent,
    bool HasGlassPanels,
    bool OverridesBackdropOpacity)
{
    public static PortableApplicationAppearance From(ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new(
            theme.Appearance,
            theme.PlatformProfile,
            theme.Accent.Kind,
            theme.Accent.CustomColor,
            theme.TextScaleOverride,
            theme.Density,
            theme.ShowTabBar,
            theme.ShowWorkspacesPanel,
            theme.TabStripPlacement,
            theme.WorkspacePanelPlacement,
            theme.IsTranslucent,
            theme.BackdropOpacityPercent,
            theme.HasGlassPanels,
            theme.OverridesBackdropOpacity);
    }

    public ThemePreference ApplyTo(ThemePreference existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var accent = AccentKind switch
        {
            AccentPreferenceKind.Custom when CustomAccent is { } color =>
                AccentPreference.Custom(color),
            AccentPreferenceKind.FollowHost when CustomAccent is null =>
                AccentPreference.FollowHost,
            AccentPreferenceKind.AsuraBronze when CustomAccent is null =>
                AccentPreference.AsuraBronze,
            _ => throw new JsonException("The appearance theme contains an invalid accent."),
        };
        return new ThemePreference(
            existing.Id,
            existing.Name,
            Appearance,
            PlatformProfile,
            accent,
            TextScaleOverride,
            Density,
            ShowTabBar,
            ShowWorkspacesPanel,
            TabStripPlacement,
            WorkspacePanelPlacement,
            IsTranslucent,
            BackdropOpacityPercent,
            HasGlassPanels,
            OverridesBackdropOpacity);
    }
}

public static class PortableAppearanceThemeFile
{
    public const long MaximumImportBytes = 256 * 1024;
    public const string SuggestedFileName = "asura-appearance.json";

    public static async Task WriteAsync(
        string path,
        PortableAppearanceTheme theme,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(theme);
        theme.Validate();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            }))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    theme,
                    PortableAppearanceThemeJsonContext.Default.PortableAppearanceTheme,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static async Task<PortableAppearanceTheme> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(Path.GetFullPath(path), new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        var buffer = new byte[checked((int)MaximumImportBytes + 1)];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(count, buffer.Length - count),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count is 0 || count > MaximumImportBytes)
        {
            throw new JsonException(
                $"An appearance theme must contain between 1 and {MaximumImportBytes} bytes.");
        }

        using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
        ValidateDocumentShape(document.RootElement);
        var result = JsonSerializer.Deserialize(
            buffer.AsSpan(0, count),
            PortableAppearanceThemeJsonContext.Default.PortableAppearanceTheme)
            ?? throw new JsonException("The appearance theme document is empty.");
        result.Validate();
        return result;
    }

    private static void ValidateDocumentShape(JsonElement root)
    {
        RequireObject(root, ["formatVersion", "application", "terminalPalette"]);
        var application = root.GetProperty("application");
        RequireObject(application,
        [
            "appearance", "platformProfile", "accentKind", "customAccent",
            "textScaleOverride", "density", "showTabBar", "showWorkspacesPanel",
            "tabStripPlacement", "workspacePanelPlacement", "isTranslucent",
            "backdropOpacityPercent", "hasGlassPanels", "overridesBackdropOpacity",
        ]);
        ValidateNullableColor(application.GetProperty("customAccent"));

        var terminal = root.GetProperty("terminalPalette");
        if (terminal.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        RequireObject(terminal,
        [
            "name", "foreground", "background", "cursor", "selectionBackground",
            "ansiColors",
        ]);
        ValidateColor(terminal.GetProperty("foreground"));
        ValidateColor(terminal.GetProperty("background"));
        ValidateColor(terminal.GetProperty("cursor"));
        ValidateColor(terminal.GetProperty("selectionBackground"));
        var ansi = terminal.GetProperty("ansiColors");
        if (ansi.ValueKind != JsonValueKind.Array || ansi.GetArrayLength() != 16)
        {
            throw new JsonException("A terminal palette must contain exactly 16 ANSI colors.");
        }

        foreach (var color in ansi.EnumerateArray())
        {
            ValidateColor(color);
        }
    }

    private static void ValidateNullableColor(JsonElement color)
    {
        if (color.ValueKind != JsonValueKind.Null)
        {
            ValidateColor(color);
        }
    }

    private static void ValidateColor(JsonElement color) =>
        RequireObject(color, ["red", "green", "blue"]);

    private static void RequireObject(JsonElement element, string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The appearance theme contains an invalid object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Unknown appearance member '{property.Name}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw new JsonException(
                    $"Duplicate appearance member '{property.Name}'.");
            }
        }

        var missing = requiredProperties.FirstOrDefault(property => !seen.Contains(property));
        if (missing is not null)
        {
            throw new JsonException($"Required appearance member '{missing}' is missing.");
        }
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(PortableAppearanceTheme))]
internal sealed partial class PortableAppearanceThemeJsonContext : JsonSerializerContext;
