using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Resolves a relative file request against one trusted File Viewer session and derives
/// execution material, approval text, and authorization digest from the same fields.
/// </summary>
public sealed class AgentFileActionComposer
{
    public const int MaximumAgentListPageSize = 100;
    public const int MaximumAgentReadBytes = 64 * 1024;
    public const int MaximumRelativePathSegments = 64;
    public const int MaximumPathSegmentBytes = 255;
    public const int MaximumRelativePathBytes = 4 * 1024;
    public const int MaximumAgentFileNameBytes = 1024;
    public const int MaximumAgentMediaTypeBytes = 256;
    public const int MaximumAgentSearchQueryBytes = 256;
    public const int MaximumAgentSearchResults = 100;
    public const int MaximumAgentAccessGrants = 100;
    public const int MaximumAgentTransfers = 100;
    public const int MaximumAgentTextBytes = 8 * 1024;
    public const long MaximumAgentCopyBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentFileAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var operation = DescribeOperation(request);
        var resolved = ResolveForPreparation(context, operation);
        var prepared = PrepareRequest(request, operation, resolved.Panel);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            prepared.ToolName,
            resolved.Context,
            CreateArgumentDigest(
                envelope.ActionId,
                prepared.ToolName,
                prepared.Arguments),
            CreatePresentation(
                resolved.Context.Target,
                resolved.Panel,
                prepared.Arguments),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentFileAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentFileAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var operation = DescribeOperation(action.Request);
        var resolved = ResolveForExecution(freshContext, operation);
        var prepared = PrepareRequest(action.Request, operation, resolved.Panel);
        var proposal = action.Proposal;
        var argumentDigest = CreateArgumentDigest(
            proposal.Id,
            prepared.ToolName,
            prepared.Arguments);
        if (!string.Equals(
                proposal.ToolName,
                prepared.ToolName,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared file action no longer matches its typed request and trusted scope.");
        }

        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh File Viewer target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            prepared.ToolName,
            resolved.Context.Target,
            targetIdentity,
            resolved.Context.BindingFingerprint,
            argumentDigest,
            proposal.PolicyGeneration);
    }

    /// <summary>
    /// Materializes a request path without string path parsing or provider-root widening.
    /// SessionHost uses the same operation to create its exact provider request.
    /// </summary>
    public static FilePanelLocation ResolveLocation(
        FileSessionMetadata metadata,
        ImmutableArray<FilePanelPathSegment> relativePath)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var root = RequireGovernedRoot(metadata);
        var relative = RequireRelativePath(relativePath);
        var resolvedPath = FilePanelPath.FromSegments(
            root.Path.Segments.Concat(relative));
        return new FilePanelLocation(
            metadata.TrustedRoot.ProviderProfileId,
            metadata.TrustedRoot.Authority,
            new FilePanelAddress.Hierarchical(resolvedPath));
    }

    public static int GetEffectiveListPageSize(FileSessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Math.Min(metadata.MaximumListPageSize, MaximumAgentListPageSize);
    }

    public static long GetEffectiveReadMaximumBytes(FileSessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Math.Min(metadata.MaximumPreviewBytes, MaximumAgentReadBytes);
    }

    public static bool SupportsProviderCapability(
        string toolName,
        FilePanelCapability available,
        FilePanelCapability required) =>
        toolName switch
        {
            BuiltInAgentTools.FilesAccessRead =>
                (available & (
                    FilePanelCapability.Permissions
                    | FilePanelCapability.AccessControlLists)) != FilePanelCapability.None,
            BuiltInAgentTools.FilesTransfers => true,
            _ => available.HasFlag(required),
        };

    private static OperationDescriptor DescribeOperation(AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List list => new(
                BuiltInAgentTools.FilesList,
                SessionCapabilities.FilesList,
                FilePanelCapability.List,
                list.SessionId),
            AgentFileRequest.Search search => new(
                BuiltInAgentTools.FilesSearch,
                SessionCapabilities.FilesSearch,
                FilePanelCapability.Search,
                search.SessionId),
            AgentFileRequest.Stat stat => new(
                BuiltInAgentTools.FilesStat,
                SessionCapabilities.FilesStat,
                FilePanelCapability.Stat,
                stat.SessionId),
            AgentFileRequest.Read read => new(
                BuiltInAgentTools.FilesRead,
                SessionCapabilities.FilesPreview,
                FilePanelCapability.RangedRead,
                read.SessionId),
            AgentFileRequest.AccessRead accessRead => new(
                BuiltInAgentTools.FilesAccessRead,
                SessionCapabilities.FilesReadAccessControl,
                FilePanelCapability.None,
                accessRead.SessionId),
            AgentFileRequest.Transfers transfers => new(
                BuiltInAgentTools.FilesTransfers,
                SessionCapabilities.FilesTransfersRead,
                FilePanelCapability.None,
                transfers.SessionId),
            AgentFileRequest.CreateDirectory createDirectory => new(
                BuiltInAgentTools.FilesCreateDirectory,
                SessionCapabilities.FilesCreateDirectory,
                FilePanelCapability.CreateDirectory
                | FilePanelCapability.GovernedCreateDirectory,
                createDirectory.SessionId),
            AgentFileRequest.CreateText createText => new(
                GovernedFileToolNames.CreateText,
                GovernedFileToolNames.SessionWrite,
                FilePanelCapability.StreamingWrite
                | FilePanelCapability.GovernedCreateFile,
                createText.SessionId),
            AgentFileRequest.ReplaceText replaceText => new(
                GovernedFileToolNames.ReplaceText,
                GovernedFileToolNames.SessionWrite,
                FilePanelCapability.StreamingWrite
                | FilePanelCapability.GovernedReplaceFile,
                replaceText.SessionId),
            AgentFileRequest.Copy copy => new(
                GovernedFileToolNames.Copy,
                GovernedFileToolNames.SessionCopy,
                FilePanelCapability.Copy
                | FilePanelCapability.GovernedCopySource
                | FilePanelCapability.GovernedCopy,
                copy.SessionId),
            AgentFileRequest.Move move => new(
                BuiltInAgentTools.FilesMove,
                SessionCapabilities.FilesRename,
                FilePanelCapability.Rename
                | FilePanelCapability.GovernedRename,
                move.SessionId),
            AgentFileRequest.Delete delete => new(
                BuiltInAgentTools.FilesDelete,
                SessionCapabilities.FilesDelete,
                FilePanelCapability.Delete
                | FilePanelCapability.GovernedDelete,
                delete.SessionId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The agent file request kind is not supported."),
        };

    private static PreparedRequest PrepareRequest(
        AgentFileRequest request,
        OperationDescriptor operation,
        AgentContextPanel panel)
    {
        var metadata = panel.FileMetadata
            ?? throw new ArgumentException(
                "The File Viewer session has no trusted provider scope.",
                nameof(panel));
        var relativePath = RequestPath(request);
        if (request is AgentFileRequest.Read
            or AgentFileRequest.CreateDirectory
            or AgentFileRequest.CreateText
            or AgentFileRequest.ReplaceText
            or AgentFileRequest.Copy
            or AgentFileRequest.Move
            or AgentFileRequest.Delete)
        {
            RequireNonRootPath(relativePath);
        }

        _ = ResolveLocation(metadata, relativePath);
        if (request is AgentFileRequest.Move move)
        {
            RequireNonRootPath(move.DestinationRelativePath);
            _ = ResolveLocation(metadata, move.DestinationRelativePath);
            if (move.DestinationRelativePath.SequenceEqual(move.RelativePath))
            {
                throw new ArgumentException(
                    "A governed file move requires different source and destination paths.",
                    nameof(request));
            }
        }
        if (request is AgentFileRequest.Copy copyRequest)
        {
            RequireNonRootPath(copyRequest.DestinationRelativePath);
            _ = ResolveLocation(metadata, copyRequest.DestinationRelativePath);
            if (copyRequest.DestinationRelativePath.SequenceEqual(copyRequest.RelativePath))
            {
                throw new ArgumentException(
                    "A governed file copy requires different source and destination paths.",
                    nameof(request));
            }
        }
        if (request is AgentFileRequest.Search search)
        {
            _ = RequireSearchQuery(search.Query);
            if (!Enum.IsDefined(search.Scope)
                || search.MaximumResults is < 1 or > MaximumAgentSearchResults)
            {
                throw new ArgumentException(
                    "The agent file search bounds are invalid.",
                    nameof(request));
            }
        }
        var root = (FilePanelAddress.Hierarchical)metadata.TrustedRoot.Address;
        var rootDisplay = DisplayPath(root.Path.Segments, isRelative: false);
        var relativeDisplay = DisplayPath(relativePath, isRelative: true);
        var sessionRevision = panel.SessionRevision
            ?? throw new ArgumentException(
                "The File Viewer session has no live revision.",
                nameof(panel));
        var arguments = new List<MaterialArgument>
        {
            Argument("session_id", RequireIdentifier(operation.SessionId.Value, "session ID")),
            Argument("session_revision", Invariant(sessionRevision)),
            Argument("provider_profile_id", metadata.TrustedRoot.ProviderProfileId),
            Argument(
                "authority",
                metadata.TrustedRoot.Authority ?? string.Empty,
                metadata.TrustedRoot.Authority ?? "<none>"),
            Argument("trusted_root", rootDisplay),
            Argument("relative_path", relativeDisplay),
        };

        switch (request)
        {
            case AgentFileRequest.List:
                arguments.Add(Argument(
                    "page_size",
                    Invariant(GetEffectiveListPageSize(metadata))));
                arguments.Add(Argument("first_page_only", "true"));
                arguments.Add(Argument("show_hidden", "false"));
                break;
            case AgentFileRequest.Search searchRequest:
                arguments.Add(Argument("query", searchRequest.Query));
                arguments.Add(Argument(
                    "scope",
                    SearchScopeName(searchRequest.Scope)));
                arguments.Add(Argument(
                    "maximum_results",
                    Invariant(searchRequest.MaximumResults)));
                arguments.Add(Argument("show_hidden", "false"));
                break;
            case AgentFileRequest.Read:
                arguments.Add(Argument(
                    "maximum_bytes",
                    Invariant(GetEffectiveReadMaximumBytes(metadata))));
                arguments.Add(Argument(
                    "preview_kinds",
                    "text,structured_text"));
                break;
            case AgentFileRequest.AccessRead:
                arguments.Add(Argument("observation", "access_control"));
                arguments.Add(Argument(
                    "maximum_grants",
                    Invariant(MaximumAgentAccessGrants)));
                break;
            case AgentFileRequest.Transfers:
                arguments.Add(Argument("owned_by_session", "true"));
                arguments.Add(Argument(
                    "maximum_results",
                    Invariant(MaximumAgentTransfers)));
                break;
            case AgentFileRequest.CreateDirectory:
                arguments.Add(Argument("effect", "create_directory"));
                arguments.Add(Argument("precondition", "must_not_exist"));
                break;
            case AgentFileRequest.CreateText createText:
                AddTextMutationArguments(arguments, createText.Content, "create_text");
                arguments.Add(Argument("precondition", "must_not_exist"));
                break;
            case AgentFileRequest.ReplaceText replaceText:
                AddTextMutationArguments(arguments, replaceText.Content, "replace_text");
                arguments.Add(Argument("entry_ref", replaceText.EntryReference.Value));
                arguments.Add(Argument("precondition", "version_matches"));
                break;
            case AgentFileRequest.Copy copy:
                arguments.Add(Argument("entry_ref", copy.EntryReference.Value));
                arguments.Add(Argument(
                    "destination_relative_path",
                    DisplayPath(copy.DestinationRelativePath, isRelative: true)));
                arguments.Add(Argument("effect", "copy"));
                arguments.Add(Argument("maximum_bytes", Invariant(MaximumAgentCopyBytes)));
                arguments.Add(Argument("destination_precondition", "must_not_exist"));
                break;
            case AgentFileRequest.Move moveRequest:
                arguments.Add(Argument(
                    "entry_ref",
                    RequireEntryReference(moveRequest.EntryReference, nameof(request)).Value));
                arguments.Add(Argument(
                    "destination_relative_path",
                    DisplayPath(moveRequest.DestinationRelativePath, isRelative: true)));
                arguments.Add(Argument("effect", "move_or_rename"));
                arguments.Add(Argument("destination_precondition", "must_not_exist"));
                break;
            case AgentFileRequest.Delete delete:
                arguments.Add(Argument(
                    "entry_ref",
                    RequireEntryReference(delete.EntryReference, nameof(request)).Value));
                arguments.Add(Argument("effect", "permanent_delete"));
                arguments.Add(Argument("recursive", delete.Recursive ? "true" : "false"));
                arguments.Add(Argument("precondition", "must_exist"));
                break;
        }

        return new PreparedRequest(
            operation.ToolName,
            Array.AsReadOnly(arguments.ToArray()));
    }

    private static ImmutableArray<FilePanelPathSegment> RequestPath(
        AgentFileRequest request) =>
        request switch
        {
            AgentFileRequest.List list => list.RelativePath,
            AgentFileRequest.Search search => search.RelativePath,
            AgentFileRequest.Stat stat => stat.RelativePath,
            AgentFileRequest.Read read => read.RelativePath,
            AgentFileRequest.AccessRead accessRead => accessRead.RelativePath,
            AgentFileRequest.Transfers => [],
            AgentFileRequest.CreateDirectory createDirectory =>
                createDirectory.RelativePath,
            AgentFileRequest.CreateText createText => createText.RelativePath,
            AgentFileRequest.ReplaceText replaceText => replaceText.RelativePath,
            AgentFileRequest.Copy copy => copy.RelativePath,
            AgentFileRequest.Move move => move.RelativePath,
            AgentFileRequest.Delete delete => delete.RelativePath,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The agent file request kind is not supported."),
        };

    private static void AddTextMutationArguments(
        ICollection<MaterialArgument> arguments,
        string content,
        string effect)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = StrictUtf8.GetBytes(content);
        try
        {
            if (bytes.Length > MaximumAgentTextBytes)
            {
                throw new ArgumentException(
                    $"Agent text content cannot exceed {MaximumAgentTextBytes} UTF-8 bytes.",
                    nameof(content));
            }

            if (content.EnumerateRunes().Any(rune =>
                    Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator
                    || (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control
                        && rune.Value is not '\t' and not '\n' and not '\r')))
            {
                throw new ArgumentException(
                    "Agent text content contains unsupported control characters.",
                    nameof(content));
            }

            if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(content))
            {
                throw new ArgumentException(
                    "Agent text content appears to contain literal secret material.",
                    nameof(content));
            }

            arguments.Add(Argument("effect", effect));
            arguments.Add(Argument("content_bytes", Invariant(bytes.Length)));
            arguments.Add(Argument(
                "content_sha256",
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static AgentFileEntryReference RequireEntryReference(
        AgentFileEntryReference? reference,
        string parameterName) =>
        reference ?? throw new ArgumentException(
            "This file mutation requires an opaque reference from files.stat.",
            parameterName);

    private static ResolvedFileContext ResolveForPreparation(
        AgentContextSnapshot context,
        OperationDescriptor operation)
    {
        var panel = RequireMatchingFilePanel(context, operation);
        AgentTarget exactTarget;
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                RequireSinglePanelContext(context);
                ValidatePanelTarget(panelTarget, panel);
                exactTarget = panelTarget;
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                RequireSinglePanelContext(context);
                ValidateSessionTarget(sessionTarget, panel, operation.SessionId);
                exactTarget = sessionTarget;
                break;
            default:
                var narrowedPanel = ExactPanelTarget(panel);
                if (!panel.HasRegisteredGraph
                    || !panel.IsCurrentPanelSession
                    || !AgentTargetScope.Contains(context.Target, narrowedPanel))
                {
                    throw new ArgumentException(
                        "The matching File Viewer session is stale or outside the resolved target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
        }

        return new ResolvedFileContext(
            new AgentContextSnapshot(
                exactTarget,
                [panel],
                context.CapturedAtUtc),
            panel);
    }

    private static ResolvedFileContext ResolveForExecution(
        AgentContextSnapshot context,
        OperationDescriptor operation)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingFilePanel(context, operation);
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                ValidatePanelTarget(panelTarget, panel);
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                ValidateSessionTarget(sessionTarget, panel, operation.SessionId);
                break;
            default:
                throw new ArgumentException(
                    "Execution binding requires a freshly resolved exact File Viewer target.",
                    nameof(context));
        }

        return new ResolvedFileContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingFilePanel(
        AgentContextSnapshot context,
        OperationDescriptor operation)
    {
        var matches = context.Panels
            .Where(panel => panel.SessionId == operation.SessionId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching File Viewer session.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.FileViewer)
        {
            throw new ArgumentException(
                "An agent file action cannot target a non-File Viewer panel.",
                nameof(context));
        }

        if (panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "An agent file action requires an active File Viewer session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(
                operation.SessionCapability,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The File Viewer session does not support "
                + $"'{operation.SessionCapability}'.",
                nameof(context));
        }

        var metadata = panel.FileMetadata
            ?? throw new ArgumentException(
                "The File Viewer session has no trusted provider scope.",
                nameof(context));
        if (!SupportsProviderCapability(
                operation.ToolName,
                metadata.Capabilities,
                operation.ProviderCapability))
        {
            throw new ArgumentException(
                $"The file provider does not support "
                + $"'{operation.ProviderCapability}'.",
                nameof(context));
        }

        _ = RequireGovernedRoot(metadata);
        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact File Viewer target must resolve to one panel/session.",
                nameof(context));
        }
    }

    private static void ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target.WindowId != panel.WindowId
            || target.WorkspaceId != panel.WorkspaceId
            || target.TabId != panel.TabId
            || target.PanelId != panel.PanelId
            || !panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession)
        {
            throw new ArgumentException(
                "The resolved File Viewer owner is stale or does not match the exact panel target.",
                nameof(target));
        }
    }

    private static void ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel,
        SessionId requestSessionId)
    {
        if (target.SessionId != requestSessionId
            || (panel.HasRegisteredGraph && !panel.IsCurrentPanelSession))
        {
            throw new ArgumentException(
                "The resolved File Viewer owner is stale or does not match the exact session target.",
                nameof(target));
        }
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId);

    private static AgentApprovalPresentation CreatePresentation(
        AgentTarget target,
        AgentContextPanel panel,
        IReadOnlyList<MaterialArgument> arguments)
    {
        var metadata = panel.FileMetadata!;
        var targetTitle = target switch
        {
            AgentTarget.Panel exactPanel =>
                $"{EscapeForApproval(panel.PanelTitle ?? "File Viewer")} — panel "
                + $"{EscapeForApproval(exactPanel.PanelId.Value)} — session "
                + EscapeForApproval(panel.SessionId!.Value.Value),
            AgentTarget.ConnectionSession exactSession =>
                $"{EscapeForApproval(panel.PanelTitle ?? "File Viewer")} — session "
                + $"{EscapeForApproval(exactSession.SessionId.Value)} — panel "
                + EscapeForApproval(panel.PanelId.Value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The approval target kind is not supported."),
        };
        var host = metadata.TrustedRoot.Authority is { } authority
            ? $"File provider {EscapeForApproval(metadata.TrustedRoot.ProviderProfileId)} "
              + $"({EscapeForApproval(authority)})"
            : $"File provider {EscapeForApproval(metadata.TrustedRoot.ProviderProfileId)}";
        var approvalArguments = arguments
            .Select(argument => new AgentApprovalArgument(
                argument.Name,
                EscapeForApproval(
                    argument.ApprovalDisplayValue ?? argument.Value),
                AgentApprovalArgument.MaximumEscapedValueBytes))
            .ToArray();
        return new AgentApprovalPresentation(
            targetTitle,
            host,
            workingDirectory: null,
            approvalArguments);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        string toolName,
        IReadOnlyList<MaterialArgument> arguments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-file-action");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, toolName);
        AppendCanonical(hash, Invariant(arguments.Count));
        foreach (var argument in arguments)
        {
            AppendCanonical(hash, argument.Name);
            AppendCanonical(hash, argument.Value);
        }

        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCanonical(IncrementalHash hash, string value)
    {
        var byteCount = GetStrictUtf8ByteCount(value, nameof(value));
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);

        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static MaterialArgument Argument(string name, string value) =>
        Argument(name, value, approvalDisplayValue: null);

    private static MaterialArgument Argument(
        string name,
        string value,
        string? approvalDisplayValue)
    {
        _ = GetStrictUtf8ByteCount(name, nameof(name));
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        if (approvalDisplayValue is not null)
        {
            _ = GetStrictUtf8ByteCount(
                approvalDisplayValue,
                nameof(approvalDisplayValue));
        }

        return new MaterialArgument(
            string.Concat(name),
            string.Concat(value),
            approvalDisplayValue is null
                ? null
                : string.Concat(approvalDisplayValue));
    }

    private static FilePanelAddress.Hierarchical RequireGovernedRoot(
        FileSessionMetadata metadata)
    {
        if (metadata.TrustedRoot.Version is not null
            || metadata.TrustedRoot.Address
                is not FilePanelAddress.Hierarchical hierarchical)
        {
            throw new ArgumentException(
                "Governed file tools require a versionless hierarchical trusted root.",
                nameof(metadata));
        }

        _ = RequirePathSegments(
            hierarchical.Path.Segments,
            FileSessionMetadata.MaximumTrustedRootSegments,
            FileSessionMetadata.MaximumTrustedRootBytes,
            "trusted file root");
        if (metadata.TrustedRoot.Authority is { } authority)
        {
            if (GetStrictUtf8ByteCount(authority, nameof(metadata)) > 256)
            {
                throw new ArgumentException(
                    "The file authority must be bounded for governed use.",
                    nameof(metadata));
            }

            RequirePrintableNonSecret(authority, "file authority");
        }

        return hierarchical;
    }

    private static ImmutableArray<FilePanelPathSegment> RequireRelativePath(
        ImmutableArray<FilePanelPathSegment> relativePath)
    {
        if (relativePath.IsDefault)
        {
            throw new ArgumentException(
                "An agent file path must be an initialized segment array.",
                nameof(relativePath));
        }

        for (var index = 0; index < relativePath.Length; index++)
        {
            var value = relativePath[index].Value;
            if (value?.Contains('\\', StringComparison.Ordinal) == true
                || (index == 0
                    && value is { Length: >= 2 }
                    && char.IsAsciiLetter(value[0])
                    && value[1] == ':'))
            {
                throw new ArgumentException(
                    "An agent file path must be relative and cannot contain "
                    + "Windows separators or a drive prefix.",
                    nameof(relativePath));
            }
        }

        return RequirePathSegments(
            relativePath,
            MaximumRelativePathSegments,
            MaximumRelativePathBytes,
            "relative file path");
    }

    private static void RequireNonRootPath(
        ImmutableArray<FilePanelPathSegment> relativePath)
    {
        if (relativePath.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "This agent file operation requires a non-root relative path.",
                nameof(relativePath));
        }
    }

    private static ImmutableArray<FilePanelPathSegment> RequirePathSegments(
        ImmutableArray<FilePanelPathSegment> segments,
        int maximumSegments,
        int maximumBytes,
        string label)
    {
        if (segments.Length > maximumSegments)
        {
            throw new ArgumentException(
                $"The {label} cannot contain more than {maximumSegments} segments.",
                nameof(segments));
        }

        var totalBytes = 0;
        foreach (var segment in segments)
        {
            var value = segment.Value;
            if (string.IsNullOrEmpty(value)
                || value is "." or ".."
                || value.Contains('/', StringComparison.Ordinal)
                || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The {label} contains an invalid segment.",
                    nameof(segments));
            }

            var byteCount = GetStrictUtf8ByteCount(value, nameof(segments));
            if (byteCount > MaximumPathSegmentBytes)
            {
                throw new ArgumentException(
                    $"A {label} segment cannot exceed "
                    + $"{MaximumPathSegmentBytes} UTF-8 bytes.",
                    nameof(segments));
            }

            RequirePrintableNonSecret(value, label);
            totalBytes = checked(totalBytes + byteCount + (totalBytes == 0 ? 0 : 1));
            if (totalBytes > maximumBytes)
            {
                throw new ArgumentException(
                    $"The {label} cannot exceed {maximumBytes} UTF-8 bytes.",
                    nameof(segments));
            }
        }

        return segments;
    }

    private static void RequirePrintableNonSecret(string value, string label)
    {
        if (value.EnumerateRunes().Any(rune =>
                Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator))
        {
            throw new ArgumentException(
                $"The {label} must be printable.",
                label);
        }

        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                $"The {label} appears to contain literal secret material.",
                label);
        }
    }

    private static string RequireIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || GetStrictUtf8ByteCount(value, label) > 256)
        {
            throw new ArgumentException(
                $"The agent file {label} must be printable and bounded.",
                label);
        }

        return string.Concat(value);
    }

    private static string DisplayPath(
        IEnumerable<FilePanelPathSegment> segments,
        bool isRelative)
    {
        var values = segments.Select(segment => segment.Value).ToArray();
        if (values.Length == 0)
        {
            return isRelative ? "." : "/";
        }

        var path = string.Join('/', values);
        return isRelative ? path : $"/{path}";
    }

    private static int GetStrictUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Agent file material must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }

    private static string EscapeForApproval(string value)
    {
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\0':
                    builder.Append(@"\0");
                    break;
                case '\a':
                    builder.Append(@"\a");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    if (Rune.GetUnicodeCategory(rune) is
                        UnicodeCategory.Control
                        or UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator)
                    {
                        builder
                            .Append(rune.IsBmp ? @"\u" : @"\U")
                            .Append(rune.Value.ToString(
                                rune.IsBmp ? "X4" : "X8",
                                CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string RequireSearchQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || GetStrictUtf8ByteCount(value, nameof(value))
                > MaximumAgentSearchQueryBytes)
        {
            throw new ArgumentException(
                "An agent file search query must be printable and bounded.",
                nameof(value));
        }

        RequirePrintableNonSecret(value, "file search query");
        return string.Concat(value);
    }

    private static string SearchScopeName(FilePanelDiscoveryScope scope) =>
        scope switch
        {
            FilePanelDiscoveryScope.CurrentDirectory => "current_directory",
            FilePanelDiscoveryScope.Subtree => "subtree",
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                null),
        };

    private sealed record OperationDescriptor(
        string ToolName,
        string SessionCapability,
        FilePanelCapability ProviderCapability,
        SessionId SessionId);

    private sealed record MaterialArgument(
        string Name,
        string Value,
        string? ApprovalDisplayValue);

    private sealed record PreparedRequest(
        string ToolName,
        IReadOnlyList<MaterialArgument> Arguments);

    private sealed record ResolvedFileContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}
