using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Browser;

/// <summary>
/// Crash-recovery label stored beside, never inside, a temporary Chromium
/// profile. It contains identity metadata only and lets the next process seal
/// a private orphan before removing the plaintext runtime tree.
/// </summary>
internal static class BrowserProfileRuntimeManifest
{
    private const string FileName = ".asura-profile";
    private const int Magic = 0x47535052;
    private const int Version = 1;
    private const int MaximumIdentityBytes = 4_096;
    private const long PhaseOffset = sizeof(int) + sizeof(int);

    internal static string PathFor(string entryDirectory) =>
        Path.Combine(entryDirectory, FileName);

    internal static void Write(string entryDirectory, BrowserProfileStateKey key)
    {
        var path = PathFor(entryDirectory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((int)BrowserProfileRuntimePhase.Preparing);
        WriteBounded(writer, key.Selection.ProfileId.Value);
        writer.Write((int)key.Selection.Partition.Kind);
        WriteBounded(writer, key.Selection.Partition.Identity);
        WriteBounded(writer, key.Route);
        writer.Flush();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static BrowserProfileRuntimeRecord Read(string entryDirectory)
    {
        var path = PathFor(entryDirectory);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists
            || info.LinkTarget is not null
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException(
                "A browser runtime profile has no valid recovery manifest.");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException(
                "A browser runtime profile has an unsupported recovery manifest.");
        }

        var phase = (BrowserProfileRuntimePhase)reader.ReadInt32();
        if (!Enum.IsDefined(phase))
        {
            throw new InvalidDataException(
                "A browser runtime profile manifest has an invalid phase.");
        }

        var profileId = new BrowserProfileId(ReadBounded(reader));
        var partitionKind = (BrowserProfileKind)reader.ReadInt32();
        var partition = new BrowserProfileKey(partitionKind, ReadBounded(reader));
        var route = ReadBounded(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "A browser runtime profile manifest contains trailing data.");
        }

        return new BrowserProfileRuntimeRecord(
            new BrowserProfileStateKey(
                new BrowserProfileSelection(profileId, partition),
                route),
            phase);
    }

    internal static void MarkActive(string entryDirectory)
    {
        var path = PathFor(entryDirectory);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.Position = PhaseOffset;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((int)BrowserProfileRuntimePhase.Active);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void WriteBounded(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumIdentityBytes)
        {
            throw new InvalidDataException(
                "A browser runtime profile identity is too large.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadBounded(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaximumIdentityBytes)
        {
            throw new InvalidDataException(
                "A browser runtime profile manifest contains an invalid identity.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException(
                "A browser runtime profile manifest is truncated.");
        }

        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
    }
}

internal enum BrowserProfileRuntimePhase
{
    Preparing,
    Active,
}

internal readonly record struct BrowserProfileRuntimeRecord(
    BrowserProfileStateKey StateKey,
    BrowserProfileRuntimePhase Phase);
