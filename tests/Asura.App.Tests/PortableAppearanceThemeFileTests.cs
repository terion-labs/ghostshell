using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class PortableAppearanceThemeFileTests
{
    [Fact]
    public async Task Round_trip_keeps_portable_appearance_without_definition_identity()
    {
        var directory = Directory.CreateTempSubdirectory("asura-appearance-");
        try
        {
            var path = Path.Combine(directory.FullName, "appearance.json");
            var source = new ThemePreference(
                ThemePreference.Default.Id,
                ThemePreference.Default.Name,
                AppearanceMode.Light,
                PlatformProfile.Kde,
                AccentPreference.Custom(RgbColor.Parse("#2864A8")),
                textScaleOverride: 1.25,
                InterfaceDensity.Compact,
                showTabBar: false,
                showWorkspacesPanel: true,
                TabStripPlacement.Bottom,
                WorkspacePanelPlacement.Right,
                isTranslucent: false,
                backdropOpacityPercent: 91,
                hasGlassPanels: false,
                overridesBackdropOpacity: true);

            await PortableAppearanceThemeFile.WriteAsync(
                path,
                PortableAppearanceTheme.Create(source, TerminalPalette.Nord),
                CancellationToken.None);
            var restored = await PortableAppearanceThemeFile.ReadAsync(
                path,
                CancellationToken.None);
            var applied = restored.Application.ApplyTo(ThemePreference.Default);
            var json = await File.ReadAllTextAsync(path);

            Assert.Equal(source, applied);
            Assert.True(TerminalPalette.Nord.Matches(restored.TerminalPalette!));
            Assert.DoesNotContain("builtin.theme.automatic", json, StringComparison.Ordinal);
            Assert.DoesNotContain("revision", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_unknown_versions_and_fields()
    {
        var directory = Directory.CreateTempSubdirectory("asura-appearance-");
        try
        {
            var unknownVersion = Path.Combine(directory.FullName, "version.json");
            await File.WriteAllTextAsync(
                unknownVersion,
                "{\"formatVersion\":99,\"application\":{}}",
                CancellationToken.None);
            await Assert.ThrowsAsync<JsonException>(() =>
                PortableAppearanceThemeFile.ReadAsync(
                    unknownVersion,
                    CancellationToken.None));

            var valid = Path.Combine(directory.FullName, "valid.json");
            await PortableAppearanceThemeFile.WriteAsync(
                valid,
                PortableAppearanceTheme.Create(ThemePreference.Default),
                CancellationToken.None);
            var json = await File.ReadAllTextAsync(valid);
            await File.WriteAllTextAsync(
                valid,
                json.Replace("\"formatVersion\": 1", "\"formatVersion\": 1,\n  \"machinePath\": \"/tmp/private\"", StringComparison.Ordinal),
                CancellationToken.None);

            await Assert.ThrowsAsync<JsonException>(() =>
                PortableAppearanceThemeFile.ReadAsync(valid, CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_missing_nested_unknown_undefined_and_bad_palette_shape()
    {
        var directory = Directory.CreateTempSubdirectory("asura-appearance-");
        try
        {
            var path = Path.Combine(directory.FullName, "appearance.json");
            await PortableAppearanceThemeFile.WriteAsync(
                path,
                PortableAppearanceTheme.Create(
                    ThemePreference.Default,
                    TerminalPalette.Nord),
                CancellationToken.None);
            var source = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();

            foreach (var mutation in new Action<JsonObject>[]
            {
                root => root.Remove("terminalPalette"),
                root => root["application"]!.AsObject()["unexpected"] = true,
                root => root["application"]!.AsObject()["appearance"] = 999,
                root => root["terminalPalette"]!.AsObject()["foreground"]!
                    .AsObject()["unexpected"] = 1,
                root => root["terminalPalette"]!.AsObject()["ansiColors"]!
                    .AsArray().RemoveAt(0),
            })
            {
                var candidate = JsonNode.Parse(source.ToJsonString())!.AsObject();
                mutation(candidate);
                await File.WriteAllTextAsync(path, candidate.ToJsonString());
                await Assert.ThrowsAsync<JsonException>(() =>
                    PortableAppearanceThemeFile.ReadAsync(path, CancellationToken.None));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_duplicate_members_at_every_nested_level()
    {
        var directory = Directory.CreateTempSubdirectory("asura-appearance-");
        try
        {
            var path = Path.Combine(directory.FullName, "appearance.json");
            await PortableAppearanceThemeFile.WriteAsync(
                path,
                PortableAppearanceTheme.Create(
                    ThemePreference.Default,
                    TerminalPalette.Nord),
                CancellationToken.None);
            var source = await File.ReadAllTextAsync(path);

            foreach (var candidate in new[]
            {
                source.Replace(
                    "\"formatVersion\": 1,",
                    "\"formatVersion\": 1,\n  \"formatVersion\": 1,",
                    StringComparison.Ordinal),
                source.Replace(
                    "\"application\": {",
                    "\"application\": {\n    \"appearance\": 0,",
                    StringComparison.Ordinal),
                source.Replace(
                    "\"foreground\": {",
                    "\"foreground\": {\n      \"red\": 0,",
                    StringComparison.Ordinal),
            })
            {
                await File.WriteAllTextAsync(path, candidate);
                await Assert.ThrowsAsync<JsonException>(() =>
                    PortableAppearanceThemeFile.ReadAsync(path, CancellationToken.None));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Import_enforces_zero_exact_and_over_limit_files()
    {
        var directory = Directory.CreateTempSubdirectory("asura-appearance-");
        try
        {
            var path = Path.Combine(directory.FullName, "appearance.json");
            await File.WriteAllBytesAsync(path, []);
            await Assert.ThrowsAsync<JsonException>(() =>
                PortableAppearanceThemeFile.ReadAsync(path, CancellationToken.None));

            await PortableAppearanceThemeFile.WriteAsync(
                path,
                PortableAppearanceTheme.Create(ThemePreference.Default),
                CancellationToken.None);
            var valid = await File.ReadAllBytesAsync(path);
            var exact = new byte[checked((int)PortableAppearanceThemeFile.MaximumImportBytes)];
            valid.CopyTo(exact, 0);
            Array.Fill(exact, (byte)' ', valid.Length, exact.Length - valid.Length);
            await File.WriteAllBytesAsync(path, exact);
            _ = await PortableAppearanceThemeFile.ReadAsync(path, CancellationToken.None);

            await File.WriteAllBytesAsync(
                path,
                new byte[checked((int)PortableAppearanceThemeFile.MaximumImportBytes + 1)]);
            await Assert.ThrowsAsync<JsonException>(() =>
                PortableAppearanceThemeFile.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
