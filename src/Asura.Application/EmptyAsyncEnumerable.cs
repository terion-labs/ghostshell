namespace Asura.Application;

/// <summary>
/// A stream that ends immediately.
///
/// It exists so an interface can default a watch method to "this kind of thing
/// never happens here" without every implementation writing an empty iterator,
/// and without the ceremony of an <c>async</c> method that never awaits.
/// </summary>
internal sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
{
    public static readonly EmptyAsyncEnumerable<T> Instance = new();

    private EmptyAsyncEnumerable()
    {
    }

    public T Current => default!;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this;
    }

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
