namespace Asura.Infrastructure;

internal static class SqliteSchema
{
    public static IReadOnlyList<SqliteMigration> Migrations { get; } =
    [
        new(
            1,
            "durable-definitions-and-recovery",
            """
            CREATE TABLE definitions (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                revision INTEGER NOT NULL CHECK (revision > 0),
                name TEXT NOT NULL,
                payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (kind, id)
            ) WITHOUT ROWID;

            CREATE INDEX definitions_name_idx
                ON definitions(kind, name COLLATE NOCASE);

            CREATE TABLE definition_references (
                owner_kind TEXT NOT NULL,
                owner_id TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                target_id TEXT NOT NULL,
                role TEXT NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, target_kind, target_id, role),
                FOREIGN KEY (owner_kind, owner_id)
                    REFERENCES definitions(kind, id)
                    ON DELETE CASCADE
                    DEFERRABLE INITIALLY DEFERRED
            ) WITHOUT ROWID;

            CREATE INDEX definition_references_target_idx
                ON definition_references(target_kind, target_id);

            CREATE TABLE app_lifecycle (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                clean_shutdown INTEGER NOT NULL CHECK (clean_shutdown IN (0, 1)),
                current_run_id TEXT,
                started_utc TEXT,
                last_clean_utc TEXT,
                CHECK (
                    (clean_shutdown = 1 AND current_run_id IS NULL AND started_utc IS NULL)
                    OR
                    (clean_shutdown = 0 AND current_run_id IS NOT NULL AND started_utc IS NOT NULL)
                )
            );

            INSERT INTO app_lifecycle(
                singleton_id,
                clean_shutdown,
                current_run_id,
                started_utc,
                last_clean_utc)
            VALUES (1, 1, NULL, NULL, NULL);

            CREATE TABLE runtime_snapshots (
                run_id TEXT NOT NULL,
                snapshot_key TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, snapshot_key)
            ) WITHOUT ROWID;

            CREATE INDEX runtime_snapshots_updated_idx
                ON runtime_snapshots(updated_utc);

            CREATE TABLE audit_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                correlation_id TEXT NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action TEXT NOT NULL,
                target_kind TEXT,
                target_id TEXT,
                outcome TEXT NOT NULL,
                details_json TEXT NOT NULL CHECK (json_valid(details_json)),
                occurred_utc TEXT NOT NULL
            );

            CREATE INDEX audit_events_correlation_idx
                ON audit_events(correlation_id, sequence);
            """),
        new(
            2,
            "bounded-recent-session-history",
            """
            CREATE TABLE recent_sessions (
                session_id TEXT PRIMARY KEY CHECK (length(session_id) > 0),
                definition_kind TEXT NOT NULL CHECK (length(definition_kind) > 0),
                definition_id TEXT NOT NULL CHECK (length(definition_id) > 0),
                panel_kind TEXT NOT NULL CHECK (
                    panel_kind IN ('Terminal', 'Browser', 'FileViewer', 'Statistics', 'ProcessMonitor')),
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
                started_utc TEXT NOT NULL,
                ended_utc TEXT,
                outcome TEXT NOT NULL CHECK (
                    outcome IN (
                        'Active',
                        'GracefullyClosed',
                        'ForceTerminated',
                        'Failed',
                        'Cancelled',
                        'Interrupted')),
                CHECK (
                    (outcome = 'Active' AND ended_utc IS NULL)
                    OR
                    (outcome <> 'Active' AND ended_utc IS NOT NULL)),
                CHECK (ended_utc IS NULL OR ended_utc >= started_utc)
            ) WITHOUT ROWID;

            CREATE INDEX recent_sessions_recency_idx
                ON recent_sessions(
                    COALESCE(ended_utc, started_utc) DESC,
                    started_utc DESC,
                    session_id);
            """),
        new(
            3,
            "revisioned-recent-session-retention",
            """
            CREATE TABLE recent_session_retention (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                revision INTEGER NOT NULL CHECK (revision > 0),
                maximum_entries INTEGER NOT NULL CHECK (
                    maximum_entries BETWEEN 0 AND 1000),
                maximum_age_ticks INTEGER NOT NULL CHECK (
                    maximum_age_ticks BETWEEN 1 AND 315360000000000)
            );

            INSERT INTO recent_session_retention(
                singleton_id,
                revision,
                maximum_entries,
                maximum_age_ticks)
            VALUES (1, 1, 100, 25920000000000);
            """),
        new(
            4,
            "versioned-first-run-progress",
            """
            CREATE TABLE onboarding_progress (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                completed_version INTEGER NOT NULL CHECK (completed_version >= 0),
                revision INTEGER NOT NULL CHECK (revision > 0)
            );

            INSERT INTO onboarding_progress(
                singleton_id,
                completed_version,
                revision)
            SELECT
                1,
                CASE
                    WHEN EXISTS (SELECT 1 FROM definitions)
                        OR EXISTS (SELECT 1 FROM recent_sessions)
                        OR EXISTS (SELECT 1 FROM audit_events)
                        OR EXISTS (
                            SELECT 1
                            FROM app_lifecycle
                            WHERE singleton_id = 1
                                AND last_clean_utc IS NOT NULL)
                    THEN 1
                    ELSE 0
                END,
                1;
            """),
        new(
            5,
            "durable-agent-action-audit-state",
            """
            CREATE TABLE agent_action_audit_state (
                action_id TEXT PRIMARY KEY CHECK (length(action_id) BETWEEN 1 AND 256),
                phase TEXT NOT NULL CHECK (
                    phase IN (
                        'Requested',
                        'Approved',
                        'Started',
                        'Succeeded',
                        'Denied',
                        'Failed',
                        'Cancelled')),
                last_event_id TEXT NOT NULL UNIQUE,
                updated_utc TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE INDEX agent_action_audit_state_phase_idx
                ON agent_action_audit_state(phase, updated_utc, action_id);

            INSERT INTO agent_action_audit_state(
                action_id, phase, last_event_id, updated_utc)
            SELECT
                event.correlation_id,
                event.outcome,
                event.event_id,
                event.occurred_utc
            FROM audit_events AS event
            WHERE json_extract(event.details_json, '$.kind') = 'agent-action'
              AND event.sequence = (
                    SELECT MAX(candidate.sequence)
                    FROM audit_events AS candidate
                    WHERE candidate.correlation_id = event.correlation_id
                      AND json_extract(candidate.details_json, '$.kind') = 'agent-action');
            """),
        new(
            6,
            "indexed-agent-run-audit-reading",
            """
            CREATE INDEX audit_events_agent_run_idx
                ON audit_events(
                    json_extract(details_json, '$.runId'),
                    sequence DESC)
                WHERE json_extract(details_json, '$.kind') IN (
                    'agent-action',
                    'agent-run-policy-transition');
            """),
        new(
            7,
            "session-restore-preference",
            """
            CREATE TABLE session_restore_preference (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                restore_sessions_on_start INTEGER NOT NULL CHECK (
                    restore_sessions_on_start IN (0, 1))
            );

            INSERT INTO session_restore_preference(
                singleton_id,
                restore_sessions_on_start)
            VALUES (1, 1);
            """),
        new(
            8,
            "file-preview-settings",
            """
            CREATE TABLE file_preview_settings (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                auto_load_threshold_bytes INTEGER NOT NULL CHECK (
                    auto_load_threshold_bytes > 0),
                keep_previews_between_runs INTEGER NOT NULL CHECK (
                    keep_previews_between_runs IN (0, 1)),
                cache_budget_bytes INTEGER NOT NULL CHECK (cache_budget_bytes > 0)
            );

            INSERT INTO file_preview_settings(
                singleton_id,
                auto_load_threshold_bytes,
                keep_previews_between_runs,
                cache_budget_bytes)
            VALUES (1, 2097152, 1, 536870912);
            """),
        // Retaining a record of every session someone opened is not something to
        // start doing on their behalf. Migration 3 seeded it on; this turns it
        // off wherever it was never deliberately chosen, and leaves it off for
        // new installs. Anyone who wants history turns it on, once.
        new(
            9,
            "session-history-is-opt-in",
            """
            UPDATE recent_session_retention
            SET revision = revision + 1,
                maximum_entries = 0
            WHERE singleton_id = 1;
            """),
        new(
            10,
            "terminal-multiplexing",
            """
            CREATE TABLE terminal_multiplexing_preference (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                mode INTEGER NOT NULL CHECK (mode IN (0, 1))
            );

            INSERT INTO terminal_multiplexing_preference(singleton_id, mode)
            VALUES (1, 0);

            CREATE TABLE terminal_multiplexer_leases (
                connection_id TEXT NOT NULL CHECK (length(connection_id) > 0),
                session_name TEXT NOT NULL CHECK (
                    length(session_name) BETWEEN 1 AND 64
                    AND session_name GLOB 'asura-[a-z0-9-]*'),
                state INTEGER NOT NULL CHECK (state IN (0, 1)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (connection_id, session_name)
            ) WITHOUT ROWID;

            CREATE INDEX terminal_multiplexer_leases_state_idx
                ON terminal_multiplexer_leases(state, updated_utc);
            """),
        new(
            11,
            "durable-native-agent-checkpoints",
            """
            CREATE TABLE agent_session_checkpoints (
                run_id TEXT PRIMARY KEY CHECK (
                    length(run_id) BETWEEN 1 AND 256),
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                generation INTEGER NOT NULL CHECK (generation >= 0),
                revision INTEGER NOT NULL CHECK (revision >= 0),
                payload_json TEXT NOT NULL CHECK (
                    json_valid(payload_json)
                    AND json_type(payload_json) = 'object'
                    AND length(CAST(payload_json AS BLOB)) BETWEEN 1 AND 33554432),
                payload_sha256 TEXT NOT NULL CHECK (
                    length(payload_sha256) = 64
                    AND payload_sha256 NOT GLOB '*[^0-9a-f]*'),
                updated_utc TEXT NOT NULL CHECK (
                    length(updated_utc) BETWEEN 20 AND 64)
            ) WITHOUT ROWID;

            CREATE INDEX agent_session_checkpoints_updated_idx
                ON agent_session_checkpoints(updated_utc DESC, run_id);
            """),
        new(
            12,
            "favorite-agent-models",
            """
            CREATE TABLE agent_model_favorites (
                provider_id TEXT NOT NULL CHECK (
                    length(provider_id) BETWEEN 1 AND 256),
                model_id TEXT NOT NULL CHECK (
                    length(model_id) BETWEEN 1 AND 256),
                created_utc TEXT NOT NULL CHECK (
                    length(created_utc) BETWEEN 20 AND 64),
                PRIMARY KEY (provider_id, model_id)
            ) WITHOUT ROWID;

            CREATE INDEX agent_model_favorites_created_idx
                ON agent_model_favorites(created_utc, provider_id, model_id);
            """),
        new(
            13,
            "default-agent-policy",
            """
            CREATE TABLE agent_policy_preference (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                policy_json TEXT CHECK (
                    policy_json IS NULL
                    OR (
                        json_valid(policy_json)
                        AND json_type(policy_json) = 'object'
                        AND length(CAST(policy_json AS BLOB)) BETWEEN 2 AND 65536))
            );

            INSERT INTO agent_policy_preference(singleton_id, policy_json)
            VALUES (1, NULL);
            """),
        new(
            14,
            "workspace-scoped-agent-checkpoints",
            """
            ALTER TABLE agent_session_checkpoints
                ADD COLUMN workspace_id TEXT CHECK (
                    workspace_id IS NULL
                    OR length(workspace_id) BETWEEN 1 AND 256);

            CREATE INDEX agent_session_checkpoints_workspace_updated_idx
                ON agent_session_checkpoints(
                    workspace_id,
                    updated_utc DESC,
                    run_id);
            """),
        new(
            15,
            "browser-profile-preference",
            """
            CREATE TABLE browser_profile_preference (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                sharing INTEGER NOT NULL CHECK (sharing IN (0, 1))
            );

            INSERT INTO browser_profile_preference(singleton_id, sharing)
            VALUES (1, 0);
            """),
        new(
            16,
            "git-panel-preference",
            """
            CREATE TABLE git_panel_preference (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                unstaged_view_is_tree INTEGER NOT NULL CHECK (
                    unstaged_view_is_tree IN (0, 1)),
                staged_view_is_tree INTEGER NOT NULL CHECK (
                    staged_view_is_tree IN (0, 1))
            );

            INSERT INTO git_panel_preference(
                singleton_id,
                unstaged_view_is_tree,
                staged_view_is_tree)
            VALUES (1, 1, 1);
            """),
        new(
            17,
            "named-browser-profile-preference",
            """
            ALTER TABLE browser_profile_preference
                ADD COLUMN default_profile_id TEXT CHECK (
                    default_profile_id IS NULL
                    OR length(default_profile_id) BETWEEN 1 AND 256);
            """),
        new(
            18,
            "bounded-mcp-diagnostic-summary",
            """
            CREATE TABLE mcp_server_diagnostic_summary (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                payload_json TEXT NOT NULL CHECK (
                    json_valid(payload_json)
                    AND json_type(payload_json) = 'array'
                    AND length(CAST(payload_json AS BLOB)) BETWEEN 2 AND 262144),
                updated_utc TEXT NOT NULL CHECK (
                    length(updated_utc) BETWEEN 20 AND 64)
            );

            INSERT INTO mcp_server_diagnostic_summary(
                singleton_id,
                payload_json,
                updated_utc)
            VALUES (1, '[]', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """),
        new(
            19,
            "revisioned-agent-run-history",
            """
            CREATE TABLE agent_run_history_retention (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                revision INTEGER NOT NULL CHECK (revision > 0),
                maximum_runs INTEGER NOT NULL CHECK (
                    maximum_runs BETWEEN 1 AND 256),
                maximum_age_ticks INTEGER NOT NULL CHECK (
                    maximum_age_ticks BETWEEN 1 AND 315360000000000),
                updated_utc TEXT NOT NULL CHECK (
                    length(updated_utc) BETWEEN 20 AND 64)
            );

            INSERT INTO agent_run_history_retention(
                singleton_id,
                revision,
                maximum_runs,
                maximum_age_ticks,
                updated_utc)
            VALUES (
                1,
                1,
                50,
                77760000000000,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

            CREATE TABLE agent_run_history_retention_events (
                revision INTEGER PRIMARY KEY CHECK (revision > 1),
                previous_maximum_runs INTEGER NOT NULL,
                maximum_runs INTEGER NOT NULL,
                previous_maximum_age_ticks INTEGER NOT NULL,
                maximum_age_ticks INTEGER NOT NULL,
                occurred_utc TEXT NOT NULL CHECK (
                    length(occurred_utc) BETWEEN 20 AND 64)
            ) WITHOUT ROWID;

            CREATE TABLE agent_run_history_metadata (
                run_id TEXT PRIMARY KEY CHECK (length(run_id) BETWEEN 1 AND 256),
                workspace_id TEXT CHECK (
                    workspace_id IS NULL
                    OR length(workspace_id) BETWEEN 1 AND 256),
                provider_id TEXT CHECK (
                    provider_id IS NULL
                    OR length(provider_id) BETWEEN 1 AND 256),
                model_id TEXT CHECK (
                    model_id IS NULL
                    OR length(model_id) BETWEEN 1 AND 256),
                baseline_policy_json TEXT NOT NULL CHECK (
                    json_valid(baseline_policy_json)
                    AND json_type(baseline_policy_json) = 'object'),
                run_policy_json TEXT NOT NULL CHECK (
                    json_valid(run_policy_json)
                    AND json_type(run_policy_json) = 'object'),
                effective_policy_json TEXT NOT NULL CHECK (
                    json_valid(effective_policy_json)
                    AND json_type(effective_policy_json) = 'object'),
                policy_generation INTEGER NOT NULL CHECK (policy_generation > 0),
                updated_utc TEXT NOT NULL CHECK (
                    length(updated_utc) BETWEEN 20 AND 64)
            ) WITHOUT ROWID;

            CREATE INDEX agent_run_history_metadata_scope_idx
                ON agent_run_history_metadata(
                    workspace_id,
                    updated_utc DESC,
                    run_id);

            CREATE TABLE agent_run_history_tombstones (
                run_id TEXT PRIMARY KEY CHECK (length(run_id) BETWEEN 1 AND 256),
                workspace_id TEXT CHECK (
                    workspace_id IS NULL
                    OR length(workspace_id) BETWEEN 1 AND 256),
                deleted_utc TEXT NOT NULL CHECK (
                    length(deleted_utc) BETWEEN 20 AND 64)
            ) WITHOUT ROWID;

            CREATE INDEX agent_run_history_tombstones_scope_idx
                ON agent_run_history_tombstones(
                    workspace_id,
                    deleted_utc DESC,
                    run_id);
            """),
        new(
            20,
            "migrate-durable-definition-payloads",
            """
            -- A definition schema change is a database migration, not a read-time
            -- fallback. The guard makes an unrecognized legacy version abort the
            -- transaction before any payload is rewritten.
            CREATE TABLE definition_payload_migration_v20_guard (
                valid INTEGER NOT NULL CHECK (valid = 1)
            );

            INSERT INTO definition_payload_migration_v20_guard(valid)
            SELECT CASE
                WHEN json_type(payload_json, '$.schemaVersion') = 'integer'
                    AND json_extract(payload_json, '$.schemaVersion') = schema_version
                    AND (
                        (kind = 'theme' AND schema_version IN (1, 2))
                        OR (
                            kind = 'quick-terminal-settings'
                            AND schema_version IN (1, 2)
                            AND (
                                schema_version = 2
                                OR json_type(payload_json, '$.blurRadius') = 'integer'))
                        OR (
                            kind = 'ai-provider-profile'
                            AND (
                                schema_version = 2
                                OR (
                                    schema_version = 1
                                    AND lower(json_extract(
                                        payload_json,
                                        '$.providerKind')) IN (
                                            'anthropic',
                                            'openai',
                                            'openaicompatible'))))
                        OR (
                            kind = 'mcp-server-profile'
                            AND (
                                schema_version = 2
                                OR (
                                    schema_version = 1
                                    AND json_type(
                                        payload_json,
                                        '$.executable') = 'text'
                                    AND json_type(
                                        payload_json,
                                        '$.arguments') = 'array'
                                    AND json_type(
                                        payload_json,
                                        '$.environment') = 'array'))))
                THEN 1
                ELSE 0
            END
            FROM definitions
            WHERE kind IN (
                'theme',
                'quick-terminal-settings',
                'ai-provider-profile',
                'mcp-server-profile');

            -- Theme schema 1 gains the schema-2 defaults. Older schema-2
            -- payloads may still carry settings removed while the format was
            -- in development; translate the blur switch and remove those
            -- retired properties so strict current deserialization succeeds.
            UPDATE definitions
            SET schema_version = 2,
                payload_json = json_remove(
                    CASE
                        WHEN json_type(
                            payload_json,
                            '$.backdropBlurRadius') = 'integer'
                        THEN json_set(
                            payload_json,
                            '$.schemaVersion',
                            2,
                            '$.isTranslucent',
                            json(CASE
                                WHEN json_extract(
                                    payload_json,
                                    '$.backdropBlurRadius') > 0
                                THEN 'true'
                                ELSE 'false'
                            END))
                        ELSE json_set(payload_json, '$.schemaVersion', 2)
                    END,
                    '$.cornerRadiusOverride',
                    '$.backdropBlurRadius')
            WHERE kind = 'theme'
                AND (
                    schema_version = 1
                    OR json_type(
                        payload_json,
                        '$.cornerRadiusOverride') IS NOT NULL
                    OR json_type(
                        payload_json,
                        '$.backdropBlurRadius') IS NOT NULL);

            -- Quick Terminal schema 1 represented translucency as a blur
            -- radius. Preserve the only behavior that value controlled.
            UPDATE definitions
            SET schema_version = 2,
                payload_json = json_remove(
                    CASE
                        WHEN json_type(payload_json, '$.blurRadius') = 'integer'
                        THEN json_set(
                            payload_json,
                            '$.schemaVersion',
                            2,
                            '$.isTranslucent',
                            json(CASE
                                WHEN json_extract(
                                    payload_json,
                                    '$.blurRadius') > 0
                                THEN 'true'
                                ELSE 'false'
                            END))
                        ELSE json_set(payload_json, '$.schemaVersion', 2)
                    END,
                    '$.blurRadius')
            WHERE kind = 'quick-terminal-settings'
                AND (
                    schema_version = 1
                    OR json_type(payload_json, '$.blurRadius') IS NOT NULL);

            -- AI-provider schema 2 split provider identity from wire protocol.
            -- Version 1 had only these three identities, so the mapping is
            -- complete and deterministic. Persist the matching registered
            -- capability ceiling because every schema-2 constructor argument
            -- is required by the strict durable-definition codec.
            UPDATE definitions
            SET schema_version = 2,
                payload_json = json_set(
                    payload_json,
                    '$.schemaVersion',
                    2,
                    '$.protocol',
                    CASE lower(json_extract(payload_json, '$.providerKind'))
                        WHEN 'anthropic' THEN 'AnthropicMessages'
                        WHEN 'openai' THEN 'OpenAiResponses'
                        WHEN 'openaicompatible' THEN 'OpenAiChatCompletions'
                    END,
                    '$.capabilities',
                    json_object(
                        'supportsToolCalling', json('true'),
                        'supportsToolBatches', json('true'),
                        'supportsImageInput', json('true'),
                        'supportsReasoning',
                            json(CASE
                                WHEN lower(json_extract(
                                    payload_json,
                                    '$.providerKind')) = 'openaicompatible'
                                THEN 'false'
                                ELSE 'true'
                            END),
                        'supportsModelDiscovery', json('true')))
            WHERE kind = 'ai-provider-profile'
                AND schema_version = 1;

            -- MCP schema 2 wraps the original stdio fields in a discriminated
            -- transport. All version-1 profiles were stdio profiles.
            UPDATE definitions
            SET schema_version = 2,
                payload_json = json_remove(
                    json_set(
                        payload_json,
                        '$.schemaVersion',
                        2,
                        '$.transport',
                        json_object(
                            '$type',
                            'stdio',
                            'executable',
                            json_extract(payload_json, '$.executable'),
                            'arguments',
                            json(json_extract(payload_json, '$.arguments')),
                            'workingDirectory',
                            json_extract(payload_json, '$.workingDirectory'),
                            'environment',
                            json(json_extract(payload_json, '$.environment')))),
                    '$.executable',
                    '$.arguments',
                    '$.workingDirectory',
                    '$.environment')
            WHERE kind = 'mcp-server-profile'
                AND schema_version = 1;

            DROP TABLE definition_payload_migration_v20_guard;
            """,
            IsDestructive: true),
    ];
}
