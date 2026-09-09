using System.Reflection;
using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Infrastructure.Tests;

public sealed class CalciteSqlLanguageServiceTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task MissingWorkerReturnsAnUnavailableNoOpSession()
    {
        var service = new CalciteSqlLanguageService(null, RequestTimeout);

        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        var completion = await session.CompleteAsync("select", 6, CancellationToken.None);
        var diagnostics = await session.DiagnoseAsync("select", CancellationToken.None);

        Assert.False(service.IsAvailable);
        Assert.False(session.IsAvailable);
        Assert.Equal(6, completion.ReplacementStart);
        Assert.Empty(completion.Items);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ExchangesCatalogCompletionDiagnosticsAndShutdownFrames()
    {
        var service = Service("normal");

        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);
        var diagnostics = await session.DiagnoseAsync(
            "select missing from people",
            CancellationToken.None);
        await session.UpdateCatalogAsync(Catalog("contacts"), CancellationToken.None);

        Assert.True(service.IsAvailable);
        Assert.True(session.IsAvailable);
        Assert.Equal(9, completion.ReplacementStart);
        Assert.Equal(2, completion.ReplacementLength);
        Assert.Collection(
            completion.Items,
            item =>
            {
                Assert.Equal("name", item.Label);
                Assert.Equal(SqlCompletionItemKind.Column, item.Kind);
                Assert.Equal("VARCHAR", item.Detail);
            },
            item => Assert.Equal(SqlCompletionItemKind.Table, item.Kind));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SqlDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(7, diagnostic.Start);
        Assert.Equal("unknownColumn", diagnostic.Code);
    }

    [Fact]
    public async Task CompletionCarriesThePreferredObjectWithoutMutatingTheCatalog()
    {
        var service = Service("preferred-object");
        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        var preferred = new DatabaseObjectId("app", "private", "contacts");

        var completion = await session.CompleteAsync(
            "SELECT c",
            8,
            new SqlCompletionContext(preferred),
            CancellationToken.None);

        var item = Assert.Single(completion.Items);
        Assert.Equal("app.private.contacts", item.Label);
        Assert.Equal("contacts", item.InsertText);
        Assert.True(session.IsAvailable);
    }

    [Fact]
    public async Task DrainsLargeStandardErrorWithoutBlockingTheProtocol()
    {
        var service = Service("stderr");

        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);

        Assert.True(session.IsAvailable);
        Assert.NotEmpty(completion.Items);
    }

    [Fact]
    public async Task InitializationFailureRetainsABoundedActionableReason()
    {
        var service = Service("init-error");

        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);

        Assert.False(session.IsAvailable);
        Assert.NotNull(session.UnavailableReason);
        Assert.Contains("internalError", session.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("token lists", session.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(session.UnavailableReason!.Length, 1, 320);
    }

    [Fact]
    public async Task PermanentInitializationFailureDoesNotRespawnPerInteraction()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-init-failure-{Guid.NewGuid():N}.marker");
        try
        {
            var service = Service("init-error", marker);
            await using var session = await service.OpenSessionAsync(
                Catalog(),
                CancellationToken.None);

            for (var index = 0; index < 5; index++)
            {
                var completion = await session.CompleteAsync(
                    "select p.na",
                    11,
                    CancellationToken.None);
                Assert.Empty(completion.Items);
            }

            Assert.False(session.IsAvailable);
            Assert.False(session.CanRetry);
            Assert.Equal("1", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task WorkerDoesNotInheritParentCredentialEnvironment()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-environment-{Guid.NewGuid():N}.marker");
        const string secretName = "ASURA_SQL_LANGUAGE_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(secretName);
        try
        {
            Environment.SetEnvironmentVariable(secretName, "do-not-inherit");
            var service = Service("environment", marker);

            await using var session = await service.OpenSessionAsync(
                Catalog(),
                CancellationToken.None);
            var completion = await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None);

            Assert.True(session.IsAvailable);
            Assert.NotEmpty(completion.Items);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("crash")]
    public async Task InvalidOrCrashedWorkerFailsSoft(string mode)
    {
        var service = Service(mode);

        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);

        Assert.Empty(completion.Items);
        Assert.False(session.IsAvailable);
    }

    [Fact]
    public async Task RestartsAndReinitializesAfterOneWorkerCrash()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-{Guid.NewGuid():N}.marker");
        try
        {
            var service = Service("crash-once", marker);
            await using var session = await service.OpenSessionAsync(
                Catalog(),
                CancellationToken.None);

            var completion = await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None);

            Assert.True(File.Exists(marker));
            Assert.True(session.IsAvailable);
            Assert.NotEmpty(completion.Items);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task ARequestAfterTheRetryBudgetCanRecoverTheSession()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-recovery-{Guid.NewGuid():N}.marker");
        try
        {
            var service = Service("crash-twice", marker);
            await using var session = await service.OpenSessionAsync(
                Catalog(),
                CancellationToken.None);

            var failed = await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None);
            Assert.Empty(failed.Items);
            Assert.False(session.IsAvailable);
            Assert.NotNull(session.UnavailableReason);

            await Task.Delay(600);
            var recovered = await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None);

            Assert.Equal("2", await File.ReadAllTextAsync(marker));
            Assert.True(session.IsAvailable);
            Assert.Null(session.UnavailableReason);
            Assert.NotEmpty(recovered.Items);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RepeatedRuntimeCrashesIncreaseTheRetryCooldown()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-crash-count-{Guid.NewGuid():N}.marker");
        try
        {
            var service = Service("crash-count", marker);
            await using var session = await service.OpenSessionAsync(
                Catalog(),
                CancellationToken.None);

            Assert.Empty((await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None)).Items);
            Assert.Equal("2", await File.ReadAllTextAsync(marker));

            await Task.Delay(600);
            Assert.True(session.CanRetry);
            Assert.Empty((await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None)).Items);
            Assert.Equal("4", await File.ReadAllTextAsync(marker));

            await Task.Delay(600);
            Assert.False(session.CanRetry);
            Assert.Empty((await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None)).Items);
            Assert.Equal("4", await File.ReadAllTextAsync(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task RejectedCatalogUpdateDoesNotReplaceRestartSnapshot()
    {
        var marker = Path.Combine(
            Path.GetTempPath(),
            $"asura-sql-worker-catalog-{Guid.NewGuid():N}.marker");
        try
        {
            var service = Service("catalog-atomic", marker);
            await using var session = await service.OpenSessionAsync(
                Catalog("people"),
                CancellationToken.None);

            await session.UpdateCatalogAsync(Catalog("reject"), CancellationToken.None);
            var completion = await session.CompleteAsync(
                "select p.na",
                11,
                CancellationToken.None);

            Assert.True(File.Exists(marker));
            Assert.True(session.IsAvailable);
            Assert.Equal("people", Assert.Single(completion.Items).Label);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task CallerCancellationTerminatesTheInFlightWorker()
    {
        var service = Service("hang");
        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(
            "select p.na",
            11,
            cancellation.Token));
        Assert.False(session.IsAvailable);
    }

    [Fact]
    public async Task RequestTimeoutFailsSoftAndStopsTheWorker()
    {
        var service = Service("hang", requestTimeout: TimeSpan.FromMilliseconds(100));
        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);

        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);

        Assert.Empty(completion.Items);
        Assert.False(session.IsAvailable);
    }

    [Fact]
    public async Task InternalWorkerErrorStopsAndExposesARecoverableFailure()
    {
        var service = Service("operation-error");
        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);

        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);

        Assert.Empty(completion.Items);
        Assert.False(session.IsAvailable);
        Assert.False(session.CanRetry);
        Assert.Contains("internalError", session.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("advisor failed", session.UnavailableReason, StringComparison.Ordinal);
        await Task.Delay(300);
        Assert.True(session.CanRetry);
    }

    [Fact]
    public async Task InvalidParametersDoNotPoisonAHealthyWorker()
    {
        var service = Service("invalid-params");
        await using var session = await service.OpenSessionAsync(
            Catalog(),
            CancellationToken.None);

        var completion = await session.CompleteAsync(
            "select p.na",
            11,
            CancellationToken.None);

        Assert.Empty(completion.Items);
        Assert.True(session.IsAvailable);
        Assert.Null(session.UnavailableReason);
    }

    [Fact]
    public async Task OversizedCatalogFailsSoftBeforeWritingAnUnboundedFrame()
    {
        var hugeType = new string('x', SqlLanguageWorkerProtocol.MaximumMessageBytes);
        var catalog = Catalog() with
        {
            Objects =
            [
                new SqlCatalogObject(
                    new DatabaseObjectId("app", "public", "people"),
                    DatabaseTableKind.Table,
                    [new SqlCatalogColumn(
                        "id",
                        hugeType,
                        DatabaseValueKind.SignedInteger,
                        false)]),
            ],
        };
        var service = Service("normal");

        await using var session = await service.OpenSessionAsync(
            catalog,
            CancellationToken.None);

        Assert.False(session.IsAvailable);
    }

    private static CalciteSqlLanguageService Service(
        string mode,
        string? marker = null,
        TimeSpan? requestTimeout = null)
    {
        var arguments = marker is null
            ? new[] { WorkerAssemblyPath(), mode }
            : [WorkerAssemblyPath(), mode, marker];
        return new CalciteSqlLanguageService(
            new SqlLanguageWorkerLaunch(ResolveDotnetHost(), arguments),
            requestTimeout ?? RequestTimeout);
    }

    private static SqlCatalogSnapshot Catalog(string objectName = "people") => new(
        "postgres",
        "app",
        "public",
        [
            new SqlCatalogObject(
                new DatabaseObjectId("app", "public", objectName),
                DatabaseTableKind.Table,
                [
                    new SqlCatalogColumn(
                        "id",
                        "bigint",
                        DatabaseValueKind.SignedInteger,
                        false),
                    new SqlCatalogColumn(
                        "name",
                        "varchar",
                        DatabaseValueKind.Text,
                        true),
                ]),
        ]);

    private static string WorkerAssemblyPath()
    {
        var path = typeof(CalciteSqlLanguageServiceTests)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => string.Equals(
                attribute.Key,
                "SqlLanguageTestWorkerPath",
                StringComparison.Ordinal))
            .Value;
        return File.Exists(path)
            ? path!
            : throw new FileNotFoundException("SQL language test worker was not built.", path);
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                ".dotnet",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return "dotnet";
    }
}
