using System.Runtime.InteropServices;
using Porta.Pty;

namespace Asura.Terminal;

/// <summary>Only the source-pinned descriptor-safe shim may service Unix PTY calls.</summary>
internal static class PortablePtyRuntime
{
    private static readonly Lazy<nint> Library = new(Load);

    internal static void EnsureInitialized()
    {
        if (!OperatingSystem.IsWindows())
        {
            _ = Library.Value;
        }
    }

    private static unsafe nint Load()
    {
        var name = OperatingSystem.IsMacOS() ? "libasura_pty.dylib" : "libasura_pty.so";
        var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name));
        try
        {
            var abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "asura_pty_descriptor_boundary_abi");
            if (abi() != 1)
            {
                throw new InvalidOperationException("The bundled PTY descriptor boundary is incompatible.");
            }
            NativeLibrary.SetDllImportResolver(typeof(PtyProvider).Assembly,
                (library, _, _) => string.Equals(library, "libporta_pty", StringComparison.Ordinal) ? handle : 0);
            return handle;
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }
}
