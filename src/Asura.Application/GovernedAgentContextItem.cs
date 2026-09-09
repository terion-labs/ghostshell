using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Presentation-only description of one panel in a governed run's fixed
/// scope. It conveys no reusable session, attachment, or execution authority.
/// </summary>
public sealed record GovernedAgentContextItem
{
    public const int MaximumDisplayTextBytes = 128;
    public const int MaximumFileRootDisplayBytes = 4 * 1024;
    public const int MaximumSupportedOperations = 32;
    private const int MaximumIdentifierBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public GovernedAgentContextItem(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        SessionId sessionId,
        string? workspaceTitle,
        string? tabTitle,
        string? panelTitle,
        string? connectionBoundary,
        string? workingDirectory,
        SessionLifecycle lifecycle,
        SessionHealth health,
        bool isVisible,
        bool isFocused,
        bool hasActiveWork,
        IEnumerable<string> supportedOperations)
        : this(
            windowId,
            workspaceId,
            tabId,
            panelId,
            sessionId,
            PanelKind.Terminal,
            workspaceTitle,
            tabTitle,
            panelTitle,
            connectionBoundary,
            workingDirectory,
            lifecycle,
            health,
            isVisible,
            isFocused,
            hasActiveWork,
            supportedOperations)
    {
    }

    public GovernedAgentContextItem(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        SessionId sessionId,
        PanelKind kind,
        string? workspaceTitle,
        string? tabTitle,
        string? panelTitle,
        string? connectionBoundary,
        string? workingDirectory,
        SessionLifecycle lifecycle,
        SessionHealth health,
        bool isVisible,
        bool isFocused,
        bool hasActiveWork,
        IEnumerable<string> supportedOperations,
        string? fileProviderProfileId = null,
        string? fileRootDisplay = null)
    {
        RequireIdentifier(windowId.Value, nameof(windowId));
        RequireIdentifier(workspaceId.Value, nameof(workspaceId));
        RequireIdentifier(tabId.Value, nameof(tabId));
        RequireIdentifier(panelId.Value, nameof(panelId));
        RequireIdentifier(sessionId.Value, nameof(sessionId));
        if (!Enum.IsDefined(lifecycle))
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(health))
        {
            throw new ArgumentOutOfRangeException(nameof(health));
        }

        if ((fileProviderProfileId is null) != (fileRootDisplay is null))
        {
            throw new ArgumentException(
                "A governed file scope requires both its provider profile and trusted root.");
        }

        if (kind != PanelKind.FileViewer && fileProviderProfileId is not null)
        {
            throw new ArgumentException(
                "Only a File Viewer context can expose a governed file scope.",
                nameof(fileProviderProfileId));
        }

        ArgumentNullException.ThrowIfNull(supportedOperations);
        var operations = supportedOperations
            .Take(MaximumSupportedOperations + 1)
            .Select(CopyOperation)
            .ToImmutableArray();
        if (operations.Length > MaximumSupportedOperations)
        {
            throw new ArgumentException(
                $"A governed context item cannot contain more than "
                + $"{MaximumSupportedOperations} operations.",
                nameof(supportedOperations));
        }

        if (operations.Distinct(StringComparer.Ordinal).Count() != operations.Length)
        {
            throw new ArgumentException(
                "A governed context item cannot contain duplicate operations.",
                nameof(supportedOperations));
        }

        WindowId = windowId;
        WorkspaceId = workspaceId;
        TabId = tabId;
        PanelId = panelId;
        SessionId = sessionId;
        Kind = kind;
        WorkspaceTitle = CopyDisplayText(workspaceTitle, nameof(workspaceTitle));
        TabTitle = CopyDisplayText(tabTitle, nameof(tabTitle));
        PanelTitle = CopyDisplayText(panelTitle, nameof(panelTitle));
        ConnectionBoundary = CopyDisplayText(
            connectionBoundary,
            nameof(connectionBoundary));
        WorkingDirectory = CopyDisplayText(
            workingDirectory,
            nameof(workingDirectory));
        FileProviderProfileId = CopyFileProviderProfileId(fileProviderProfileId);
        FileRootDisplay = CopyFileRootDisplay(fileRootDisplay);
        Lifecycle = lifecycle;
        Health = health;
        IsVisible = isVisible;
        IsFocused = isFocused;
        HasActiveWork = hasActiveWork;
        SupportedOperations = operations;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }

    public PanelInstanceId PanelId { get; }

    public SessionId SessionId { get; }

    public PanelKind Kind { get; }

    public string? WorkspaceTitle { get; }

    public string? TabTitle { get; }

    public string? PanelTitle { get; }

    public string? ConnectionBoundary { get; }

    public string? WorkingDirectory { get; }

    public string? FileProviderProfileId { get; }

    public string? FileRootDisplay { get; }

    public SessionLifecycle Lifecycle { get; }

    public SessionHealth Health { get; }

    public bool IsVisible { get; }

    public bool IsFocused { get; }

    public bool HasActiveWork { get; }

    public ImmutableArray<string> SupportedOperations { get; }

    private static string? CopyDisplayText(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || ContainsUnsafeDisplayCharacter(value)
            || Encoding.UTF8.GetByteCount(value) > MaximumDisplayTextBytes)
        {
            throw new ArgumentException(
                "Governed context display text must be non-empty and bounded.",
                parameterName);
        }

        return string.Concat(value);
    }

    private static string CopyOperation(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > 128)
        {
            throw new ArgumentException(
                "A governed context operation is invalid.",
                nameof(value));
        }

        return string.Concat(value);
    }

    private static string? CopyFileProviderProfileId(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "A governed file-provider profile ID is invalid.",
                nameof(value));
        }

        return string.Concat(value);
    }

    private static string? CopyFileRootDisplay(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || ContainsUnsafeDisplayCharacter(value)
            || GetStrictUtf8ByteCount(value, nameof(value))
                > MaximumFileRootDisplayBytes
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "A governed file root must be printable, bounded, and non-secret.",
                nameof(value));
        }

        return string.Concat(value);
    }

    private static int GetStrictUtf8ByteCount(
        string value,
        string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Governed file scope text must contain valid Unicode.",
                parameterName,
                exception);
        }
    }

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > MaximumIdentifierBytes)
        {
            throw new ArgumentException(
                "A governed context identifier must be printable and bounded.",
                parameterName);
        }
    }

    private static bool ContainsUnsafeDisplayCharacter(string value) =>
        value.Any(character =>
            char.IsControl(character)
            || char.GetUnicodeCategory(character) is
                UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator);
}
