using System.Security;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Inspects and clears only the two explicit disposable roots supplied at
/// construction. Scans are bounded and complete before a clear begins, so no
/// file is removed from a partially validated tree.
/// </summary>
public sealed partial class FileSystemLocalArtifactControl : ILocalArtifactControl
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly LocalArtifactPaths _paths;
    private readonly LocalArtifactScanLimits _limits;
    private readonly Action? _beforeFirstMutation;

    public FileSystemLocalArtifactControl(LocalArtifactPaths paths)
        : this(paths, LocalArtifactScanLimits.Default)
    {
    }

    internal FileSystemLocalArtifactControl(
        LocalArtifactPaths paths,
        LocalArtifactScanLimits limits,
        Action? beforeFirstMutation = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        limits.Validate();
        _limits = limits;
        _beforeFirstMutation = beforeFirstMutation;
    }

    public async ValueTask<LocalArtifactControlResult<LocalArtifactInventory>> InspectAsync(
        CancellationToken cancellationToken) =>
        await Task.Run(
            () => Inspect(cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

    public async ValueTask<LocalArtifactControlResult<LocalArtifactClearReceipt>> ClearAsync(
        LocalArtifactKind kind,
        CancellationToken cancellationToken) =>
        await Task.Run(
            () => Clear(kind, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

    private LocalArtifactControlResult<LocalArtifactInventory> Inspect(
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateConfiguredBoundaryChains();
            var budget = new ScanBudget(_limits);
            var cache = Scan(LocalArtifactKind.Cache, budget, cancellationToken);
            var logs = Scan(
                LocalArtifactKind.InactiveApplicationLogs,
                budget,
                cancellationToken);
            return LocalArtifactControlResult<LocalArtifactInventory>.Success(
                new LocalArtifactInventory(
                [
                    cache.ToSummary(),
                    logs.ToSummary(),
                ]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<LocalArtifactInventory>(
                LocalArtifactControlErrorCode.Cancelled,
                "Local artifact inspection was cancelled.");
        }
        catch (ArtifactScanException exception)
        {
            return Failure<LocalArtifactInventory>(exception.Code, exception.Message);
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Failure<LocalArtifactInventory>(
                LocalArtifactControlErrorCode.AccessDenied,
                "A local artifact directory could not be inspected.");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<LocalArtifactInventory>(
                StorageErrorCode(exception),
                "Local artifacts could not be inspected.");
        }
    }

    private LocalArtifactControlResult<LocalArtifactClearReceipt> Clear(
        LocalArtifactKind kind,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind))
        {
            return Failure<LocalArtifactClearReceipt>(
                LocalArtifactControlErrorCode.UnsupportedArtifactKind,
                "The requested local artifact category is not supported.");
        }

        ArtifactPlan plan;
        try
        {
            ValidateConfiguredBoundaryChains();
            var budget = new ScanBudget(_limits);
            plan = Scan(kind, budget, cancellationToken);

            // Cancellation has authority only while the operation is read-only.
            // Once mutation starts, finishing the validated plan gives callers a
            // deterministic receipt instead of an ambiguous half-cancelled state.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<LocalArtifactClearReceipt>(
                LocalArtifactControlErrorCode.Cancelled,
                "Local artifact clearing was cancelled.");
        }
        catch (ArtifactScanException exception)
        {
            return Failure<LocalArtifactClearReceipt>(exception.Code, exception.Message);
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Failure<LocalArtifactClearReceipt>(
                LocalArtifactControlErrorCode.AccessDenied,
                "The local artifact directory could not be inspected.");
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<LocalArtifactClearReceipt>(
                StorageErrorCode(exception),
                "The local artifact directory could not be inspected.");
        }

        _beforeFirstMutation?.Invoke();
        return OperatingSystem.IsMacOS()
            ? ExecuteMacOsPlan(plan)
            : ExecutePlan(plan);
    }

    private ArtifactPlan Scan(
        LocalArtifactKind kind,
        ScanBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = _paths.DirectoryFor(kind);
        ValidateExistingDirectoryChain(root);
        var rootState = GetRootState(root);
        if (rootState == ArtifactRootState.Absent)
        {
            return new ArtifactPlan(kind, root, [], [], 0, Identity: null);
        }

        if (rootState != ArtifactRootState.Directory)
        {
            throw UnsafeEntry();
        }

        var rootIdentity = ReadMacOsIdentity(root);
        var protectedIdentities = CaptureMacOsProtectedIdentities(kind, rootIdentity);
        var files = new List<PlannedFile>();
        var directories = new List<PlannedDirectory>();
        var pending = new Stack<PendingDirectory>();
        pending.Push(new PendingDirectory(root, 0));
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Enumerate(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var depth = current.Depth + 1;
                budget.AddEntry(depth);
                var entryPath = Path.GetFullPath(entry.FullName);
                if (!LocalArtifactPaths.IsContainedDescendant(root, entryPath))
                {
                    throw UnsafeEntry();
                }

                entry.Refresh();
                var attributes = entry.Attributes;
                if (HasUnsafeAttributes(attributes))
                {
                    throw UnsafeEntry();
                }

                if ((attributes & FileAttributes.Directory) != FileAttributes.None)
                {
                    if (entry is not DirectoryInfo)
                    {
                        throw UnsafeEntry();
                    }

                    var identity = ReadMacOsIdentity(entryPath);
                    ValidateMacOsPlannedIdentity(rootIdentity, identity, protectedIdentities);
                    directories.Add(new PlannedDirectory(entryPath, depth, identity));
                    pending.Push(new PendingDirectory(entryPath, depth));
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw UnsafeEntry();
                }

                if (!NativeFileKind.IsRegularFile(entryPath))
                {
                    throw UnsafeEntry();
                }

                var fileIdentity = ReadMacOsIdentity(entryPath);
                if (_paths.IsProtectedActiveLog(entryPath))
                {
                    continue;
                }

                ValidateMacOsPlannedIdentity(rootIdentity, fileIdentity, protectedIdentities);

                var length = fileIdentity?.Size ?? file.Length;
                budget.AddBytes(length);
                totalBytes = checked(totalBytes + length);
                files.Add(new PlannedFile(entryPath, length, fileIdentity));
            }
        }

        files.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Path, right.Path));
        directories.Sort(static (left, right) =>
        {
            var depthComparison = right.Depth.CompareTo(left.Depth);
            return depthComparison != 0
                ? depthComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        return new ArtifactPlan(kind, root, files, directories, totalBytes, rootIdentity);
    }

    private LocalArtifactControlResult<LocalArtifactClearReceipt> ExecutePlan(
        ArtifactPlan plan)
    {
        long filesRemoved = 0;
        long bytesRemoved = 0;
        var mutationStarted = false;
        try
        {
            ValidateExistingDirectoryChain(plan.Root);
            foreach (var file in plan.Files)
            {
                if (!TryValidatePlannedFile(plan.Root, file))
                {
                    continue;
                }

                mutationStarted = true;
                File.Delete(file.Path);
                filesRemoved++;
                bytesRemoved = checked(bytesRemoved + file.Length);
            }

            foreach (var directory in plan.Directories)
            {
                if (!IsEmptyPlannedDirectory(plan.Root, directory.Path))
                {
                    continue;
                }

                mutationStarted = true;
                Directory.Delete(directory.Path, recursive: false);
            }

            return LocalArtifactControlResult<LocalArtifactClearReceipt>.Success(
                new LocalArtifactClearReceipt(
                    plan.Kind,
                    filesRemoved,
                    bytesRemoved));
        }
        catch (ArtifactScanException exception)
        {
            return Failure<LocalArtifactClearReceipt>(
                mutationStarted
                    ? LocalArtifactControlErrorCode.PartialRemoval
                    : exception.Code,
                exception.Message,
                filesRemoved,
                bytesRemoved);
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Failure<LocalArtifactClearReceipt>(
                mutationStarted
                    ? LocalArtifactControlErrorCode.PartialRemoval
                    : LocalArtifactControlErrorCode.AccessDenied,
                "Local artifacts could not be completely cleared.",
                filesRemoved,
                bytesRemoved);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Failure<LocalArtifactClearReceipt>(
                mutationStarted
                    ? LocalArtifactControlErrorCode.PartialRemoval
                    : StorageErrorCode(exception),
                "Local artifacts could not be completely cleared.",
                filesRemoved,
                bytesRemoved);
        }
    }

    private bool TryValidatePlannedFile(string root, PlannedFile file)
    {
        if (!LocalArtifactPaths.IsContainedDescendant(root, file.Path)
            || _paths.IsProtectedActiveLog(file.Path))
        {
            throw UnsafeEntry();
        }

        ValidateExistingDirectoryChain(Path.GetDirectoryName(file.Path)!);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(file.Path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if (HasUnsafeAttributes(attributes)
            || (attributes & FileAttributes.Directory) != FileAttributes.None)
        {
            throw UnsafeEntry();
        }

        if (!NativeFileKind.IsRegularFile(file.Path))
        {
            throw UnsafeEntry();
        }

        var currentLength = new FileInfo(file.Path).Length;
        if (currentLength != file.Length)
        {
            throw UnsafeEntry();
        }

        return true;
    }

    private static bool IsEmptyPlannedDirectory(string root, string path)
    {
        if (!LocalArtifactPaths.IsContainedDescendant(root, path))
        {
            throw UnsafeEntry();
        }

        ValidateExistingDirectoryChain(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if (HasUnsafeAttributes(attributes)
            || (attributes & FileAttributes.Directory) == FileAttributes.None)
        {
            throw UnsafeEntry();
        }

        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !entries.MoveNext();
    }

    private static ArtifactRootState GetRootState(string root)
    {
        try
        {
            var attributes = File.GetAttributes(root);
            if (HasUnsafeAttributes(attributes))
            {
                return ArtifactRootState.Unsafe;
            }

            return (attributes & FileAttributes.Directory) != FileAttributes.None ? ArtifactRootState.Directory
                : ArtifactRootState.Unsafe;
        }
        catch (FileNotFoundException)
        {
            return ArtifactRootState.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return ArtifactRootState.Absent;
        }
    }

    // Every existing path component is checked separately because a lexical
    // full path can still escape through a symlinked ancestor. The chain is
    // checked again immediately before mutation; a malicious same-user swap in
    // the remaining syscall window is outside the single-user desktop threat
    // model and would require platform-specific handle-relative deletion.
    private static void ValidateExistingDirectoryChain(string directory)
    {
        var pathRoot = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw UnsafeEntry();
        }

        ValidateDirectoryComponent(pathRoot, allowAbsent: false);
        var relativePath = directory[pathRoot.Length..];
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = pathRoot;
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            if (!ValidateDirectoryComponent(current, allowAbsent: true))
            {
                return;
            }
        }
    }

    private static bool ValidateDirectoryComponent(string path, bool allowAbsent)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) when (allowAbsent)
        {
            return false;
        }
        catch (DirectoryNotFoundException) when (allowAbsent)
        {
            return false;
        }

        if (HasUnsafeAttributes(attributes)
            || (attributes & FileAttributes.Directory) == FileAttributes.None)
        {
            throw UnsafeEntry();
        }

        return true;
    }

    private void ValidateConfiguredBoundaryChains()
    {
        ValidateExistingDirectoryChain(_paths.CacheDirectory);
        ValidateExistingDirectoryChain(_paths.ApplicationLogDirectory);
        if (_paths.DurableDataDirectory is not null)
        {
            ValidateExistingDirectoryChain(_paths.DurableDataDirectory);
        }

        if (_paths.ActiveApplicationLogPath is null)
        {
            return;
        }

        ValidateExistingDirectoryChain(
            Path.GetDirectoryName(_paths.ActiveApplicationLogPath)!);
        ValidateOptionalRegularFile(_paths.ActiveApplicationLogPath);
    }

    private static void ValidateOptionalRegularFile(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (HasUnsafeAttributes(attributes)
            || (attributes & FileAttributes.Directory) != FileAttributes.None || !NativeFileKind.IsRegularFile(path))
        {
            throw UnsafeEntry();
        }
    }

    private static IEnumerable<FileSystemInfo> Enumerate(string directory)
    {
        var information = new DirectoryInfo(directory);
        return information.EnumerateFileSystemInfos("*", EnumerationOptions);
    }

    private static bool HasUnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != FileAttributes.None;

    private static ArtifactScanException UnsafeEntry() => new(
        LocalArtifactControlErrorCode.UnsafeLayout,
        "A local artifact directory contains an unsafe filesystem entry.");

    private static bool IsAccessFailure(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException;

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException
            or NotSupportedException
            or ArgumentException
            or OverflowException;

    private static LocalArtifactControlErrorCode StorageErrorCode(Exception exception) =>
        exception is IOException
            ? LocalArtifactControlErrorCode.IoFailure
            : LocalArtifactControlErrorCode.Unavailable;

    private static LocalArtifactControlResult<T> Failure<T>(
        LocalArtifactControlErrorCode code,
        string message,
        long filesRemoved = 0,
        long bytesRemoved = 0) =>
        LocalArtifactControlResult<T>.Failure(
            new LocalArtifactControlError(
                code,
                message,
                filesRemoved,
                bytesRemoved));

    private enum ArtifactRootState
    {
        Absent,
        Directory,
        Unsafe,
    }

    private sealed record PlannedFile(
        string Path,
        long Length,
        MacOsFileIdentity? Identity);

    private sealed record PlannedDirectory(
        string Path,
        int Depth,
        MacOsFileIdentity? Identity);

    private sealed record PendingDirectory(string Path, int Depth);

    private sealed record ArtifactPlan(
        LocalArtifactKind Kind,
        string Root,
        IReadOnlyList<PlannedFile> Files,
        IReadOnlyList<PlannedDirectory> Directories,
        long TotalBytes,
        MacOsFileIdentity? Identity)
    {
        internal LocalArtifactSummary ToSummary() =>
            new(Kind, Files.Count, TotalBytes);
    }

    private sealed class ScanBudget(LocalArtifactScanLimits limits)
    {
        private int _entries;
        private long _bytes;

        internal void AddEntry(int depth)
        {
            _entries++;
            if (_entries > limits.MaximumEntries || depth > limits.MaximumDepth)
            {
                throw LimitExceeded();
            }
        }

        internal void AddBytes(long bytes)
        {
            if (bytes < 0)
            {
                throw UnsafeEntry();
            }

            _bytes = checked(_bytes + bytes);
            if (_bytes > limits.MaximumBytes)
            {
                throw LimitExceeded();
            }
        }

        private static ArtifactScanException LimitExceeded() => new(
            LocalArtifactControlErrorCode.LimitExceeded,
            "Local artifact inspection exceeded its safety limit.");
    }

    private sealed class ArtifactScanException(
        LocalArtifactControlErrorCode code,
        string message)
        : Exception(message)
    {
        internal LocalArtifactControlErrorCode Code { get; } = code;

    }
}
