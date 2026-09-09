using System.Xml.Linq;

namespace Asura.Architecture.Tests;

/// <summary>
/// The design language uses a switch for a setting you turn on or off, and keeps
/// a checkbox for a choice you are asked to acknowledge or select. Mixing them
/// makes a behaviour setting read as a consent prompt and vice versa.
/// </summary>
public sealed class ToggleAffordanceContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>
    /// Checkboxes that are deliberate: a consent acknowledgement the user is meant
    /// to read and tick, or one entry in a multi-selection list. Neither is a
    /// setting being switched on.
    /// </summary>
    private static readonly string[] AllowedCheckBoxOwners =
    [
        // Consent acknowledgements for insecure transports and untrusted servers.
        "McpServerTrustConfirmationDialog.axaml",
        // A mutually exclusive authentication mode, not an on/off setting.
        "AiProviderProfileEditorDialog.axaml",
        // One-off persistence and consent choices: save-on-connect, keychain
        // storage, and the insecure-transport acknowledgements of the file
        // family, which this unified editor absorbed.
        "ConnectionEditorDialog.axaml",
    ];

    /// <summary>
    /// The rule covers settings surfaces and definition editors. A toolbar filter
    /// or a multi-selection list is a different affordance and is not in scope.
    /// </summary>
    private static bool IsSettingsSurface(string file)
    {
        var name = Path.GetFileName(file);
        return file.Contains(Path.Combine("Views", "SettingsPages"), StringComparison.Ordinal)
            || name.EndsWith("EditorDialog.axaml", StringComparison.Ordinal)
            || name is "SettingsView.axaml";
    }

    [Fact]
    public void Behaviour_settings_use_switches_rather_than_checkboxes()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", "Asura.App", "Views"),
                "*.axaml",
                SearchOption.AllDirectories)
            .Where(IsSettingsSurface)
            .Where(file => !AllowedCheckBoxOwners.Contains(
                Path.GetFileName(file),
                StringComparer.Ordinal))
            .Where(file => XDocument.Load(file)
                .Descendants()
                .Any(element => string.Equals(element.Name.LocalName, "CheckBox", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These views use a CheckBox for what reads as a behaviour setting. Use a "
            + "ToggleSwitch, or add the file to the allow-list with the reason:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The On/Off labels are cleared once in the style sheet. Restating them per
    /// instance is how one switch ended up showing its state twice — once in the
    /// row's own status text and again beside the switch.
    /// </summary>
    [Fact]
    public void Switches_do_not_restate_their_state_labels_per_instance()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", "Asura.App", "Views"),
                "*.axaml",
                SearchOption.AllDirectories)
            .Where(file => XDocument.Load(file)
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "ToggleSwitch", StringComparison.Ordinal))
                .Any(element =>
                    element.Attribute("OnContent") is not null
                    || element.Attribute("OffContent") is not null))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These views set OnContent/OffContent on a ToggleSwitch. The style sheet "
            + "already clears them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}
