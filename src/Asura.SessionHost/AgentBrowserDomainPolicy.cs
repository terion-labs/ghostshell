using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

/// <summary>
/// Revalidates browser authorization, document state, and input bindings after
/// a one-action authorization has been consumed and again immediately before
/// renderer dispatch.
/// </summary>
internal static class AgentBrowserDomainPolicy
{
    public const string AuthorizationDeniedStableCode =
        "browser_action_not_authorized";
    public const string NavigationInProgressStableCode =
        "navigation_in_progress";
    public const string BrowserStateChangedStableCode =
        "browser_state_changed";

    public static AgentBrowserDomainPolicyDecision Evaluate(
        AgentBrowserRequest request,
        BrowserSessionState currentState,
        AgentAuthorizationSource authorizationSource)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentState);

        if ((authorizationSource == AgentAuthorizationSource.AutoPolicy
                && !IsAllowedAutomatically(request))
            || authorizationSource is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.AutoPolicy
                or AgentAuthorizationSource.YoloPolicy))
        {
            return AgentBrowserDomainPolicyDecision.Deny(Denied());
        }

        if (RequiresOriginGuard(request)
            && currentState.LoadState == BrowserLoadState.Loading)
        {
            return AgentBrowserDomainPolicyDecision.Deny(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    NavigationInProgressStableCode,
                    "The browser is already loading a page.",
                    Retryable: true));
        }

        if (request is AgentBrowserRequest.Snapshot
            && currentState.LoadState != BrowserLoadState.Ready)
        {
            return AgentBrowserDomainPolicyDecision.Deny(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    NavigationInProgressStableCode,
                    "The browser document is not ready for snapshot capture.",
                    Retryable: true));
        }

        if (request is AgentBrowserRequest.Click
            or AgentBrowserRequest.Fill
            or AgentBrowserRequest.Check)
        {
            if (currentState.LoadState != BrowserLoadState.Ready)
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        NavigationInProgressStableCode,
                        "The browser document is not ready for interaction.",
                        Retryable: true));
            }

            var requestedDocumentRevision = request switch
            {
                AgentBrowserRequest.Click click =>
                    click.Value.DocumentRevision,
                AgentBrowserRequest.Fill fill =>
                    fill.Value.DocumentRevision,
                AgentBrowserRequest.Check check =>
                    check.Value.DocumentRevision,
                _ => throw new InvalidOperationException(
                    "The browser interaction kind is unsupported."),
            };
            if (requestedDocumentRevision != currentState.DocumentRevision)
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        BrowserStateChangedStableCode,
                        "The browser document changed after the element was observed.",
                        Retryable: true));
            }
        }

        if (request is AgentBrowserRequest.Mouse
            or AgentBrowserRequest.Key
            or AgentBrowserRequest.Scroll
            or AgentBrowserRequest.Evaluate)
        {
            if (currentState.LoadState != BrowserLoadState.Ready)
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        NavigationInProgressStableCode,
                        "The browser document is not ready for automation.",
                        Retryable: true));
            }

            var binding = request switch
            {
                AgentBrowserRequest.Mouse mouse => mouse.Value.Binding,
                AgentBrowserRequest.Key key => key.Value.Binding,
                AgentBrowserRequest.Scroll scroll => scroll.Value.Binding,
                AgentBrowserRequest.Evaluate evaluate => evaluate.Value.Binding,
                _ => throw new InvalidOperationException(),
            };
            if (!binding.Matches(currentState))
            {
                return AgentBrowserDomainPolicyDecision.Deny(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        BrowserStateChangedStableCode,
                        "The browser document, viewport, or input epoch changed.",
                        Retryable: true));
            }

            if (request is AgentBrowserRequest.Evaluate
                {
                    Value.World: BrowserEvaluationWorld.Main,
                }
                && authorizationSource != AgentAuthorizationSource.HumanApproval)
            {
                return AgentBrowserDomainPolicyDecision.Deny(Denied());
            }
        }

        if (request is AgentBrowserRequest.Wait
            {
                Value.Condition: BrowserWaitCondition.ElementState element,
            }
            && element.SourceDocumentRevision
                != currentState.DocumentRevision)
        {
            return AgentBrowserDomainPolicyDecision.Deny(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    BrowserStateChangedStableCode,
                    "The browser document changed after the wait reference was observed.",
                    Retryable: true));
        }

        var allowedOrigin = OriginFor(request);
        return AgentBrowserDomainPolicyDecision.Allow(
            allowedOrigin,
            allowedOrigin is null
                ? null
                : BrowserNavigationStartBinding.FromState(currentState));
    }

    private static bool IsAllowedAutomatically(AgentBrowserRequest request) =>
        request switch
        {
            AgentBrowserRequest.ReadState => true,
            AgentBrowserRequest.Snapshot => true,
            AgentBrowserRequest.Wait => true,
            AgentBrowserRequest.Click => false,
            AgentBrowserRequest.Fill => false,
            AgentBrowserRequest.Check => false,
            AgentBrowserRequest.Mouse => false,
            AgentBrowserRequest.Key => false,
            AgentBrowserRequest.Scroll => false,
            AgentBrowserRequest.Evaluate => false,
            AgentBrowserRequest.Navigate => true,
            AgentBrowserRequest.Reload => true,
            AgentBrowserRequest.Stop => true,
            AgentBrowserRequest.Back => false,
            AgentBrowserRequest.Forward => false,
            _ => false,
        };

    private static BrowserNavigationOrigin? OriginFor(
        AgentBrowserRequest request)
    {
        return request switch
        {
            AgentBrowserRequest.Navigate navigate =>
                BrowserNavigationOrigin.FromAddress(navigate.Value.Address),
            AgentBrowserRequest.Click
                or AgentBrowserRequest.Fill
                or AgentBrowserRequest.Check
                or AgentBrowserRequest.Mouse
                or AgentBrowserRequest.Key
                or AgentBrowserRequest.Scroll
                or AgentBrowserRequest.Evaluate
                or AgentBrowserRequest.Back
                or AgentBrowserRequest.Forward
                or AgentBrowserRequest.Reload =>
                BrowserNavigationOrigin.Unrestricted,
            AgentBrowserRequest.ReadState
                or AgentBrowserRequest.Snapshot
                or AgentBrowserRequest.Wait
                or AgentBrowserRequest.Stop => null,
            _ => null,
        };
    }

    private static bool RequiresOriginGuard(AgentBrowserRequest request) =>
        request is AgentBrowserRequest.Navigate
            or AgentBrowserRequest.Click
            or AgentBrowserRequest.Fill
            or AgentBrowserRequest.Check
            or AgentBrowserRequest.Mouse
            or AgentBrowserRequest.Key
            or AgentBrowserRequest.Scroll
            or AgentBrowserRequest.Evaluate
            or AgentBrowserRequest.Back
            or AgentBrowserRequest.Forward
            or AgentBrowserRequest.Reload;

    private static HostError Denied() =>
        new(
            HostErrorCode.InvalidRequest,
            AuthorizationDeniedStableCode,
            "The governed browser action does not have the required authorization source.");
}

