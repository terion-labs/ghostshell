using Asura.ConnectionBackend;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceHttpBackendReviewTests
{
    [Theory]
    [InlineData("X_Custom")]
    [InlineData("X!#$%&'*+-.^_`|~")]
    [InlineData("Authorization")]
    public void HeaderNamesAcceptTheCompleteHttpTokenGrammar(string name)
    {
        WorkspaceHttpProtocol.Validate([new(name, ["value"])]);
    }

    [Theory]
    [InlineData("X:Injected")]
    [InlineData("X Header")]
    [InlineData("X\tHeader")]
    [InlineData("X\r\nHeader")]
    [InlineData("X(Host)")]
    [InlineData("X/Header")]
    [InlineData("Xé")]
    public void HeaderNamesStillRejectSeparatorsControlsAndNonAscii(string name)
    {
        Assert.Throws<InvalidDataException>(() => WorkspaceHttpProtocol.Validate([new(name, ["value"])]));
    }
}
