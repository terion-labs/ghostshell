using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Docker;

internal sealed partial class DockerPanelSession : IDockerPanelSession
{
    public const int MaximumResourcesPerKind = 500;
    public const int MaximumLogLines = 1_000;
    public const int MaximumFileEntries = 500;
    public const int MaximumFileBytes = 1024 * 1024;

    private readonly object _initialSnapshotGate = new();
    private readonly IDockerEngineClient _client;
    private readonly SemaphoreSlim _containerControlGate = new(1, 1);
    private readonly DockerContainerRevisionPool _containerRevisions = new();
    private readonly DockerPanelSessionLifetime _lifetime;
    private readonly DockerResourceLeasePool _resources = new();
    private readonly DockerSessionTarget _target;
    private DockerEngineSnapshot? _initialSnapshot;

    public DockerPanelSession(
        SessionId id,
        DockerSessionTarget target,
        IDockerEngineClient client,
        DockerEngineSnapshot initialSnapshot,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _initialSnapshot = initialSnapshot
            ?? throw new ArgumentNullException(nameof(initialSnapshot));
        Binding = target.Binding;
        State = new DockerPanelSessionState(
            BoundedText(target.Connection.Name, 256, "Docker"),
            target.Connection.ConnectionKind,
            DockerEngineGeneration.New(),
            ProjectEngine(initialSnapshot.Engine),
            IsReady: true);
        _lifetime = new DockerPanelSessionLifetime(
            id,
            capabilities,
            timeProvider);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => PanelKind.Docker;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public DockerSessionBinding Binding { get; }

    public DockerPanelSessionState State { get; }

    public ValueTask<DockerResult<DockerPanelSnapshot>> ReadStateAsync(
        int maximumResourcesPerKind,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResourcesPerKind, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumResourcesPerKind,
            MaximumResourcesPerKind);
        return ExecuteReadAsync(
            ReadEngineSnapshotAsync,
            snapshot => ProjectSnapshot(snapshot, maximumResourcesPerKind),
            cancellationToken);
    }

    public ValueTask<DockerResult<DockerInspectionSnapshot>> InspectAsync(
        DockerResourceReferenceId reference,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(reference, out var resource))
        {
            return ValueTask.FromResult(UnknownReference<DockerInspectionSnapshot>());
        }

