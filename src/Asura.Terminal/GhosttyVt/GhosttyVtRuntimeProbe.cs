using System.Reflection;
using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

public sealed record GhosttyVtRuntimeAvailability(
    bool IsAvailable,
    string? LibraryPath,
    string? Version,
    uint ExtensionAbi,
    bool SupportsSimd,
    bool SupportsKittyGraphics,
    bool SupportsTmuxControlMode,
    string Detail);

/// <summary>
/// Validates the pinned libghostty-vt boundary before a terminal session uses it.
/// The export set mirrors the Ghostty headers consumed by this binding, rather than
/// treating a successfully loaded file as proof of ABI compatibility.
/// </summary>
public static class GhosttyVtRuntimeProbe
{
    private const string ConfiguredPathVariable = "ASURA_GHOSTTY_VT_PATH";
    private const uint RequiredExtensionAbi = 1;

    // The probe derives its compatibility closure from the binding itself so a
    // newly imported native call cannot silently bypass startup validation.
    private static readonly string[] RequiredExports = [.. typeof(GhosttyVtNative)
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Select(static method => method.GetCustomAttribute<LibraryImportAttribute>(inherit: false))
        .Where(static attribute => attribute is not null)
        .Select(static attribute => attribute!.EntryPoint)
        .Where(static entryPoint => !string.IsNullOrWhiteSpace(entryPoint))
        .Select(static entryPoint => entryPoint!)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    internal static IReadOnlyList<string> RequiredExportsForTesting => RequiredExports;

    public static GhosttyVtRuntimeAvailability Detect()
    {
        if (!GhosttyVtAbi.TryValidateManagedLayouts(out var layoutDetail))
        {
            return new GhosttyVtRuntimeAvailability(
                false,
                null,
                null,
                0,
                false,
                false,
                false,
                layoutDetail);
        }

        foreach (var candidate in GetCandidates())
        {
            if (!TryLoadCompatible(candidate, out var handle, out var buildInfo))
            {
                continue;
            }

            try
            {
                return new GhosttyVtRuntimeAvailability(
                    true,
                    candidate,
                    buildInfo.Version,
                    buildInfo.ExtensionAbi,
                    buildInfo.SupportsSimd,
                    buildInfo.SupportsKittyGraphics,
                    buildInfo.SupportsTmuxControlMode,
                    $"libghostty-vt {buildInfo.Version} with Asura extension ABI " +
                    $"{buildInfo.ExtensionAbi} is available.");
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        return new GhosttyVtRuntimeAvailability(
            false,
            null,
            null,
            0,
            false,
            false,
            false,
            "A compatible libghostty-vt runtime is missing. " +
            $"Run ./scripts/bootstrap.sh or set {ConfiguredPathVariable}.");
    }

    /// <summary>
    /// Loads only explicit application-owned paths. This deliberately excludes
    /// bare library names because it runs from the unmanaged-resolution event;
    /// retrying a bare name there would recursively invoke the same event.
    /// </summary>
    internal static bool TryLoadConfiguredRuntime(out nint handle)
    {
        foreach (var candidate in GetExplicitCandidates())
        {
            if (TryLoadCompatible(candidate, out handle, out _))
            {
                return true;
            }
        }

        handle = 0;
        return false;
    }

    private static IReadOnlyList<string> GetCandidates()
    {
        var fileName = GetPlatformLibraryFileName();

        return
        [
            .. GetExplicitCandidates(),
            fileName,
            GhosttyVtNative.LibraryName,
        ];
    }

    private static IReadOnlyList<string> GetExplicitCandidates()
    {
        var fileName = GetPlatformLibraryFileName();
        var configuredPath = Environment.GetEnvironmentVariable(ConfiguredPathVariable);
        var runtimeIdentifier = GetRuntimeIdentifier();

        string?[] candidates =
        [
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native", fileName),
        ];

        return [.. candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(static candidate => candidate!)
            .Distinct(StringComparer.Ordinal)];
    }

    private static bool TryLoadCompatible(
        string candidate,
        out nint handle,
        out BuildInformation buildInfo)
    {
        if (!NativeLibrary.TryLoad(candidate, out handle))
        {
            buildInfo = default;
            return false;
        }

        var loadedHandle = handle;
        var missingExport = RequiredExports.FirstOrDefault(
            export => !NativeLibrary.TryGetExport(loadedHandle, export, out _));
        var extensionAbi = missingExport is null ? ReadExtensionAbi(handle) : 0;
        if (missingExport is null
            && extensionAbi == RequiredExtensionAbi
            && TryReadBuildInfo(handle, extensionAbi, out buildInfo))
        {
            return true;
        }

        NativeLibrary.Free(handle);
        handle = 0;
        buildInfo = default;
        return false;
    }

    private static string GetPlatformLibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "ghostty-vt.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libghostty-vt.dylib";
        }

        return "libghostty-vt.so";
    }

    private static string GetRuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        return $"{platform}-{architecture}";
    }

    private static uint ReadExtensionAbi(nint handle)
    {
        var address = NativeLibrary.GetExport(handle, "ghostty_asura_extension_abi");
        var query = Marshal.GetDelegateForFunctionPointer<ExtensionAbiFunction>(address);
        return query();
    }

    private static unsafe bool TryReadBuildInfo(
        nint handle,
        uint extensionAbi,
        out BuildInformation buildInfo)
    {
        var address = NativeLibrary.GetExport(handle, "ghostty_build_info");
        var query = Marshal.GetDelegateForFunctionPointer<BuildInfoFunction>(address);

        GhosttyVtString version = default;
        byte simd = 0;
        byte kitty = 0;
        byte tmux = 0;

        if (query(GhosttyVtBuildInfo.VersionString, (nint)(&version)) != GhosttyVtResult.Success ||
            query(GhosttyVtBuildInfo.Simd, (nint)(&simd)) != GhosttyVtResult.Success ||
            query(GhosttyVtBuildInfo.KittyGraphics, (nint)(&kitty)) != GhosttyVtResult.Success ||
            query(GhosttyVtBuildInfo.TmuxControlMode, (nint)(&tmux)) != GhosttyVtResult.Success)
        {
            buildInfo = default;
            return false;
        }

        var copiedVersion = version.CopyUtf8();
        if (string.IsNullOrWhiteSpace(copiedVersion))
        {
            buildInfo = default;
            return false;
        }

        buildInfo = new BuildInformation(
            copiedVersion,
            extensionAbi,
            simd != 0,
            kitty != 0,
            tmux != 0);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ExtensionAbiFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate GhosttyVtResult BuildInfoFunction(GhosttyVtBuildInfo data, nint output);

    private readonly record struct BuildInformation(
        string Version,
        uint ExtensionAbi,
        bool SupportsSimd,
        bool SupportsKittyGraphics,
        bool SupportsTmuxControlMode);
}
