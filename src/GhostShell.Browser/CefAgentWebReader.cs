using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.Browser;

internal sealed class CefAgentWebReader
{
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan DomQuietWindow = TimeSpan.FromMilliseconds(500);
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private readonly WebContentMarkdownConverter _converter = new();
    private readonly IWorkspaceNetworkConnector? _networkConnector;

    public CefAgentWebReader(int? socksProxyPort = null)
    {
        if (socksProxyPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(socksProxyPort));
        }

        _networkConnector = socksProxyPort is { } port
            ? new LegacySocksConnector(port)
            : null;
    }

    public CefAgentWebReader(IWorkspaceNetworkConnector networkConnector)
    {
        _networkConnector = networkConnector
            ?? throw new ArgumentNullException(nameof(networkConnector));
    }

    public async ValueTask<AgentWebToolExecutionResult> ReadAsync(
        AgentWebReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Failed(AgentWebToolErrorCode.Cancelled);
        }

        if (!CefBrowserView.HasPeerBoundTransport)
        {
            return Failed(AgentWebToolErrorCode.DestinationDenied);
        }

        try
        {
            await BrowserGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(AgentWebToolErrorCode.Cancelled);
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            deadline.CancelAfter(ReadDeadline);
            return await ReadCoreAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failed(
                cancellationToken.IsCancellationRequested
                    ? AgentWebToolErrorCode.Cancelled
                    : AgentWebToolErrorCode.TimedOut);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return Failed(AgentWebToolErrorCode.Unavailable);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    internal ValueTask<AgentWebToolExecutionResult> ConvertAsync(
        BrowserAddress finalAddress,
        AgentWebReadFormat format,
        string json,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            var pageTitle = root.GetProperty("title").GetString() ?? string.Empty;
            var renderedHtml = root.GetProperty("html").GetString() ?? string.Empty;
            var links = root.GetProperty("links")
                .EnumerateArray()
                .Select(link => link.GetString() ?? throw new JsonException(
                    "Page link is not a string."))
                .ToArray();
            var sourceTruncated = root.GetProperty("truncated").GetBoolean();
            string title;
            string content;
            if (format is AgentWebReadFormat.Markdown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                title = pageTitle;
                content = _converter.ConvertArticle(renderedHtml);
            }
            else
            {
                title = pageTitle;
                content = renderedHtml;
            }

            content = BoundedWebText.Truncate(
                content,
                AgentWebReadResult.MaximumContentBytes,
                out var contentTruncated);
            title = BoundedWebText.Truncate(
                title,
                AgentWebReadResult.MaximumTitleBytes,
                out _);
            return ValueTask.FromResult(Succeeded(new AgentWebReadResult(
                    finalAddress.Value.AbsoluteUri,
                    title,
                    format,
                    content,
                    links,
                    sourceTruncated || contentTruncated)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or InvalidOperationException
            or JsonException)
        {
            _ = exception;
            return ValueTask.FromResult(Failed(
                format is AgentWebReadFormat.Markdown
                    ? AgentWebToolErrorCode.ConverterFailed
                    : AgentWebToolErrorCode.ExtractionFailed));
        }
    }

    private async ValueTask<AgentWebToolExecutionResult> ReadCoreAsync(
        AgentWebReadRequest request,
        CancellationToken cancellationToken)
    {
        CefBrowserNetworkContext? network = null;
        CefBrowserView? browser = null;
        try
        {
            (network, browser) = await AvaloniaBrowserUiDispatcher.Instance
                .InvokeAsync(() =>
                {
                    var createdNetwork = _networkConnector is { } connector
                        ? CefBrowserNetworkContext.CreateIsolatedAgentWeb(
                            connector.BrowserProxyEndpoint)
                        : CefBrowserNetworkContext.CreateIsolatedAgentWeb();
                    var proxyResolver = _networkConnector?.LocalProxyCredentials is { } credentials
                        ? new WorkspaceProxyAuthenticationResolver(
                            _networkConnector.BrowserProxyEndpoint,
                            credentials)
                        : null;
                    var createdBrowser = createdNetwork.CreateView(proxyResolver);
                    createdBrowser.SetResourceRequestPolicy(
                        (candidate, token) => BrowserDestinationPolicy.LocalSystem
                            .AllowsCefTransportAsync(candidate, token));
                    return (createdNetwork, createdBrowser);
                });
            if (!await browser.BeginDomObservationWhenReadyAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return Failed(AgentWebToolErrorCode.Unavailable);
            }

            var navigation = new TaskCompletionSource<NavigationOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await BeginNavigationAsync(
                    browser,
                    new BrowserAddress(request.Address),
                    navigation,
                    cancellationToken)
                .ConfigureAwait(false);
            var outcome = await navigation.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!outcome.IsSuccess || outcome.Address is null)
            {
                return Failed(outcome.ErrorCode);
            }

            browser.MarkDomActivity();
            await browser.WaitForDomQuietAsync(
                    DomQuietWindow,
                    cancellationToken)
                .ConfigureAwait(false);
            var extracted = await (request.Format is AgentWebReadFormat.Markdown
                    ? browser.ExtractReadableArticleAsync()
                    : browser.ExtractRenderedDocumentAsync())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (extracted.Status != NativeBrowserAutomationStatus.Acknowledged
                || extracted.ResultJson is null)
            {
                return Failed(AgentWebToolErrorCode.ExtractionFailed);
            }

            return await ConvertAsync(
                    outcome.Address,
                    request.Format,
                    extracted.ResultJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (browser is not null || network is not null)
            {
                await AvaloniaBrowserUiDispatcher.Instance.InvokeAsync(() =>
                {
                    browser?.EndDomObservation();
                    browser?.Dispose();
                    network?.Dispose();
                    return true;
                });
            }
        }
    }

    private sealed class LegacySocksConnector(int port) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Direct;

        public Uri LocalProxyEndpoint { get; } =
            new($"socks5://127.0.0.1:{port}", UriKind.Absolute);

        public ValueTask<Stream> ConnectTcpAsync(
            string host,
            int targetPort,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());
    }

    private static async ValueTask BeginNavigationAsync(
        CefBrowserView browser,
        BrowserAddress address,
        TaskCompletionSource<NavigationOutcome> completion,
        CancellationToken cancellationToken)
    {
        await AvaloniaBrowserUiDispatcher.Instance.InvokeAsync(() =>
        {
            browser.NavigationStarted += (_, args) =>
            {
                try
                {
                    browser.SetActiveNavigationRequestPolicy(
                        (candidate, token) => BrowserDestinationPolicy.LocalSystem
                            .AllowsCefTransportAsync(candidate, token));
                }
                catch (InvalidOperationException)
                {
                    args.Cancel = true;
                }
            };
            browser.NavigationRejected += (_, _) =>
                completion.TrySetResult(NavigationOutcome.Denied());
            browser.RenderProcessFailed += (_, _) =>
                completion.TrySetResult(NavigationOutcome.RenderProcessFailed());
            browser.NavigationCompleted += (_, args) =>
                completion.TrySetResult(
                    args.IsSuccess && args.Address is not null
                        ? NavigationOutcome.Succeeded(args.Address)
                        : NavigationOutcome.LoadFailed());
            browser.Navigate(address);
            return true;
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static AgentWebToolExecutionResult Failed(AgentWebToolErrorCode code) =>
        new AgentWebToolExecutionResult.Failed(code);

    private static AgentWebToolExecutionResult Succeeded(AgentWebToolResult result) =>
        new AgentWebToolExecutionResult.Succeeded(result);

    private sealed record NavigationOutcome(
        bool IsSuccess,
        BrowserAddress? Address,
        AgentWebToolErrorCode ErrorCode)
    {
        public static NavigationOutcome Succeeded(BrowserAddress address) =>
            new(true, address, AgentWebToolErrorCode.LoadFailed);

        public static NavigationOutcome Denied() =>
            new(false, null, AgentWebToolErrorCode.DestinationDenied);

        public static NavigationOutcome LoadFailed() =>
            new(false, null, AgentWebToolErrorCode.LoadFailed);

        public static NavigationOutcome RenderProcessFailed() =>
            new(false, null, AgentWebToolErrorCode.RenderProcessFailed);
    }
}
