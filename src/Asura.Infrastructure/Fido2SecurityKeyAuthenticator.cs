using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Security keys over libfido2.
///
/// UNVERIFIED AGAINST HARDWARE. The library loads and enumerates correctly —
/// that much is proven — but no key has yet answered an enrollment or a
/// derivation here. Until one has, nothing in the application may depend on
/// this for access to data it cannot otherwise reach; the probe under
/// tools is the intended way to find out, with a key in hand.
///
/// Everything is done on a background thread because every call blocks on a
/// person touching the key, and every native object is freed on the way out
/// including the failure paths — a leaked fido_dev_t keeps the device open
/// and the next attempt cannot claim it.
/// </summary>
public sealed class Fido2SecurityKeyAuthenticator : ISecurityKeyAuthenticator
{
    /// <summary>
    /// The relying party this profile presents. Keys scope credentials by
    /// it, so it must stay fixed: change it and every enrolled key stops
    /// recognising its own credential.
    /// </summary>
    private const string RelyingPartyId = "asura.local";

    private const string RelyingPartyName = "Asura";

    /// <summary>COSE_ES256 — the algorithm every FIDO2 key implements.</summary>
    private const int Es256 = -7;

    /// <summary>FIDO_EXT_HMAC_SECRET.</summary>
    private const int HmacSecretExtension = 0x02;

    private const int FidoOk = 0;

    /// <summary>fido_opt_t: omit, false, true.</summary>
    private const int OptOmit = 0;

    private const int OptFalse = 1;

