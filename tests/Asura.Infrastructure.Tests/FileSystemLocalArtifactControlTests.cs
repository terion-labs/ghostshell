using System.Diagnostics;
using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class FileSystemLocalArtifactControlTests
{
    [Fact]
    public async Task AbsentAndEmptyRootsAreSuccessfulAndPreserved()
    {
        using var fixture = LocalArtifactFixture.Create(createArtifactRoots: false);
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var absentInventory = await control.InspectAsync(CancellationToken.None);
        var absentClear = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        AssertEmptyInventory(absentInventory);
        Assert.True(absentClear.IsSuccess, absentClear.Error?.Message);
        Assert.Equal(0, absentClear.Value!.FilesRemoved);
        Assert.False(Directory.Exists(fixture.Paths.CacheDirectory));

        Directory.CreateDirectory(fixture.Paths.CacheDirectory);
        Directory.CreateDirectory(fixture.Paths.ApplicationLogDirectory);
        var emptyInventory = await control.InspectAsync(CancellationToken.None);
        var emptyClear = await control.ClearAsync(
            LocalArtifactKind.InactiveApplicationLogs,
            CancellationToken.None);

        AssertEmptyInventory(emptyInventory);
        Assert.True(emptyClear.IsSuccess, emptyClear.Error?.Message);
        Assert.Equal(0, emptyClear.Value!.FilesRemoved);
        Assert.True(Directory.Exists(fixture.Paths.CacheDirectory));
        Assert.True(Directory.Exists(fixture.Paths.ApplicationLogDirectory));
    }

    [Fact]
    public async Task InspectionAndClearKeepCategoriesDurableDataAndActiveLogIsolated()
    {
        using var fixture = LocalArtifactFixture.Create(activeLogFileName: "active.log");
        var cacheNested = Path.Combine(fixture.Paths.CacheDirectory, "nested");
        Directory.CreateDirectory(cacheNested);
        var cacheFirst = fixture.WriteCache("first.cache", "abc");
        var cacheSecond = fixture.Write(cacheNested, "second.cache", "12345");
        var inactiveLog = fixture.WriteLog("old.log", "1234");
        var activeLog = fixture.WriteLog("active.log", "active");
        var database = fixture.WriteDurable("asura.db", "durable");
        var backup = fixture.WriteDurable(
            Path.Combine("backups", "profile.db"),
            "backup");
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var inventoryResult = await control.InspectAsync(CancellationToken.None);

        Assert.True(inventoryResult.IsSuccess, inventoryResult.Error?.Message);
        AssertSummary(inventoryResult.Value!, LocalArtifactKind.Cache, 2, 8);
        AssertSummary(
            inventoryResult.Value!,
            LocalArtifactKind.InactiveApplicationLogs,
            1,
            4);

        var cacheClear = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.True(cacheClear.IsSuccess, cacheClear.Error?.Message);
        Assert.Equal(LocalArtifactKind.Cache, cacheClear.Value!.Kind);
        Assert.Equal(2, cacheClear.Value.FilesRemoved);
        Assert.Equal(8, cacheClear.Value.BytesRemoved);
        Assert.False(File.Exists(cacheFirst));
        Assert.False(File.Exists(cacheSecond));
        Assert.False(Directory.Exists(cacheNested));
        Assert.True(Directory.Exists(fixture.Paths.CacheDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.CacheDirectory));
        Assert.True(File.Exists(inactiveLog));
        Assert.True(File.Exists(activeLog));
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(backup));

        var logClear = await control.ClearAsync(
            LocalArtifactKind.InactiveApplicationLogs,
            CancellationToken.None);

        Assert.True(logClear.IsSuccess, logClear.Error?.Message);
        Assert.Equal(1, logClear.Value!.FilesRemoved);
        Assert.Equal(4, logClear.Value.BytesRemoved);
        Assert.False(File.Exists(inactiveLog));
        Assert.True(File.Exists(activeLog));
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(backup));
        Assert.True(Directory.Exists(fixture.Paths.ApplicationLogDirectory));
    }

    [Fact]
    public async Task ClearingAnAlreadyClearedCategoryIsIdempotent()
    {
        using var fixture = LocalArtifactFixture.Create();
        fixture.WriteCache("discard.cache", "data");
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var first = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);
        var second = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(1, first.Value!.FilesRemoved);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(0, second.Value!.FilesRemoved);
        Assert.Equal(0, second.Value.BytesRemoved);
    }

    [Fact]
    public async Task SymlinkRejectsTheWholePlanBeforeAnyDeletion()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var plannedFile = fixture.WriteCache("planned.cache", "planned");
        var outsideFile = fixture.WriteDurable("outside.txt", "outside");
        File.CreateSymbolicLink(
            Path.Combine(fixture.Paths.CacheDirectory, "linked.cache"),
            outsideFile);
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(plannedFile));
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public async Task SymlinkedAncestorRejectsConfiguredRootBeforeAnyDeletion()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var actualParent = Path.Combine(fixture.Root, "actual-parent");
        var linkedParent = Path.Combine(fixture.Root, "linked-parent");
        var actualCache = Path.Combine(actualParent, "cache");
        Directory.CreateDirectory(actualCache);
        Directory.CreateSymbolicLink(linkedParent, actualParent);
        var plannedFile = Path.Combine(actualCache, "planned.cache");
        File.WriteAllText(plannedFile, "planned");
        var paths = new LocalArtifactPaths(
            Path.Combine(linkedParent, "cache"),
            fixture.Paths.ApplicationLogDirectory,
            durableDataDirectory: fixture.Paths.DurableDataDirectory);
        var control = new FileSystemLocalArtifactControl(paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(plannedFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SymlinkedProtectedBoundaryRejectsCacheClearBeforeDeletion(
        bool aliasLogs)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var plannedFile = fixture.WriteCache("planned.cache", "planned");
        var alias = Path.Combine(fixture.Root, aliasLogs ? "logs-alias" : "data-alias");
        Directory.CreateSymbolicLink(alias, fixture.Paths.CacheDirectory);
        var protectedDirectoryName = aliasLogs ? "nested-logs" : "nested-data";
        var protectedFile = fixture.Write(
            Path.Combine(fixture.Paths.CacheDirectory, protectedDirectoryName),
            "protected.bin",
            "protected");
        var paths = aliasLogs
            ? new LocalArtifactPaths(
                fixture.Paths.CacheDirectory,
                Path.Combine(alias, protectedDirectoryName),
                durableDataDirectory: fixture.Paths.DurableDataDirectory)
            : new LocalArtifactPaths(
                fixture.Paths.CacheDirectory,
                fixture.Paths.ApplicationLogDirectory,
                durableDataDirectory: Path.Combine(alias, protectedDirectoryName));
        var control = new FileSystemLocalArtifactControl(paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(plannedFile));
        Assert.True(File.Exists(protectedFile));
    }

    [Fact]
    public async Task SymlinkedActiveLogBoundaryRejectsLogClearBeforeDeletion()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var inactiveLog = fixture.WriteLog("old.log", "old");
        var target = fixture.WriteDurable("outside.log", "outside");
        var activeAlias = Path.Combine(
            fixture.Paths.ApplicationLogDirectory,
            "active.log");
        File.CreateSymbolicLink(activeAlias, target);
        var paths = new LocalArtifactPaths(
            fixture.Paths.CacheDirectory,
            fixture.Paths.ApplicationLogDirectory,
            activeAlias,
            fixture.Paths.DurableDataDirectory);
        var control = new FileSystemLocalArtifactControl(paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.InactiveApplicationLogs,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(inactiveLog));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task NonRegularUnixEntryRejectsTheWholePlan()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var plannedFile = fixture.WriteCache("planned.cache", "planned");
        var fifo = Path.Combine(fixture.Paths.CacheDirectory, "events.fifo");
        CreateFifo(fifo);
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.True(File.Exists(plannedFile));
    }

    [Fact]
    public async Task EntryDepthAndByteLimitsFailBeforeMutation()
    {
        using var entryFixture = LocalArtifactFixture.Create();
        var entryFirst = entryFixture.WriteCache("first.cache", "1");
        var entrySecond = entryFixture.WriteCache("second.cache", "2");
        var entryControl = new FileSystemLocalArtifactControl(
            entryFixture.Paths,
            new LocalArtifactScanLimits(1, 4, 100));

        var entryResult = await entryControl.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        AssertLimitFailure(entryResult);
        Assert.True(File.Exists(entryFirst));
        Assert.True(File.Exists(entrySecond));

        using var depthFixture = LocalArtifactFixture.Create();
        var nested = Path.Combine(depthFixture.Paths.CacheDirectory, "nested");
        Directory.CreateDirectory(nested);
        var deepFile = depthFixture.Write(nested, "deep.cache", "1");
        var depthControl = new FileSystemLocalArtifactControl(
            depthFixture.Paths,
            new LocalArtifactScanLimits(10, 1, 100));

        var depthResult = await depthControl.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        AssertLimitFailure(depthResult);
        Assert.True(File.Exists(deepFile));

        using var byteFixture = LocalArtifactFixture.Create();
        var largeFile = byteFixture.WriteCache("large.cache", "1234");
        var byteControl = new FileSystemLocalArtifactControl(
            byteFixture.Paths,
            new LocalArtifactScanLimits(10, 4, 3));

        var byteResult = await byteControl.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        AssertLimitFailure(byteResult);
        Assert.True(File.Exists(largeFile));
    }

    [Fact]
    public async Task CancellationBeforeMutationLeavesEveryFileUntouched()
    {
        using var fixture = LocalArtifactFixture.Create();
        var cacheFile = fixture.WriteCache("planned.cache", "planned");
        var control = new FileSystemLocalArtifactControl(fixture.Paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var clear = await control.ClearAsync(
            LocalArtifactKind.Cache,
            cancellation.Token);
        var inspect = await control.InspectAsync(cancellation.Token);

        Assert.False(clear.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.Cancelled, clear.Error!.Code);
        Assert.Equal(0, clear.Error.FilesRemoved);
        Assert.True(File.Exists(cacheFile));
        Assert.False(inspect.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.Cancelled, inspect.Error!.Code);
    }

    [Fact]
    public async Task MacOsFileIdentitySwapAfterPlanningFailsClosed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var planned = fixture.WriteCache("planned-secret.cache", "planned-secret");
        var displaced = Path.Combine(fixture.Paths.CacheDirectory, "displaced.cache");
        var control = new FileSystemLocalArtifactControl(
            fixture.Paths,
            LocalArtifactScanLimits.Default,
            () =>
            {
                File.Move(planned, displaced);
                File.WriteAllText(planned, "replacement-secret");
            });

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(planned));
        Assert.True(File.Exists(displaced));
        Assert.DoesNotContain("planned-secret", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-secret", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacOsDirectoryIdentitySwapAfterPlanningFailsClosed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var plannedDirectory = Path.Combine(fixture.Paths.CacheDirectory, "planned-directory");
        var displacedDirectory = Path.Combine(fixture.Paths.CacheDirectory, "displaced-directory");
        var plannedFile = fixture.Write(plannedDirectory, "planned.cache", "planned");
        var replacementFile = Path.Combine(plannedDirectory, "replacement.cache");
        var control = new FileSystemLocalArtifactControl(
            fixture.Paths,
            LocalArtifactScanLimits.Default,
            () =>
            {
                Directory.Move(plannedDirectory, displacedDirectory);
                Directory.CreateDirectory(plannedDirectory);
                File.WriteAllText(replacementFile, "replacement");
            });

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(Path.Combine(displacedDirectory, Path.GetFileName(plannedFile))));
        Assert.True(File.Exists(replacementFile));
    }

    [Fact]
    public async Task MacOsHardLinkAliasToActiveLogRejectsWholePlan()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create(activeLogFileName: "active.log");
        var inactive = fixture.WriteLog("inactive.log", "inactive");
        var active = fixture.WriteLog("active.log", "active-secret");
        var alias = Path.Combine(fixture.Paths.ApplicationLogDirectory, "active-alias.log");
        CreateHardLink(active, alias);
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.InactiveApplicationLogs,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.UnsafeLayout, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
        Assert.True(File.Exists(inactive));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(alias));
        Assert.DoesNotContain("active", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MacOsCancellationAfterPlanningDoesNotInterruptMutation()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var planned = fixture.WriteCache("planned.cache", "planned");
        using var cancellation = new CancellationTokenSource();
        var control = new FileSystemLocalArtifactControl(
            fixture.Paths,
            LocalArtifactScanLimits.Default,
            cancellation.Cancel);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            cancellation.Token);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.FilesRemoved);
        Assert.False(File.Exists(planned));
    }

    [Fact]
    public async Task UndefinedArtifactKindReturnsTypedFailureWithoutScanning()
    {
        using var fixture = LocalArtifactFixture.Create();
        var cacheFile = fixture.WriteCache("planned.cache", "planned");
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var result = await control.ClearAsync(
            (LocalArtifactKind)999,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            LocalArtifactControlErrorCode.UnsupportedArtifactKind,
            result.Error!.Code);
        Assert.True(File.Exists(cacheFile));
    }

    [Fact]
    public async Task FailureAfterMutationReportsTheExactPartialRemoval()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = LocalArtifactFixture.Create();
        var first = fixture.WriteCache("a-first.cache", "abc");
        var blockedDirectory = Path.Combine(
            fixture.Paths.CacheDirectory,
            "z-blocked");
        Directory.CreateDirectory(blockedDirectory);
        var blocked = fixture.Write(blockedDirectory, "second.cache", "12345");
        File.SetUnixFileMode(
            blockedDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var control = new FileSystemLocalArtifactControl(fixture.Paths);

            var result = await control.ClearAsync(
                LocalArtifactKind.Cache,
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                LocalArtifactControlErrorCode.PartialRemoval,
                result.Error!.Code);
            Assert.Equal(1, result.Error.FilesRemoved);
            Assert.Equal(3, result.Error.BytesRemoved);
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(blocked));
        }
        finally
        {
            File.SetUnixFileMode(
                blockedDirectory,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DurableParentMayContainArtifactRootsButIsNeverEnumerated()
    {
        using var fixture = LocalArtifactFixture.CreateWindowsStyleLayout();
        var cache = fixture.WriteCache("discard.cache", "cache");
        var database = fixture.WriteDurable("asura.db", "durable");
        var backup = fixture.WriteDurable(
            Path.Combine("backups", "profile.db"),
            "backup");
        var control = new FileSystemLocalArtifactControl(fixture.Paths);

        var result = await control.ClearAsync(
            LocalArtifactKind.Cache,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(File.Exists(cache));
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void PathsRejectOverlappingOrBroadMutationRoots()
    {
        using var fixture = LocalArtifactFixture.Create();
        var root = fixture.Root;
        var cache = Path.Combine(root, "cache");
        var logs = Path.Combine(root, "logs");
        var data = Path.Combine(root, "data");

        Assert.Throws<ArgumentException>(() =>
            new LocalArtifactPaths(cache, cache));
        Assert.Throws<ArgumentException>(() =>
            new LocalArtifactPaths(cache, Path.Combine(cache, "logs")));
        Assert.Throws<ArgumentException>(() =>
            new LocalArtifactPaths(root, logs, durableDataDirectory: data));
        Assert.Throws<ArgumentException>(() =>
            new LocalArtifactPaths(cache, logs, Path.Combine(cache, "active.log")));
        Assert.Throws<ArgumentException>(() =>
            new LocalArtifactPaths(
                Path.GetPathRoot(root)!,
                logs));
    }

    [Fact]
    public void DefaultPathsUsePlatformLocationsAndNoPersistentActiveLog()
    {
        var paths = LocalArtifactPaths.CreateDefault();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Null(paths.ActiveApplicationLogPath);
        Assert.False(string.Equals(
            paths.CacheDirectory,
            paths.ApplicationLogDirectory,
            LocalArtifactPaths.PathComparison));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(
                Path.Combine(userProfile, "Library", "Caches", ApplicationStorageIdentity.DirectoryName),
                paths.CacheDirectory);
            Assert.Equal(
                Path.Combine(userProfile, "Library", "Logs", ApplicationStorageIdentity.DirectoryName),
                paths.ApplicationLogDirectory);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var product = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationStorageIdentity.DirectoryName);
            Assert.Equal(Path.Combine(product, "Cache"), paths.CacheDirectory);
            Assert.Equal(Path.Combine(product, "Logs"), paths.ApplicationLogDirectory);
            return;
        }

        Assert.EndsWith(
            ApplicationStorageIdentity.PosixDirectoryName,
            paths.CacheDirectory,
            StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine(ApplicationStorageIdentity.PosixDirectoryName, "logs"),
            paths.ApplicationLogDirectory,
            StringComparison.Ordinal);
    }

    private static void AssertEmptyInventory(
        LocalArtifactControlResult<LocalArtifactInventory> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.All(
            result.Value!.Artifacts,
            artifact =>
            {
                Assert.Equal(0, artifact.FileCount);
                Assert.Equal(0, artifact.TotalBytes);
            });
    }

    private static void AssertSummary(
        LocalArtifactInventory inventory,
        LocalArtifactKind kind,
        long files,
        long bytes)
    {
        var summary = Assert.Single(
            inventory.Artifacts,
            artifact => artifact.Kind == kind);
        Assert.Equal(files, summary.FileCount);
        Assert.Equal(bytes, summary.TotalBytes);
    }

    private static void AssertLimitFailure(
        LocalArtifactControlResult<LocalArtifactClearReceipt> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(LocalArtifactControlErrorCode.LimitExceeded, result.Error!.Code);
        Assert.Equal(0, result.Error.FilesRemoved);
    }

    private static void CreateFifo(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/mkfifo",
            UseShellExecute = false,
            ArgumentList = { path },
        });
        Assert.NotNull(process);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void CreateHardLink(string source, string destination)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/ln",
            UseShellExecute = false,
            ArgumentList = { source, destination },
        });
        Assert.NotNull(process);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class LocalArtifactFixture : IDisposable
    {
        private LocalArtifactFixture(string root, LocalArtifactPaths paths)
        {
            Root = root;
            Paths = paths;
        }

        internal string Root { get; }

        internal LocalArtifactPaths Paths { get; }

        internal static LocalArtifactFixture Create(
            bool createArtifactRoots = true,
            string? activeLogFileName = null)
        {
            var root = CreateRoot();
            var cache = Path.Combine(root, "cache");
            var logs = Path.Combine(root, "logs");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            if (createArtifactRoots)
            {
                Directory.CreateDirectory(cache);
                Directory.CreateDirectory(logs);
            }

            var activeLog = activeLogFileName is null
                ? null
                : Path.Combine(logs, activeLogFileName);
            return new LocalArtifactFixture(
                root,
                new LocalArtifactPaths(cache, logs, activeLog, data));
        }

        internal static LocalArtifactFixture CreateWindowsStyleLayout()
        {
            var root = CreateRoot();
            var data = Path.Combine(root, "Asura");
            var cache = Path.Combine(data, "Cache");
            var logs = Path.Combine(data, "Logs");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(logs);
            return new LocalArtifactFixture(
                root,
                new LocalArtifactPaths(
                    cache,
                    logs,
                    durableDataDirectory: data));
        }

        internal string WriteCache(string name, string content) =>
            Write(Paths.CacheDirectory, name, content);

        internal string WriteLog(string name, string content) =>
            Write(Paths.ApplicationLogDirectory, name, content);

        internal string WriteDurable(string name, string content) =>
            Write(Paths.DurableDataDirectory!, name, content);

        internal string Write(string directory, string name, string content)
        {
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string CreateRoot()
        {
            var temporaryRoot = Path.GetTempPath();
            if (OperatingSystem.IsMacOS()
                && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal))
            {
                // /var is a system symlink on macOS. Tests use its canonical
                // spelling so only the symlinks created by each case are unsafe.
                temporaryRoot = "/private" + temporaryRoot;
            }

            var root = Path.Combine(
                temporaryRoot,
                "asura-local-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
