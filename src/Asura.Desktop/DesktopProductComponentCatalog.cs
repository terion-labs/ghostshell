using Asura.App.ViewModels;

namespace Asura.Desktop;

internal sealed class DesktopProductComponentCatalog : IProductComponentCatalog
{
    public IReadOnlyList<ProductComponentViewModel> Components { get; } =
    [
        new("Avalonia Desktop", "12.0.5", "Cross-platform desktop UI", "MIT"),
        new(
            "Chromium Embedded Framework",
            "150.0.9",
            "Bundled embedded Chromium runtime",
            "BSD-3-Clause + bundled third-party notices"),
        new(
            "Exclr8CEF",
            "0.8.0-asura.11",
            "Avalonia off-screen Chromium binding",
            "MIT"),
        new(
            "Ghostty",
            "1.3.1",
            "Native terminal rendering and shell integration",
            "MIT + GPL-3.0-or-later resources"),
        new(".NET", "10", "Managed runtime", "MIT + bundled notices"),
        new(
            "SQLite3 Multiple Ciphers",
            "2.4.0",
            "Encrypted local durable storage",
            "MIT"),
        new("SSH.NET", "2026.0.0", "SSH and SFTP connectivity", "MIT"),
        new("Fluent Icons", "2.1.333", "Interface iconography", "MIT"),
    ];
}
