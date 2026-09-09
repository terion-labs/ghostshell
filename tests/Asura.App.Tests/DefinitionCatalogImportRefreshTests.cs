using System.Reflection;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DefinitionCatalogImportRefreshTests
{
    [Fact]
    public async Task Successful_catalog_reload_returns_unit_and_forwards_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        var (catalog, proxy) = CreateCatalog();
        var refresh = new DefinitionCatalogImportRefresh(catalog);

        var result = await refresh.ReloadAsync(cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(Unit.Value, result.Value);
        Assert.Equal(cancellation.Token, proxy.LastCancellationToken);
        Assert.Equal(1, proxy.ReloadCalls);
    }

    [Fact]
    public async Task Failed_catalog_reload_preserves_the_exact_typed_error()
    {
        var error = new DefinitionStoreError(
            DefinitionStoreErrorCode.StorageUnavailable,
            "The durable catalog is unavailable.");
        var (catalog, proxy) = CreateCatalog();
        proxy.ReloadResult =
            DefinitionStoreResult<DefinitionCatalogSnapshot>.Failure(error);
        var refresh = new DefinitionCatalogImportRefresh(catalog);

        var result = await refresh.ReloadAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
        Assert.Equal(1, proxy.ReloadCalls);
    }

    [Fact]
    public async Task Catalog_cancellation_is_not_rewritten_by_the_refresh_adapter()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var (catalog, proxy) = CreateCatalog();
        proxy.ThrowCancellation = true;
        var refresh = new DefinitionCatalogImportRefresh(catalog);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresh.ReloadAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, proxy.ReloadCalls);
    }

    [Fact]
    public void Constructor_rejects_a_missing_catalog()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DefinitionCatalogImportRefresh(null!));
    }

    private static (IDefinitionCatalog Catalog, RecordingDefinitionCatalog Proxy)
        CreateCatalog()
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingDefinitionCatalog>();
        return (catalog, (RecordingDefinitionCatalog)(object)catalog);
    }

    public class RecordingDefinitionCatalog : DispatchProxy
    {
        private int _reloadCalls;

        public DefinitionStoreResult<DefinitionCatalogSnapshot> ReloadResult { get; set; } =
            DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(
                DefinitionCatalogSnapshot.Empty);

        public CancellationToken LastCancellationToken { get; private set; }

        public int ReloadCalls => Volatile.Read(ref _reloadCalls);

        public bool ThrowCancellation { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (!string.Equals(targetMethod?.Name, nameof(IDefinitionCatalog.ReloadAsync), StringComparison.Ordinal))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var cancellationToken = Assert.IsType<CancellationToken>(Assert.Single(args!));
            LastCancellationToken = cancellationToken;
            Interlocked.Increment(ref _reloadCalls);
            if (ThrowCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return ValueTask.FromResult(ReloadResult);
        }
    }
}
