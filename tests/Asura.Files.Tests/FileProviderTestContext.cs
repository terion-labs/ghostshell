namespace Asura.Files.Tests;

public sealed class FileProviderTestContext : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private readonly Func<ValueTask>? _assertServerSideCopyObserved;

    public FileProviderTestContext(
        IFileProvider provider,
        FileLocation root,
        Func<ValueTask>? dispose = null,
        Func<ValueTask>? assertServerSideCopyObserved = null)
    {
        Provider = provider;
        Root = root;
        _dispose = dispose ?? (() => ValueTask.CompletedTask);
        _assertServerSideCopyObserved = assertServerSideCopyObserved;
    }

    public IFileProvider Provider { get; }

    public FileLocation Root { get; }

    public bool CanObserveServerSideCopy => _assertServerSideCopyObserved is not null;

    public ValueTask AssertServerSideCopyObservedAsync() =>
        _assertServerSideCopyObserved?.Invoke()
        ?? ValueTask.FromException(new InvalidOperationException(
            "A provider that declares server-side copy must expose a conformance probe for the underlying operation."));

    public ValueTask DisposeAsync() => _dispose();
}
