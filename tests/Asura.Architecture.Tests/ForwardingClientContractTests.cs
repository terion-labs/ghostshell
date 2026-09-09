using System.Reflection;

using Asura.Application;

namespace Asura.Architecture.Tests;

/// <summary>
/// A default on an interface is a trapdoor.
///
/// The file seams are long chains of forwarders — a hosted client over a
/// session over a catalog runtime over a generation binding over the provider
/// — and an operation added to the interface with a default implementation
/// compiles cleanly through every one of them. Each forwarder silently inherits
/// "this connection cannot do that", and the operation is refused by a layer
/// that never asked the layer below.
///
/// That is exactly how permissions came to be refused on a local filesystem
/// that reads and writes them perfectly well: two forwarders in the middle had
/// never been told the question existed. Nothing failed to build, no test
/// covered the chain, and the shell offered an action its own plumbing dropped.
///
/// So: a type that forwards one of these interfaces answers every member of it
/// itself. The defaults exist for the ends of the chain — a provider that
/// genuinely has no such notion — never for the middle.
/// </summary>
public sealed class ForwardingClientContractTests
{
    [Theory]
    [InlineData(typeof(IFilePanelClient))]
    [InlineData(typeof(IFilePanelSession))]
    public void No_forwarder_inherits_a_default_it_should_be_passing_on(Type seam)
    {
        var offenders = new List<string>();
        foreach (var implementation in Implementations(seam))
        {
            var map = implementation.GetInterfaceMap(seam);
            for (var index = 0; index < map.InterfaceMethods.Length; index++)
            {
                // A member answered by the interface's own body rather than by
                // the class: the default came through instead of the forward.
                if (map.TargetMethods[index].DeclaringType == seam)
                {
                    offenders.Add(
                        $"{implementation.Name}.{map.InterfaceMethods[index].Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These take {seam.Name}'s default rather than passing the question "
            + "on to whatever they wrap: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The types that stand between the shell and a provider. A provider itself
    /// is not here: <see cref="Asura.Files.IFileProvider"/>'s defaults are
    /// the honest answer for a connection with no such notion, and the
    /// conformance suite is what holds a provider that claims otherwise to it.
    /// </summary>
    private static IEnumerable<Type> Implementations(Type seam) =>
        new[]
        {
            typeof(Asura.Files.FilePanelClient).Assembly,
            typeof(Asura.App.SessionHostedFilePanelClient).Assembly,
        }
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => type is { IsClass: true, IsAbstract: false }
            && seam.IsAssignableFrom(type)
            // A client that exists to refuse everything is the one place the
            // defaults are the point.
            && !string.Equals(type.Name, "UnavailableFilePanelClient", StringComparison.Ordinal))
        .OrderBy(type => type.FullName, StringComparer.Ordinal);
}
