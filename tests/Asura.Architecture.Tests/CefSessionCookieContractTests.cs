namespace Asura.Architecture.Tests;

public sealed class CefSessionCookieContractTests
{
    [Fact]
    public void OnlyDiskBackedRequestContextsEnableSessionCookiePersistence()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "vendor", "exclr8cef", "native", "shim", "exclr8cef_osr.cc"));
        var start = source.IndexOf("int excef_create_request_context(", StringComparison.Ordinal);
        var end = source.IndexOf("CefRefPtr<CefRequestContext> ctx", start, StringComparison.Ordinal);
        var settings = source[start..end];
        var conditional = settings.IndexOf("if (cache_path && *cache_path) {", StringComparison.Ordinal);
        var branchEnd = settings.IndexOf('}', conditional);
        Assert.Contains("settings.persist_session_cookies = true;", settings[conditional..branchEnd], StringComparison.Ordinal);
        Assert.DoesNotContain("persist_session_cookies", settings[..conditional], StringComparison.Ordinal);
        Assert.DoesNotContain("persist_session_cookies", settings[branchEnd..], StringComparison.Ordinal);
        var build = File.ReadAllText(Path.Combine(root, "scripts", "build-cef-runtime.sh"));
        Assert.Contains("test-browser-session-cookies.sh\" \"${cef_artifact_dir}\"", build, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Could not locate the Asura repository root.");
    }
}
