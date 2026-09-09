using System.Collections.Immutable;
using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentFileActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FileOperation.List, BuiltInAgentTools.FilesList)]
    [InlineData(FileOperation.Stat, BuiltInAgentTools.FilesStat)]
    [InlineData(FileOperation.Read, BuiltInAgentTools.FilesRead)]
    [InlineData(
        FileOperation.CreateDirectory,
        BuiltInAgentTools.FilesCreateDirectory)]
    [InlineData(FileOperation.Move, BuiltInAgentTools.FilesMove)]
    [InlineData(FileOperation.Delete, BuiltInAgentTools.FilesDelete)]
    public void Closed_request_kinds_map_to_trusted_tools(
        FileOperation operation,
        string expectedTool)
    {
        var request = Request(operation, "logs", "today.txt");
        var context = FileContext();

        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            context,
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(expectedTool, action.Proposal.ToolName);
        Assert.Same(context.Target, action.Proposal.Target);
        Assert.Equal(context.BindingFingerprint, action.Proposal.TargetFingerprint);
        Assert.Equal(
            AgentTargetIdentity.Create(context.Target),
            action.Proposal.TargetIdentity);
    }

    [Fact]
    public void File_request_action_result_and_host_port_have_closed_typed_shapes()
    {
        var requestKinds = typeof(AgentFileRequest)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var resultKinds = typeof(AgentFileActionResult)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var hostMethod = Assert.Single(typeof(IAgentFileSessionHost).GetMethods());

        Assert.True(typeof(AgentFileRequest).IsAbstract);
        Assert.Equal(
            [
                "AccessRead",
                "Copy",
                "CreateDirectory",
                "CreateText",
                "Delete",
                "List",
                "Move",
                "Read",
                "ReplaceText",
                "Search",
                "Stat",
                "Transfers",
            ],
            requestKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(requestKinds, type => Assert.True(type.IsSealed));
        Assert.All(requestKinds, type => Assert.Contains(
            type.GetProperties(),
            property => property is
            {
                Name: "SessionId",
                PropertyType: not null,
            } && property.PropertyType == typeof(SessionId)));
        Assert.DoesNotContain(
            requestKinds.SelectMany(type => type.GetProperties()),
            property => property.Name.Contains("Page", StringComparison.Ordinal)
                || property.Name.Contains("Continuation", StringComparison.Ordinal)
                || property.Name.Contains("Absolute", StringComparison.Ordinal));
        Assert.True(typeof(AgentFileActionResult).IsAbstract);
        Assert.Equal(
            [
                "AccessControl",
                "Copied",
                "CreatedDirectory",
                "CreatedText",
                "Deleted",
                "Entry",
                "Moved",
                "Page",
                "Preview",
                "ReplacedText",
                "SearchResults",
                "Transfers",
            ],
            resultKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(resultKinds, type => Assert.True(type.IsSealed));
        Assert.Empty(typeof(AgentFileAction).GetConstructors());
        Assert.Empty(typeof(AgentActionExecutionBinding).GetConstructors());
        Assert.Equal("RunAgentFileActionAsync", hostMethod.Name);
        Assert.DoesNotContain(
            hostMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(object));
    }

    [Fact]
    public void Built_in_file_tools_have_their_exact_capability_and_risk()
    {
        var expected = new[]
        {
            (
                BuiltInAgentTools.FilesList,
                "List files",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesSearch,
                "Search file names",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesStat,
                "Inspect file metadata",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesAccessRead,
                "Read file access control",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesTransfers,
                "List file transfers",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesRead,
                "Read file preview",
                AgentCapability.ReadFiles,
                AgentActionRisk.Observation),
            (
                BuiltInAgentTools.FilesCreateDirectory,
                "Create directory",
                AgentCapability.EditFiles,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.FilesMove,
                "Move or rename path",
                AgentCapability.EditFiles,
                AgentActionRisk.Mutation),
            (
                BuiltInAgentTools.FilesDelete,
                "Permanently delete path",
                AgentCapability.EditFiles,
                AgentActionRisk.Destructive),
        };

        Assert.Equal("files.list", BuiltInAgentTools.FilesList);
        Assert.Equal("files.search", BuiltInAgentTools.FilesSearch);
        Assert.Equal("files.stat", BuiltInAgentTools.FilesStat);
        Assert.Equal("files.read", BuiltInAgentTools.FilesRead);
        Assert.Equal("files.access_read", BuiltInAgentTools.FilesAccessRead);
        Assert.Equal("files.transfers", BuiltInAgentTools.FilesTransfers);
        Assert.Equal("files.mkdir", BuiltInAgentTools.FilesCreateDirectory);
        Assert.Equal("files.move", BuiltInAgentTools.FilesMove);
        Assert.Equal("files.delete", BuiltInAgentTools.FilesDelete);
        foreach (var (name, title, capability, risk) in expected)
        {
            Assert.True(BuiltInAgentTools.Catalog.TryGet(name, out var descriptor));
            Assert.NotNull(descriptor);
            Assert.Equal(title, descriptor.Title);
            Assert.Equal(capability, descriptor.Capability);
            Assert.Equal(risk, descriptor.Risk);
        }
    }

    [Fact]
    public void List_binds_exact_scope_relative_path_revision_and_fixed_first_page()
    {
        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            FileContext(),
            List("logs", "literal name"));

        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => AssertArgument(argument, "session_id", "file-session-1"),
            argument => AssertArgument(argument, "session_revision", "17"),
            argument => AssertArgument(argument, "provider_profile_id", "files.production"),
            argument => AssertArgument(argument, "authority", "storage.example"),
            argument => AssertArgument(argument, "trusted_root", "/srv/data"),
            argument => AssertArgument(
                argument,
                "relative_path",
                "logs/literal name"),
            argument => AssertArgument(argument, "page_size", "100"),
            argument => AssertArgument(argument, "first_page_only", "true"),
            argument => AssertArgument(argument, "show_hidden", "false"));
        Assert.Equal(
            "Artifacts — panel file-panel-1 — session file-session-1",
            action.Proposal.Presentation.TargetTitle);
        Assert.Equal(
            "File provider files.production (storage.example)",
            action.Proposal.Presentation.Host);
        Assert.Null(action.Proposal.Presentation.WorkingDirectory);
    }

    [Fact]
    public void Read_binds_the_clamped_provider_limit_and_allowed_preview_kinds()
    {
        var metadata = Metadata(maximumPreviewBytes: 4096);
        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            FileContext(metadata: metadata),
            Read("report.json"));

        Assert.Collection(
            action.Proposal.Presentation.Arguments.TakeLast(2),
            argument => AssertArgument(argument, "maximum_bytes", "4096"),
            argument => AssertArgument(
                argument,
                "preview_kinds",
                "text,structured_text"));
        Assert.Equal(
            4096,
            AgentFileActionComposer.GetEffectiveReadMaximumBytes(metadata));
    }

    [Fact]
    public void Search_access_and_transfer_observations_bind_exact_bounds()
    {
        var composer = new AgentFileActionComposer();
        var context = FileContext();
        var search = composer.Prepare(
            Envelope(),
            context,
            new AgentFileRequest.Search(
                Session(),
                Segments("logs"),
                "error",
                FilePanelDiscoveryScope.Subtree,
                17));
        var access = composer.Prepare(
            Envelope(),
            context,
            new AgentFileRequest.AccessRead(
                Session(),
                Segments("report.txt")));
        var transfers = composer.Prepare(
            Envelope(),
            context,
            new AgentFileRequest.Transfers(Session()));

        Assert.Equal(BuiltInAgentTools.FilesSearch, search.Proposal.ToolName);
        Assert.Contains(search.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "query", StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "error", StringComparison.Ordinal));
        Assert.Contains(search.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "scope", StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "subtree", StringComparison.Ordinal));
        Assert.Contains(search.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "maximum_results", StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "17", StringComparison.Ordinal));
        Assert.Contains(access.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "maximum_grants"
, StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "100", StringComparison.Ordinal));
        Assert.Contains(transfers.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "owned_by_session"
, StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "true", StringComparison.Ordinal));
        Assert.NotEqual(search.Proposal.ArgumentDigest, access.Proposal.ArgumentDigest);
        Assert.NotEqual(access.Proposal.ArgumentDigest, transfers.Proposal.ArgumentDigest);
    }

    [Fact]
    public void List_binds_the_clamped_provider_page_limit()
    {
        var metadata = Metadata(maximumListPageSize: 7);
        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            FileContext(metadata: metadata),
            List());

        Assert.Equal(
            "7",
            Assert.Single(
                action.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "page_size", StringComparison.Ordinal)).DisplayValue);
        Assert.Equal(
            7,
            AgentFileActionComposer.GetEffectiveListPageSize(metadata));
    }

    [Fact]
    public void Mutations_bind_host_derived_semantics_into_approval_and_digest()
    {
        var composer = new AgentFileActionComposer();
        var context = FileContext();
        var envelope = Envelope();

        var create = composer.Prepare(
            envelope,
            context,
            CreateDirectory("new-directory"));
        var delete = composer.Prepare(
            envelope,
            context,
            Delete("obsolete.txt"));
        var move = composer.Prepare(
            envelope,
            context,
            Move(["draft.txt"], ["published", "report.txt"]));

        Assert.Collection(
            create.Proposal.Presentation.Arguments.TakeLast(2),
            argument => AssertArgument(
                argument,
                "effect",
                "create_directory"),
            argument => AssertArgument(
                argument,
                "precondition",
                "must_not_exist"));
        Assert.Collection(
            move.Proposal.Presentation.Arguments.TakeLast(4),
            argument => AssertArgument(argument, "entry_ref", Reference().Value),
            argument => AssertArgument(
                argument,
                "destination_relative_path",
                "published/report.txt"),
            argument => AssertArgument(argument, "effect", "move_or_rename"),
            argument => AssertArgument(
                argument,
                "destination_precondition",
                "must_not_exist"));
        Assert.Collection(
            delete.Proposal.Presentation.Arguments.TakeLast(4),
            argument => AssertArgument(argument, "entry_ref", Reference().Value),
            argument => AssertArgument(
                argument,
                "effect",
                "permanent_delete"),
            argument => AssertArgument(argument, "recursive", "false"),
            argument => AssertArgument(
                argument,
                "precondition",
                "must_exist"));
        Assert.NotEqual(
            create.Proposal.ArgumentDigest,
            delete.Proposal.ArgumentDigest);
        Assert.DoesNotContain(
            create.Proposal.Presentation.Arguments,
            argument => argument.Name is "version" or "retry");
        Assert.DoesNotContain(
            delete.Proposal.Presentation.Arguments,
            argument => argument.Name is "version" or "retry");
    }

    [Fact]
    public void Empty_relative_path_resolves_to_the_exact_trusted_root()
    {
        var metadata = Metadata();

        var location = AgentFileActionComposer.ResolveLocation(
            metadata,
            []);
        var path = Assert.IsType<FilePanelAddress.Hierarchical>(location.Address).Path;

        Assert.Equal(metadata.TrustedRoot, location);
        Assert.Equal(["srv", "data"], path.Segments.Select(segment => segment.Value), StringComparer.Ordinal);
    }

    [Fact]
    public void Relative_segments_are_appended_structurally_without_path_string_parsing()
    {
        var location = AgentFileActionComposer.ResolveLocation(
            Metadata(),
            Segments("folder", "literal name", "file.txt"));
        var path = Assert.IsType<FilePanelAddress.Hierarchical>(location.Address).Path;

        Assert.Equal(
            ["srv", "data", "folder", "literal name", "file.txt"],
            path.Segments.Select(segment => segment.Value), StringComparer.Ordinal);
        Assert.Equal("files.production", location.ProviderProfileId);
        Assert.Equal("storage.example", location.Authority);
        Assert.Null(location.Version);
    }

    [Fact]
    public void Every_operation_and_relative_path_change_alters_bound_material()
    {
        var composer = new AgentFileActionComposer();
        var context = FileContext();
        var envelope = Envelope();
        AgentFileRequest[] requests =
        [
            List("first"),
            List("second"),
            Stat("first"),
            Stat("second"),
            Read("first"),
            Read("second"),
            CreateDirectory("first"),
            CreateDirectory("second"),
            Move(["first"], ["moved-first"]),
            Move(["second"], ["moved-second"]),
            Delete("first"),
            Delete("second"),
        ];

        var actions = requests
            .Select(request => composer.Prepare(envelope, context, request))
            .ToArray();

        Assert.Equal(
            actions.Length,
            actions.Select(action => action.Proposal.ArgumentDigest).Distinct().Count());
        Assert.Equal(
            actions.Length,
            actions.Select(ApprovalMaterial).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Trusted_root_authority_bounds_and_session_revision_change_the_digest()
    {
        var composer = new AgentFileActionComposer();
        var request = Read("report.txt");
        var baseline = composer.Prepare(Envelope(), FileContext(), request);
        var variants = new[]
        {
            composer.Prepare(
                Envelope(),
                FileContext(metadata: Metadata(rootSegments: ["srv", "other"])),
                request),
            composer.Prepare(
                Envelope(),
                FileContext(metadata: Metadata(authority: "other.example")),
                request),
            composer.Prepare(
                Envelope(),
                FileContext(metadata: Metadata(maximumPreviewBytes: 1024)),
                request),
            composer.Prepare(
                Envelope(),
                FileContext(sessionRevision: 18),
                request),
        };

        Assert.All(variants, action =>
        {
            Assert.NotEqual(
                baseline.Proposal.ArgumentDigest,
                action.Proposal.ArgumentDigest);
            Assert.NotEqual(ApprovalMaterial(baseline), ApprovalMaterial(action), StringComparer.Ordinal);
        });
    }

    [Fact]
    public void Broad_action_narrows_to_one_exact_file_panel_and_session()
    {
        var broad = MultipleFileContext();
        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            broad,
            new AgentFileRequest.List(
                new SessionId("file-session-2"),
                Segments("logs")));
        var target = Assert.IsType<AgentTarget.Panel>(action.Proposal.Target);

        Assert.Equal(new PanelInstanceId("file-panel-2"), target.PanelId);
        Assert.NotEqual(broad.BindingFingerprint, action.Proposal.TargetFingerprint);
        Assert.Equal(
            AgentTargetIdentity.Create(target),
            action.Proposal.TargetIdentity);
    }

    [Fact]
    public void Exact_session_target_is_supported_and_identifies_its_owner_panel()
    {
        var context = FileContext(
            target: new AgentTarget.ConnectionSession(Session()));
        var action = new AgentFileActionComposer().Prepare(
            Envelope(),
            context,
            Stat("report.txt"));

        Assert.IsType<AgentTarget.ConnectionSession>(action.Proposal.Target);
        Assert.Equal(
            "Artifacts — session file-session-1 — panel file-panel-1",
            action.Proposal.Presentation.TargetTitle);
    }

    [Fact]
    public void Execution_binding_recomputes_fresh_exact_context_evidence()
    {
        var composer = new AgentFileActionComposer();
        var action = composer.Prepare(
            Envelope(),
            FileContext(graphRevision: 11),
            Stat("report.txt"));
        var fresh = FileContext(graphRevision: 12);

        var binding = composer.BindForExecution(action, fresh);

        Assert.Equal(action.Proposal.Id, binding.ActionId);
        Assert.Equal(action.Proposal.TargetIdentity, binding.TargetIdentity);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.Equal(fresh.BindingFingerprint, binding.TargetFingerprint);
        Assert.NotEqual(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
    }

    [Fact]
    public void Execution_binding_rejects_scope_bound_material_drift()
    {
        var composer = new AgentFileActionComposer();
        var action = composer.Prepare(
            Envelope(),
            FileContext(),
            Read("report.txt"));

        Assert.Throws<InvalidOperationException>(() =>
            composer.BindForExecution(
                action,
                FileContext(sessionRevision: 18)));
        Assert.Throws<InvalidOperationException>(() =>
            composer.BindForExecution(
                action,
                FileContext(metadata: Metadata(
                    maximumPreviewBytes: 1024))));
    }

    [Fact]
    public void Missing_or_incoherent_live_file_context_fails_closed()
    {
        var composer = new AgentFileActionComposer();
        var envelope = Envelope();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(envelope, ContextWithoutSession(), List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(envelope, DuplicateSessionContext(), List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(kind: PanelKind.Terminal),
                List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(lifecycle: SessionLifecycle.Closing),
                List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(capabilities: [SessionCapabilities.FilesStat]),
                List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(metadata: null, includeDefaultMetadata: false),
                List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(metadata: Metadata(
                    providerCapabilities: FilePanelCapability.Stat)),
                List()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                envelope,
                FileContext(),
                new AgentFileRequest.List(
                    new SessionId("other-session"),
                    [])));
    }

    [Fact]
    public void Non_hierarchical_or_versioned_file_roots_are_not_agent_authority()
    {
        var objectMetadata = new FileSessionMetadata(
            new FilePanelLocation(
                "files.production",
                "storage.example",
                new FilePanelAddress.ObjectKey("literal/object")),
            ReadCapabilities(),
            100,
            4096);
        var versionedMetadata = new FileSessionMetadata(
            Root().WithVersion("version-1"),
            ReadCapabilities(),
            100,
            4096);

        Assert.Throws<ArgumentException>(() =>
            new AgentFileActionComposer().Prepare(
                Envelope(),
                FileContext(metadata: objectMetadata),
                List()));
        Assert.Throws<ArgumentException>(() =>
            new AgentFileActionComposer().Prepare(
                Envelope(),
                FileContext(metadata: versionedMetadata),
                Read()));
    }

    [Fact]
    public void Request_path_must_be_initialized_printable_bounded_and_non_secret()
    {
        var invalidPaths = new[]
        {
            default(ImmutableArray<FilePanelPathSegment>),
            [default(FilePanelPathSegment)],
            Segments("line\nbreak"),
            Segments("hidden\u200Bformat"),
            Segments("hidden\U000E0001format"),
            Segments("password=hunter2"),
            Segments(@"absolute\windows"),
            Segments("C:", "absolute"),
            Segments("\uD800"),
            Segments(new string('x', AgentFileActionComposer.MaximumPathSegmentBytes + 1)),
            [.. Enumerable
                .Range(0, AgentFileActionComposer.MaximumRelativePathSegments + 1)
                .Select(index => new FilePanelPathSegment($"segment-{index}"))],
            [.. Enumerable
                .Range(0, 20)
                .Select(index => new FilePanelPathSegment(
                    $"{index:D2}-{new string('x', 247)}"))],
        };

        foreach (var path in invalidPaths)
        {
            Assert.Throws<ArgumentException>(() =>
                new AgentFileActionComposer().Prepare(
                    Envelope(),
                    FileContext(),
                    new AgentFileRequest.Stat(Session(), path)));
        }
    }

    [Fact]
    public void Read_and_mutation_requests_cannot_target_the_trusted_root()
    {
        var composer = new AgentFileActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(Envelope(), FileContext(), Read()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(),
                CreateDirectory()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(),
                Move([], ["destination"])));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(),
                Delete()));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(),
                Move(["same"], ["same"])));
    }

    [Fact]
    public void Mutation_requests_require_both_session_and_provider_capabilities()
    {
        var composer = new AgentFileActionComposer();

        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(
                    capabilities: [SessionCapabilities.FilesDelete]),
                CreateDirectory("new-directory")));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(
                    metadata: Metadata(
                        providerCapabilities:
                            FilePanelCapability.CreateDirectory)),
                Delete("obsolete.txt")));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(
                    metadata: Metadata(
                        providerCapabilities:
                            FilePanelCapability.CreateDirectory)),
                CreateDirectory("new-directory")));
        Assert.Throws<ArgumentException>(() =>
            composer.Prepare(
                Envelope(),
                FileContext(
                    metadata: Metadata(
                        providerCapabilities:
                            FilePanelCapability.Delete)),
                Delete("obsolete.txt")));
    }

    [Fact]
    public void Trusted_root_and_authority_secret_shapes_fail_before_approval()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentFileActionComposer().Prepare(
                Envelope(),
                FileContext(metadata: Metadata(
                    rootSegments: ["password=hunter2"])),
                List()));
        Assert.Throws<ArgumentException>(() =>
            new AgentFileActionComposer().Prepare(
                Envelope(),
                FileContext(metadata: Metadata(
                    authority: "token=hunter2")),
                List()));
    }

    [Fact]
    public void Root_and_relative_path_are_approval_safe_and_reversible()
    {
        var metadata = Metadata(rootSegments: ["literal\\root"]);
        var plain = new AgentFileActionComposer().Prepare(
            Envelope(),
            FileContext(metadata: metadata),
            Stat("literal child"));
        var escapedText = new AgentFileActionComposer().Prepare(
            Envelope(),
            FileContext(metadata: metadata),
            Stat("literal  child"));

        Assert.Equal(
            @"/literal\\root",
            Assert.Single(
                plain.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "trusted_root", StringComparison.Ordinal)).DisplayValue);
        Assert.Equal(
            "literal child",
            Assert.Single(
                plain.Proposal.Presentation.Arguments,
                argument => string.Equals(argument.Name, "relative_path", StringComparison.Ordinal)).DisplayValue);
        Assert.NotEqual(
            plain.Proposal.ArgumentDigest,
            escapedText.Proposal.ArgumentDigest);
        Assert.NotEqual(ApprovalMaterial(plain), ApprovalMaterial(escapedText), StringComparer.Ordinal);
    }

    [Fact]
    public void Context_fingerprint_binds_every_file_scope_field()
    {
        var baseline = FileContext().BindingFingerprint;
        var variants = new[]
        {
            FileContext(metadata: Metadata(rootSegments: ["srv", "other"]))
                .BindingFingerprint,
            FileContext(metadata: Metadata(authority: "other.example"))
                .BindingFingerprint,
            FileContext(metadata: Metadata(
                providerCapabilities: FilePanelCapability.List))
                .BindingFingerprint,
            FileContext(metadata: Metadata(maximumListPageSize: 7))
                .BindingFingerprint,
            FileContext(metadata: Metadata(maximumPreviewBytes: 1024))
                .BindingFingerprint,
        };

        Assert.Equal(variants.Length, variants.Distinct().Count());
        Assert.DoesNotContain(baseline, variants);
    }

    [Fact]
    public void File_session_metadata_accepts_structured_non_hierarchical_human_roots()
    {
        var metadata = new FileSessionMetadata(
            new FilePanelLocation(
                "files.objects",
                "bucket",
                new FilePanelAddress.ObjectKey("literal/../key")),
            FilePanelCapability.Stat,
            10,
            1024);

        Assert.IsType<FilePanelAddress.ObjectKey>(metadata.TrustedRoot.Address);
        Assert.Equal(FilePanelCapability.Stat, metadata.Capabilities);
    }

    [Fact]
    public void File_session_metadata_rejects_unknown_capabilities_and_unbounded_roots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSessionMetadata(
                Root(),
                (FilePanelCapability)(1UL << 63),
                10,
                1024));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSessionMetadata(
                Root(),
                ReadCapabilities(),
                0,
                1024));
        Assert.Throws<ArgumentException>(() =>
            new FileSessionMetadata(
                new FilePanelLocation(
                    "files.production",
                    "storage.example",
                    new FilePanelAddress.Hierarchical(
                        FilePanelPath.FromSegments(
                            Enumerable
                                .Range(
                                    0,
                                    FileSessionMetadata.MaximumTrustedRootSegments + 1)
                                .Select(index =>
                                    new FilePanelPathSegment($"segment-{index}"))))),
                ReadCapabilities(),
                10,
                1024));
        Assert.Throws<ArgumentException>(() =>
            new FileSessionMetadata(
                new FilePanelLocation(
                    "files.production",
                    "storage.example",
                    new FilePanelAddress.Hierarchical(
                        FilePanelPath.FromSegments(
                            [new FilePanelPathSegment("\uD800")]))),
                ReadCapabilities(),
                10,
                1024));
    }

    private static AgentFileRequest Request(
        FileOperation operation,
        params string[] relativePath) =>
        operation switch
        {
            FileOperation.List => List(relativePath),
            FileOperation.Stat => Stat(relativePath),
            FileOperation.Read => Read(relativePath),
            FileOperation.CreateDirectory => CreateDirectory(relativePath),
            FileOperation.Move => Move(relativePath, ["moved", .. relativePath]),
            FileOperation.Delete => Delete(relativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static AgentFileRequest List(params string[] relativePath) =>
        new AgentFileRequest.List(Session(), Segments(relativePath));

    private static AgentFileRequest Stat(params string[] relativePath) =>
        new AgentFileRequest.Stat(Session(), Segments(relativePath));

    private static AgentFileRequest Read(params string[] relativePath) =>
        new AgentFileRequest.Read(Session(), Segments(relativePath));

    private static AgentFileRequest CreateDirectory(
        params string[] relativePath) =>
        new AgentFileRequest.CreateDirectory(
            Session(),
            Segments(relativePath));

    private static AgentFileRequest Move(
        string[] sourceRelativePath,
        string[] destinationRelativePath) =>
        new AgentFileRequest.Move(
            Session(),
            Segments(sourceRelativePath),
            Segments(destinationRelativePath),
            Reference());

    private static AgentFileRequest Delete(params string[] relativePath) =>
        new AgentFileRequest.Delete(
            Session(),
            Segments(relativePath),
            EntryReference: Reference());

    private static AgentFileEntryReference Reference() =>
        new(new string('A', AgentFileEntryReference.EncodedLength));

    private static ImmutableArray<FilePanelPathSegment> Segments(
        params string[] values) =>
        [.. values.Select(value => new FilePanelPathSegment(value))];

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("action-1"),
            new AgentRunId("run-1"),
            new ActorDescriptor(
                new ActorId("agent-1"),
                ActorKind.Agent,
                "Agent"),
            policyGeneration: 7,
            Now,
            Now.AddMinutes(1));

    private static AgentContextSnapshot FileContext(
        AgentTarget? target = null,
        PanelKind kind = PanelKind.FileViewer,
        IEnumerable<string>? capabilities = null,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        long graphRevision = 11,
        long sessionRevision = 17,
        FileSessionMetadata? metadata = null,
        bool includeDefaultMetadata = true,
        SessionId? sessionId = null)
    {
        var resolvedSessionId = sessionId ?? Session();
        var panel = new PanelInstance(Panel(), kind, "Artifacts", resolvedSessionId);
        var tab = new TabInstance(Tab(), "Files", [panel], panel.Id);
        var workspace = new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            graphRevision,
            lastSequence: graphRevision);
        var descriptor = Descriptor(
            resolvedSessionId,
            kind,
            Panel(),
            capabilities ?? AllFileCapabilities(),
            lifecycle,
            sessionRevision,
            includeDefaultMetadata ? metadata ?? Metadata() : metadata);
        return new AgentContextSnapshot(
            target ?? ExactPanelTarget(),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    descriptor),
            ],
            Now);
    }

    private static AgentContextSnapshot ContextWithoutSession()
    {
        var panel = new PanelInstance(
            Panel(),
            PanelKind.FileViewer,
            "Artifacts");
        var tab = new TabInstance(Tab(), "Files", [panel], panel.Id);
        var workspace = new WorkspaceInstance(
            Workspace(),
            "Operations",
            [tab],
            tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            ExactPanelTarget(),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), Panel(), session: null)],
            Now);
    }

    private static AgentContextSnapshot MultipleFileContext()
    {
        var secondPanelId = new PanelInstanceId("file-panel-2");
        var secondSessionId = new SessionId("file-session-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.FileViewer,
            "Artifacts",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.FileViewer,
            "Backups",
            secondSessionId);
        var tab = new TabInstance(
            Tab(),
            "Files",
            [firstPanel, secondPanel],
            firstPanel.Id);
        var workspace = new WorkspaceInstance(
            Workspace(),
            "Operations",
            [tab],
            tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.FileViewer,
                        Panel(),
                        AllFileCapabilities(),
                        fileMetadata: Metadata())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        secondSessionId,
                        PanelKind.FileViewer,
                        secondPanelId,
                        AllFileCapabilities(),
                        fileMetadata: Metadata(
                            rootSegments: ["backups"]))),
            ],
            Now);
    }

    private static AgentContextSnapshot DuplicateSessionContext()
    {
        var secondPanelId = new PanelInstanceId("file-panel-2");
        var firstPanel = new PanelInstance(
            Panel(),
            PanelKind.FileViewer,
            "Artifacts",
            Session());
        var secondPanel = new PanelInstance(
            secondPanelId,
            PanelKind.FileViewer,
            "Backups",
            Session());
        var tab = new TabInstance(
            Tab(),
            "Files",
            [firstPanel, secondPanel],
            firstPanel.Id);
        var workspace = new WorkspaceInstance(
            Workspace(),
            "Operations",
            [tab],
            tab.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            workspace,
            revision: 11,
            lastSequence: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    Descriptor(
                        Session(),
                        PanelKind.FileViewer,
                        Panel(),
                        AllFileCapabilities(),
                        fileMetadata: Metadata())),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    secondPanelId,
                    Descriptor(
                        Session(),
                        PanelKind.FileViewer,
                        secondPanelId,
                        AllFileCapabilities(),
                        fileMetadata: Metadata())),
            ],
            Now);
    }

    private static SessionDescriptor Descriptor(
        SessionId sessionId,
        PanelKind kind,
        PanelInstanceId panelId,
        IEnumerable<string> capabilities,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        long revision = 17,
        FileSessionMetadata? fileMetadata = null) =>
        new(
            sessionId,
            kind,
            lifecycle,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                panelId),
            new CapabilitySet(capabilities),
            Revision: revision,
            HasActiveWork: false,
            StatusDetail: "Ready",
            FileMetadata: fileMetadata);

    private static FileSessionMetadata Metadata(
        string[]? rootSegments = null,
        string? authority = "storage.example",
        FilePanelCapability? providerCapabilities = null,
        int maximumListPageSize = 250,
        long maximumPreviewBytes = 256 * 1024) =>
        new(
            new FilePanelLocation(
                "files.production",
                authority,
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments(
                        Segments(rootSegments ?? ["srv", "data"])))),
            providerCapabilities ?? AllProviderCapabilities(),
            maximumListPageSize,
            maximumPreviewBytes);

    private static FilePanelLocation Root() =>
        Metadata().TrustedRoot;

    private static FilePanelCapability ReadCapabilities() =>
        FilePanelCapability.List
        | FilePanelCapability.Stat
        | FilePanelCapability.RangedRead;

    private static FilePanelCapability AllProviderCapabilities() =>
        ReadCapabilities()
        | FilePanelCapability.Search
        | FilePanelCapability.Permissions
        | FilePanelCapability.CreateDirectory
        | FilePanelCapability.Rename
        | FilePanelCapability.Delete
        | FilePanelCapability.GovernedCreateDirectory
        | FilePanelCapability.GovernedRename
        | FilePanelCapability.GovernedDelete;

    private static string[] AllFileCapabilities() =>
    [
        SessionCapabilities.FilesList,
        SessionCapabilities.FilesSearch,
        SessionCapabilities.FilesStat,
        SessionCapabilities.FilesPreview,
        SessionCapabilities.FilesReadAccessControl,
        SessionCapabilities.FilesTransfersRead,
        SessionCapabilities.FilesCreateDirectory,
        SessionCapabilities.FilesRename,
        SessionCapabilities.FilesDelete,
    ];

    private static string ApprovalMaterial(AgentFileAction action) =>
        string.Join(
            "\n",
            action.Proposal.Presentation.Arguments.Select(
                argument => $"{argument.Name}:{argument.DisplayValue}"));

    private static void AssertArgument(
        AgentApprovalArgument argument,
        string name,
        string displayValue) =>
        Assert.Equal(
            (name, displayValue),
            (argument.Name, argument.DisplayValue));

    private static AgentTarget.Panel ExactPanelTarget() =>
        new(Window(), Workspace(), Tab(), Panel());

    private static WindowInstanceId Window() => new("file-window-1");

    private static WorkspaceInstanceId Workspace() => new("file-workspace-1");

    private static TabInstanceId Tab() => new("file-tab-1");

    private static PanelInstanceId Panel() => new("file-panel-1");

    private static SessionId Session() => new("file-session-1");

    public enum FileOperation
    {
        List,
        Stat,
        Read,
        CreateDirectory,
        Move,
        Delete,
    }
}