internal sealed record AgentBrowserDomainPolicyDecision
{
    private AgentBrowserDomainPolicyDecision(
        BrowserNavigationOrigin? allowedOrigin,
        BrowserNavigationStartBinding? startBinding,
        HostError? error)
    {
        if ((allowedOrigin is not null || startBinding is not null)
            && error is not null)
        {
            throw new ArgumentException(
                "A denied browser policy decision cannot carry a navigation binding.");
        }

        if ((allowedOrigin is null) != (startBinding is null))
        {
            throw new ArgumentException(
                "A governed browser policy decision must carry both its origin and starting document.");
        }

        AllowedOrigin = allowedOrigin;
        StartBinding = startBinding;
        Error = error;
    }

    public BrowserNavigationOrigin? AllowedOrigin { get; }

    public BrowserNavigationStartBinding? StartBinding { get; }

    public HostError? Error { get; }

    public bool IsAllowed => Error is null;

    public static AgentBrowserDomainPolicyDecision Allow(
        BrowserNavigationOrigin? allowedOrigin,
        BrowserNavigationStartBinding? startBinding) =>
        new(allowedOrigin, startBinding, error: null);

    public static AgentBrowserDomainPolicyDecision Deny(HostError error) =>
        new(
            allowedOrigin: null,
            startBinding: null,
            error: error ?? throw new ArgumentNullException(nameof(error)));
}
