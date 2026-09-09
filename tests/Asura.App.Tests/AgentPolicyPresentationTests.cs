using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class AgentPolicyPresentationTests
{
    [Theory]
    [InlineData(
        AgentCapability.BrowserScripting,
        "Browser scripting",
        "Evaluate JavaScript in an exact browser document.")]
    [InlineData(
        AgentCapability.BrowserDiagnostics,
        "Browser diagnostics",
        "Use browser console, network, and approved DevTools diagnostics.")]
    [InlineData(
        AgentCapability.DatabaseRead,
        "Database read",
        "Read bounded relational database and Redis data.")]
    [InlineData(
        AgentCapability.DatabaseWrite,
        "Database write",
        "Modify relational database and Redis data.")]
    [InlineData(
        AgentCapability.DockerData,
        "Docker data",
        "Inspect Docker workloads without lifecycle control.")]
    [InlineData(
        AgentCapability.GitData,
        "Git data",
        "Inspect Git repositories without changing them.")]
    [InlineData(
        AgentCapability.SystemData,
        "System data",
        "Read aggregate local system statistics.")]
    [InlineData(
        AgentCapability.ProcessData,
        "Process data",
        "Read bounded local process information.")]
    [InlineData(
        AgentCapability.ArtifactTransfer,
        "Artifact transfer",
        "Transfer approved browser and cross-panel artifacts.")]
    public void New_panel_capabilities_have_stable_user_facing_copy(
        AgentCapability capability,
        string expectedName,
        string expectedDescription)
    {
        Assert.Equal(
            expectedName,
            AgentPolicyPresentation.CapabilityName(capability));
        Assert.Equal(
            expectedDescription,
            AgentPolicyPresentation.CapabilityDescription(capability));
    }
}
