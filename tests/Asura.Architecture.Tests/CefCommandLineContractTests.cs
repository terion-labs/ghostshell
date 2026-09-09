namespace Asura.Architecture.Tests;

public sealed class CefCommandLineContractTests
{
    [Fact]
    public void Host_feature_policy_is_merged_with_cef_defaults()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "vendor",
            "exclr8cef",
            "native",
            "shim",
            "exclr8cef_app.cc"));

        Assert.Contains("IsFeatureListSwitch", source);
        Assert.Contains("command_line->GetSwitchValue(s.name)", source);
        Assert.Contains("command_line->RemoveSwitch(s.name)", source);
        Assert.Contains(
            "command_line->AppendSwitchWithValue(s.name, merged_value)",
            source);
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

        throw new DirectoryNotFoundException(
            "Could not locate the Asura repository root.");
    }
}
