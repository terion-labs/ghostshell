using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.Architecture.Tests;

public sealed class AgentBrowserExecutionBoundaryTests
{
    [Fact]
    public void GovernedBrowserHostAcceptsOnlyAuthorizationAndPreparedAction()
    {
        Assert.Empty(typeof(AgentBrowserAction).GetConstructors());

        var method = Assert.Single(
            typeof(IAgentBrowserSessionHost).GetMethods());
        Assert.Equal(
            nameof(IAgentBrowserSessionHost.RunAgentBrowserActionAsync),
            method.Name);
        Assert.Equal(
            [
                typeof(AgentAuthorizationId),
                typeof(AgentBrowserAction),
                typeof(CancellationToken),
            ],
            method.GetParameters().Select(parameter => parameter.ParameterType));

        var forbidden = new[]
        {
            typeof(AgentActionPermit),
            typeof(AgentActionProposal),
            typeof(string),
            typeof(object),
            typeof(JsonElement),
            typeof(Delegate),
        };
        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => forbidden.Contains(parameter.ParameterType)
                || typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact]
    public void ConcreteBrowserHostExposesNoAlternateAgentExecutionShape()
    {
        var methods = typeof(InMemorySessionHostClient)
            .GetMethods()
            .Where(method =>
                method.IsPublic
                && method.Name.Contains(
                    "AgentBrowser",
                    StringComparison.Ordinal))
            .ToArray();
        var method = Assert.Single(methods);

        Assert.Equal(
            nameof(IAgentBrowserSessionHost.RunAgentBrowserActionAsync),
            method.Name);
        Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(AgentActionPermit)
                || parameter.ParameterType == typeof(AgentActionProposal)
                || parameter.ParameterType == typeof(string)
                || parameter.ParameterType == typeof(object)
                || parameter.ParameterType == typeof(JsonElement)
                || typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact]
    public void BrowserRequestsCarryNoAttachmentOrApprovalIdentity()
    {
        Type[] intendedRequestTypes =
        [
            typeof(AgentBrowserRequest.ReadState),
            typeof(AgentBrowserRequest.Snapshot),
            typeof(AgentBrowserRequest.Wait),
            typeof(AgentBrowserRequest.Click),
            typeof(AgentBrowserRequest.Fill),
            typeof(AgentBrowserRequest.Check),
            typeof(AgentBrowserRequest.Mouse),
            typeof(AgentBrowserRequest.Key),
            typeof(AgentBrowserRequest.Scroll),
            typeof(AgentBrowserRequest.Evaluate),
            typeof(AgentBrowserRequest.Navigate),
            typeof(AgentBrowserRequest.Back),
            typeof(AgentBrowserRequest.Forward),
            typeof(AgentBrowserRequest.Reload),
            typeof(AgentBrowserRequest.Stop),
        ];
        var requestTypes = typeof(AgentBrowserRequest)
            .GetNestedTypes()
            .Where(type => !type.IsAbstract)
            .ToArray();

        Assert.Equal(
            intendedRequestTypes.OrderBy(type => type.FullName, StringComparer.Ordinal),
            requestTypes.OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.All(
            requestTypes,
            type =>
            {
                Assert.DoesNotContain(
                    type.GetProperties(),
                    property => property.PropertyType == typeof(AttachmentId)
                        || property.PropertyType == typeof(ClientId)
                        || property.PropertyType == typeof(AgentApprovalId)
                        || property.PropertyType == typeof(AgentAuthorizationId));
                Assert.DoesNotContain(
                    type.GetConstructors().SelectMany(
                        constructor => constructor.GetParameters()),
                    parameter => parameter.ParameterType == typeof(AttachmentId)
                        || parameter.ParameterType == typeof(ClientId)
                        || parameter.ParameterType == typeof(AgentApprovalId)
                        || parameter.ParameterType == typeof(AgentAuthorizationId));
            });
    }
}