    /// <summary>How many devices a single enumeration may report.</summary>
    private const int MaximumDevices = 8;

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(Fido2SecurityKeyAuthenticator).Assembly,
                ResolveNativeLibrary);
            Native.fido_init(0);
            return true;
        }
        catch (Exception exception)
            when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // A build without the native library is a build without security
            // keys, not a build that crashes when someone opens settings.
            return false;
        }
    });

    public bool IsSupported => Initialized.Value;

    /// <summary>
    /// Finds libfido2. A shipped build carries it beside the application,
    /// which the default probing already finds; a developer build usually
    /// has it from a package manager instead, and those directories are not
    /// on any default search path — so they are named here rather than
    /// leaving the feature mysteriously absent on the machine of whoever is
    /// working on it.
    /// </summary>
    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Native.LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var shipped))
        {
            return shipped;
        }

        foreach (var candidate in DeveloperLibraryPaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var local))
            {
                return local;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> DeveloperLibraryPaths()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/lib/libfido2.dylib";
            yield return "/usr/local/lib/libfido2.dylib";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/lib/x86_64-linux-gnu/libfido2.so.1";
            yield return "/usr/lib/libfido2.so.1";
            yield return "/usr/local/lib/libfido2.so.1";
        }
    }

    public ValueTask<bool> IsKeyPresentAsync(CancellationToken cancellationToken) =>
        !IsSupported
            ? new ValueTask<bool>(false)
            : new ValueTask<bool>(Task.Run(
                () => WithFirstDevicePath(path => path is not null) is true,
                cancellationToken));

    public ValueTask<(SecurityKeyEnrollment? Enrollment, string? Failure)> EnrollAsync(
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return new((null, "This build cannot talk to security keys."));
        }

        return new(Task.Run(Enroll, cancellationToken));
    }

    public ValueTask<(byte[]? Secret, string? Failure)> DeriveSecretAsync(
        SecurityKeyEnrollment enrollment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (!IsSupported)
        {
            return new((null, "This build cannot talk to security keys."));
        }

        return new(Task.Run(() => Derive(enrollment), cancellationToken));
    }

    private static (SecurityKeyEnrollment? Enrollment, string? Failure) Enroll()
    {
        var path = WithFirstDevicePath(devicePath => devicePath);
        if (path is null)
        {
            return (null, "No security key is attached.");
        }

        var device = Native.fido_dev_new();
        var credential = Native.fido_cred_new();
        try
        {
            var opened = Native.fido_dev_open(device, path);
            if (opened != FidoOk)
            {
                return (null, Describe("The security key could not be opened", opened));
            }

            // The client-data hash is required by the protocol and carries no
            // meaning for a local credential: nothing verifies it later, so
            // it is random rather than pretending to bind an origin.
            var clientData = RandomNumberGenerator.GetBytes(32);
            var userId = RandomNumberGenerator.GetBytes(32);
            Native.fido_cred_set_type(credential, Es256);
            Native.fido_cred_set_clientdata_hash(credential, clientData, (nuint)clientData.Length);
            Native.fido_cred_set_rp(credential, RelyingPartyId, RelyingPartyName);
            Native.fido_cred_set_user(
                credential,
                userId,
                (nuint)userId.Length,
                RelyingPartyName,
                RelyingPartyName,
                null);
            Native.fido_cred_set_extensions(credential, HmacSecretExtension);
            // Non-resident: the key stores nothing, so it cannot run out of
            // slots and the credential lives here instead.
            Native.fido_cred_set_rk(credential, OptFalse);
            Native.fido_cred_set_uv(credential, OptOmit);

            var made = Native.fido_dev_make_cred(device, credential, null);
            if (made != FidoOk)
            {
                return (null, Describe("The security key refused enrollment", made));
            }

            var idPointer = Native.fido_cred_id_ptr(credential);
            var idLength = (int)Native.fido_cred_id_len(credential);
            if (idPointer == IntPtr.Zero || idLength <= 0)
            {
                return (null, "The security key returned no credential.");
            }

            var credentialId = new byte[idLength];
            Marshal.Copy(idPointer, credentialId, 0, idLength);
            return (
                new SecurityKeyEnrollment(credentialId, RandomNumberGenerator.GetBytes(32)),
                null);
        }
        finally
        {
            Native.fido_cred_free(ref credential);
            Native.fido_dev_close(device);
            Native.fido_dev_free(ref device);
        }
    }

    private static (byte[]? Secret, string? Failure) Derive(SecurityKeyEnrollment enrollment)
    {
        var path = WithFirstDevicePath(devicePath => devicePath);
        if (path is null)
        {
            return (null, "No security key is attached.");
        }

        var device = Native.fido_dev_new();
        var assertion = Native.fido_assert_new();
        try
        {
            var opened = Native.fido_dev_open(device, path);
            if (opened != FidoOk)
            {
                return (null, Describe("The security key could not be opened", opened));
            }

            var clientData = RandomNumberGenerator.GetBytes(32);
            Native.fido_assert_set_clientdata_hash(
                assertion,
                clientData,
                (nuint)clientData.Length);
            Native.fido_assert_set_rp(assertion, RelyingPartyId);
            Native.fido_assert_allow_cred(
                assertion,
                enrollment.CredentialId,
                (nuint)enrollment.CredentialId.Length);
            Native.fido_assert_set_extensions(assertion, HmacSecretExtension);
            Native.fido_assert_set_hmac_salt(
                assertion,
                enrollment.Salt,
                (nuint)enrollment.Salt.Length);

            var asserted = Native.fido_dev_get_assert(device, assertion, null);
            if (asserted != FidoOk)
            {
                return (null, Describe("The security key refused", asserted));
            }

            var secretPointer = Native.fido_assert_hmac_secret_ptr(assertion, 0);
            var secretLength = (int)Native.fido_assert_hmac_secret_len(assertion, 0);
            if (secretPointer == IntPtr.Zero || secretLength <= 0)
            {
                return (
                    null,
                    "This security key did not return a secret; it may not support "
                        + "the hmac-secret extension.");
            }

            var secret = new byte[secretLength];
            Marshal.Copy(secretPointer, secret, 0, secretLength);
            return (secret, null);
        }
        finally
        {
            Native.fido_assert_free(ref assertion);
            Native.fido_dev_close(device);
            Native.fido_dev_free(ref device);
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> against the first attached key's path, or
    /// against null when none answered. One enumeration, always freed.
    /// </summary>
    private static T WithFirstDevicePath<T>(Func<string?, T> use)
    {
        var list = Native.fido_dev_info_new((nuint)MaximumDevices);
        try
        {
            var listed = Native.fido_dev_info_manifest(
                list,
                (nuint)MaximumDevices,
                out var found);
            if (listed != FidoOk || found == 0)
            {
                return use(null);
            }

            var info = Native.fido_dev_info_ptr(list, 0);
            return use(info == IntPtr.Zero
                ? null
                : Marshal.PtrToStringAnsi(Native.fido_dev_info_path(info)));
        }
        finally
        {
            Native.fido_dev_info_free(ref list, (nuint)MaximumDevices);
        }
    }

    /// <summary>
    /// The library's own words for a failure, which name the real cause —
    /// a key waiting for a PIN, a touch that never came — far better than
    /// anything guessed from a number.
    /// </summary>
    private static string Describe(string what, int error)
    {
        var reason = Marshal.PtrToStringAnsi(Native.fido_strerr(error));
        return string.IsNullOrEmpty(reason)
            ? $"{what} ({error})."
            : $"{what}: {reason}.";
    }

    private static class Native
    {
        /// <summary>
        /// Resolved by name so the build's per-runtime native artifact is
        /// found the same way the terminal library is, with a resolver
        /// filling in developer installs.
        /// </summary>
        public const string LibraryName = "fido2";

        private const string Library = LibraryName;

        [DllImport(Library)]
        public static extern void fido_init(int flags);

        [DllImport(Library)]
        public static extern IntPtr fido_strerr(int error);

        [DllImport(Library)]
        public static extern IntPtr fido_dev_info_new(nuint n);

        [DllImport(Library)]
        public static extern int fido_dev_info_manifest(IntPtr list, nuint ilen, out nuint olen);

        [DllImport(Library)]
        public static extern void fido_dev_info_free(ref IntPtr list, nuint n);

        [DllImport(Library)]
        public static extern IntPtr fido_dev_info_ptr(IntPtr list, nuint index);

        [DllImport(Library)]
        public static extern IntPtr fido_dev_info_path(IntPtr info);

        [DllImport(Library)]
        public static extern IntPtr fido_dev_new();

        [DllImport(Library)]
        public static extern void fido_dev_free(ref IntPtr device);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_dev_open(IntPtr device, string path);

        [DllImport(Library)]
        public static extern int fido_dev_close(IntPtr device);

        [DllImport(Library)]
        public static extern IntPtr fido_cred_new();

        [DllImport(Library)]
        public static extern void fido_cred_free(ref IntPtr credential);

        [DllImport(Library)]
        public static extern int fido_cred_set_type(IntPtr credential, int type);

        [DllImport(Library)]
        public static extern int fido_cred_set_clientdata_hash(
            IntPtr credential,
            byte[] hash,
            nuint length);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_cred_set_rp(IntPtr credential, string id, string name);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_cred_set_user(
            IntPtr credential,
            byte[] userId,
            nuint userIdLength,
            string name,
            string displayName,
            string? icon);

        [DllImport(Library)]
        public static extern int fido_cred_set_extensions(IntPtr credential, int extensions);

        [DllImport(Library)]
        public static extern int fido_cred_set_rk(IntPtr credential, int option);

        [DllImport(Library)]
        public static extern int fido_cred_set_uv(IntPtr credential, int option);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_dev_make_cred(
            IntPtr device,
            IntPtr credential,
            string? pin);

        [DllImport(Library)]
        public static extern IntPtr fido_cred_id_ptr(IntPtr credential);

        [DllImport(Library)]
        public static extern nuint fido_cred_id_len(IntPtr credential);

        [DllImport(Library)]
        public static extern IntPtr fido_assert_new();

        [DllImport(Library)]
        public static extern void fido_assert_free(ref IntPtr assertion);

        [DllImport(Library)]
        public static extern int fido_assert_set_clientdata_hash(
            IntPtr assertion,
            byte[] hash,
            nuint length);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_assert_set_rp(IntPtr assertion, string id);

        [DllImport(Library)]
        public static extern int fido_assert_allow_cred(
            IntPtr assertion,
            byte[] credentialId,
            nuint length);

        [DllImport(Library)]
        public static extern int fido_assert_set_extensions(IntPtr assertion, int extensions);

        [DllImport(Library)]
        public static extern int fido_assert_set_hmac_salt(
            IntPtr assertion,
            byte[] salt,
            nuint length);

        [DllImport(Library, CharSet = CharSet.Ansi)]
        public static extern int fido_dev_get_assert(
            IntPtr device,
            IntPtr assertion,
            string? pin);

        [DllImport(Library)]
        public static extern IntPtr fido_assert_hmac_secret_ptr(IntPtr assertion, nuint index);

        [DllImport(Library)]
        public static extern nuint fido_assert_hmac_secret_len(IntPtr assertion, nuint index);
    }
}
