using System.Globalization;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.Sqlite;

namespace GhostShell.Infrastructure;

public sealed class SqliteDefinitionBundleStore : IDefinitionBundleStore
{
    private const int MaximumDefinitionCount = 10_000;
    private readonly GhostShellDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteDefinitionBundleStore(GhostShellDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DefinitionStoreResult<PortableDefinitionBundle>> ExportAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind, id, schema_version, name, payload_json
                FROM definitions
                ORDER BY kind, name COLLATE NOCASE, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var documents = new List<PortableDefinitionDocument>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var document = ReadDocument(reader);
                if (!KnownDefinitionRegistry.TryParse(
                        document,
                        out var definition,
                        out var problem))
                {
                    return FromProblem<PortableDefinitionBundle>(problem!);
                }

                if (definition is BrowserProfileDefinition
                    {
                        Id: var browserProfileId,
                    } && browserProfileId == BuiltInBrowserProfiles.Default.Id)
                {
                    continue;
                }

                documents.Add(definition is BrowserProfileDefinition profile
                    ? SanitizeExportedBrowserProfile(document, profile)
                    : document);
            }

            return DefinitionStoreResult<PortableDefinitionBundle>.Success(
                new PortableDefinitionBundle(
                    PortableDefinitionBundle.CurrentFormatVersion,
                    _timeProvider.GetUtcNow(),
                    documents));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<PortableDefinitionBundle>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition export was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<PortableDefinitionBundle>(
                MapSqliteError(exception),
                "The definition store could not create an export.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<PortableDefinitionBundle>(
                DefinitionStoreErrorCode.StorageFailure,
                "A stored definition has corrupt metadata.");
        }
    }

    public async ValueTask<DefinitionStoreResult<DefinitionImportPreflight>> PreflightImportAsync(
        PortableDefinitionBundle bundle,
        DefinitionImportMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var parsed = ParseBundle(bundle, mode);
        if (parsed.Issues.Any(issue => issue.IsBlocking))
        {
            return DefinitionStoreResult<DefinitionImportPreflight>.Success(
                new DefinitionImportPreflight(bundle, mode, parsed.Issues));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                var existing = await ReadExistingKeysAsync(
                        connection,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddConflictIssues(parsed, existing, mode);

                var validator = new SqliteDefinitionGraphValidator(
                    connection,
                    transaction,
                    parsed.Definitions);
                var problems = await validator.ValidateBatchAsync(cancellationToken)
                    .ConfigureAwait(false);
                var storageProblem = problems.FirstOrDefault(problem =>
                    problem.Kind == DefinitionProblemKind.StorageFailure);
                if (storageProblem is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return FromProblem<DefinitionImportPreflight>(storageProblem);
                }

                parsed.Issues.AddRange(problems.Select(ToImportIssue));
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DefinitionStoreResult<DefinitionImportPreflight>.Success(
                    new DefinitionImportPreflight(bundle, mode, parsed.Issues));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<DefinitionImportPreflight>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition import preflight was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<DefinitionImportPreflight>(
                MapSqliteError(exception),
                "The definition store could not inspect this import.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<DefinitionImportPreflight>(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored definition metadata is corrupt.");
        }
    }

    public async ValueTask<DefinitionStoreResult<DefinitionImportResult>> CommitImportAsync(
        DefinitionImportPreflight preflight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        var parsed = ParseBundle(preflight.Bundle, preflight.Mode);
        var blockingIssue = parsed.Issues.FirstOrDefault(issue => issue.IsBlocking);
        if (blockingIssue is not null)
        {
            return FromImportIssue<DefinitionImportResult>(blockingIssue);
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var existing = await ReadExistingKeysAsync(
                        connection,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (preflight.Mode == DefinitionImportMode.FailOnConflict
                    && parsed.Definitions.Keys.Any(existing.Contains))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure<DefinitionImportResult>(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "A definition already exists; no definitions were imported.");
                }

                var validator = new SqliteDefinitionGraphValidator(
                    connection,
                    transaction,
                    parsed.Definitions);
                var problems = await validator.ValidateBatchAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (problems.Count > 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return FromProblem<DefinitionImportResult>(problems[0]);
                }

                var inserted = 0;
                var replaced = 0;
                var now = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                foreach (var document in parsed.Documents)
                {
                    var key = new DefinitionKey(document.Kind, document.Id);
                    var wasExisting = existing.Contains(key);
                    await UpsertDocumentAsync(
                            connection,
                            transaction,
                            document,
                            now,
                            preflight.Mode,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ReplaceReferencesAsync(
                            connection,
                            transaction,
                            key,
                            DefinitionReferenceExtractor.Extract(
                                (IDurableDefinition)parsed.Definitions[key]),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (wasExisting)
                    {
                        replaced++;
                    }
                    else
                    {
                        inserted++;
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DefinitionStoreResult<DefinitionImportResult>.Success(
                    new DefinitionImportResult(inserted, replaced));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<DefinitionImportResult>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition import was cancelled and rolled back.");
        }
        catch (SqliteException exception)
        {
            return Failure<DefinitionImportResult>(
                MapSqliteError(exception),
                "The definition import failed and was rolled back.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<DefinitionImportResult>(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored definition metadata is corrupt.");
        }
    }

    private static ParsedBundle ParseBundle(
        PortableDefinitionBundle bundle,
        DefinitionImportMode mode)
    {
        var parsed = new ParsedBundle();
        if (bundle.FormatVersion != PortableDefinitionBundle.CurrentFormatVersion)
        {
            parsed.Issues.Add(new(
                DefinitionImportIssueCode.InvalidBundle,
                null,
                "The portable definition bundle version is not supported.",
                true));
        }

        if (!Enum.IsDefined(mode))
        {
            parsed.Issues.Add(new(
                DefinitionImportIssueCode.InvalidBundle,
                null,
                "The definition import mode is not supported.",
                true));
        }

        if (bundle.Definitions is null)
        {
            parsed.Issues.Add(new(
                DefinitionImportIssueCode.InvalidBundle,
                null,
                "The portable definition bundle has no definition collection.",
                true));
            return parsed;
        }

        int declaredCount;
        try
        {
            declaredCount = bundle.Definitions.Count;
        }
        catch (Exception exception) when (IsBundleCollectionException(exception))
        {
            parsed.Issues.Add(InvalidBundle(
                "The portable definition collection cannot be read."));
            return parsed;
        }

        if (declaredCount is < 0 or > MaximumDefinitionCount)
        {
            parsed.Issues.Add(InvalidBundle(
                "The portable definition bundle contains an invalid number of definitions."));
            return parsed;
        }

        PortableDefinitionDocument[] documents;
        try
        {
            documents = [.. bundle.Definitions.Take(MaximumDefinitionCount + 1)];
        }
        catch (Exception exception) when (IsBundleCollectionException(exception))
        {
            parsed.Issues.Add(InvalidBundle(
                "The portable definition collection changed while it was being read."));
            return parsed;
        }

        if (documents.Length != declaredCount)
        {
            parsed.Issues.Add(InvalidBundle(
                "The portable definition collection changed while it was being read."));
            return parsed;
        }

        foreach (var document in documents)
        {
            if (document is null)
            {
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.InvalidBundle,
                    null,
                    "The bundle contains an empty definition document.",
                    true));
                continue;
            }

            if (!KnownDefinitionRegistry.TryParse(
                    document,
                    out var definition,
                    out var problem))
            {
                parsed.Issues.Add(ToImportIssue(problem!));
                continue;
            }

            var importedDocument = document;
            if (definition is AiProviderProfile aiProviderProfile)
            {
                var disabledProfile = new AiProviderProfile(
                    aiProviderProfile.Id,
                    aiProviderProfile.SchemaVersion,
                    aiProviderProfile.Name,
                    aiProviderProfile.ProviderKind,
                    aiProviderProfile.Endpoint,
                    DetachImportedAuthentication(aiProviderProfile.Authentication),
                    aiProviderProfile.DefaultModel,
                    aiProviderProfile.Order,
                    isEnabled: false,
                    aiProviderProfile.Protocol,
                    aiProviderProfile.Capabilities);
                definition = disabledProfile;
                importedDocument = document with
                {
                    SchemaVersion = disabledProfile.SchemaVersion,
                    Name = disabledProfile.Name,
                    PayloadJson = DefinitionJson.Serialize(disabledProfile),
                };
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.ImportedAiProviderProfileDisabled,
                    disabledProfile.Key,
                    "The imported AI provider was disabled and its API-key or OAuth binding was detached. Review its endpoint, model, capabilities, and authentication in Settings before enabling it.",
                    false));
            }
            else if (definition is McpServerProfile profile)
            {
                importedDocument = document with
                {
                    SchemaVersion = profile.SchemaVersion,
                    Name = profile.Name,
                };
                var disabledProfile = new McpServerProfile(
                    profile.Id,
                    profile.SchemaVersion,
                    profile.Name,
                    profile.Transport,
                    profile.EnabledTools,
                    isEnabled: false,
                    isTrusted: false);
                definition = disabledProfile;
                importedDocument = importedDocument with
                {
                    PayloadJson = DefinitionJson.Serialize(disabledProfile),
                };
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.ImportedMcpProfileDisabled,
                    disabledProfile.Key,
                    "The imported MCP server was disabled. Review its executable, arguments, vault bindings, and tool allowlist in Settings before enabling it.",
                    false));
            }
            else if (definition is BrowserProfileDefinition browserProfile)
            {
                var disabledProfile = new BrowserProfileDefinition(
                    browserProfile.Id,
                    browserProfile.SchemaVersion,
                    browserProfile.Name,
                    browserProfile.Persistence,
                    browserProfile.Privacy,
                    authentication: null,
                    isEnabled: false);
                definition = disabledProfile;
                importedDocument = document with
                {
                    SchemaVersion = disabledProfile.SchemaVersion,
                    Name = disabledProfile.Name,
                    PayloadJson = DefinitionJson.Serialize(disabledProfile),
                };
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.ImportedBrowserProfileDisabled,
                    disabledProfile.Key,
                    "The imported browser profile was disabled and its machine-local credential binding was detached. Web content is never included in a definition bundle.",
                    false));
            }
            else if (definition is NetworkConnectionProfile networkConnection)
            {
                var detached = DetachImportedNetworkCredentials(networkConnection);
                if (detached != networkConnection)
                {
                    definition = detached;
                    importedDocument = document with
                    {
                        PayloadJson = DefinitionJson.Serialize(detached),
                    };
                    parsed.Issues.Add(new(
                        DefinitionImportIssueCode.ImportedNetworkCredentialsDetached,
                        detached.Key,
                        "The imported network connection's machine-local credential bindings were detached. Add its credentials in Settings before using it.",
                        false));
                }
            }
            else if (definition is ApplicationNetworkSettings settings
                     && settings.Policy.IsEnabled)
            {
                var disabled = new ApplicationNetworkSettings(
                    settings.Id,
                    settings.SchemaVersion,
                    settings.Name,
                    DisableImportedNetworkPolicy(settings.Policy));
                definition = disabled;
                importedDocument = document with
                {
                    PayloadJson = DefinitionJson.Serialize(disabled),
                };
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.ImportedNetworkPolicyDisabled,
                    disabled.Key,
                    "Imported application networking was disabled. Review the selected connection and credentials in Settings before enabling it.",
                    false));
            }
            else if (definition is WorkspaceDefinition workspace
                     && workspace.NetworkOverride is { IsEnabled: true })
            {
                var disabled = DisableImportedWorkspaceNetworkPolicy(workspace);
                definition = disabled;
                importedDocument = document with
                {
                    PayloadJson = DefinitionJson.Serialize(disabled),
                };
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.ImportedNetworkPolicyDisabled,
                    disabled.Key,
                    "Imported workspace networking was disabled. Review the selected connection and credentials in Settings before enabling it.",
                    false));
            }

            if (!parsed.Definitions.TryAdd(definition!.Key, definition))
            {
                parsed.Issues.Add(new(
                    DefinitionImportIssueCode.DuplicateIdentity,
                    definition.Key,
                    "The bundle contains the same definition identity more than once.",
                    true));
                continue;
            }

            parsed.Documents.Add(importedDocument);
        }

        return parsed;
    }

    private static AiProviderAuthentication DetachImportedAuthentication(
        AiProviderAuthentication authentication) => authentication switch
        {
            AiProviderAuthentication.ApiKey =>
                new AiProviderAuthentication.ApiKey(SecretRef.New()),
            AiProviderAuthentication.OAuth oauth =>
                new AiProviderAuthentication.OAuth(SecretRef.New(), oauth.Flow),
            AiProviderAuthentication.None => new AiProviderAuthentication.None(),
            AiProviderAuthentication.AwsCredentialChain =>
                new AiProviderAuthentication.AwsCredentialChain(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(authentication),
                authentication,
                "The imported AI-provider authentication method is not supported."),
        };

    private static NetworkConnectionProfile DetachImportedNetworkCredentials(
        NetworkConnectionProfile profile)
    {
        var detached = profile.Configuration switch
        {
            NetworkConnectionConfiguration.Proxy proxy when proxy.PasswordSecret is not null =>
                new NetworkConnectionConfiguration.Proxy(
                    proxy.Protocol,
                    proxy.Host,
                    proxy.Port,
                    proxy.Username,
                    SecretRef.New()),
            NetworkConnectionConfiguration.WireGuard =>
                new NetworkConnectionConfiguration.WireGuard(SecretRef.New()),
            NetworkConnectionConfiguration.OpenVpn openVpn =>
                new NetworkConnectionConfiguration.OpenVpn(
                    SecretRef.New(), openVpn.Username,
                    openVpn.PasswordSecret is null ? null : SecretRef.New()),
            NetworkConnectionConfiguration.AnyConnect anyConnect =>
                new NetworkConnectionConfiguration.AnyConnect(
                    anyConnect.Gateway,
                    anyConnect.Username,
                    anyConnect.PasswordSecret is null ? null : SecretRef.New(),
                    anyConnect.AuthenticationGroup,
                    anyConnect.ClientCertificateSecret is null ? null : SecretRef.New()),
            NetworkConnectionConfiguration.Tailscale tailscale =>
                new NetworkConnectionConfiguration.Tailscale(
                    tailscale.ExitNode,
                    tailscale.ControlServer,
                    tailscale.AuthKeySecret is null ? null : SecretRef.New()),
            NetworkConnectionConfiguration.Proxy => profile.Configuration,
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.Configuration,
                "The imported network connection type is not supported."),
        };
        return detached == profile.Configuration
            ? profile
            : new NetworkConnectionProfile(
                profile.Id,
                profile.SchemaVersion,
                profile.Name,
                detached);
    }

    private static NetworkPolicy DisableImportedNetworkPolicy(NetworkPolicy policy) =>
        new(
            policy.Connections,
            policy.SelectedConnectionId,
            isEnabled: false,
            policy.KillSwitchEnabled);

    private static WorkspaceDefinition DisableImportedWorkspaceNetworkPolicy(
        WorkspaceDefinition workspace) =>
        new(
            workspace.Id,
            workspace.SchemaVersion,
            workspace.Name,
            workspace.Description,
            workspace.Accent,
            workspace.Entries,
            workspace.AgentPolicyOverride,
            workspace.Icon,
            workspace.AutoSave,
            workspace.Color,
            workspace.AgentPanelPinned,
            workspace.TerminalMultiplexingOverride,
            workspace.BrowserProfileOverride,
            workspace.HasExplicitAccent,
            workspace.IsIsolated,
            workspace.IsolationMounts,
            workspace.IsolationImageReference,
            workspace.RunAgentInIsolation,
            DisableImportedNetworkPolicy(workspace.NetworkOverride!));

    private static PortableDefinitionDocument SanitizeExportedBrowserProfile(
        PortableDefinitionDocument document,
        BrowserProfileDefinition profile)
    {
        var sanitized = new BrowserProfileDefinition(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            profile.Persistence,
            profile.Privacy,
            authentication: null,
            isEnabled: profile.IsEnabled);
        return document with
        {
            PayloadJson = DefinitionJson.Serialize(sanitized),
        };
    }

    private static DefinitionImportIssue InvalidBundle(string message) =>
        new(DefinitionImportIssueCode.InvalidBundle, null, message, true);

    private static void AddConflictIssues(
        ParsedBundle parsed,
        IReadOnlySet<DefinitionKey> existing,
        DefinitionImportMode mode)
    {
        if (mode != DefinitionImportMode.FailOnConflict)
        {
            return;
        }

        foreach (var key in parsed.Definitions.Keys.Where(existing.Contains))
        {
            parsed.Issues.Add(new(
                DefinitionImportIssueCode.ExistingIdentity,
                key,
                "A definition with this identity already exists.",
                true));
        }
    }

    private static PortableDefinitionDocument ReadDocument(SqliteDataReader reader) =>
        new(
            new DefinitionKind(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4));

    private static async Task<HashSet<DefinitionKey>> ReadExistingKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<DefinitionKey>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind, id FROM definitions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(
                new DefinitionKind(reader.GetString(0)),
                reader.GetString(1)));
        }

        return result;
    }

    private static async Task UpsertDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PortableDefinitionDocument document,
        string now,
        DefinitionImportMode mode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = mode == DefinitionImportMode.ReplaceExisting
            ? """
                INSERT INTO definitions(
                    kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
                VALUES ($kind, $id, $schemaVersion, 1, $name, $payloadJson, $now, $now)
                ON CONFLICT(kind, id) DO UPDATE SET
                    schema_version = excluded.schema_version,
                    revision = definitions.revision + 1,
                    name = excluded.name,
                    payload_json = excluded.payload_json,
                    updated_utc = excluded.updated_utc;
                """
            : """
                INSERT INTO definitions(
                    kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
                VALUES ($kind, $id, $schemaVersion, 1, $name, $payloadJson, $now, $now);
                """;
        command.Parameters.AddWithValue("$kind", document.Kind.Value);
        command.Parameters.AddWithValue("$id", document.Id);
        command.Parameters.AddWithValue("$schemaVersion", document.SchemaVersion);
        command.Parameters.AddWithValue("$name", document.Name);
        command.Parameters.AddWithValue("$payloadJson", document.PayloadJson);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DefinitionKey owner,
        IReadOnlyList<DefinitionReference> references,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM definition_references
                WHERE owner_kind = $ownerKind AND owner_id = $ownerId;
                """;
            delete.Parameters.AddWithValue("$ownerKind", owner.Kind.Value);
            delete.Parameters.AddWithValue("$ownerId", owner.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var reference in references.Distinct())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO definition_references(
                    owner_kind, owner_id, target_kind, target_id, role)
                VALUES ($ownerKind, $ownerId, $targetKind, $targetId, $role);
                """;
            insert.Parameters.AddWithValue("$ownerKind", owner.Kind.Value);
            insert.Parameters.AddWithValue("$ownerId", owner.Value);
            insert.Parameters.AddWithValue("$targetKind", reference.Target.Kind.Value);
            insert.Parameters.AddWithValue("$targetId", reference.Target.Value);
            insert.Parameters.AddWithValue("$role", reference.Role);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DefinitionImportIssue ToImportIssue(DefinitionProblem problem) =>
        new(
            problem.Kind switch
            {
                DefinitionProblemKind.UnsupportedKind => DefinitionImportIssueCode.UnsupportedKind,
                DefinitionProblemKind.UnsupportedSchema => DefinitionImportIssueCode.UnsupportedSchema,
                DefinitionProblemKind.UnsafePayload => DefinitionImportIssueCode.UnsafePayload,
                DefinitionProblemKind.MissingDependency or DefinitionProblemKind.DependencyConflict =>
                    DefinitionImportIssueCode.MissingDependency,
                _ => DefinitionImportIssueCode.InvalidPayload,
            },
            problem.Definition,
            problem.Message,
            true);

    private static DefinitionStoreResult<T> FromProblem<T>(DefinitionProblem problem) =>
        Failure<T>(
            problem.Kind switch
            {
                DefinitionProblemKind.UnsupportedKind => DefinitionStoreErrorCode.UnsupportedKind,
                DefinitionProblemKind.UnsupportedSchema => DefinitionStoreErrorCode.UnsupportedSchema,
                DefinitionProblemKind.UnsafePayload => DefinitionStoreErrorCode.UnsafePayload,
                DefinitionProblemKind.MissingDependency or DefinitionProblemKind.DependencyConflict =>
                    DefinitionStoreErrorCode.DependencyConflict,
                DefinitionProblemKind.InvalidDefinition => DefinitionStoreErrorCode.InvalidDefinition,
                _ => DefinitionStoreErrorCode.StorageFailure,
            },
            problem.Message);

    private static DefinitionStoreResult<T> FromImportIssue<T>(DefinitionImportIssue issue) =>
        Failure<T>(
            issue.Code switch
            {
                DefinitionImportIssueCode.UnsupportedKind => DefinitionStoreErrorCode.UnsupportedKind,
                DefinitionImportIssueCode.UnsupportedSchema => DefinitionStoreErrorCode.UnsupportedSchema,
                DefinitionImportIssueCode.UnsafePayload => DefinitionStoreErrorCode.UnsafePayload,
                DefinitionImportIssueCode.MissingDependency => DefinitionStoreErrorCode.DependencyConflict,
                DefinitionImportIssueCode.ExistingIdentity => DefinitionStoreErrorCode.RevisionConflict,
                _ => DefinitionStoreErrorCode.InvalidDefinition,
            },
            issue.Message);

    private static DefinitionStoreResult<T> Failure<T>(
        DefinitionStoreErrorCode code,
        string message) =>
        DefinitionStoreResult<T>.Failure(new DefinitionStoreError(code, message));

    private static DefinitionStoreErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? DefinitionStoreErrorCode.StorageUnavailable
            : DefinitionStoreErrorCode.StorageFailure;

    private static bool IsStorageFormatException(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or InvalidCastException
            or FormatException
            or OverflowException;

    private static bool IsBundleCollectionException(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or IndexOutOfRangeException
            or NotSupportedException;

    private sealed class ParsedBundle
    {
        public Dictionary<DefinitionKey, object> Definitions { get; } = [];

        public List<PortableDefinitionDocument> Documents { get; } = [];

        public List<DefinitionImportIssue> Issues { get; } = [];
    }
}
