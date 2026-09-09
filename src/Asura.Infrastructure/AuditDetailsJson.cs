using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Defines the only audit-detail JSON shapes accepted on disk. The explicit codec keeps the
/// persistence boundary closed even if application-wide JSON settings change later.
/// </summary>
internal static class AuditDetailsJson
{
    private const int PreviousSchemaVersion = 1;
    private const int IntermediateSchemaVersion = 2;
    private const int CurrentSchemaVersion = 3;
    private const int MaximumEncodedLength = 2 * 1024;
    private const string EmptyKind = "none";
    private const string SecretAccessKind = "secret-access";
    private const string TerminalStartupCommandsKind = "terminal-startup-commands";
    private const string AgentActionKind = "agent-action";
    private const string AgentRunPolicyTransitionKind = "agent-run-policy-transition";
    private const string AgentCapabilityRequestKind = "agent-capability-request";

    public static string Serialize(AuditDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", CurrentSchemaVersion);

        switch (details)
        {
            case AuditDetails.EmptyDetails:
                writer.WriteString("kind", EmptyKind);
                break;
            case AuditDetails.SecretAccessDetails secretAccess:
                writer.WriteString("kind", SecretAccessKind);
                writer.WriteString("purposeKind", secretAccess.PurposeKind.ToString());
                if (secretAccess.ErrorCode is { } errorCode)
                {
                    writer.WriteString("errorCode", errorCode.ToString());
                }
                else
                {
                    writer.WriteNull("errorCode");
                }

                break;
            case AuditDetails.TerminalStartupCommandDetails startupCommands:
                writer.WriteString("kind", TerminalStartupCommandsKind);
                writer.WriteNumber("commandCount", startupCommands.CommandCount);
                if (startupCommands.ErrorCode is { } startupErrorCode)
                {
                    writer.WriteString("errorCode", startupErrorCode.ToString());
                }
                else
                {
                    writer.WriteNull("errorCode");
                }

                break;
            case AuditDetails.AgentActionDetails agentAction:
                writer.WriteString("kind", AgentActionKind);
                writer.WriteString("runId", agentAction.RunId.Value);
                writer.WriteString("capability", agentAction.Capability.ToString());
                writer.WriteString("risk", agentAction.Risk.ToString());
                writer.WriteString("permission", agentAction.Permission.ToString());
                writer.WriteString("decision", agentAction.Decision.ToString());
                writer.WriteString("argumentDigest", agentAction.ArgumentDigest.Value);
                WriteNullableEnum(writer, "authorizationSource", agentAction.AuthorizationSource);
                WriteNullableEnum(writer, "errorCode", agentAction.ErrorCode);
                if (agentAction.ResultCode is { } resultCode)
                {
                    writer.WriteString("resultCode", resultCode);
                }
                else
                {
                    writer.WriteNull("resultCode");
                }

                WriteNullableLong(
                    writer,
                    "policyGeneration",
                    agentAction.Binding.PolicyGeneration);
                WriteNullableString(
                    writer,
                    "targetIdentity",
                    agentAction.Binding.TargetIdentity?.Value);
                WriteNullableString(
                    writer,
                    "approvalIdDigest",
                    agentAction.Binding.ApprovalIdDigest?.Value);
                WriteNullableEnum(
                    writer,
                    "approvalDuration",
                    agentAction.Binding.ApprovalDuration);
                WriteNullableString(
                    writer,
                    "authorizationIdDigest",
                    agentAction.Binding.AuthorizationIdDigest?.Value);
                WriteNullableDateTime(
                    writer,
                    "authorityExpiresAtUtc",
                    agentAction.Binding.AuthorityExpiresAtUtc);
                WriteNullableLong(
                    writer,
                    "executionDurationMilliseconds",
                    agentAction.Binding.ExecutionDurationMilliseconds);
                WriteNullableInt(
                    writer,
                    "resultCount",
                    agentAction.Binding.ResultCount);
                WriteNullableString(
                    writer,
                    "artifactReference",
                    agentAction.Binding.ArtifactReference);
                break;
            case AuditDetails.AgentRunPolicyTransitionDetails policyTransition:
                writer.WriteString("kind", AgentRunPolicyTransitionKind);
                writer.WriteString("runId", policyTransition.RunId.Value);
                writer.WriteString("transition", policyTransition.Transition.ToString());
                writer.WriteNumber(
                    "policyGeneration",
                    policyTransition.PolicyGeneration);
                writer.WriteString(
                    "targetIdentityDigest",
                    policyTransition.TargetIdentityDigest.Value);
                WriteNullableDateTime(
                    writer,
                    "yoloExpiresAtUtc",
                    policyTransition.YoloExpiresAtUtc);
                WriteNullableString(
                    writer,
                    "capabilityRequestId",
                    policyTransition.CapabilityRequestId?.Value);
                break;
            case AuditDetails.AgentCapabilityRequestDetails capabilityRequest:
                writer.WriteString("kind", AgentCapabilityRequestKind);
                writer.WriteString("runId", capabilityRequest.RunId.Value);
                writer.WriteString("capability", capabilityRequest.Capability.ToString());
                writer.WriteNumber(
                    "policyGeneration",
                    capabilityRequest.PolicyGeneration);
                writer.WriteString(
                    "targetIdentityDigest",
                    capabilityRequest.TargetIdentityDigest.Value);
                WriteNullableEnum(writer, "decision", capabilityRequest.Decision);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(details),
                    details.GetType(),
                    "The audit detail shape is not supported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static bool TryDeserialize(string json, out AuditDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > MaximumEncodedLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return TryRead(document.RootElement, out details);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRead(JsonElement root, out AuditDetails? details)
    {
        details = null;
        if (root.ValueKind != JsonValueKind.Object
            || !TryReadSchemaVersion(root, out var schemaVersion)
            || !TryReadString(root, "kind", out var kind))
        {
            return false;
        }

        if (string.Equals(kind, EmptyKind, StringComparison.Ordinal) && root.GetPropertyCount() == 2)
        {
            details = AuditDetails.None;
            return true;
        }

        if (string.Equals(kind, SecretAccessKind
, StringComparison.Ordinal) && root.GetPropertyCount() == 4
            && TryReadEnum(root, "purposeKind", out SecretUseKind purposeKind)
            && TryReadNullableEnum(root, "errorCode", out SecretVaultErrorCode? errorCode))
        {
            details = AuditDetails.ForSecretAccess(purposeKind, errorCode);
            return true;
        }

        if (string.Equals(kind, AgentActionKind, StringComparison.Ordinal))
        {
            return TryReadAgentAction(root, schemaVersion, out details);
        }

        if (string.Equals(kind, AgentRunPolicyTransitionKind, StringComparison.Ordinal))
        {
            return TryReadAgentRunPolicyTransition(root, schemaVersion, out details);
        }

        if (string.Equals(kind, AgentCapabilityRequestKind, StringComparison.Ordinal))
        {
            return TryReadAgentCapabilityRequest(root, schemaVersion, out details);
        }

        if (!string.Equals(kind, TerminalStartupCommandsKind
, StringComparison.Ordinal) || root.GetPropertyCount() != 4
            || !TryReadPositiveInt(root, "commandCount", out var commandCount)
            || !TryReadNullableEnum(
                root,
                "errorCode",
                out TerminalStartupCommandDispatchErrorCode? startupErrorCode))
        {
            return false;
        }

        details = AuditDetails.ForTerminalStartupCommands(commandCount, startupErrorCode);
        return true;
    }

    private static bool TryReadAgentRunPolicyTransition(
        JsonElement root,
        int schemaVersion,
        out AuditDetails? details)
    {
        details = null;
        var hasRequestId = schemaVersion == CurrentSchemaVersion;
        if (schemaVersion is not (IntermediateSchemaVersion or CurrentSchemaVersion)
            || root.GetPropertyCount() != (hasRequestId ? 8 : 7)
            || !TryReadString(root, "runId", out var runId)
            || !TryReadEnum(
                root,
                "transition",
                out AgentRunPolicyTransition transition)
            || !TryReadNonNegativeLong(
                root,
                "policyGeneration",
                out var policyGeneration)
            || !TryReadString(
                root,
                "targetIdentityDigest",
                out var targetIdentityDigest)
            || !TryReadNullableDateTime(
                root,
                "yoloExpiresAtUtc",
                out var yoloExpiresAtUtc)
            || hasRequestId
                && !TryReadNullableString(
                    root,
                    "capabilityRequestId",
                    out _))
        {
            return false;
        }

        try
        {
            string? capabilityRequestId = null;
            if (hasRequestId
                && !TryReadNullableString(
                    root,
                    "capabilityRequestId",
                    out capabilityRequestId))
            {
                return false;
            }

            details = AuditDetails.ForAgentRunPolicyTransition(
                new AgentRunId(runId!),
                transition,
                policyGeneration,
                new AgentActionDigest(targetIdentityDigest!),
                yoloExpiresAtUtc,
                capabilityRequestId is null
                    ? null
                    : new AgentCapabilityRequestId(capabilityRequestId));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadAgentCapabilityRequest(
        JsonElement root,
        int schemaVersion,
        out AuditDetails? details)
    {
        details = null;
        if (schemaVersion != CurrentSchemaVersion
            || root.GetPropertyCount() != 7
            || !TryReadString(root, "runId", out var runId)
            || !TryReadEnum(root, "capability", out AgentCapability capability)
            || !TryReadNonNegativeLong(
                root,
                "policyGeneration",
                out var policyGeneration)
            || !TryReadString(
                root,
                "targetIdentityDigest",
                out var targetIdentityDigest)
            || !TryReadNullableEnum(
                root,
                "decision",
                out AgentCapabilityRequestAuditDecision? decision))
        {
            return false;
        }

        try
        {
            details = AuditDetails.ForAgentCapabilityRequest(
                new AgentRunId(runId!),
                capability,
                policyGeneration,
                new AgentActionDigest(targetIdentityDigest!),
                decision);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadAgentAction(
        JsonElement root,
        int schemaVersion,
        out AuditDetails? details)
    {
        details = null;
        var expectedPropertyCount = schemaVersion == PreviousSchemaVersion ? 11 : 20;
        if (root.GetPropertyCount() != expectedPropertyCount
            || !TryReadString(root, "runId", out var runId)
            || string.IsNullOrWhiteSpace(runId)
            || !TryReadEnum(root, "capability", out AgentCapability capability)
            || !TryReadEnum(root, "risk", out AgentActionRisk risk)
            || !TryReadEnum(root, "permission", out AgentPermission permission)
            || !TryReadEnum(root, "decision", out AgentPolicyDecision decision)
            || !TryReadString(root, "argumentDigest", out var digest)
            || !TryReadNullableEnum(
                root,
                "authorizationSource",
                out AgentAuthorizationSource? authorizationSource)
            || !TryReadNullableEnum(
                root,
                "errorCode",
                out AgentAuthorizationErrorCode? errorCode)
            || !TryReadNullableString(root, "resultCode", out var resultCode))
        {
            return false;
        }

        AgentActionAuditBinding binding;
        if (schemaVersion == PreviousSchemaVersion)
        {
            binding = AgentActionAuditBinding.Empty;
        }
        else if (!TryReadNullableLong(
                     root,
                     "policyGeneration",
                     out var policyGeneration)
                 || !TryReadNullableString(
                     root,
                     "targetIdentity",
                     out var targetIdentity)
                 || !TryReadNullableString(
                     root,
                     "approvalIdDigest",
                     out var approvalIdDigest)
                 || !TryReadNullableEnum(
                     root,
                     "approvalDuration",
                     out AgentApprovalDuration? approvalDuration)
                 || !TryReadNullableString(
                     root,
                     "authorizationIdDigest",
                     out var authorizationIdDigest)
                 || !TryReadNullableDateTime(
                     root,
                     "authorityExpiresAtUtc",
                     out var authorityExpiresAtUtc)
                 || !TryReadNullableLong(
                     root,
                     "executionDurationMilliseconds",
                     out var executionDurationMilliseconds)
                 || !TryReadNullableInt(root, "resultCount", out var resultCount)
                 || !TryReadNullableString(
                     root,
                     "artifactReference",
                     out var artifactReference))
        {
            return false;
        }
        else
        {
            try
            {
                binding = new AgentActionAuditBinding(
                    policyGeneration,
                    targetIdentity is null
                        ? null
                        : new AgentActionDigest(targetIdentity),
                    approvalIdDigest is null
                        ? null
                        : new AgentActionDigest(approvalIdDigest),
                    approvalDuration,
                    authorizationIdDigest is null
                        ? null
                        : new AgentActionDigest(authorizationIdDigest),
                    authorityExpiresAtUtc,
                    executionDurationMilliseconds,
                    resultCount,
                    artifactReference);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        try
        {
            details = AuditDetails.ForAgentAction(
                new AgentRunId(runId!),
                capability,
                risk,
                permission,
                decision,
                new AgentActionDigest(digest!),
                authorizationSource,
                errorCode,
                resultCode,
                binding);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadSchemaVersion(
        JsonElement root,
        out int schemaVersion)
    {
        schemaVersion = 0;
        return root.TryGetProperty("schemaVersion", out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out schemaVersion)
            && schemaVersion is PreviousSchemaVersion
                or IntermediateSchemaVersion
                or CurrentSchemaVersion;
    }

    private static bool TryReadString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryReadPositiveInt(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value > 0;
    }

    private static bool TryReadNonNegativeLong(
        JsonElement root,
        string propertyName,
        out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0;
    }

    private static bool TryReadEnum<T>(
        JsonElement root,
        string propertyName,
        out T value)
        where T : struct, Enum
    {
        value = default;
        return TryReadString(root, propertyName, out var text)
            && Enum.TryParse(text, ignoreCase: false, out value)
            && Enum.IsDefined(value)
            && string.Equals(value.ToString(), text, StringComparison.Ordinal);
    }

    private static bool TryReadNullableEnum<T>(
        JsonElement root,
        string propertyName,
        out T? value)
        where T : struct, Enum
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (!Enum.TryParse(text, ignoreCase: false, out T parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadNullableString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryReadNullableLong(
        JsonElement root,
        string propertyName,
        out long? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var concrete))
        {
            return false;
        }

        value = concrete;
        return true;
    }

    private static bool TryReadNullableInt(
        JsonElement root,
        string propertyName,
        out int? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var concrete))
        {
            return false;
        }

        value = concrete;
        return true;
    }

    private static bool TryReadNullableDateTime(
        JsonElement root,
        string propertyName,
        out DateTimeOffset? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                property.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var concrete)
            || concrete.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = concrete;
        return true;
    }

    private static void WriteNullableEnum<T>(
        Utf8JsonWriter writer,
        string propertyName,
        T? value)
        where T : struct, Enum
    {
        if (value is { } concrete)
        {
            writer.WriteString(propertyName, concrete.ToString());
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableLong(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value is { } concrete)
        {
            writer.WriteNumber(propertyName, concrete);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableInt(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value is { } concrete)
        {
            writer.WriteNumber(propertyName, concrete);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableDateTime(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is { } concrete)
        {
            writer.WriteString(
                propertyName,
                concrete.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
