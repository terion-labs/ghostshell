using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.Architecture.Tests;

public sealed class AgentTerminalExecutionBoundaryTests
{
    [Fact]
    public void GovernedTerminalHostAcceptsOnlyAuthorizationAndPreparedAction()
    {
        Assert.Empty(typeof(AgentActionProposal).GetConstructors());
        Assert.Empty(typeof(AgentTerminalAction).GetConstructors());
        Assert.Empty(typeof(AgentActionExecutionBinding).GetConstructors());

        var method = Assert.Single(
            typeof(IAgentTerminalSessionHost).GetMethods());
        Assert.Equal(
            nameof(IAgentTerminalSessionHost.RunAgentTerminalActionAsync),
            method.Name);
        Assert.Equal(
            [
                typeof(AgentAuthorizationId),
                typeof(AgentTerminalAction),
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
    public void ConcreteTerminalHostExposesNoAlternateAgentExecutionShape()
    {
        var agentExecutionMethods = typeof(InMemorySessionHostClient)
            .GetMethods()
            .Where(method =>
                method.IsPublic
                && method.Name.Contains("AgentTerminal", StringComparison.Ordinal))
            .ToArray();
        var method = Assert.Single(agentExecutionMethods);

        Assert.Equal(
            nameof(IAgentTerminalSessionHost.RunAgentTerminalActionAsync),
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
    public void AgentMutationRequestsContainExecutionMaterialButNoInputLeaseId()
    {
        Assert.Equal(
            [typeof(SessionId), typeof(string)],
            PublicPropertyTypes(typeof(AgentTerminalRequest.SendText)));
        Assert.Equal(
            [typeof(SessionId), typeof(string)],
            PublicPropertyTypes(typeof(AgentTerminalRequest.Paste)));
        Assert.Equal(
            ["SessionId", "Text"],
            PublicPropertyNames(typeof(AgentTerminalRequest.Paste)));
        Assert.Equal(
            [typeof(SessionId), typeof(TerminalKeyStroke)],
            PublicPropertyTypes(typeof(AgentTerminalRequest.SendKey)));
        Assert.Equal(
            [typeof(SessionId)],
            PublicPropertyTypes(typeof(AgentTerminalRequest.Interrupt)));

        var mutationTypes = new[]
        {
            typeof(AgentTerminalRequest.SendText),
            typeof(AgentTerminalRequest.Paste),
            typeof(AgentTerminalRequest.SendKey),
            typeof(AgentTerminalRequest.Interrupt),
        };
        Assert.All(
            mutationTypes,
            type =>
            {
                Assert.DoesNotContain(
                    type.GetProperties(),
                    property => property.PropertyType == typeof(InputLeaseId));
                Assert.DoesNotContain(
                    type.GetConstructors().SelectMany(
                        constructor => constructor.GetParameters()),
                    parameter => parameter.ParameterType == typeof(InputLeaseId));
            });

        var pasteConstructor = Assert.Single(
            typeof(AgentTerminalRequest.Paste).GetConstructors());
        Assert.Equal(
            [typeof(SessionId), typeof(string)],
            pasteConstructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(AgentTerminalRequest.Paste).GetMembers(),
            member => member.Name.Contains(
                "ConfirmedUnsafe",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            pasteConstructor.GetParameters(),
            parameter => parameter.Name?.Contains(
                "confirmedUnsafe",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    private static Type[] PublicPropertyTypes(Type type) =>
        [.. type.GetProperties()
            .Where(property => property.DeclaringType == type)
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.PropertyType)];

    private static string[] PublicPropertyNames(Type type) =>
        [.. type.GetProperties()
            .Where(property => property.DeclaringType == type)
            .OrderBy(property => property.MetadataToken)
            .Select(property => property.Name)];
}
