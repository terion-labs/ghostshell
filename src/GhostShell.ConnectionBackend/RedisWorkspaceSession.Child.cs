using System.Text;
using System.Threading.Channels;
using GhostShell.Application;
using StackExchange.Redis;

namespace GhostShell.ConnectionBackend;

internal sealed partial class RedisWorkspaceSession
{
    /// <summary>
    /// Hosts the existing Redis driver behind a private duplex pipe. This entry point
    /// has no host connector, vault, proxy, or SSH profile capability.
    /// </summary>
    internal static async Task RunChildAsync(Stream input, Stream output, IRedisPanelSessionFactory factory, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var responses = Channel.CreateBounded<RedisWorkspaceResponse>(new BoundedChannelOptions(RedisWorkspaceProtocol.MaximumQueuedEvents + 1)
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var writer = WriteResponsesAsync();
        IRedisPanelSession? session = null;
        try
        {
            var open = await RedisWorkspaceProtocol.ReadAsync(input, RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, lifetime.Token).ConfigureAwait(false);
            if (open.Id != 1 || open.Operation != RedisWorkspaceOperation.Open || open.Text is null)
            {
                throw new InvalidDataException("The Redis backend requires a session-open request.");
            }
            try
            {
                session = await factory.OpenAsync(open.Text, tunnel: null, lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await responses.Writer.WriteAsync(new(open.Id, RedisWorkspaceFailure.Unavailable), lifetime.Token).ConfigureAwait(false);
                return;
            }
            session.MessageReceived += OnMessage;
            await responses.Writer.WriteAsync(new(open.Id, Facts: session.Facts), lifetime.Token).ConfigureAwait(false);
            var previousId = open.Id;
            while (true)
            {
                RedisWorkspaceRequest request;
                try
                {
                    request = await RedisWorkspaceProtocol.ReadAsync(input, RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, lifetime.Token).ConfigureAwait(false);
                }
                catch (EndOfStreamException) { break; }
                if (request.Id != checked(previousId + 1) || request.Operation == RedisWorkspaceOperation.Open || !Enum.IsDefined(request.Operation))
                {
                    throw new InvalidDataException("The Redis backend request sequence is invalid.");
                }
                previousId = request.Id;
                RedisWorkspaceResponse response;
                try
                {
                    response = await ExecuteAsync(session, request, lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    response = new(request.Id, ClassifyFailure(exception, request.Operation));
                }
                await responses.Writer.WriteAsync(response, lifetime.Token).ConfigureAwait(false);
                if (response.Failure is RedisWorkspaceFailure.Unavailable or RedisWorkspaceFailure.OutcomeUnknown) { break; }
            }
        }
        finally
        {
            try
            {
                if (session is not null)
                {
                    session.MessageReceived -= OnMessage;
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                responses.Writer.TryComplete();
                await writer.ConfigureAwait(false);
            }
        }

        void OnMessage(object? sender, RedisPubSubMessage message)
        {
            // Subscriber callbacks cannot wait for a stalled frontend. Overflow ends
            // the session explicitly; silently dropping messages would falsify its history.
            if (Encoding.UTF8.GetByteCount(message.Payload) > RedisWorkspaceProtocol.MaximumEventBytes
                || Encoding.UTF8.GetByteCount(message.Channel) > RedisWorkspaceProtocol.MaximumEventBytes
                || !responses.Writer.TryWrite(new(0, Message: message)))
            {
                lifetime.Cancel();
            }
        }

        async Task WriteResponsesAsync()
        {
            try
            {
                await foreach (var response in responses.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    var frame = await RedisWorkspaceProtocol.SerializeAsync(response,
                        RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, lifetime.Token).ConfigureAwait(false);
                    if (response.Message is not null && frame.Length > RedisWorkspaceProtocol.MaximumEventBytes)
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame);
                        throw new InvalidDataException("The Redis subscription exceeded its message budget.");
                    }
                    await RedisWorkspaceProtocol.WriteAsync(output, frame, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static RedisWorkspaceFailure ClassifyFailure(Exception exception, RedisWorkspaceOperation operation) => exception switch
    {
        ArgumentException => RedisWorkspaceFailure.InvalidRequest,
        NotSupportedException => RedisWorkspaceFailure.Unsupported,
        RedisServerException => RedisWorkspaceFailure.Rejected,
        InvalidOperationException => RedisWorkspaceFailure.Rejected,
        _ => RedisWorkspaceProtocol.Mutates(operation) ? RedisWorkspaceFailure.OutcomeUnknown : RedisWorkspaceFailure.Unavailable,
    };

    private static async Task<RedisWorkspaceResponse> ExecuteAsync(IRedisPanelSession session, RedisWorkspaceRequest request, CancellationToken token)
    {
        var response = new RedisWorkspaceResponse(request.Id);
        switch (request.Operation)
        {
            case RedisWorkspaceOperation.SelectDatabase:
                await session.SelectDatabaseAsync(request.Count, token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.ScanKeys:
                response = response with { Scan = await session.ScanKeysAsync(Text(), request.OtherText, request.Count, token).ConfigureAwait(false) };
                break;
            case RedisWorkspaceOperation.ReadKey:
                response = response with { Key = await session.ReadKeyAsync(Key(), request.Count, token).ConfigureAwait(false) };
                break;
            case RedisWorkspaceOperation.SetString:
                await session.SetStringAsync(Key(), Text(), request.Expiry, token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.SetHashField:
                await session.SetHashFieldAsync(Key(), Text(), OtherText(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.AppendListValue:
                await session.AppendListValueAsync(Key(), Text(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.SetListValue:
                await session.SetListValueAsync(Key(), request.Index, Text(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.AddSetValue:
                await session.AddSetValueAsync(Key(), Text(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.AddSortedSetValue:
                await session.AddSortedSetValueAsync(Key(), Text(), request.Number, token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.AddStreamEntry:
                await session.AddStreamEntryAsync(Key(), Text(), OtherText(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.SetJson:
                await session.SetJsonAsync(Key(), Text(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.AddTimeSeriesSample:
                await session.AddTimeSeriesSampleAsync(Key(), request.Number, token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.DeleteKey:
                response = response with { Deleted = await session.DeleteKeyAsync(Key(), token).ConfigureAwait(false) };
                break;
            case RedisWorkspaceOperation.RemoveEntry:
                response = response with
                {
                    Removal = await session.RemoveEntryAsync(Key(), Text(),
                    request.Entry ?? throw new ArgumentException("A Redis collection entry is required."), token).ConfigureAwait(false)
                };
                break;
            case RedisWorkspaceOperation.SetExpiry:
                await session.SetExpiryAsync(Key(), request.Expiry, token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.Subscribe:
                await session.SubscribeAsync(Subscription(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.Unsubscribe:
                await session.UnsubscribeAsync(Subscription(), token).ConfigureAwait(false);
                break;
            case RedisWorkspaceOperation.Publish:
                response = response with { Count = await session.PublishAsync(Text(), OtherText(), request.Sharded, token).ConfigureAwait(false) };
                break;
            case RedisWorkspaceOperation.ListSearchIndexes:
                response = response with { Indexes = await session.ListSearchIndexesAsync(token).ConfigureAwait(false) };
                break;
            case RedisWorkspaceOperation.Search:
                response = response with { Search = await session.SearchAsync(Text(), OtherText(), request.Count, token).ConfigureAwait(false) };
                break;
            default:
                throw new ArgumentException("The Redis operation is invalid.");
        }
        return response with { Facts = session.Facts };

        string Text() => request.Text ?? throw new ArgumentException("Redis text is required.");
        string OtherText() => request.OtherText ?? throw new ArgumentException("Redis text is required.");
        RedisKeyReference Key() => request.Key is { Bytes: not null, DisplayName: not null } key
            ? key : throw new ArgumentException("A Redis key is required.");
        RedisSubscription Subscription() => request.Subscription is { Name: not null } subscription && Enum.IsDefined(subscription.Kind)
            ? subscription : throw new ArgumentException("A Redis subscription is required.");
    }
}
