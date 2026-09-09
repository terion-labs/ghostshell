using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Narrows a run scope to one exact hosted Database Viewer, binds a closed
/// relational/Redis request to authorization evidence, and projects hostile
/// session output through bounded result constructors.
/// </summary>
public sealed partial class AgentDatabaseReadActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentDatabaseReadAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentDatabaseReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var resolved = ResolveForPreparation(context, request);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            request.ToolName,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreatePresentation(resolved.Panel, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentDatabaseReadAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentDatabaseReadAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        var resolved = ResolveForExecution(freshContext, action.Request);
        ValidatePreparedAction(action);
        var proposal = action.Proposal;
        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh database target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            action.Request.ToolName,
            resolved.Context.Target,
            targetIdentity,
            resolved.Context.BindingFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }

    private static ResolvedDatabaseContext ResolveForPreparation(
        AgentContextSnapshot context,
        AgentDatabaseReadRequest request)
    {
        var panel = RequireMatchingDatabasePanel(context, request);
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
                ValidateSessionTarget(sessionTarget, panel);
                exactTarget = sessionTarget;
                break;
            case AgentTarget.OpenTab:
            case AgentTarget.Workspace:
                var narrowedPanel = ExactPanelTarget(panel);
                if (!AgentTargetScope.Contains(context.Target, narrowedPanel))
                {
                    throw new ArgumentException(
                        "The database panel is outside the resolved run target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
            default:
                throw new ArgumentException(
                    "A database read requires an exact panel/session, tab, or workspace target.",
                    nameof(context));
        }

        return new ResolvedDatabaseContext(
            new AgentContextSnapshot(exactTarget, [panel], context.CapturedAtUtc),
            panel);
    }

    private static ResolvedDatabaseContext ResolveForExecution(
        AgentContextSnapshot context,
        AgentDatabaseReadRequest request)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingDatabasePanel(context, request);
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                ValidatePanelTarget(panelTarget, panel);
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                ValidateSessionTarget(sessionTarget, panel);
                break;
            default:
                throw new ArgumentException(
                    "Execution binding requires a freshly resolved exact database target.",
                    nameof(context));
        }

        return new ResolvedDatabaseContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingDatabasePanel(
        AgentContextSnapshot context,
        AgentDatabaseReadRequest request)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == request.PanelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching database panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.DatabaseViewer
            || !panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "A database read requires one current active Database Viewer session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(
                request.RequiredSessionCapability,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The database session does not support this read operation.",
                nameof(context));
        }

        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact database target must resolve to one panel/session.",
                nameof(context));
        }
    }

    private static void ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target != ExactPanelTarget(panel))
        {
            throw new ArgumentException(
                "The resolved graph owner does not match the exact database panel target.",
                nameof(target));
        }
    }

    private static void ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel)
    {
        if (panel.SessionId is not { } sessionId || target.SessionId != sessionId)
        {
            throw new ArgumentException(
                "The resolved graph owner does not match the exact database session target.",
                nameof(target));
        }
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(panel.WindowId, panel.WorkspaceId, panel.TabId, panel.PanelId);

    private static AgentApprovalPresentation CreatePresentation(
        AgentContextPanel panel,
        AgentDatabaseReadRequest request)
    {
        var arguments = new List<AgentApprovalArgument>
        {
            new("panel_id", panel.PanelId.Value),
            new("operation", request.ToolName),
        };
        switch (request)
        {
            case AgentDatabaseReadRequest.ListObjects value:
                arguments.Add(new("maximum_objects", value.MaximumObjects.ToString(
                    CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.DescribeObject value:
                arguments.Add(new("object_ref", value.Reference.Value));
                break;
            case AgentDatabaseReadRequest.ReadTable value:
                arguments.Add(new("object_ref", value.Reference.Value));
                arguments.Add(new("offset", value.Offset.ToString(CultureInfo.InvariantCulture)));
                arguments.Add(new("limit", value.Limit.ToString(CultureInfo.InvariantCulture)));
                arguments.Add(new("filter_count", value.Filters.Count.ToString(
                    CultureInfo.InvariantCulture)));
                arguments.Add(new("sort_count", value.Sorts.Count.ToString(
                    CultureInfo.InvariantCulture)));
                arguments.Add(new("column_count", value.Columns.Count.ToString(
                    CultureInfo.InvariantCulture)));
                arguments.Add(new("excluded_column_count", value.ExcludeColumns.Count.ToString(
                    CultureInfo.InvariantCulture)));
                arguments.Add(new("maximum_cell_bytes", value.MaximumCellBytes.ToString(
                    CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.SchemaGraph value:
                arguments.Add(new("maximum_objects", value.MaximumObjects.ToString(
                    CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.RedisScan value:
                arguments.Add(new("count", value.Count.ToString(CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.RedisRead value:
                arguments.Add(new("key_ref", value.Reference.Value));
                arguments.Add(new("maximum_entries", value.MaximumEntries.ToString(
                    CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.RedisSearch value:
                arguments.Add(new("limit", value.Limit.ToString(CultureInfo.InvariantCulture)));
                break;
            case AgentDatabaseReadRequest.RedisListIndexes value:
                arguments.Add(new("maximum_indexes", value.MaximumIndexes.ToString(
                    CultureInfo.InvariantCulture)));
                break;
        }

        return new AgentApprovalPresentation(
            "Database read",
            panel.PanelTitle ?? "Database Viewer",
            workingDirectory: null,
            arguments);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentDatabaseReadRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-database-read-action");
        AppendCanonical(hash, "2");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, request.ToolName);
        AppendCanonical(hash, request.PanelId.Value);
        switch (request)
        {
            case AgentDatabaseReadRequest.ReadState:
                break;
            case AgentDatabaseReadRequest.ListObjects value:
                AppendCanonical(hash, value.MaximumObjects);
                break;
            case AgentDatabaseReadRequest.DescribeObject value:
                AppendCanonical(hash, value.Reference.Value);
                break;
            case AgentDatabaseReadRequest.ReadTable value:
                AppendCanonical(hash, value.Reference.Value);
                AppendCanonical(hash, value.Offset);
                AppendCanonical(hash, value.Limit);
                AppendCanonical(hash, value.Filters.Count);
                foreach (var filter in value.Filters)
                {
                    AppendCanonical(hash, filter.ColumnName);
                    AppendCanonical(hash, (int)filter.Operator);
                    AppendFilterValue(hash, filter.Value);
                }

                AppendCanonical(hash, value.Sorts.Count);
                foreach (var sort in value.Sorts)
                {
                    AppendCanonical(hash, sort.ColumnName);
                    AppendCanonical(hash, sort.Descending ? 1 : 0);
                }

                AppendCanonical(hash, value.Columns.Count);
                foreach (var column in value.Columns)
                {
                    AppendCanonical(hash, column);
                }

                AppendCanonical(hash, value.ExcludeColumns.Count);
                foreach (var column in value.ExcludeColumns)
                {
                    AppendCanonical(hash, column);
                }

                AppendCanonical(hash, value.MaximumCellBytes);

                break;
            case AgentDatabaseReadRequest.SchemaGraph value:
                AppendCanonical(hash, value.MaximumObjects);
                break;
            case AgentDatabaseReadRequest.RedisScan value:
                AppendCanonical(hash, value.Pattern);
                AppendCanonical(hash, value.Cursor ?? string.Empty);
                AppendCanonical(hash, value.Count);
                break;
            case AgentDatabaseReadRequest.RedisRead value:
                AppendCanonical(hash, value.Reference.Value);
                AppendCanonical(hash, value.MaximumEntries);
                break;
            case AgentDatabaseReadRequest.RedisSearch value:
                AppendCanonical(hash, value.Index);
                AppendCanonical(hash, value.Query);
                AppendCanonical(hash, value.Limit);
                break;
            case AgentDatabaseReadRequest.RedisListIndexes value:
                AppendCanonical(hash, value.MaximumIndexes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendFilterValue(
        IncrementalHash hash,
        AgentDatabaseFilterValue? value)
    {
        switch (value)
        {
            case null:
                AppendCanonical(hash, "none");
                break;
            case AgentDatabaseFilterValue.Text text:
                AppendCanonical(hash, "text");
                AppendCanonical(hash, text.Value);
                break;
            case AgentDatabaseFilterValue.Boolean boolean:
                AppendCanonical(hash, "boolean");
                AppendCanonical(hash, boolean.Value ? 1 : 0);
                break;
            case AgentDatabaseFilterValue.Integer integer:
                AppendCanonical(hash, "integer");
                AppendCanonical(hash, integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case AgentDatabaseFilterValue.Decimal number:
                AppendCanonical(hash, "decimal");
                AppendCanonical(hash, number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case AgentDatabaseFilterValue.List list:
                AppendCanonical(hash, "list");
                AppendCanonical(hash, list.Values.Count);
                foreach (var item in list.Values)
                {
                    AppendFilterValue(hash, item);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static void ValidatePreparedAction(AgentDatabaseReadAction action)
    {
        var digest = CreateArgumentDigest(action.Proposal.Id, action.Request);
        if (!string.Equals(
                action.Proposal.ToolName,
                action.Request.ToolName,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest != digest)
        {
            throw new InvalidOperationException(
                "The prepared database action no longer matches its typed request.");
        }
    }

    private static void AppendCanonical(IncrementalHash hash, int value) =>
        AppendCanonical(hash, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendCanonical(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record ResolvedDatabaseContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}
