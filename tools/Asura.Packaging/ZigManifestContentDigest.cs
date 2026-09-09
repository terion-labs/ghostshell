using System.Buffers.Binary;
using System.Numerics;

namespace Asura.Packaging;

/// <summary>
/// Computes the file-content digest stored by Zig 0.15.2 in Build.Cache
/// manifests: SipHash128(1, 3) with Zig's versioned manifest key.
/// </summary>
internal sealed class ZigManifestContentDigest
{
    private const ulong Key0 = 0x575617cf84a25233;
    private const ulong Key1 = 0x60f0d677e4cdbb01;

    private readonly byte[] _tail = new byte[8];
    private ulong _v0 = Key0 ^ 0x736f6d6570736575;
    private ulong _v1 = (Key1 ^ 0x646f72616e646f6d) ^ 0xee;
    private ulong _v2 = Key0 ^ 0x6c7967656e657261;
    private ulong _v3 = Key1 ^ 0x7465646279746573;
    private ulong _messageLength;
    private int _tailLength;
    private bool _finished;

    public void Append(ReadOnlySpan<byte> content)
    {
        if (_finished)
        {
            throw new InvalidOperationException(
                "The Zig manifest digest has already been finalized.");
        }

        _messageLength = unchecked(_messageLength + (ulong)content.Length);
        if (_tailLength > 0)
        {
            var copied = Math.Min(_tail.Length - _tailLength, content.Length);
            content[..copied].CopyTo(_tail.AsSpan(_tailLength));
            _tailLength += copied;
            content = content[copied..];
            if (_tailLength == _tail.Length)
            {
                Compress(BinaryPrimitives.ReadUInt64LittleEndian(_tail));
                _tailLength = 0;
            }
        }

        while (content.Length >= 8)
        {
            Compress(BinaryPrimitives.ReadUInt64LittleEndian(content));
            content = content[8..];
        }

        content.CopyTo(_tail);
        _tailLength = content.Length;
    }

    public string FinishHex()
    {
        if (_finished)
        {
            throw new InvalidOperationException(
                "The Zig manifest digest has already been finalized.");
        }

        _finished = true;
        Span<byte> finalBlock = stackalloc byte[8];
        finalBlock.Clear();
        _tail.AsSpan(0, _tailLength).CopyTo(finalBlock);
        finalBlock[7] = unchecked((byte)_messageLength);
        Compress(BinaryPrimitives.ReadUInt64LittleEndian(finalBlock));

        _v2 ^= 0xee;
        SipRound();
        SipRound();
        SipRound();
        var low = _v0 ^ _v1 ^ _v2 ^ _v3;

        _v1 ^= 0xdd;
        SipRound();
        SipRound();
        SipRound();
        var high = _v0 ^ _v1 ^ _v2 ^ _v3;

        Span<byte> digest = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(digest, low);
        BinaryPrimitives.WriteUInt64LittleEndian(digest[8..], high);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string Compute(ReadOnlySpan<byte> content)
    {
        var digest = new ZigManifestContentDigest();
        digest.Append(content);
        return digest.FinishHex();
    }

    private void Compress(ulong message)
    {
        _v3 ^= message;
        SipRound();
        _v0 ^= message;
    }

    private void SipRound()
    {
        _v0 = unchecked(_v0 + _v1);
        _v1 = BitOperations.RotateLeft(_v1, 13);
        _v1 ^= _v0;
        _v0 = BitOperations.RotateLeft(_v0, 32);
        _v2 = unchecked(_v2 + _v3);
        _v3 = BitOperations.RotateLeft(_v3, 16);
        _v3 ^= _v2;
        _v0 = unchecked(_v0 + _v3);
        _v3 = BitOperations.RotateLeft(_v3, 21);
        _v3 ^= _v0;
        _v2 = unchecked(_v2 + _v1);
        _v1 = BitOperations.RotateLeft(_v1, 17);
        _v1 ^= _v2;
        _v2 = BitOperations.RotateLeft(_v2, 32);
    }
}
