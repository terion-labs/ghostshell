using System.Collections.ObjectModel;
using System.Text;
using Asura.Core;

namespace Asura.Application;

public sealed partial record AgentContextPanel
{
    private const int MaximumCapabilities = 128;
    private const int MaximumCapabilityBytes = 128;
    private const int MaximumIdentifierBytes = 256;
    private const int MaximumTitleBytes = 1024;

    private static void ValidateSessionOwner(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        PanelKind kind,
        SessionDescriptor? session)
    {
        if (session is null)
        {
            return;
        }

        var owner = session.Owner;
        ValidateIdentifier(session.Id.Value, nameof(session));
        if (session.Kind != kind
            || owner.WindowId != windowId
            || owner.WorkspaceId != workspaceId
            || owner.TabId != tabId
            || owner.PanelId != panelId)
        {
            throw new ArgumentException(
                "The live session metadata does not exactly own the context panel.",
                nameof(session));
        }

        if (session.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            throw new ArgumentException(
                "A context panel cannot expose a session that is no longer live.",
                nameof(session));
        }

        if (kind != PanelKind.Terminal && session.TerminalMetadata is not null)
        {
            throw new ArgumentException(
                "Only terminal sessions can expose terminal connection metadata.",
                nameof(session));
        }

        if (kind != PanelKind.FileViewer && session.FileMetadata is not null)
        {
            throw new ArgumentException(
                "Only File Viewer sessions can expose trusted file metadata.",
                nameof(session));
        }

        if (kind != PanelKind.Browser && session.BrowserMetadata is not null)
        {
            throw new ArgumentException(
                "Only browser sessions can expose trusted document metadata.",
                nameof(session));
        }

        if (kind != PanelKind.Git && session.GitMetadata is not null)
        {
            throw new ArgumentException(
                "Only Git sessions can expose trusted repository metadata.",
                nameof(session));
        }

        if (kind == PanelKind.Git && session.GitMetadata is null)
        {
            throw new ArgumentException(
                "A Git session requires trusted repository metadata.",
                nameof(session));
        }
    }

    private static string? CopyTitle(string? title, string parameterName)
    {
        if (title is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title)
            || title.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(title) > MaximumTitleBytes)
        {
            throw new ArgumentException(
                $"A context title must be printable and at most {MaximumTitleBytes} UTF-8 bytes.",
                parameterName);
        }

        return string.Concat(title);
    }

    private static IReadOnlyList<string> CopyCapabilities(CapabilitySet? capabilities)
    {
        if (capabilities is null)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var values = capabilities.Values
            .Select(value =>
            {
                if (string.IsNullOrWhiteSpace(value)
                    || value.Any(char.IsControl)
                    || Encoding.UTF8.GetByteCount(value) > MaximumCapabilityBytes)
                {
                    throw new ArgumentException(
                        "A session capability is invalid.",
                        nameof(capabilities));
                }

                return string.Concat(value);
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (values.Length > MaximumCapabilities)
        {
            throw new ArgumentException(
                $"A context panel cannot expose more than {MaximumCapabilities} capabilities.",
                nameof(capabilities));
        }

        return new ReadOnlyCollection<string>(values);
    }

    private static string? CopyConnectionBoundary(TerminalSessionMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        return CopyContextText(
            metadata.ConnectionBoundary,
            TerminalConnectionMetadata.MaximumBoundaryBytes,
            nameof(metadata));
    }

    private static string? CopyWorkingDirectory(
        string? workingDirectory,
        string parameterName) =>
        workingDirectory is null
            ? null
            : CopyContextText(
                workingDirectory,
                TerminalConnectionMetadata.MaximumWorkingDirectoryBytes,
                parameterName);

    private static string CopyContextText(
        string value,
        int maximumBytes,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character =>
                char.IsControl(character)
                || char.GetUnicodeCategory(character) is
                    System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator)
            || Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            throw new ArgumentException(
                "Terminal context metadata must be printable and bounded.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > MaximumIdentifierBytes)
        {
            throw new ArgumentException(
                $"A context identifier must be printable and at most {MaximumIdentifierBytes} UTF-8 bytes.",
                parameterName);
        }
    }
}
