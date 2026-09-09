using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Canonically binds an exact resolved target to its current graph/session
/// ownership and revisions. It is comparison evidence, not reusable authority.
/// </summary>
public static class AgentContextBindingFingerprint
{
    public static AgentActionDigest Create(AgentContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(context.Target, context.Panels);
    }

    internal static AgentActionDigest Create(
        AgentTarget target,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(panels);
        var builder = new StringBuilder();
        Append(builder, AgentTargetIdentity.Create(target).Value);
        Append(builder, panels.Count);
        foreach (var panel in panels)
        {
            Append(builder, panel.WindowId.Value);
            Append(builder, panel.WorkspaceId.Value);
            Append(builder, panel.WorkspaceRevision);
            Append(builder, panel.GraphSequence);
            Append(builder, panel.GraphTabOrder);
            Append(builder, panel.GraphPanelOrder);
            Append(builder, panel.TabId.Value);
            Append(builder, panel.PanelId.Value);
            Append(builder, (int)panel.Kind);
            Append(builder, panel.HasRegisteredGraph);
            Append(builder, panel.IsCurrentPanelSession);
            Append(builder, panel.SessionId?.Value);
            Append(builder, panel.SessionRevision);
            Append(builder, panel.Lifecycle is { } lifecycle ? (int)lifecycle : null);
            Append(builder, panel.Health is { } health ? (int)health : null);
            Append(builder, panel.HasActiveWork);
            Append(builder, panel.ConnectionId?.Value);
            Append(builder, panel.ConnectionBoundary);
            Append(builder, panel.InitialWorkingDirectory);
            Append(builder, panel.CurrentWorkingDirectory);
            AppendFileMetadata(builder, panel.FileMetadata);
            AppendBrowserMetadata(builder, panel.BrowserMetadata);
            AppendGitMetadata(builder, panel.GitMetadata);
            Append(builder, panel.Capabilities.Count);
            foreach (var capability in panel.Capabilities)
            {
                Append(builder, capability);
            }
        }

        return AgentActionDigest.FromUtf8(builder.ToString());
    }

    private static void AppendGitMetadata(
        StringBuilder builder,
        GitSessionMetadata? metadata)
    {
        if (metadata is null)
        {
            Append(builder, (string?)null);
            return;
        }

        Append(builder, metadata.RepositoryIdentity.Value);
        Append(builder, metadata.BindingRevision);
        Append(builder, metadata.ConnectionDisplayName);
        Append(builder, (int)metadata.ConnectionKind);
        Append(builder, metadata.MutationsQuarantined);
    }

    private static void AppendBrowserMetadata(
        StringBuilder builder,
        BrowserSessionMetadata? metadata)
    {
        if (metadata is null)
        {
            Append(builder, (string?)null);
            return;
        }

        Append(builder, metadata.Origin.Scheme);
        Append(builder, metadata.Origin.IdnHost);
        Append(builder, metadata.Origin.Port);
        Append(builder, metadata.Origin.IsBlank);
        Append(builder, metadata.Address?.ToString());
        Append(builder, metadata.DocumentRevision);
        Append(builder, metadata.Viewport.WidthCss.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, metadata.Viewport.HeightCss.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, metadata.Viewport.DeviceScaleFactor.ToString("R", CultureInfo.InvariantCulture));
        Append(builder, metadata.ViewportRevision);
        Append(builder, metadata.InputEpoch);
    }

    private static void AppendFileMetadata(
        StringBuilder builder,
        FileSessionMetadata? metadata)
    {
        if (metadata is null)
        {
            Append(builder, (string?)null);
            return;
        }

        Append(builder, metadata.TrustedRoot.ProviderProfileId);
        Append(builder, metadata.TrustedRoot.Authority);
        switch (metadata.TrustedRoot.Address)
        {
            case FilePanelAddress.Hierarchical hierarchical:
                Append(builder, "hierarchical");
                Append(builder, hierarchical.Path.Segments.Length);
                foreach (var segment in hierarchical.Path.Segments)
                {
                    Append(builder, segment.Value);
                }

                break;
            case FilePanelAddress.ObjectKey objectKey:
                Append(builder, "object");
                Append(builder, objectKey.Key);
                break;
            case FilePanelAddress.ContainerRoot:
                Append(builder, "container");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(metadata),
                    metadata.TrustedRoot.Address.GetType(),
                    "The file-session root address is unsupported.");
        }

        Append(builder, metadata.TrustedRoot.Version);
        Append(builder, (long)metadata.Capabilities);
        Append(builder, metadata.MaximumListPageSize);
        Append(builder, metadata.MaximumPreviewBytes);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("|n");
            return;
        }

        builder
            .Append('|')
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    private static void Append(StringBuilder builder, int? value) =>
        Append(
            builder,
            value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, long? value) =>
        Append(
            builder,
            value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, bool value) =>
        Append(builder, value ? "1" : "0");
}
