using System.Security.Cryptography;

namespace Asura.Application;

/// <summary>
/// Owns a bounded secret byte buffer and clears that buffer when disposed.
/// Callers must keep the material alive until an asynchronous vault operation completes.
/// </summary>
public sealed class SecretMaterial : IDisposable
{
    public const int MaximumLength = 1024 * 1024;

    private readonly Lock _gate = new();
    private byte[]? _buffer;

    private SecretMaterial(byte[] buffer) => _buffer = buffer;

    ~SecretMaterial() => Dispose(false);

    public int Length
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_buffer is null, this);
                return _buffer.Length;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _buffer is null;
            }
        }
    }

    public static SecretMaterial CopyFrom(ReadOnlySpan<byte> value)
    {
        ValidateLength(value.Length);
        return new SecretMaterial(value.ToArray());
    }

    /// <summary>
    /// Transfers ownership of <paramref name="buffer"/>. The caller must not read or mutate it afterwards.
    /// </summary>
    public static SecretMaterial TakeOwnership(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateLength(buffer.Length);
        return new SecretMaterial(buffer);
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            if (destination.Length < _buffer.Length)
            {
                throw new ArgumentException("The destination is too small for the secret material.", nameof(destination));
            }

            _buffer.CopyTo(destination);
        }
    }

    public SecretMaterial Clone()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return new SecretMaterial([.. _buffer]);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public override string ToString() => "[secret material]";

    private static void ValidateLength(int length)
    {
        if (length is <= 0 or > MaximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Secret material must contain between 1 and {MaximumLength} bytes.");
        }
    }

    private void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_buffer is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = null;
        }
    }
}
