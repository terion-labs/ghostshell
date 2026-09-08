using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class UnifiedConnectionEditorViewModelTests
{
    [Fact]
    public async Task Inline_database_tunnel_requires_exact_host_key_review_and_keeps_its_draft_identity()
    {
        var client = new StructuralDatabaseClient();
        var security = new DatabaseHostKeyRuntime();
        var editor = new DatabaseConnectionEditorViewModel(client, [], securityRuntime: security)
        {
            Name = "Database",
            Host = "db.test",
            TunnelHost = "bastion.test",
            TunnelUsername = "user",
        };
        editor.SelectedTunnel = editor.TunnelOptions.Single(option => option.IsInline);
        Assert.Equal(SshHostKeyPolicy.Strict, editor.TunnelHostKeyPolicy);
        await editor.TestAsync(CancellationToken.None);
        Assert.True(editor.HasHostKeyReview);
        Assert.Null(client.LastProbeConnectionString);
        var review = Assert.IsType<SshHostKeyReview>(editor.HostKeyReview);
        await editor.TrustHostKeyAsync(SshHostKeyReviewId.New(), CancellationToken.None);
        Assert.Null(security.Confirmed);
        await editor.TrustHostKeyAsync(review.Id, CancellationToken.None);
        Assert.Equal(review.Id, security.Confirmed!.ReviewId);
        Assert.NotNull(client.LastProbeConnectionString);
        var request = editor.CreateSaveRequest();
        Assert.Null(request.ExistingId);
        var id = request.DraftId!.Value;
        Assert.Equal(DatabaseConnectionProfile.InlineTunnelId(id), review.ConnectionId);
        var inline = DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(review.ConnectionId,
            "Tunnel", request.InlineTunnel!, new ConnectionAuthentication.SshAgent());
        var saved = new DatabaseConnectionProfile(id, 1, "Saved", "postgres", "Host=db.test", inlineTunnel: inline);
        var reopened = new DatabaseConnectionEditorViewModel(client, [], saved, securityRuntime: security);
        await reopened.TestAsync(CancellationToken.None);
        Assert.False(reopened.HasHostKeyReview);
        Assert.Equal(id, reopened.CreateSaveRequest().ExistingId);
        var otherDraft = new DatabaseConnectionEditorViewModel(client, [], securityRuntime: security)
        {
            Name = "New draft",
            Host = "db.test",
            TunnelHost = "bastion.test",
            TunnelUsername = "user",
        };
        otherDraft.SelectedTunnel = otherDraft.TunnelOptions.Single(option => option.IsInline);
        await otherDraft.TestAsync(CancellationToken.None);
        Assert.True(otherDraft.HasHostKeyReview);
        Assert.NotEqual(id, otherDraft.CreateSaveRequest().DraftId);
    }

    private sealed class DatabaseHostKeyRuntime : IConnectionSecurityRuntime
    {
        private readonly HashSet<ConnectionId> _trusted = [];
        public SshHostKeyTrustRequest? Confirmed { get; private set; }
        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> PrepareSshHostKeyAsync(ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress, CancellationToken cancellationToken)
        {
            var identity = new SshHostKeyIdentity("ssh-ed25519", "SHA256:" + new string('A', 43));
            var trusted = _trusted.Contains(profile.Id);
            return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(new SshHostKeyReview(
                SshHostKeyReviewId.New(), profile.Id, "bastion.test:22",
                trusted ? SshHostKeyDisposition.Trusted : SshHostKeyDisposition.Unknown,
                identity, trusted ? identity : null, DateTimeOffset.UtcNow.AddMinutes(1))));
        }
        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress, CancellationToken cancellationToken) =>
            PrepareSshHostKeyAsync(profile, progress, cancellationToken);
        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(SshHostKeyTrustRequest request,
            CancellationToken cancellationToken)
        {
            Confirmed = request;
            _trusted.Add(request.ConnectionId);
            var identity = new SshHostKeyIdentity("ssh-ed25519", "SHA256:" + new string('A', 43));
            return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(new SshHostKeyReview(
                request.ReviewId, request.ConnectionId, "bastion.test:22", SshHostKeyDisposition.Trusted,
                identity, identity, DateTimeOffset.UtcNow.AddMinutes(1))));
        }
        public ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Type_options_span_every_available_family()
    {
        var editor = CreateEditor();

        var families = editor.TypeOptions
            .Select(option => option.Family)
            .Distinct()
            .ToArray();

        Assert.Equal(
            [
                SavedConnectionFamily.Terminal,
                SavedConnectionFamily.Files,
                SavedConnectionFamily.Database,
            ],
            families);
        Assert.Contains(editor.TypeOptions, option => string.Equals(option.DisplayName, "Terminal · SSH", StringComparison.Ordinal));
        Assert.Contains(editor.TypeOptions, option => string.Equals(option.DisplayName, "Files · SFTP", StringComparison.Ordinal));
        Assert.Contains(editor.TypeOptions, option => string.Equals(option.DisplayName, "Database · PostgreSQL", StringComparison.Ordinal));
    }

    [Fact]
    public void Locked_family_restricts_the_type_selector_to_that_family()
    {
        var editor = CreateEditor(
            lockedFamily: SavedConnectionFamily.Files,
            initialFamily: SavedConnectionFamily.Files);

        Assert.All(
            editor.TypeOptions,
            option => Assert.Equal(SavedConnectionFamily.Files, option.Family));
        Assert.True(editor.IsFiles);
    }

    [Fact]
    public void Selecting_a_type_switches_family_and_pushes_the_kind_down()
    {
        var editor = CreateEditor();

        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Files · WebDAV", StringComparison.Ordinal));

        Assert.True(editor.IsFiles);
        Assert.Equal(FileProviderKind.WebDav, editor.Files!.Kind);

        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Database · SQLite", StringComparison.Ordinal));

        Assert.True(editor.IsDatabase);
        Assert.Equal("sqlite", editor.Database!.SelectedDriver.Id);
        Assert.True(editor.CanTest);
        Assert.Equal("Test", editor.TestLabel);
    }

    [Fact]
    public void Name_survives_switching_families()
    {
        var editor = CreateEditor();
        editor.Name = "Production";

        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Database · PostgreSQL", StringComparison.Ordinal));

        Assert.Equal("Production", editor.Name);
        Assert.Equal("Production", editor.Database!.Name);
    }

    [Fact]
    public void Save_result_matches_the_selected_family()
    {
        var editor = CreateEditor();
        editor.Name = "Local box";

        var terminal = Assert.IsType<UnifiedConnectionEditorResult.Terminal>(
            editor.CreateSaveResult(saveConnection: false));
        Assert.False(terminal.SaveConnection);
        Assert.Equal("Local box", terminal.Request.Profile.Name);

        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Database · PostgreSQL", StringComparison.Ordinal));
        editor.Database!.Host = "db.example";
        editor.Database.Port = "5433";
        editor.Database.DatabaseName = "app";

        var database = Assert.IsType<UnifiedConnectionEditorResult.Database>(
            editor.CreateSaveResult());
        Assert.Equal("postgres", database.Request.DriverId);
        Assert.Equal("db.example", database.Request.Details.Host);
        Assert.Equal(5433, database.Request.Details.Port);
    }

    [Fact]
    public void Git_types_join_the_terminal_family_as_their_own_group()
    {
        var editor = CreateEditor();

        Assert.Contains(editor.TypeOptions, option => string.Equals(option.DisplayName, "Git · Local repository", StringComparison.Ordinal));
        Assert.Contains(editor.TypeOptions, option => string.Equals(option.DisplayName, "Git · SSH", StringComparison.Ordinal));

        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Git · SSH", StringComparison.Ordinal));

        Assert.True(editor.IsTerminal);
        Assert.Equal(ConnectionKind.Ssh, editor.Terminal.Kind);
        Assert.True(editor.Terminal.OpensGitRepository);
        Assert.True(editor.Terminal.IsSsh);
        Assert.False(editor.Terminal.ShowsStartupOptions);
        Assert.Equal("Run diagnostics", editor.TestLabel);

        // Leaving the Git group clears the repository mode again.
        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Terminal · SSH", StringComparison.Ordinal));

        Assert.False(editor.Terminal.OpensGitRepository);
        Assert.True(editor.Terminal.ShowsStartupOptions);
    }

    [Fact]
    public void A_git_type_saves_a_profile_that_opens_its_repository()
    {
        var editor = CreateEditor();
        editor.Name = "GhostSHELL repo";
        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Git · Local repository", StringComparison.Ordinal));

        // The repository path is required before the profile can be built.
        Assert.Throws<ArgumentException>(() => editor.CreateSaveResult());

        editor.Terminal.RepositoryPath = "  /repo/ghostshell  ";
        var result = Assert.IsType<UnifiedConnectionEditorResult.Terminal>(
            editor.CreateSaveResult());
        var profile = result.Request.Profile;

        Assert.Equal(ConnectionKind.Local, profile.ConnectionKind);
        Assert.Equal(PanelKind.Git, profile.PreferredPanel);
        Assert.Equal("/repo/ghostshell", profile.Startup.Directory);
        Assert.Equal(PanelKind.Git, profile.PanelLaunchCapabilities.DefaultPanel);
        Assert.Contains(PanelKind.Terminal, profile.PanelLaunchCapabilities.SupportedPanels);
    }

    [Fact]
    public void A_git_ssh_type_validates_the_host_like_a_terminal_ssh_type()
    {
        var editor = CreateEditor();
        editor.Name = "Repo over SSH";
        editor.SelectedType = editor.TypeOptions
            .Single(option => string.Equals(option.DisplayName, "Git · SSH", StringComparison.Ordinal));
        editor.Terminal.RepositoryPath = "/srv/repo";

        Assert.Throws<ArgumentException>(() => editor.CreateSaveResult());

        editor.Terminal.Host = "bastion.example";
        var profile = Assert.IsType<UnifiedConnectionEditorResult.Terminal>(
            editor.CreateSaveResult()).Request.Profile;

        Assert.Equal(PanelKind.Git, profile.PreferredPanel);
        Assert.Equal("/srv/repo", profile.Startup.Directory);
        var ssh = Assert.IsType<ConnectionEndpoint.Ssh>(profile.Endpoint);
        Assert.Equal("bastion.example", ssh.Host);
    }

    [Fact]
    public void An_existing_git_connection_round_trips_through_the_editor()
    {
        var existing = new ConnectionProfile(
            ConnectionId.New(),
            ConnectionProfile.CurrentSchemaVersion,
            "Repo over SSH",
            new ConnectionEndpoint.Ssh("bastion.example", 2202, "ops"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/repo"),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew,
            preferredPanel: PanelKind.Git);
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            existing,
            expectedRevision: 3);
        var editor = new UnifiedConnectionEditorViewModel(
            terminal,
            files: null,
            database: null,
            lockedFamily: SavedConnectionFamily.Terminal);

        Assert.Equal("Git · SSH", editor.SelectedType.DisplayName);
        Assert.True(terminal.OpensGitRepository);
        Assert.Equal("/srv/repo", terminal.RepositoryPath);
        Assert.Equal("bastion.example", terminal.Host);
        Assert.Equal(2202, terminal.Port);
        Assert.Equal("ops", terminal.Username);

        var saved = Assert.IsType<UnifiedConnectionEditorResult.Terminal>(
            editor.CreateSaveResult());
        Assert.Equal(existing.Id, saved.Request.Profile.Id);
        Assert.Equal(PanelKind.Git, saved.Request.Profile.PreferredPanel);
        Assert.Equal("/srv/repo", saved.Request.Profile.Startup.Directory);
        Assert.Equal(3, saved.Request.ExpectedRevision!.Value);
    }

    [Fact]
    public void Repository_browsing_needs_the_git_client_and_a_supported_endpoint()
    {
        var withoutClient = new ConnectionEditorViewModel(new StubConnectionRuntime())
        {
            OpensGitRepository = true,
        };
        Assert.False(withoutClient.CanBrowseRepository);
        Assert.Throws<InvalidOperationException>(() => withoutClient.CreateRepositoryPicker());

        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            gitClient: new FakeGitRepositoryClient())
        {
            OpensGitRepository = true,
            RepositoryPath = "/repo/ghostshell",
        };
        Assert.True(terminal.CanBrowseRepository);
        Assert.NotNull(terminal.CreateRepositoryPicker());

        terminal.Kind = ConnectionKind.Docker;
        Assert.False(terminal.CanBrowseRepository);
    }

    [Fact]
    public async Task Git_diagnostics_also_prove_the_repository_opens()
    {
        var client = new FakeGitRepositoryClient();
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            gitClient: client)
        {
            Name = "GhostSHELL repo",
            OpensGitRepository = true,
            RepositoryPath = "/repo/ghostshell",
        };

        await terminal.TestAsync(CancellationToken.None);

        Assert.Equal("Repository opened", terminal.TestStatus);
        Assert.Contains("/repo", terminal.TestDetail, StringComparison.Ordinal);

        client.FailNextOpen = true;
        await terminal.TestAsync(CancellationToken.None);

        Assert.Equal("Repository not opened", terminal.TestStatus);
        Assert.Contains("refusal", terminal.TestDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_ssh_connections_offer_a_reference_source_for_git_ssh_only()
    {
        var bastion = SshConnection("bastion");
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            savedConnections:
            [
                bastion,
                LocalConnection("local shell"),
                GitReferenceConnection(bastion.Id, "already a reference"),
            ]);

        // Only standalone SSH-kind saved connections appear, after "Enter
        // manually": a profile that already delegates cannot be a source, so
        // references never chain.
        Assert.Equal(
            ["Enter manually", "bastion"],
            [.. terminal.SavedSshSources.Select(option => option.DisplayName)]);
        Assert.Equal(SavedSshSourceOption.Manual, terminal.SelectedSavedSshSource);

        // The selector belongs to the Git · SSH type alone.
        Assert.False(terminal.ShowsSavedSshSources);
        terminal.Kind = ConnectionKind.Ssh;
        Assert.False(terminal.ShowsSavedSshSources);
        terminal.OpensGitRepository = true;
        Assert.True(terminal.ShowsSavedSshSources);
        terminal.Kind = ConnectionKind.Local;
        Assert.False(terminal.ShowsSavedSshSources);
    }

    [Fact]
    public void Choosing_a_saved_connection_folds_the_endpoint_fields_behind_a_summary()
    {
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            savedConnections: [SshConnection("bastion")])
        {
            Kind = ConnectionKind.Ssh,
            OpensGitRepository = true,
        };

        // Manual entry shows the fields; a chosen source speaks for them, and
        // the summary reads the referenced connection's endpoint, not the
        // fields, which stay untouched.
        Assert.True(terminal.ShowsSshEndpointFields);
        terminal.SelectedSavedSshSource = terminal.SavedSshSources[1];
        Assert.False(terminal.ShowsSshEndpointFields);
        Assert.Equal("ops@bastion.example · port 22", terminal.SavedSshSourceSummary);
        Assert.Equal(string.Empty, terminal.Host);

        // Returning to manual entry brings the fields back, still untouched.
        terminal.SelectedSavedSshSource = SavedSshSourceOption.Manual;
        Assert.True(terminal.ShowsSshEndpointFields);
        Assert.Equal(string.Empty, terminal.Host);
    }

    [Fact]
    public void Selecting_a_saved_connection_stores_a_reference_not_a_copy()
    {
        var source = new ConnectionProfile(
            ConnectionId.New(),
            ConnectionProfile.CurrentSchemaVersion,
            "bastion",
            new ConnectionEndpoint.Ssh("bastion.example", 2202, "ops"),
            new ConnectionAuthentication.PrivateKey(
                new SecretRef("key-ref"),
                new SecretRef("phrase-ref")),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            savedConnections: [source])
        {
            Name = "Repo over SSH",
            Kind = ConnectionKind.Ssh,
            OpensGitRepository = true,
            RepositoryPath = "/srv/repo",
        };

        terminal.SelectedSavedSshSource = terminal.SavedSshSources[1];

        // The profile references the source and stores no endpoint or
        // credentials of its own — only the stand-in the schema requires.
        var profile = terminal.CreateSaveRequest().Profile;
        Assert.Equal(source.Id, profile.HostConnectionId);
        Assert.NotEqual(source.Id, profile.Id);
        Assert.Equal(ConnectionProfile.DelegatedSshEndpoint, profile.Endpoint);
        Assert.IsType<ConnectionAuthentication.None>(profile.Authentication);
        Assert.Equal("/srv/repo", profile.Startup.Directory);
        Assert.Equal(PanelKind.Git, profile.PreferredPanel);

        // Selecting never wrote into the endpoint fields.
        Assert.Equal(string.Empty, terminal.Host);
        Assert.Equal(string.Empty, terminal.Username);

        // Resolution at use hands back the source's current endpoint and
        // credentials under this profile's identity and repository path.
        var resolved = profile.ResolveHostConnection(
            id => id == source.Id ? source : null);
        Assert.NotNull(resolved);
        Assert.Equal(profile.Id, resolved!.Id);
        var ssh = Assert.IsType<ConnectionEndpoint.Ssh>(resolved.Endpoint);
        Assert.Equal("bastion.example", ssh.Host);
        Assert.Equal(2202, ssh.Port);
        Assert.Equal(source.Authentication, resolved.Authentication);
        Assert.Equal(SshHostKeyPolicy.AcceptNew, resolved.HostKeyPolicy);
        Assert.Equal("/srv/repo", resolved.Startup.Directory);
    }

    [Fact]
    public void Returning_to_manual_entry_builds_a_standalone_profile_from_the_fields()
    {
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            savedConnections: [SshConnection("bastion")])
        {
            Name = "Repo over SSH",
            Kind = ConnectionKind.Ssh,
            OpensGitRepository = true,
            RepositoryPath = "/srv/repo",
        };

        terminal.SelectedSavedSshSource = terminal.SavedSshSources[1];
        terminal.SelectedSavedSshSource = SavedSshSourceOption.Manual;
        terminal.Host = "edited.example";

        var profile = terminal.CreateSaveRequest().Profile;
        Assert.Null(profile.HostConnectionId);
        var ssh = Assert.IsType<ConnectionEndpoint.Ssh>(profile.Endpoint);
        Assert.Equal("edited.example", ssh.Host);
    }

    [Fact]
    public void A_reference_profile_reopens_with_the_selector_on_its_source()
    {
        var source = SshConnection("bastion");
        var existing = GitReferenceConnection(source.Id);
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            existing,
            expectedRevision: 3,
            savedConnections: [source, existing]);

        Assert.Equal("bastion", terminal.SelectedSavedSshSource.DisplayName);
        Assert.False(terminal.ShowsSshEndpointFields);
        // The summary reflects the source's CURRENT endpoint, and the stored
        // stand-in endpoint never leaks into the fields.
        Assert.Equal("ops@bastion.example · port 22", terminal.SavedSshSourceSummary);
        Assert.Equal(string.Empty, terminal.Host);

        var saved = terminal.CreateSaveRequest().Profile;
        Assert.Equal(existing.Id, saved.Id);
        Assert.Equal(source.Id, saved.HostConnectionId);
    }

    [Fact]
    public void A_reference_profile_with_a_missing_source_falls_back_to_manual_entry()
    {
        var existing = GitReferenceConnection(ConnectionId.New());
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            existing,
            expectedRevision: 3,
            savedConnections: [existing, SshConnection("bastion")]);

        Assert.Equal(SavedSshSourceOption.Manual, terminal.SelectedSavedSshSource);
        Assert.True(terminal.ShowsSshEndpointFields);
        Assert.Equal(string.Empty, terminal.Host);
        Assert.Equal("Referenced connection missing", terminal.TestStatus);
        Assert.Contains("no longer exists", terminal.TestDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_diagnostics_resolve_the_referenced_connection()
    {
        var runtime = new StubConnectionRuntime();
        var client = new FakeGitRepositoryClient();
        var terminal = new ConnectionEditorViewModel(
            runtime,
            gitClient: client,
            savedConnections: [SshConnection("bastion")])
        {
            Name = "Repo over SSH",
            Kind = ConnectionKind.Ssh,
            OpensGitRepository = true,
            RepositoryPath = "/srv/repo",
        };
        terminal.SelectedSavedSshSource = terminal.SavedSshSources[1];

        await terminal.TestAsync(CancellationToken.None);

        // The test ran against the referenced endpoint, not the stand-in, and
        // the repository probe still proved the path opens on that target.
        var tested = Assert.IsType<ConnectionEndpoint.Ssh>(
            runtime.LastTestedProfile!.Endpoint);
        Assert.Equal("bastion.example", tested.Host);
        Assert.IsType<ConnectionAuthentication.SshAgent>(
            runtime.LastTestedProfile.Authentication);
        Assert.Equal("Repository opened", terminal.TestStatus);
    }

    [Fact]
    public void The_saved_connection_selector_hides_without_ssh_connections()
    {
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            savedConnections: [LocalConnection("local shell")])
        {
            Kind = ConnectionKind.Ssh,
            OpensGitRepository = true,
        };

        Assert.Equal(
            ["Enter manually"],
            [.. terminal.SavedSshSources.Select(option => option.DisplayName)]);
        Assert.False(terminal.ShowsSavedSshSources);
    }

    [Fact]
    public void Editing_an_existing_git_ssh_profile_defaults_to_manual_entry()
    {
        var existing = new ConnectionProfile(
            ConnectionId.New(),
            ConnectionProfile.CurrentSchemaVersion,
            "Repo over SSH",
            new ConnectionEndpoint.Ssh("bastion.example", 2202, "ops"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/repo"),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew,
            preferredPanel: PanelKind.Git);
        var terminal = new ConnectionEditorViewModel(
            new StubConnectionRuntime(),
            existing,
            expectedRevision: 3,
            savedConnections: [existing, SshConnection("bastion")]);

        // The fields already hold the profile; nothing was copied over them,
        // and the profile never offers itself as a source.
        Assert.Equal(SavedSshSourceOption.Manual, terminal.SelectedSavedSshSource);
        Assert.Equal(
            ["Enter manually", "bastion"],
            [.. terminal.SavedSshSources.Select(option => option.DisplayName)]);
        Assert.Equal("bastion.example", terminal.Host);
        Assert.Equal(2202, terminal.Port);
    }

    [Fact]
    public void Database_editor_round_trips_an_existing_profile()
    {
        var tunnel = SshConnection("bastion");
        var existing = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Analytics",
            "postgres",
            "Host=warehouse;Port=5432;Database=events;Username=reader",
            passwordSecret: new SecretRef("stored-password"),
            tunnelConnectionId: tunnel.Id);

        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [tunnel],
            existing);

        Assert.True(editor.IsEditing);
        Assert.True(editor.HasStoredPassword);
        Assert.Equal("Analytics", editor.Name);
        Assert.Equal("postgres", editor.SelectedDriver.Id);
        Assert.Equal("warehouse", editor.Host);
        Assert.Equal("5432", editor.Port);
        Assert.Equal("events", editor.DatabaseName);
        Assert.Equal("reader", editor.Username);
        Assert.Equal(tunnel.Id, editor.SelectedTunnel.Id);

        var request = editor.CreateSaveRequest();
        Assert.Equal(existing.Id, request.ExistingId);
        Assert.Null(request.Details.Password);
        Assert.Equal(tunnel.Id, request.TunnelConnectionId);
    }

    [Fact]
    public void Database_editor_validates_name_port_and_file_path()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            []);

        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.Name = "Broken";
        editor.Port = "not-a-port";
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.SelectedDriver = editor.Drivers.Single(driver => string.Equals(driver.Id, "sqlite", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.FilePath = "/data/app.db";
        var request = editor.CreateSaveRequest();
        Assert.Equal("/data/app.db", request.Details.FilePath);
    }

    [Fact]
    public void Database_tunnel_options_offer_only_ssh_connections()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [SshConnection("bastion"), LocalConnection("local shell")]);

        Assert.Equal(
            ["No tunnel", "bastion", "Custom — this connection only"],
            [.. editor.TunnelOptions.Select(option => option.DisplayName)]);
    }

    [Fact]
    public void A_pasted_connection_string_is_the_other_view_of_the_same_fields()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [])
        {
            Name = "Pasted",
            UseConnectionString = true,
            ConnectionStringInput = "Host=db.example;Port=5433;Database=app;Username=reader;Password=secret"
        };

        var request = editor.CreateSaveRequest();
        Assert.Equal("db.example", request.Details.Host);
        Assert.Equal(5433, request.Details.Port);
        Assert.Equal("secret", request.Details.Password);

        // Switching back to the fields reads the string into them.
        editor.UseConnectionString = false;
        Assert.Equal("db.example", editor.Host);
        Assert.Equal("5433", editor.Port);
        Assert.Equal("app", editor.DatabaseName);
        Assert.Equal("reader", editor.Username);
        Assert.Equal("secret", editor.Password);
    }

    [Fact]
    public void Switching_to_the_string_view_prefills_from_fields_without_the_password()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [])
        {
            Host = "db.example",
            Port = "5432",
            DatabaseName = "app",
            Password = "secret",

            UseConnectionString = true
        };

        Assert.Contains("Host=db.example", editor.ConnectionStringInput, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", editor.ConnectionStringInput, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_tunnel_travels_in_the_save_request_instead_of_a_reference()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [SshConnection("bastion")])
        {
            Name = "Tunnelled",
            Host = "db.internal"
        };
        editor.SelectedTunnel = editor.TunnelOptions.Single(option => option.IsInline);
        editor.TunnelHost = "bastion.example";
        editor.TunnelUsername = "ops";

        var request = editor.CreateSaveRequest();

        Assert.Null(request.TunnelConnectionId);
        Assert.NotNull(request.InlineTunnel);
        var inline = request.InlineTunnel!;
        Assert.Equal("bastion.example", inline.Host);
        Assert.Equal(22, inline.Port);
        Assert.Equal("ops", inline.Username);
        Assert.True(inline.UseAgent);
    }

    [Fact]
    public void An_inline_password_tunnel_without_any_password_is_refused()
    {
        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [])
        {
            Name = "Tunnelled",
            Host = "db.internal"
        };
        editor.SelectedTunnel = editor.TunnelOptions.Single(option => option.IsInline);
        editor.TunnelHost = "bastion.example";
        editor.TunnelAuthentication = DatabaseConnectionEditorViewModel.PasswordAuthentication;

        Assert.Throws<ArgumentException>(() => editor.CreateSaveRequest());

        editor.TunnelPassword = "hunter2";
        var inline = editor.CreateSaveRequest().InlineTunnel;
        Assert.NotNull(inline);
        Assert.False(inline!.UseAgent);
        Assert.Equal("hunter2", inline.Password);
    }

    [Fact]
    public void An_existing_inline_tunnel_round_trips_through_the_editor()
    {
        var profileId = DatabaseConnectionProfileId.New();
        var inline = DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
            DatabaseConnectionProfile.InlineTunnelId(profileId),
            "Analytics tunnel",
            new DatabaseInlineTunnelRequest("bastion.example", 2222, "ops", false, "kept"),
            new ConnectionAuthentication.Password(new SecretRef("tunnel-password")));
        var existing = new DatabaseConnectionProfile(
            profileId,
            DatabaseConnectionProfile.CurrentSchemaVersion,
            "Analytics",
            "postgres",
            "Host=warehouse;Database=events",
            inlineTunnel: inline);

        var editor = new DatabaseConnectionEditorViewModel(
            new StructuralDatabaseClient(),
            [],
            existing);

        Assert.True(editor.SelectedTunnel.IsInline);
        Assert.Equal("bastion.example", editor.TunnelHost);
        Assert.Equal("2222", editor.TunnelPort);
        Assert.Equal("ops", editor.TunnelUsername);
        Assert.True(editor.HasStoredTunnelPassword);
        Assert.True(editor.TunnelUsesPassword);

        // Saving without retyping keeps the stored tunnel password.
        var request = editor.CreateSaveRequest();
        Assert.NotNull(request.InlineTunnel);
        var kept = request.InlineTunnel!;
        Assert.False(kept.UseAgent);
        Assert.Null(kept.Password);
    }

    [Fact]
    public async Task The_database_test_reports_the_servers_session_facts()
    {
        var client = new StructuralDatabaseClient
        {
            SessionInfo = new DatabaseSessionInfo("16.4", "TLSv1.3"),
        };
        var editor = new DatabaseConnectionEditorViewModel(client, [])
        {
            Name = "Probe",
            Host = "db.example",
            Password = "secret"
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Connected", editor.TestStatus);
        Assert.Contains("16.4", editor.TestDetail, StringComparison.Ordinal);
        Assert.Contains("TLSv1.3", editor.TestDetail, StringComparison.Ordinal);
        // The probe connects with the typed password even though the saved
        // string never carries it.
        Assert.Contains("Password=secret", client.LastProbeConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_database_test_reports_the_reason_and_keeps_the_dialog_open()
    {
        var client = new StructuralDatabaseClient
        {
            ProbeError = new InvalidOperationException("connection refused"),
        };
        var editor = new DatabaseConnectionEditorViewModel(client, [])
        {
            Name = "Probe",
            Host = "db.example"
        };

        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Test failed", editor.TestStatus);
        Assert.Equal("connection refused", editor.TestDetail);
    }

    [Fact]
    public async Task Unified_database_test_surfaces_progress_and_result_in_the_fixed_footer()
    {
        var probe = new TaskCompletionSource<DatabaseSessionInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StructuralDatabaseClient { PendingProbe = probe };
        var database = new DatabaseConnectionEditorViewModel(client, [])
        {
            Name = "Local Redis",
            Host = "localhost",
        };
        database.SelectedDriver = database.Drivers.Single(driver => string.Equals(driver.Id, RedisDatabase.DriverId, StringComparison.Ordinal));
        var editor = new UnifiedConnectionEditorViewModel(
            new ConnectionEditorViewModel(new StubConnectionRuntime()),
            files: null,
            database,
            lockedFamily: SavedConnectionFamily.Database,
            initialFamily: SavedConnectionFamily.Database);

        var test = editor.TestAsync(CancellationToken.None);

        Assert.True(editor.IsTesting);
        Assert.True(editor.HasTestFeedback);
        Assert.Equal("Testing…", editor.TestLabel);
        Assert.Contains("Testing connection", editor.TestFooterHint, StringComparison.Ordinal);

        probe.SetResult(new DatabaseSessionInfo("8.2", null));
        await test;

        Assert.False(editor.IsTesting);
        Assert.Equal("Test", editor.TestLabel);
        Assert.Equal("Connected", editor.TestStatus);
        Assert.Contains("Redis 8.2", editor.TestFooterHint, StringComparison.Ordinal);
    }

    private static UnifiedConnectionEditorViewModel CreateEditor(
        SavedConnectionFamily? lockedFamily = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = new ConnectionEditorViewModel(new StubConnectionRuntime());
        var files = lockedFamily is null or SavedConnectionFamily.Files
            ? new FileProviderProfileEditorViewModel(
                new StubProviderRuntime(),
                [SshConnection("bastion")],
                [])
            : null;
        var database = lockedFamily is null or SavedConnectionFamily.Database
            ? new DatabaseConnectionEditorViewModel(
                new StructuralDatabaseClient(),
                [SshConnection("bastion")])
            : null;
        return new UnifiedConnectionEditorViewModel(
            terminal,
            files,
            database,
            lockedFamily,
            initialFamily);
    }

    private static ConnectionProfile SshConnection(string name) => new(
        ConnectionId.New(),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Ssh("bastion.example", username: "ops"),
        new ConnectionAuthentication.SshAgent(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private static ConnectionProfile LocalConnection(string name) => new(
        ConnectionId.New(),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    /// <summary>
    /// A Git · SSH profile that delegates its endpoint to a saved connection:
    /// stand-in endpoint, no credentials of its own, repository path kept.
    /// </summary>
    private static ConnectionProfile GitReferenceConnection(
        ConnectionId hostConnectionId,
        string name = "Repo over SSH") => new(
        ConnectionId.New(),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        ConnectionProfile.DelegatedSshEndpoint,
        new ConnectionAuthentication.None(),
        new ConnectionStartup("/srv/repo"),
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.Strict,
        preferredPanel: PanelKind.Git,
        hostConnectionId: hostConnectionId);

    private sealed class StubConnectionRuntime : IConnectionRuntime
    {
        public ConnectionProfile? LastTestedProfile { get; private set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastTestedProfile = profile;
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    profile.Id,
                    profile.ConnectionKind,
                    ConnectionTestVerification.RuntimeAvailable,
                    false)));
        }
    }

    private sealed class StubProviderRuntime : IFileProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(true, "ok", profile.Name));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A structural fake that parses and rebuilds "Key=Value;…" strings, so the
    /// round-trip assertions exercise real field mapping rather than passthrough.
    /// </summary>
    private sealed class StructuralDatabaseClient : IDatabasePanelClient
    {
        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("postgres", "PostgreSQL", "Host=…", DefaultPort: 5432, CanListDatabases: true),
            new("sqlite", "SQLite", "/path/to/database.db", IsFileBased: true),
            RedisDatabase.Descriptor,
        ];

        public DatabaseSessionInfo SessionInfo { get; init; } = new();

        public Exception? ProbeError { get; init; }

        public TaskCompletionSource<DatabaseSessionInfo>? PendingProbe { get; init; }

        public string? LastProbeConnectionString { get; private set; }

        public Task<DatabaseSessionInfo> DescribeSessionAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            LastProbeConnectionString = connectionString;
            return ProbeError is { } error
                ? Task.FromException<DatabaseSessionInfo>(error)
                : PendingProbe?.Task ?? Task.FromResult(SessionInfo);
        }

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>([]);

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
            $"SELECT * FROM {tableName} LIMIT {limit};";

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString)
        {
            var values = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            return new DatabaseConnectionDetails(
                values.GetValueOrDefault("Host"),
                values.TryGetValue("Port", out var port) ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : null,
                values.GetValueOrDefault("Database"),
                values.GetValueOrDefault("Username"),
                values.GetValueOrDefault("Password"),
                values.GetValueOrDefault("Data Source"));
        }

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
        {
            var pairs = new List<string>();
            if (details.FilePath is { } filePath)
            {
                pairs.Add($"Data Source={filePath}");
            }

            if (details.Host is { } host)
            {
                pairs.Add($"Host={host}");
            }

            if (details.Port is { } port)
            {
                pairs.Add($"Port={port}");
            }

            if (details.Database is { } database)
            {
                pairs.Add($"Database={database}");
            }

            if (details.Username is { } username)
            {
                pairs.Add($"Username={username}");
            }

            if (details.Password is { } password)
            {
                pairs.Add($"Password={password}");
            }

            return string.Join(';', pairs);
        }
    }
}
