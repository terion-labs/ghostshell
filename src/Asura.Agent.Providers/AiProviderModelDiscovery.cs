using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal sealed class AiProviderModelDiscovery(
    AiProviderHttpTransport transport,
    AiProviderRuntimeLimits limits)
{
    private const string CodexClientVersion = "0.145.0";

    public async ValueTask<IReadOnlyList<AiProviderModelDescriptor>> ListAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(limits.DiscoveryTimeout);
        try
        {
            var discovery = AiProviderCatalog.Get(profile.Identity).ModelDiscovery;
            using var request = await transport.CreateRequestAsync(
                profile,
                HttpMethod.Get,
                DiscoveryPath(discovery, limits.MaximumModels),
                "application/json",
                body: null,
                operation.Token).ConfigureAwait(false);
            using var response = await transport
                .SendAsync(profile, request, operation.Token)
                .ConfigureAwait(false);
            AiProviderHttpTransport.ValidateContent(
                response,
                "application/json",
                limits.MaximumModelResponseBytes);
            await using var responseStream = await response.Content
                .ReadAsStreamAsync(operation.Token)
                .ConfigureAwait(false);
            await using var limited = new LimitedReadStream(
                responseStream,
                limits.MaximumModelResponseBytes);
            using var document = await ParseAsync(limited, operation.Token).ConfigureAwait(false);
            return ParseModels(
                document.RootElement,
                profile.Identity,
                discovery,
                limits.MaximumModels);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.Timeout,
                innerException: exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProviderUnavailable,
                innerException: exception);
        }
    }

    public async ValueTask<IReadOnlyList<AiProviderModelDescriptor>> ListOpenAiCodexAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(limits.DiscoveryTimeout);
        try
        {
            using var request = await transport.CreateRequestAsync(
                profile,
                HttpMethod.Get,
                $"models?client_version={CodexClientVersion}",
                "application/json",
                body: null,
                operation.Token).ConfigureAwait(false);
            using var response = await transport.SendAsync(
                profile,
                request,
                operation.Token).ConfigureAwait(false);
            AiProviderHttpTransport.ValidateContent(
                response,
                "application/json",
                limits.MaximumModelResponseBytes);
            await using var responseStream = await response.Content
                .ReadAsStreamAsync(operation.Token).ConfigureAwait(false);
            await using var limited = new LimitedReadStream(
                responseStream,
                limits.MaximumModelResponseBytes);
            using var document = await ParseAsync(limited, operation.Token)
                .ConfigureAwait(false);
            return ParseOpenAiCodexModels(document.RootElement, limits.MaximumModels);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.Timeout,
                innerException: exception);
        }
    }

    private static string DiscoveryPath(
        AiProviderModelDiscoveryKind discovery,
        int maximumModels) => discovery switch
        {
            AiProviderModelDiscoveryKind.AnthropicModels =>
                $"models?limit={maximumModels}",
            AiProviderModelDiscoveryKind.OpenAiModels => "models",
            AiProviderModelDiscoveryKind.GoogleModels =>
                $"models?pageSize={maximumModels}",
            _ => throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ModelUnavailable),
        };

    private static async ValueTask<JsonDocument> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                stream,
                AiProviderJson.DocumentOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError,
                innerException: exception);
        }
    }

    private static IReadOnlyList<AiProviderModelDescriptor> ParseModels(
        JsonElement root,
        AiProviderKind identity,
        AiProviderModelDiscoveryKind discovery,
        int maximumModels)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        var data = discovery switch
        {
            AiProviderModelDiscoveryKind.AnthropicModels
                or AiProviderModelDiscoveryKind.OpenAiModels =>
                AiProviderJson.RequiredArray(root, "data"),
            AiProviderModelDiscoveryKind.GoogleModels =>
                AiProviderJson.RequiredArray(root, "models"),
            _ => throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError),
        };
        var models = new List<AiProviderModelDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            var id = ReadModelId(item, discovery);
            if (identity == AiProviderKind.GitHubCopilot
                && !IsSupportedGitHubCopilotModel(id))
            {
                continue;
            }

            if (models.Count == maximumModels)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            var displayName = AiProviderJson.OptionalBoundedString(
                    item,
                    discovery == AiProviderModelDiscoveryKind.GoogleModels
                        ? "displayName"
                        : "display_name",
                    AiProviderProfile.MaximumModelIdLength)
                ?? id;
            if (!ids.Add(id))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            try
            {
                models.Add(new AiProviderModelDescriptor(id, displayName));
            }
            catch (ArgumentException exception)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError,
                    innerException: exception);
            }
        }

        if (models.Count == 0)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ModelUnavailable);
        }

        return Array.AsReadOnly(models
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray());
    }

    private static string ReadModelId(
        JsonElement item,
        AiProviderModelDiscoveryKind discovery)
    {
        var propertyName = discovery == AiProviderModelDiscoveryKind.GoogleModels
            ? "name"
            : "id";
        var id = AiProviderJson.RequiredBoundedString(
            item,
            propertyName,
            AiProviderProfile.MaximumModelIdLength + "models/".Length);
        if (discovery != AiProviderModelDiscoveryKind.GoogleModels)
        {
            return id;
        }

        const string prefix = "models/";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)
            || id.Length == prefix.Length)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ProtocolError);
        }

        return id[prefix.Length..];
    }

    private static bool IsSupportedGitHubCopilotModel(string modelId) =>
        modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith("grok-code", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AiProviderModelDescriptor> ParseOpenAiCodexModels(
        JsonElement root,
        int maximumModels)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ProtocolError);
        }

        var data = AiProviderJson.RequiredArray(root, "models");
        var models = new List<AiProviderModelDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            var visibility = AiProviderJson.OptionalBoundedString(
                item,
                "visibility",
                16);
            if (!string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (models.Count == maximumModels)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ResponseTooLarge);
            }

            var id = AiProviderJson.RequiredBoundedString(
                item,
                "slug",
                AiProviderProfile.MaximumModelIdLength);
            var displayName = AiProviderJson.OptionalBoundedString(
                    item,
                    "display_name",
                    AiProviderProfile.MaximumModelIdLength)
                ?? id;
            if (!ids.Add(id))
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError);
            }

            try
            {
                models.Add(new AiProviderModelDescriptor(id, displayName));
            }
            catch (ArgumentException exception)
            {
                throw AiProviderClientException.Create(
                    AiProviderRuntimeErrorCode.ProtocolError,
                    innerException: exception);
            }
        }

        if (models.Count == 0)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.ModelUnavailable);
        }

        return Array.AsReadOnly(models.ToArray());
    }
}