        return ExecuteReadAsync(
            token => _client.InspectAsync(
                _target.Connection,
                resource,
                token),
            inspection => ProjectInspection(resource, inspection),
            cancellationToken);
    }

    public ValueTask<DockerResult<DockerContainerLogPage>> ReadLogsAsync(
        DockerLogReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLogRequest(request);
        if (!TryResolve(request.Container, out var resource)
            || resource.Kind != DockerResourceKind.Container)
        {
            return ValueTask.FromResult(UnknownReference<DockerContainerLogPage>());
        }

        var engineRequest = new DockerContainerLogRequest(
            resource.Id,
            request.Limit,
            request.BeforeTimestamp,
            request.SinceTimestamp,
            request.SearchText,
            request.ContextLines);
        return ExecuteReadAsync(
            token => _client.ReadContainerLogsAsync(
                _target.Connection,
                engineRequest,
                token),
            page => ProjectLogs(page, request.Limit),
            cancellationToken);
    }

    public ValueTask<DockerResult<DockerFilePage>> ListFilesAsync(
        DockerFileListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePath(request.Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.MaximumEntries,
            MaximumFileEntries);
        if (!TryResolveFileResource(request.Resource, out var resource))
        {
            return ValueTask.FromResult(UnknownReference<DockerFilePage>());
        }

        return ExecuteReadAsync(
            token => _client.ListFilesAsync(
                _target.Connection,
                resource,
                request.Path,
                token),
            listing => ProjectFilePage(
                resource,
                request.Path,
                listing,
                request.MaximumEntries),
            cancellationToken);
    }

    public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
        DockerFileStatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePath(request.Path);
        if (!TryResolveFileResource(request.Resource, out var resource))
        {
            return ValueTask.FromResult(UnknownReference<DockerFileEntry>());
        }

        return ExecuteReadAsync(
            token => _client.StatFileAsync(
                _target.Connection,
                resource,
                request.Path,
                token),
            entry => ProjectFileStat(entry, request.Path),
            cancellationToken);
    }

    public ValueTask<DockerResult<DockerFileSnapshot>> ReadFileAsync(
        DockerFileReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePath(request.Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            request.MaximumBytes,
            MaximumFileBytes);
        if (!TryResolveFileResource(request.Resource, out var resource))
        {
            return ValueTask.FromResult(UnknownReference<DockerFileSnapshot>());
        }

        return ExecuteReadAsync(
            token => _client.ReadFileAsync(
                _target.Connection,
                resource,
                request.Path,
                request.MaximumBytes,
                token),
            content => ProjectFileContent(
                resource,
                request.Path,
                content,
                request.MaximumBytes),
            cancellationToken);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken) =>
        _lifetime.SnapshotAsync(cancellationToken);

    public IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        _lifetime.WatchAsync(afterSequence, cancellationToken);

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken) =>
        _lifetime.CloseAsync(mode, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.DisposeAsync().ConfigureAwait(false);
        _containerControlGate.Dispose();
    }

    private ValueTask<DockerResult<DockerEngineSnapshot>> ReadEngineSnapshotAsync(
        CancellationToken cancellationToken)
    {
        lock (_initialSnapshotGate)
        {
            if (_initialSnapshot is { } initial)
            {
                _initialSnapshot = null;
                return ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
                    new DockerResult<DockerEngineSnapshot>.Success(initial));
            }
        }

        return _client.ReadSnapshotAsync(_target.Connection, cancellationToken);
    }

    private async ValueTask<DockerResult<TResult>> ExecuteReadAsync<TSource, TResult>(
        Func<CancellationToken, ValueTask<DockerResult<TSource>>> read,
        Func<TSource, TResult> project,
        CancellationToken cancellationToken)
    {
        if (!_lifetime.IsOpen)
        {
            return SessionClosed<TResult>();
        }

        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        try
        {
            var result = await read(operation.Token).ConfigureAwait(false);
            if (result is DockerResult<TSource>.Failure failure)
            {
                return SafeFailure<TResult>(failure.Error);
            }

            operation.Token.ThrowIfCancellationRequested();
            var source = ((DockerResult<TSource>.Success)result).Value;
            return new DockerResult<TResult>.Success(project(source));
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TResult>();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return InvalidResponse<TResult>();
        }
    }

    private bool TryResolve(
        DockerResourceReferenceId reference,
        out DockerResourceReference resource)
    {
        var found = _resources.TryResolve(reference, out var resolved);
        resource = resolved!;
        return found && resolved is not null;
    }

    private bool TryResolveFileResource(
        DockerResourceReferenceId reference,
        out DockerResourceReference resource) =>
        TryResolve(reference, out resource)
        && resource.Kind is not DockerResourceKind.Network;

    private static void ValidateLogRequest(DockerLogReadRequest request)
    {
        if (request.Limit is < 1 or > MaximumLogLines
            || request.ContextLines is < 0 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Docker log limits are outside the hosted bounds.");
        }

        if (request.BeforeTimestamp is not null
            && request.SinceTimestamp is not null)
        {
            throw new ArgumentException(
                "A Docker log request cannot page in two directions.",
                nameof(request));
        }

        ValidateOptionalText(request.BeforeTimestamp, 128, nameof(request));
        ValidateOptionalText(request.SinceTimestamp, 128, nameof(request));
        ValidateOptionalText(request.SearchText, 512, nameof(request));
    }

    private static void ValidateOptionalText(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is { } text
            && (ArgumentUtf8Length(text, parameterName) > maximumLength
                || text.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "A Docker read argument must be bounded and printable.",
                parameterName);
        }
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (ArgumentUtf8Length(path, nameof(path)) > 4_096
            || path[0] != '/'
            || path.Contains('\0')
            || path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "Docker file paths must be bounded absolute POSIX paths without traversal.",
                nameof(path));
        }
    }

    private static int ArgumentUtf8Length(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A Docker read argument is not valid Unicode.",
                parameterName,
                exception);
        }
    }

    private static DockerResult<T> UnknownReference<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.InvalidResponse,
            "The Docker resource reference is unknown or expired.",
            false));

    private static DockerResult<T> SessionClosed<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.RuntimeUnavailable,
            "The Docker session is closed.",
            false));

    private static DockerResult<T> Cancelled<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.Cancelled,
            "The Docker observation was cancelled.",
            false));

    private static DockerResult<T> InvalidResponse<T>() =>
        new DockerResult<T>.Failure(new DockerError(
            DockerErrorCode.InvalidResponse,
            "The Docker engine returned an invalid bounded response.",
            true));

    private static DockerResult<T> SafeFailure<T>(DockerError error) =>
        new DockerResult<T>.Failure(new DockerError(
            error.Code,
            error.Code switch
            {
                DockerErrorCode.RuntimeUnavailable => "The Docker runtime is unavailable.",
                DockerErrorCode.ConnectionFailed => "The Docker connection failed.",
                DockerErrorCode.CommandFailed => "Docker could not complete the read operation.",
                DockerErrorCode.TimedOut => "The Docker observation timed out.",
                DockerErrorCode.Cancelled => "The Docker observation was cancelled.",
                DockerErrorCode.FileNotFound => "The Docker file was not found.",
                DockerErrorCode.NotDirectory => "The Docker path is not a directory.",
                DockerErrorCode.FileProtocolUnavailable =>
                    "The Docker resource cannot be browsed safely.",
                _ => "The Docker engine returned an invalid response.",
            },
            error.Retryable));
}
