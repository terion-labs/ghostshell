using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Validates a prospective definition graph against one SQLite transaction. Prospective
/// definitions shadow stored rows so an imported layout and its screens are checked together.
/// </summary>
internal sealed class SqliteDefinitionGraphValidator
{
    private readonly Dictionary<DefinitionKey, object> _cache;
    private readonly SqliteConnection _connection;
    private readonly IReadOnlyDictionary<DefinitionKey, object> _prospective;
    private readonly SqliteTransaction _transaction;

    public SqliteDefinitionGraphValidator(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<DefinitionKey, object> prospective)
    {
        _connection = connection;
        _transaction = transaction;
        _prospective = prospective;
        _cache = prospective.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public async Task<IReadOnlyList<DefinitionProblem>> ValidateBatchAsync(
        CancellationToken cancellationToken)
    {
        List<DefinitionProblem> problems = [];
        foreach (var value in _prospective.Values)
        {
            var definition = (IDurableDefinition)value;
            var problem = await ValidateDefinitionAsync(definition, cancellationToken)
                .ConfigureAwait(false);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        var aiProviderOrderProblem = await ValidateAiProviderOrdersAsync(cancellationToken)
            .ConfigureAwait(false);
        if (aiProviderOrderProblem is not null)
        {
            problems.Add(aiProviderOrderProblem);
        }

        var changedDependencyTargets = _prospective.Values
            .Where(value => value is
                LayoutDefinition
                or KeymapProfile
                or ConnectionProfile
                or AiProviderProfile)
            .Select(value => ((IDurableDefinition)value).Key)
            .ToArray();
        var impactedOwners = await ReadInboundOwnersAsync(
                changedDependencyTargets,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var ownerKey in impactedOwners.Where(key => !_prospective.ContainsKey(key)))
        {
            var resolved = await ResolveAsync(ownerKey, cancellationToken).ConfigureAwait(false);
            if (resolved.Problem is not null)
            {
                problems.Add(resolved.Problem);
                continue;
            }

            if (resolved.Definition is null)
            {
                problems.Add(new(
                    DefinitionProblemKind.StorageFailure,
                    $"A stored dependency owner '{ownerKey}' is missing.",
                    ownerKey));
                continue;
            }

            var problem = await ValidateDefinitionAsync(resolved.Definition, cancellationToken)
                .ConfigureAwait(false);
            if (problem is not null)
            {
                problems.Add(new(
                    DefinitionProblemKind.DependencyConflict,
                    $"The dependency change would invalidate dependent definition '{ownerKey}': {problem.Message}",
                    ownerKey));
            }
        }

        return problems;
    }

    private async Task<DefinitionProblem?> ValidateAiProviderOrdersAsync(
        CancellationToken cancellationToken)
    {
        var prospectiveProfiles = _prospective.Values
            .OfType<AiProviderProfile>()
            .ToArray();
        if (prospectiveProfiles.Length == 0)
        {
            return null;
        }

        var effectiveProfiles = new List<AiProviderProfile>(prospectiveProfiles);
        var storedKeys = await ReadKeysAsync(
                DefinitionKind.AiProviderProfile,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var key in storedKeys.Where(key => !_prospective.ContainsKey(key)))
        {
            var resolved = await ResolveAsync(key, cancellationToken).ConfigureAwait(false);
            if (resolved.Problem is not null)
            {
                return resolved.Problem;
            }

            if (resolved.Definition is not AiProviderProfile profile)
            {
                return new(
                    DefinitionProblemKind.StorageFailure,
                    $"Stored AI provider definition '{key}' has an unexpected payload type.",
                    key);
            }

            effectiveProfiles.Add(profile);
        }

        var prospectiveKeys = prospectiveProfiles
            .Select(profile => profile.Key)
            .ToHashSet();
        var conflict = effectiveProfiles
            .GroupBy(profile => profile.Order)
            .Where(group =>
                group.Skip(1).Any()
                && group.Any(profile => prospectiveKeys.Contains(profile.Key)))
            .OrderBy(group => group.Key)
            .Select(group => group
                .OrderBy(profile => profile.Key.Value, StringComparer.Ordinal)
                .ToArray())
            .FirstOrDefault();
        if (conflict is null)
        {
            return null;
        }

        var owner = conflict.First(profile => prospectiveKeys.Contains(profile.Key));
        var other = conflict.First(profile => profile.Key != owner.Key);
        return new(
            DefinitionProblemKind.InvalidDefinition,
            $"AI providers '{owner.Key}' and '{other.Key}' both use display order {owner.Order}. Each AI provider must have a distinct display order.",
            owner.Key);
    }

    private async Task<DefinitionProblem?> ValidateDefinitionAsync(
        IDurableDefinition definition,
        CancellationToken cancellationToken)
    {
        var selfProblem = KnownDefinitionRegistry.ValidateForSave(definition);
        if (selfProblem is not null)
        {
            return selfProblem;
        }

        foreach (var reference in DefinitionReferenceExtractor.Extract(definition))
        {
            if (reference.Target == definition.Key)
            {
                return new(
                    DefinitionProblemKind.DependencyConflict,
                    $"Definition '{definition.Key}' cannot depend on itself.",
                    definition.Key);
            }

            var target = await ResolveAsync(reference.Target, cancellationToken).ConfigureAwait(false);
            if (target.Problem is not null)
            {
                return target.Problem;
            }

            if (target.Definition is null)
            {
                return new(
                    DefinitionProblemKind.MissingDependency,
                    $"Definition '{definition.Key}' requires missing {reference.Target.Kind} '{reference.Target.Value}'.",
                    definition.Key);
            }
        }

        var agentPolicy = definition switch
        {
            ScreenDefinition screen => screen.AgentPolicyOverride,
            WorkspaceDefinition workspace => workspace.AgentPolicyOverride,
            _ => null,
        };
        if (agentPolicy is not null)
        {
            var provider = await ResolveAsync(
                    new DefinitionKey(
                        DefinitionKind.AiProviderProfile,
                        agentPolicy.Provider),
                    cancellationToken)
                .ConfigureAwait(false);
            if (provider.Problem is not null)
            {
                return provider.Problem;
            }

            if (provider.Definition is not AiProviderProfile { IsEnabled: true })
            {
                return new(
                    DefinitionProblemKind.DependencyConflict,
                    $"Definition '{definition.Key}' requires an enabled AI-provider profile '{agentPolicy.Provider}'.",
                    definition.Key);
            }
        }

        return definition switch
        {
            ScreenDefinition screen =>
                await ValidateScreenAsync(screen, cancellationToken).ConfigureAwait(false),
            WorkspaceDefinition workspace =>
                await ValidateWorkspaceTabsAsync(workspace, cancellationToken).ConfigureAwait(false),
            TerminalProfile terminal =>
                await ValidateTerminalAsync(terminal, cancellationToken).ConfigureAwait(false),
            KeymapProfile keymap =>
                await ValidateKeymapAsync(keymap, cancellationToken).ConfigureAwait(false),
            FileProviderProfile fileProvider =>
                await ValidateFileProviderAsync(fileProvider, cancellationToken).ConfigureAwait(false),
            ConnectionProfile connection =>
                await ValidateConnectionAsync(connection, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// A connection that delegates its endpoint must point at a standalone SSH
    /// connection: a non-SSH target cannot host the delegated endpoint, and a
    /// delegating target would create reference chains the runtime refuses.
    /// </summary>
    private async Task<DefinitionProblem?> ValidateConnectionAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        if (connection.HostConnectionId is not { } hostId)
        {
            return null;
        }

        var resolved = await ResolveAsync(
                new DefinitionKey(DefinitionKind.Connection, hostId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        return resolved.Definition is ConnectionProfile
        {
            Endpoint: ConnectionEndpoint.Ssh,
            HostConnectionId: null,
        }
            ? null
            : new(
                DefinitionProblemKind.DependencyConflict,
                $"Connection '{connection.Key}' requires a standalone SSH connection as its host connection.",
                connection.Key);
    }

    private async Task<DefinitionProblem?> ValidateFileProviderAsync(
        FileProviderProfile fileProvider,
        CancellationToken cancellationToken)
    {
        if (fileProvider.Configuration is not FileProviderConfiguration.Sftp sftp)
        {
            return null;
        }

        var resolved = await ResolveAsync(
                new DefinitionKey(DefinitionKind.Connection, sftp.ConnectionId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        return resolved.Definition is ConnectionProfile
        {
            Endpoint: ConnectionEndpoint.Ssh,
        }
            ? null
            : new(
                DefinitionProblemKind.DependencyConflict,
                $"SFTP provider '{fileProvider.Key}' requires an SSH connection profile.",
                fileProvider.Key);
    }

    private async Task<DefinitionProblem?> ValidateTerminalAsync(
        TerminalProfile terminal,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(
                new DefinitionKey(DefinitionKind.Keymap, terminal.KeymapId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        return resolved.Definition is KeymapProfile { Layer: KeymapLayer.Terminal }
            ? null
            : new(
                DefinitionProblemKind.DependencyConflict,
                $"Terminal profile '{terminal.Key}' requires a terminal-layer keymap.",
                terminal.Key);
    }

    private async Task<DefinitionProblem?> ValidateKeymapAsync(
        KeymapProfile keymap,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<DefinitionKey> { keymap.Key };
        var current = keymap;
        while (current.BasedOn is { } basedOn)
        {
            var baseKey = new DefinitionKey(DefinitionKind.Keymap, basedOn.Value);
            if (!visited.Add(baseKey))
            {
                return new(
                    DefinitionProblemKind.DependencyConflict,
                    $"Keymap '{keymap.Key}' has a cyclic base-keymap chain.",
                    keymap.Key);
            }

            var resolved = await ResolveAsync(baseKey, cancellationToken).ConfigureAwait(false);
            if (resolved.Problem is not null)
            {
                return resolved.Problem;
            }

            if (resolved.Definition is not KeymapProfile baseKeymap)
            {
                return new(
                    DefinitionProblemKind.MissingDependency,
                    $"Keymap '{keymap.Key}' requires missing base keymap '{baseKey}'.",
                    keymap.Key);
            }

            if (baseKeymap.Layer != keymap.Layer)
            {
                return new(
                    DefinitionProblemKind.DependencyConflict,
                    $"Keymap '{keymap.Key}' cannot inherit from a different keymap layer.",
                    keymap.Key);
            }

            current = baseKeymap;
        }

        return null;
    }

    private async Task<DefinitionProblem?> ValidateScreenAsync(
        ScreenDefinition screen,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(
                new DefinitionKey(DefinitionKind.Layout, screen.LayoutId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        if (resolved.Definition is not LayoutDefinition layout)
        {
            return new(
                DefinitionProblemKind.MissingDependency,
                $"Screen '{screen.Key}' requires layout '{screen.LayoutId}'.",
                screen.Key);
        }

        return FromValidation(screen.Key, ScreenValidator.Validate(screen, layout));
    }

    private async Task<DefinitionProblem?> ValidateWorkspaceTabsAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken)
    {
        foreach (var tab in workspace.Entries.OfType<WorkspaceEntry.Tab>())
        {
            var resolved = await ResolveAsync(
                    new DefinitionKey(DefinitionKind.Layout, tab.LayoutId.Value),
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved.Problem is not null)
            {
                return resolved.Problem;
            }

            if (resolved.Definition is not LayoutDefinition layout)
            {
                return new(
                    DefinitionProblemKind.MissingDependency,
                    $"Workspace tab '{tab.Id}' requires layout '{tab.LayoutId}'.",
                    workspace.Key);
            }

            var screen = new ScreenDefinition(
                new ScreenId($"{workspace.Id.Value}.{tab.Id.Value}"),
                ScreenDefinition.CurrentSchemaVersion,
                tab.Name,
                null,
                tab.LayoutId,
                tab.Panels);
            var problem = FromValidation(
                workspace.Key,
                ScreenValidator.Validate(screen, layout));
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private async Task<ResolvedDefinition> ResolveAsync(
        DefinitionKey key,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return new((IDurableDefinition)cached, null);
        }

        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT kind, id, schema_version, name, payload_json
            FROM definitions
            WHERE kind = $kind AND id = $id;
            """;
        command.Parameters.AddWithValue("$kind", key.Kind.Value);
        command.Parameters.AddWithValue("$id", key.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(null, null);
        }

        PortableDefinitionDocument document;
        try
        {
            document = new(
                new DefinitionKind(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4));
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return new(
                null,
                new(
                    DefinitionProblemKind.StorageFailure,
                    $"Stored definition '{key}' has corrupt metadata.",
                    key));
        }

        if (!KnownDefinitionRegistry.TryParse(document, out var definition, out var problem))
        {
            return new(null, problem);
        }

        _cache.Add(key, definition!);
        return new(definition, null);
    }

    private async Task<IReadOnlyList<DefinitionKey>> ReadInboundOwnersAsync(
        IReadOnlyList<DefinitionKey> targetKeys,
        CancellationToken cancellationToken)
    {
        var owners = new HashSet<DefinitionKey>();
        foreach (var targetKey in targetKeys)
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = """
                SELECT DISTINCT owner_kind, owner_id
                FROM definition_references
                WHERE target_kind = $targetKind AND target_id = $targetId;
                """;
            command.Parameters.AddWithValue("$targetKind", targetKey.Kind.Value);
            command.Parameters.AddWithValue("$targetId", targetKey.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                owners.Add(new(
                    new DefinitionKind(reader.GetString(0)),
                    reader.GetString(1)));
            }
        }

        return [.. owners];
    }

    private async Task<IReadOnlyList<DefinitionKey>> ReadKeysAsync(
        DefinitionKind kind,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT id
            FROM definitions
            WHERE kind = $kind
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$kind", kind.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var keys = new List<DefinitionKey>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new(kind, reader.GetString(0)));
        }

        return keys;
    }

    private static DefinitionProblem? FromValidation(
        DefinitionKey key,
        DefinitionValidationResult validation) =>
        validation.IsValid
            ? null
            : new(
                DefinitionProblemKind.InvalidDefinition,
                string.Join(" ", validation.Issues.Select(issue => issue.Message)),
                key);

    private static bool IsStorageFormatException(Exception exception) =>
        exception is ArgumentException
            or InvalidCastException
            or FormatException
            or OverflowException;

    private sealed record ResolvedDefinition(
        IDurableDefinition? Definition,
        DefinitionProblem? Problem);
}
