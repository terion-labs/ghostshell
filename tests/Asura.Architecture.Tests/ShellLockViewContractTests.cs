using System.Xml.Linq;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ShellLockViewContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Lock_boundary_uses_compiled_bindings_required_by_native_aot()
    {
        var mainWindow = LoadView("MainWindow");
        var lockView = Assert.Single(
            mainWindow.Descendants(),
            element => element.Name.LocalName == "ShellLockView");

        Assert.Equal(
            "{CompiledBinding ApplicationSecurityEditor}",
            AttributeValue(lockView, "DataContext"));
        Assert.Equal(
            "{CompiledBinding IsLocked}",
            AttributeValue(lockView, "IsVisible"));

        var component = LoadView(Path.Combine("Components", "ShellLockView"));
        Assert.Equal(
            "vm:ApplicationSecurityEditorViewModel",
            AttributeValue(component.Root!, "DataType"));

        Assert.DoesNotContain(
            component.Descendants().Attributes(),
            attribute => attribute.Value.StartsWith("{Binding ", StringComparison.Ordinal));

        var biometricButton = Assert.Single(
            component.Descendants(),
            element => AttributeValue(element, "Content")
                == "{CompiledBinding BiometricUnlockLabel}");
        Assert.Equal(
            "{CompiledBinding BiometricUnlockLabel}",
            AttributeValue(biometricButton, "Content"));
        Assert.Equal(
            "{CompiledBinding CanUseBiometrics}",
            AttributeValue(biometricButton, "IsVisible"));
    }

    [Fact]
    public void Application_ui_is_compiled_binding_only_for_native_aot()
    {
        var applicationProject = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Asura.App.csproj"));
        Assert.Equal(
            "true",
            applicationProject.Descendants()
                .Single(element => element.Name.LocalName
                    == "AvaloniaUseCompiledBindingsByDefault")
                .Value);

        var applicationRoot = Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App");
        var reflectionBindings = Directory
            .EnumerateFiles(applicationRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.Ordinal)
                || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "ReflectionBinding",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(applicationRoot, path))
            .ToArray();
        Assert.Empty(reflectionBindings);

        var desktopProject = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.Desktop",
            "Asura.Desktop.csproj"));
        Assert.DoesNotContain(
            desktopProject.Descendants(),
            element => element.Name.LocalName == "TrimmerRootAssembly"
                && AttributeValue(element, "Include") == "Asura.App");
        Assert.Equal(
            "false",
            desktopProject.Descendants()
                .Single(element => element.Name.LocalName
                    == "JsonSerializerIsReflectionEnabledByDefault")
                .Value);
        Assert.Equal(
            "false",
            desktopProject.Descendants()
                .Single(element => element.Name.LocalName
                    == "IlcGenerateCompleteTypeMetadata")
                .Value);

        var sourceRoot = Path.Combine(ApplicationViews.RepositoryRoot, "src");
        var dynamicallyDiscoveredContracts = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "DefaultJsonTypeInfoResolver",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();
        Assert.Empty(dynamicallyDiscoveredContracts);

        var aotReflectionWorkarounds = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("DynamicallyAccessedMembers", StringComparison.Ordinal)
                    || source.Contains("DynamicDependency", StringComparison.Ordinal)
                    || source.Contains("UnconditionalSuppressMessage", StringComparison.Ordinal)
                    || source.Contains("TrimmerRootAssembly", StringComparison.Ordinal)
                    || source.Contains("TrimmerRootDescriptor", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();
        Assert.Empty(aotReflectionWorkarounds);

        var dockProject = File.ReadAllText(Path.Combine(
            sourceRoot,
            "Asura.Docking",
            "Asura.Docking.csproj"));
        Assert.DoesNotContain(
            "Dock.Serializer.SystemTextJson",
            dockProject,
            StringComparison.Ordinal);

        var buildProperties = XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "Directory.Build.props"));
        Assert.Equal(
            "true",
            buildProperties.Descendants()
                .Single(element => element.Name.LocalName == "IsAotCompatible")
                .Value);
    }

    private static XDocument LoadView(string relativePath) =>
        XDocument.Load(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "Views",
            $"{relativePath}.axaml"));

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;
}
