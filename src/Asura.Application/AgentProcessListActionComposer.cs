using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Narrows a run scope to one exact local Process Monitor panel, binds its
/// request to authorization evidence, and projects hostile monitor output into
/// a bounded secret-minimized result.
/// </summary>
public sealed class AgentProcessListActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentProcessListAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentProcessListRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var resolved = ResolveForPreparation(context, request.PanelId);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            BuiltInAgentTools.ProcessesList,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreatePresentation(resolved.Panel, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentProcessListAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentProcessListAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var resolved = ResolveForExecution(
            freshContext,
            action.Request.PanelId);
        var proposal = action.Proposal;
        var argumentDigest = CreateArgumentDigest(
            proposal.Id,
            action.Request);
        if (!string.Equals(
                proposal.ToolName,
                BuiltInAgentTools.ProcessesList,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared process action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh process target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            BuiltInAgentTools.ProcessesList,
            resolved.Context.Target,
            targetIdentity,
            resolved.Context.BindingFingerprint,
            argumentDigest,
            proposal.PolicyGeneration);
    }

    /// <summary>
    /// The sole public construction seam for governed process results.
    /// Monitor output is treated as hostile even though it originated locally.
    /// </summary>
    public AgentProcessListResult Project(
        AgentProcessListAction action,
        ProcessMonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidatePreparedAction(action);

        if (snapshot.CapturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A process monitor capture timestamp must be UTC.",
                nameof(snapshot));
        }

        var source = snapshot.Processes
            ?? throw new ArgumentException(
                "A process monitor capture requires a process collection.",
                nameof(snapshot));
        var captured = CopyStableSource(source, action.Request.Limit);
        ValidateCounts(snapshot, captured.Length);
        var projected = captured
            .Select(process => ProjectEntry(process, nameof(snapshot)))
            .ToArray();
        if (projected
            .Select(process => process.ProcessId)
            .Distinct()
            .Count() != projected.Length)
        {
            throw new ArgumentException(
                "A process monitor capture contains duplicate process identifiers.",
                nameof(snapshot));
        }

        if (projected.Count(process => process.IsAsura) > 1)
        {
            throw new ArgumentException(
                "A process monitor capture cannot identify multiple Asura processes.",
                nameof(snapshot));
        }

        if (action.Request.ProcessId is { } processId
            && projected.Any(process => process.ProcessId != processId))
        {
            throw new ArgumentException(
                "A process monitor capture contains rows outside the authorized PID filter.",
                nameof(snapshot));
        }

        if (action.Request.NameContains is { } nameContains
            && captured.Any(process => process is null
                || !process.Name.Contains(
                nameContains,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "A process monitor capture contains rows outside the authorized name filter.",
                nameof(snapshot));
        }

        var result = new AgentProcessListResult(
            snapshot.CapturedAtUtc,
            Order(projected, action.Request.Sort),
            snapshot.EnumeratedProcessCount,
            snapshot.ObservedProcessCount,
            snapshot.IsTruncated,
            snapshot.MatchingProcessCount);
        EnsureProjectionBound(result);
        return result;
    }

    private static ProcessMonitorEntry?[] CopyStableSource(
        IReadOnlyList<ProcessMonitorEntry> source,
        int limit)
    {
        int count;
        try
        {
            count = source.Count;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException)
        {
            throw new ArgumentException(
                "The process monitor collection could not be read.",
                nameof(source),
                exception);
        }

        if (count < 0 || count > limit)
        {
            throw new ArgumentException(
                "A process monitor capture exceeds the authorized result limit.",
                nameof(source));
        }

        var captured = new ProcessMonitorEntry?[count];
        try
        {
            for (var index = 0; index < captured.Length; index++)
            {
                captured[index] = source[index];
            }

            if (source.Count != count)
            {
                throw new InvalidOperationException(
                    "The process monitor collection changed during projection.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException
                or IndexOutOfRangeException
                or InvalidOperationException
                or NotSupportedException)
        {
            throw new ArgumentException(
                "The process monitor collection changed during projection.",
                nameof(source),
                exception);
        }

        return captured;
    }

    private static ResolvedProcessContext ResolveForPreparation(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var panel = RequireMatchingProcessPanel(context, panelId);
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
                        "The Process Monitor panel is outside the resolved run target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
            default:
                throw new ArgumentException(
                    "A process observation requires an exact panel/session, tab, or workspace target.",
                    nameof(context));
        }

        return new ResolvedProcessContext(
            new AgentContextSnapshot(
                exactTarget,
                [panel],
                context.CapturedAtUtc),
            panel);
    }

    private static ResolvedProcessContext ResolveForExecution(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingProcessPanel(context, panelId);
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
                    "Execution binding requires a freshly resolved exact Process Monitor target.",
                    nameof(context));
        }

        return new ResolvedProcessContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingProcessPanel(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == panelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching Process Monitor panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.ProcessMonitor)
        {
            throw new ArgumentException(
                "A process observation cannot target a non-Process-Monitor panel.",
                nameof(context));
        }

        if (!panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "A process observation requires one current active graph session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(
                SessionCapabilities.ProcessesList,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The Process Monitor session does not support process listing.",
                nameof(context));
        }

        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact Process Monitor target must resolve to one panel/session.",
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
                "The resolved graph owner does not match the exact process panel target.",
                nameof(target));
        }
    }

    private static void ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel)
    {
        if (panel.SessionId is not { } sessionId
            || target.SessionId != sessionId)
        {
            throw new ArgumentException(
                "The resolved graph owner does not match the exact process session target.",
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
        AgentContextPanel panel,
        AgentProcessListRequest request) =>
        new(
            "Local Process Monitor",
            "Local host",
            workingDirectory: null,
            [
                new AgentApprovalArgument(
                    "panel_id",
                    EscapeForApproval(panel.PanelId.Value)),
                new AgentApprovalArgument(
                    "sort",
                    SortName(request.Sort)),
                new AgentApprovalArgument(
                    "limit",
                    request.Limit.ToString(CultureInfo.InvariantCulture)),
                new AgentApprovalArgument(
                    "offset",
                    request.Offset.ToString(CultureInfo.InvariantCulture)),
                .. request.NameContains is { } nameContains
                    ? new[]
                    {
                        new AgentApprovalArgument(
                            "name_contains",
                            EscapeForApproval(nameContains)),
                    }
                    : [],
                .. request.ProcessId is { } processId
                    ? new[]
                    {
                        new AgentApprovalArgument(
                            "pid",
                            processId.ToString(CultureInfo.InvariantCulture)),
                    }
                    : [],
            ]);

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentProcessListRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-process-list-action");
        AppendCanonical(hash, "2");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, BuiltInAgentTools.ProcessesList);
        AppendCanonical(hash, request.PanelId.Value);
        AppendCanonical(hash, SortName(request.Sort));
        AppendCanonical(
            hash,
            request.Limit.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(
            hash,
            request.Offset.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(hash, request.NameContains ?? string.Empty);
        AppendCanonical(
            hash,
            request.ProcessId?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty);
        return new AgentActionDigest(
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void ValidatePreparedAction(AgentProcessListAction action)
    {
        var digest = CreateArgumentDigest(
            action.Proposal.Id,
            action.Request);
        if (!string.Equals(
                action.Proposal.ToolName,
                BuiltInAgentTools.ProcessesList,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest != digest)
        {
            throw new InvalidOperationException(
                "The prepared process action no longer matches its typed request.");
        }
    }

    private static void AppendCanonical(
        IncrementalHash hash,
        string value)
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

    private static string SortName(ProcessMonitorSort sort) =>
        sort switch
        {
            ProcessMonitorSort.CpuDescending => "cpu_desc",
            ProcessMonitorSort.MemoryDescending => "memory_desc",
            ProcessMonitorSort.NameAscending => "name_asc",
            ProcessMonitorSort.ProcessIdAscending => "pid_asc",
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };

    private static AgentProcessListEntry ProjectEntry(
        ProcessMonitorEntry? source,
        string parameterName)
    {
        if (source is null)
        {
            throw new ArgumentException(
                "A process monitor capture cannot contain null entries.",
                parameterName);
        }

        try
        {
            return new AgentProcessListEntry(
                source.ProcessId,
                AgentProcessDisplayName.FromUntrusted(source.Name),
                source.CpuPercent,
                source.WorkingSetBytes,
                source.StartedAtUtc,
                source.IsAsura);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A process monitor capture contains invalid process metadata.",
                parameterName,
                exception);
        }
    }

    private static IReadOnlyList<AgentProcessListEntry> Order(
        IEnumerable<AgentProcessListEntry> processes,
        ProcessMonitorSort sort)
    {
        var ordered = sort switch
        {
            ProcessMonitorSort.CpuDescending => processes
                .OrderByDescending(process =>
                    process.ProcessorUsagePercent.HasValue)
                .ThenByDescending(process =>
                    process.ProcessorUsagePercent)
                .ThenBy(
                    process => process.Name.Text,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.MemoryDescending => processes
                .OrderByDescending(process =>
                    process.WorkingSetBytes.HasValue)
                .ThenByDescending(process =>
                    process.WorkingSetBytes)
                .ThenBy(
                    process => process.Name.Text,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.NameAscending => processes
                .OrderBy(
                    process => process.Name.Text,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId),
            ProcessMonitorSort.ProcessIdAscending => processes
                .OrderBy(process => process.ProcessId),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };
        return Array.AsReadOnly(ordered.ToArray());
    }

    private static void ValidateCounts(
        ProcessMonitorSnapshot snapshot,
        int returnedCount)
    {
        if (snapshot.EnumeratedProcessCount < 0
            || snapshot.ObservedProcessCount < 0
            || snapshot.ObservedProcessCount
                > snapshot.EnumeratedProcessCount
            || snapshot.MatchingProcessCount is < 0
            || snapshot.MatchingProcessCount
                > snapshot.ObservedProcessCount
            || returnedCount > snapshot.EnumeratedProcessCount)
        {
            throw new ArgumentException(
                "A process monitor capture contains inconsistent counts.",
                nameof(snapshot));
        }
    }

    private static void EnsureProjectionBound(AgentProcessListResult result)
    {
        var bytes = 2 * 1024;
        foreach (var process in result.Processes)
        {
            bytes = checked(bytes + 224);
            bytes = checked(
                bytes
                + JsonEncodedText
                    .Encode(process.Name.Text)
                    .EncodedUtf8Bytes
                    .Length);
        }

        if (bytes > AgentProcessListResult.MaximumProjectionBytes)
        {
            throw new ArgumentException(
                "The governed process projection exceeds 64 KiB.",
                nameof(result));
        }
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
                "Agent process material must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }

    private static string EscapeForApproval(string value)
    {
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
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
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                default:
                    var category = char.GetUnicodeCategory(character);
                    if (char.IsControl(character)
                        || category is
                            UnicodeCategory.Format
                            or UnicodeCategory.LineSeparator
                            or UnicodeCategory.ParagraphSeparator)
                    {
                        builder.Append(@"\u");
                        builder.Append(
                            ((int)character).ToString(
                                "X4",
                                CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record ResolvedProcessContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}
