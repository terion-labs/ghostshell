using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceSessionFactoryRegistryTests
{
    [Fact]
    public void MissingAndClosedWorkspaceNeverResolveAnImplicitHostFactory()
    {
        var registry = new WorkspaceSessionFactoryRegistry<object>("Duplicate registration.");
        var workspace = new WorkspaceInstanceId("owned-runtime");
        Assert.Throws<InvalidOperationException>(() => registry.Resolve(workspace));
        var factory = new object();
        using (registry.Register(workspace, factory))
        {
            Assert.Same(factory, registry.Resolve(workspace));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve(new("other-runtime")));
            Assert.Throws<InvalidOperationException>(() => registry.Register(workspace, new object()));
        }
        Assert.Throws<InvalidOperationException>(() => registry.Resolve(workspace));
    }

    [Fact]
    public void RepeatedOldDisposalCannotRemoveExplicitReplacement()
    {
        var registry = new WorkspaceSessionFactoryRegistry<object>("Duplicate registration.");
        var workspace = new WorkspaceInstanceId("owned-runtime");
        using var first = registry.Register(workspace, new object());
        first.Dispose();
        var replacement = new object();
        using var second = registry.Register(workspace, replacement);
        first.Dispose();
        Assert.Same(replacement, registry.Resolve(workspace));
    }
}
