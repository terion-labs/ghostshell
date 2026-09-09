using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class GitAgentToolResultJson
{
    internal const string ContentOrigin = "untrusted_git";
    internal const string MutationOutcomeUnknownStableCode =
        "git_mutation_outcome_unknown";
    private const string Redaction = "[REDACTED SECRET VALUE]";

    public static GitAgentToolJsonProjection Project(
        GitAgentOperationResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        writer.WriteString("content_origin", ContentOrigin);
        var redactions = WriteResult(writer, result);
        writer.WriteNumber("redaction_count", redactions);
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > Asura.Agent.AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("git_result_too_large", panelId);
        }

        return new GitAgentToolJsonProjection(
            true,
            SuccessCode(result),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(HostError error, PanelInstanceId? panelId = null) =>
        AgentToolResultJson.Failure(ProviderStableCode(error), retryable: false, panelId);

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.StableCode.StartsWith("git_", StringComparison.Ordinal)
            || error.StableCode is
                "authority_revoked"
                or "session_revoked"
                or "caller_cancelled"
            || string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest
                or HostErrorCode.NotFound
                or HostErrorCode.RevisionConflict => "target_changed",
            HostErrorCode.UnsupportedProtocol
                or HostErrorCode.CapabilityNotSupported
                or HostErrorCode.SessionClosed => "git_operation_unavailable",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => "cancelled",
            _ => "git_action_failed",
        };
    }

    private static int WriteResult(Utf8JsonWriter writer, GitAgentOperationResult result)
    {
        var redactions = 0;
        switch (result)
        {
            case GitAgentOperationResult.State state:
                WriteState(writer, state.Value, ref redactions);
                break;
            case GitAgentOperationResult.Diff diff:
                WriteDiff(writer, diff.Value, ref redactions);
                break;
            case GitAgentOperationResult.RemoteRef remote:
                WriteRemote(writer, remote.Value, ref redactions);
                break;
            case GitAgentOperationResult.Mutation mutation:
                WriteMutation(writer, mutation.Value, ref redactions);
                break;
            default:
                throw new ArgumentException(
                    "Rejected or unknown Git outcomes cannot be projected as success.",
                    nameof(result));
        }

        return redactions;
    }

    private static void WriteState(
        Utf8JsonWriter writer,
        GitAgentStateSnapshot value,
        ref int redactions)
    {
        WriteOptional(writer, "state_ref", value.StateReference?.Value);
        writer.WriteString("repository", Screen(value.RepositoryLabel, ref redactions));
        writer.WriteString("connection", Screen(value.ConnectionLabel, ref redactions));
        WriteOptionalScreened(writer, "current_branch", value.CurrentBranch, ref redactions);
        WriteOptional(writer, "head_sha", value.HeadSha);
        writer.WriteBoolean("detached", value.IsDetached);
        writer.WriteBoolean("unborn", value.IsUnborn);
        writer.WriteBoolean("has_conflicts", value.HasConflicts);
        writer.WriteBoolean("dirty", value.IsDirty);
        writer.WriteBoolean("truncated", value.IsTruncated);
        writer.WriteBoolean("mutations_quarantined", value.MutationsQuarantined);
        writer.WriteStartArray("changes");
        foreach (var change in value.Changes)
        {
            writer.WriteStartObject();
            writer.WriteString("change_ref", change.Reference.Value);
            writer.WriteString("path", Screen(change.DisplayPath, ref redactions));
            writer.WriteString("kind", change.Kind.ToString().ToLowerInvariant());
            writer.WriteString("area", change.Area.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("branches");
        foreach (var branch in value.Branches)
        {
            writer.WriteStartObject();
            writer.WriteString("branch_ref", branch.Reference.Value);
            writer.WriteString("name", Screen(branch.Name, ref redactions));
            writer.WriteString("sha", branch.Sha);
            writer.WriteBoolean("current", branch.IsCurrent);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("remotes");
        foreach (var remote in value.Remotes)
        {
            writer.WriteStartObject();
            writer.WriteString("remote_ref", remote.Reference.Value);
            writer.WriteString("name", Screen(remote.Name, ref redactions));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("captured_at_utc", value.CapturedAtUtc);
    }

    private static void WriteDiff(
        Utf8JsonWriter writer,
        GitAgentDiffSnapshot value,
        ref int redactions)
    {
        writer.WriteString("path", Screen(value.DisplayPath, ref redactions));
        if (value.Text is null)
        {
            writer.WriteNull("text");
        }
        else
        {
            writer.WriteString("text", Screen(value.Text, ref redactions));
        }

        writer.WriteBoolean("binary", value.IsBinary);
        writer.WriteBoolean("truncated", value.IsTruncated);
        writer.WriteBoolean("sensitive", value.IsSensitive);
        writer.WriteNumber("line_count", value.LineCount);
        writer.WriteNumber("hunk_count", value.HunkCount);
    }

    private static void WriteRemote(
        Utf8JsonWriter writer,
        GitAgentRemoteRefSnapshot value,
        ref int redactions)
    {
        writer.WriteString("remote_state_ref", value.Reference.Value);
        writer.WriteString("remote", Screen(value.RemoteName, ref redactions));
        writer.WriteString("destination_branch", Screen(value.DestinationBranch, ref redactions));
        WriteOptional(writer, "sha", value.Sha);
        writer.WriteBoolean("absent", value.IsAbsent);
        writer.WriteString("captured_at_utc", value.CapturedAtUtc);
    }

    private static void WriteMutation(
        Utf8JsonWriter writer,
        GitAgentMutationReceipt value,
        ref int redactions)
    {
        writer.WriteString("operation", value.Operation);
        WriteOptional(writer, "state_ref", value.StateReference?.Value);
        WriteOptional(writer, "head_sha", value.HeadSha);
        WriteOptionalScreened(writer, "branch", value.BranchName, ref redactions);
        WriteOptionalScreened(writer, "remote", value.RemoteName, ref redactions);
        WriteOptional(writer, "remote_sha", value.RemoteSha);
        writer.WriteNumber("changed_path_count", value.ChangedPathCount);
    }

    private static string Screen(string value, ref int redactions)
    {
        if (!AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            return value;
        }

        redactions++;
        return Redaction;
    }

    private static void WriteOptionalScreened(
        Utf8JsonWriter writer,
        string name,
        string? value,
        ref int redactions)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, Screen(value, ref redactions));
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string SuccessCode(GitAgentOperationResult result) => result switch
    {
        GitAgentOperationResult.State => "git_state_read",
        GitAgentOperationResult.Diff => "git_diff_read",
        GitAgentOperationResult.RemoteRef => "git_remote_ref_read",
        GitAgentOperationResult.Mutation => "git_mutation_completed",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static GitAgentToolJsonProjection Rejected(
        string stableCode,
        PanelInstanceId? panelId) =>
        new(
            false,
            stableCode,
            AgentToolResultJson.Failure(stableCode, retryable: false, panelId));
}

internal sealed record GitAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
