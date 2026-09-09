using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Uses Secret Service through secret-tool. Secret bytes travel only through redirected stdin/stdout;
/// non-secret metadata is kept in atomic sidecar files so listing never asks secret-tool to reveal values.
/// </summary>
public sealed class LinuxSecretServiceSecretVault : ISecretVault
{
    private const string MetadataExtension = ".gsmeta";
    private const int MaximumToolOutputLength = (SecretMaterial.MaximumLength * 2) + 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _executable;
    private readonly string _serviceName;
    private readonly string _metadataDirectory;
    private readonly ISecretAccessPolicy _accessPolicy;
    private bool _disposed;

    public LinuxSecretServiceSecretVault(
        string executable,
        string serviceName,
        string metadataDirectory,
        ISecretAccessPolicy? accessPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataDirectory);

        _executable = Path.GetFullPath(executable);
        _serviceName = serviceName;
        _metadataDirectory = Path.GetFullPath(metadataDirectory);
        _accessPolicy = accessPolicy ?? SecretScopeAccessPolicy.Default;
        var available = OperatingSystem.IsLinux() && File.Exists(_executable);
        Availability = available
            ? new SecretVaultAvailability(
                SecretVaultAvailabilityState.Available,
                SecretVaultPersistenceKind.OsProtectedPersistent,
                SecretVaultCapabilities.All,
                "linux-secret-service",
                "linux_secret_tool",
                "Credentials are stored through the desktop Secret Service.")
            : new SecretVaultAvailability(
                SecretVaultAvailabilityState.Unavailable,
                SecretVaultPersistenceKind.None,
                SecretVaultCapabilities.None,
                "linux-secret-service",
                "linux_secret_service_unavailable",
                "Secret Service requires Linux and an available secret-tool executable.");
    }

    public SecretVaultAvailability Availability { get; }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Create,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (!Availability.CanPersist)
        {
            return ValueTask.FromResult(SecretVaultFailures.Unavailable<SecretMetadata>());
        }

        var plaintext = SecretVaultBuffers.Copy(material);
        return new ValueTask<SecretVaultResult<SecretMetadata>>(
            WithPlaintextAsync(
                plaintext,
                token => CreateCoreAsync(request, plaintext, token),
                cancellationToken));
    }

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMaterial>(
            _accessPolicy,
            SecretVaultOperation.Resolve,
            request.Scope,
            request.Purpose);
        return denied is not null
            ? ValueTask.FromResult(denied)
            : ExecuteAsync(token => ResolveCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Replace,
            request.Scope,
            request.Purpose);
        if (denied is not null)
        {
            return ValueTask.FromResult(denied);
        }

        if (!Availability.CanPersist)
        {
            return ValueTask.FromResult(SecretVaultFailures.Unavailable<SecretMetadata>());
        }

        var plaintext = SecretVaultBuffers.Copy(material);
        return new ValueTask<SecretVaultResult<SecretMetadata>>(
            WithPlaintextAsync(
                plaintext,
                token => ReplaceCoreAsync(request, plaintext, token),
                cancellationToken));
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.Relabel,
            request.Scope,
            request.Purpose);
        return denied is not null
            ? ValueTask.FromResult(denied)
            : ExecuteAsync(token => RelabelCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<Unit>(
            _accessPolicy,
            SecretVaultOperation.Delete,
            request.Scope,
            request.Purpose);
        return denied is not null
            ? ValueTask.FromResult(denied)
            : ExecuteAsync(token => DeleteCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<SecretMetadata>(
            _accessPolicy,
            SecretVaultOperation.GetMetadata,
            request.Scope,
            request.Purpose);
        return denied is not null
            ? ValueTask.FromResult(denied)
            : ExecuteAsync(
                _ => Task.FromResult(GetMetadataCore(request.Reference, request.Scope)),
                cancellationToken);
    }

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var denied = SecretVaultAuthorization.Authorize<IReadOnlyList<SecretMetadata>>(
            _accessPolicy,
            SecretVaultOperation.ListMetadata,
            request.Scope,
            request.Purpose);
        return denied is not null
            ? ValueTask.FromResult(denied)
            : ExecuteAsync(
                _ => Task.FromResult(ListMetadataCore(request)),
                cancellationToken);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    internal static string? FindSecretTool(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "secret-tool");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private async Task<SecretVaultResult<SecretMetadata>> CreateCoreAsync(
        CreateSecretRequest request,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(request.Reference);
        if (File.Exists(path))
        {
            return SecretVaultFailures.AlreadyExists<SecretMetadata>();
        }

        var now = DateTimeOffset.UtcNow;
        var metadata = new SecretMetadata(
            request.Reference,
            request.Label,
            request.Kind,
            request.Scope,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            now,
            now);

        var stored = await StoreSecretAsync(metadata, plaintext, cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            return SecretVaultFailures.PlatformFailure<SecretMetadata>();
        }

        try
        {
            WriteMetadata(path, metadata, false);
            return SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        catch
        {
            await ClearSecretAsync(request.Reference, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SecretVaultResult<SecretMaterial>> ResolveCoreAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMaterial>();
        }

        var metadata = ReadMetadata(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMaterial>(
            request.Scope,
            metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var lookup = await LookupSecretAsync(request.Reference, cancellationToken).ConfigureAwait(false);
        if (lookup is null)
        {
            return SecretVaultFailures.NotFound<SecretMaterial>();
        }

        try
        {
            metadata = metadata with { LastUsedAt = DateTimeOffset.UtcNow };
            WriteMetadata(path, metadata, true);
            var material = SecretMaterial.TakeOwnership(lookup);
            lookup = null;
            return SecretVaultResult<SecretMaterial>.Succeed(material);
        }
        finally
        {
            if (lookup is not null)
            {
                CryptographicOperations.ZeroMemory(lookup);
            }
        }
    }

    private async Task<SecretVaultResult<SecretMetadata>> ReplaceCoreAsync(
        ReplaceSecretRequest request,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var storedMetadata = ReadMetadata(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
            request.Scope,
            storedMetadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var metadata = storedMetadata with { UpdatedAt = DateTimeOffset.UtcNow };
        if (!await StoreSecretAsync(metadata, plaintext, cancellationToken).ConfigureAwait(false))
        {
            return SecretVaultFailures.PlatformFailure<SecretMetadata>();
        }

        WriteMetadata(path, metadata, true);
        return SecretVaultResult<SecretMetadata>.Succeed(metadata);
    }

    private async Task<SecretVaultResult<SecretMetadata>> RelabelCoreAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var storedMetadata = ReadMetadata(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
            request.Scope,
            storedMetadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        var plaintext = await LookupSecretAsync(request.Reference, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        try
        {
            var metadata = storedMetadata with
            {
                Label = request.Label,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (!await StoreSecretAsync(metadata, plaintext, cancellationToken).ConfigureAwait(false))
            {
                return SecretVaultFailures.PlatformFailure<SecretMetadata>();
            }

            WriteMetadata(path, metadata, true);
            return SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task<SecretVaultResult<Unit>> DeleteCoreAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(request.Reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<Unit>();
        }

        var metadata = ReadMetadata(path, request.Reference);
        var scopeMismatch = SecretVaultAuthorization.MatchStoredScope<Unit>(
            request.Scope,
            metadata.Scope);
        if (scopeMismatch is not null)
        {
            return scopeMismatch;
        }

        if (!await ClearSecretAsync(request.Reference, cancellationToken).ConfigureAwait(false))
        {
            return SecretVaultFailures.PlatformFailure<Unit>();
        }

        File.Delete(path);
        return SecretVaultResult<Unit>.Succeed(Unit.Value);
    }

    private SecretVaultResult<SecretMetadata> GetMetadataCore(
        SecretRef reference,
        SecretScope expectedScope)
    {
        var path = GetMetadataPath(reference);
        if (!File.Exists(path))
        {
            return SecretVaultFailures.NotFound<SecretMetadata>();
        }

        var metadata = ReadMetadata(path, reference);
        return SecretVaultAuthorization.MatchStoredScope<SecretMetadata>(
                expectedScope,
                metadata.Scope)
            ?? SecretVaultResult<SecretMetadata>.Succeed(metadata);
    }

    private SecretVaultResult<IReadOnlyList<SecretMetadata>> ListMetadataCore(
        ListSecretMetadataRequest request)
    {
        if (!Directory.Exists(_metadataDirectory))
        {
            return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]);
        }

        var metadata = Directory
            .EnumerateFiles(_metadataDirectory, $"*{MetadataExtension}", SearchOption.TopDirectoryOnly)
            .Select(path => ReadMetadata(path, null))
            .Where(item => request.Scope is null || item.Scope == request.Scope)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Reference.Value, StringComparer.Ordinal)
            .ToArray();
        return SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed(metadata);
    }

    private async Task<bool> StoreSecretAsync(
        SecretMetadata metadata,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        var maximumLength = Base64.GetMaxEncodedToUtf8Length(plaintext.Length);
        var encoded = new byte[maximumLength + 1];

        try
        {
            var status = Base64.EncodeToUtf8(plaintext, encoded, out _, out var written);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            encoded[written] = (byte)'\n';
            var result = await RunToolAsync(
                [
                    "store",
                    $"--label={metadata.Label}",
                    "asura-service",
                    _serviceName,
                    "asura-ref",
                    metadata.Reference.Value,
                    "asura-kind",
                    metadata.Kind.ToString(),
                    "asura-scope",
                    metadata.Scope.Kind.ToString(),
                ],
                encoded.AsMemory(0, written + 1),
                false,
                cancellationToken).ConfigureAwait(false);
            result.Dispose();
            return result.ExitCode == 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private async Task<byte[]?> LookupSecretAsync(
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        using var result = await RunToolAsync(
            ["lookup", "asura-service", _serviceName, "asura-ref", reference.Value],
            null,
            true,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var length = result.OutputLength;
        while (length > 0 && result.Output[length - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
        {
            length--;
        }

        var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(length)];
        var status = Base64.DecodeFromUtf8(
            result.Output.AsSpan(0, length),
            decoded,
            out _,
            out var written);
        if (status != OperationStatus.Done || written <= 0)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("Secret Service returned an invalid credential payload.");
        }

        if (written == decoded.Length)
        {
            return decoded;
        }

        var exact = new byte[written];
        decoded.AsSpan(0, written).CopyTo(exact);
        CryptographicOperations.ZeroMemory(decoded);
        return exact;
    }

    private async Task<bool> ClearSecretAsync(
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        using var result = await RunToolAsync(
            ["clear", "asura-service", _serviceName, "asura-ref", reference.Value],
            null,
            false,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<ToolResult> RunToolAsync(
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? standardInput,
        bool captureOutput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new IOException("Secret Service helper could not start.");
        }

        Task<BoundedOutput?>? outputTask = null;
        try
        {
            var errorTask = DrainAsync(process.StandardError.BaseStream, cancellationToken);
            outputTask = captureOutput
                ? ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken)
                : DrainOutputAsync(process.StandardOutput.BaseStream, cancellationToken);
            var inputTask = standardInput is { } input
                ? WriteInputAsync(process.StandardInput.BaseStream, input, cancellationToken)
                : Task.CompletedTask;

            await Task.WhenAll(
                    inputTask,
                    errorTask,
                    outputTask,
                    process.WaitForExitAsync(cancellationToken))
                .ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            return output is null
                ? new ToolResult(process.ExitCode, [], 0)
                : new ToolResult(process.ExitCode, output.Buffer, output.Length);
        }
        catch
        {
            if (outputTask is { IsCompletedSuccessfully: true })
            {
                outputTask.Result?.Dispose();
            }

            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw;
        }
    }

    private static async Task WriteInputAsync(
        Stream stream,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Close();
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task<BoundedOutput?> DrainOutputAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await DrainAsync(stream, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static async Task<BoundedOutput?> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumToolOutputLength];
        var length = 0;

        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var extra = new byte[1];
                    try
                    {
                        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) > 0)
                        {
                            throw new IOException("Secret Service returned an oversized payload.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(extra);
                    }

                    break;
                }

                var read = await stream
                    .ReadAsync(buffer.AsMemory(length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new BoundedOutput(buffer, length);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }
    }

    private SecretMetadata ReadMetadata(string path, SecretRef? expectedReference)
    {
        var bytes = File.ReadAllBytes(path);

        try
        {
            var metadata = JsonSerializer.Deserialize(
                    bytes,
                    InfrastructureJsonContext.Default.SecretMetadata)
                ?? throw new JsonException("Secret metadata was empty.");
            if (expectedReference is { } expected && metadata.Reference != expected)
            {
                throw new JsonException("Secret metadata did not match its reference.");
            }

            return metadata;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void WriteMetadata(string path, SecretMetadata metadata, bool overwrite)
    {
        Directory.CreateDirectory(_metadataDirectory);
        var temporaryPath = Path.Combine(_metadataDirectory, $".{Path.GetRandomFileName()}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            InfrastructureJsonContext.Default.SecretMetadata);

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetMetadataPath(SecretRef reference)
    {
        var bytes = Encoding.UTF8.GetBytes(reference.Value);

        try
        {
            return Path.Combine(
                _metadataDirectory,
                $"{Convert.ToHexString(SHA256.HashData(bytes))}{MetadataExtension}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<SecretVaultResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<SecretVaultResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (!Availability.CanPersist)
        {
            return SecretVaultFailures.Unavailable<T>();
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SecretVaultFailures.Cancelled<T>();
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SecretVaultFailures.Cancelled<T>();
        }
        catch (UnauthorizedAccessException)
        {
            return SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.AccessDenied));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return SecretVaultFailures.CorruptEntry<T>();
        }
        catch (IOException)
        {
            return SecretVaultFailures.PlatformFailure<T>(true);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return SecretVaultFailures.PlatformFailure<T>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SecretVaultResult<T>> WithPlaintextAsync<T>(
        byte[] plaintext,
        Func<CancellationToken, Task<SecretVaultResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private sealed class BoundedOutput(byte[] buffer, int length) : IDisposable
    {
        public byte[] Buffer { get; } = buffer;

        public int Length { get; } = length;

        public void Dispose() => CryptographicOperations.ZeroMemory(Buffer);
    }

    private sealed class ToolResult(int exitCode, byte[] output, int outputLength) : IDisposable
    {
        public int ExitCode { get; } = exitCode;

        public byte[] Output { get; } = output;

        public int OutputLength { get; } = outputLength;

        public void Dispose() => CryptographicOperations.ZeroMemory(Output);
    }
}
