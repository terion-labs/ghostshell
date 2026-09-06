using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Mcp.Tests;

public sealed class AgentMcpSessionHostTests
{
    [Theory]
    [InlineData(McpProfileChange.Disable)]
    [InlineData(McpProfileChange.Delete)]
    [InlineData(McpProfileChange.Edit)]
    public async Task CatalogChangeClosesIdleAffectedProcessWithoutToolCall(
        McpProfileChange change)
    {
        var markerRoot = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-catalog-" + Guid.NewGuid().ToString("N"));
        var startedPath = markerRoot + ".started";
        var closedPath = markerRoot + ".closed";
        var calledPath = markerRoot + ".called";
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "lifecycle-marker",
                hostArguments:
                [
                    startedPath,
                    closedPath,
                    calledPath,
                ],
                shutdownGracePeriod: TimeSpan.FromSeconds(2));
            await using (fixture)
            {
                _ = await fixture.OpenAsync();
                Assert.True(File.Exists(startedPath));

                PublishProfileChange(fixture, change);

                await WaitForFileAsync(closedPath);
                Assert.False(File.Exists(calledPath));
            }
        }
        finally
        {
            File.Delete(startedPath);
            File.Delete(closedPath);
            File.Delete(calledPath);
        }
    }

    [Fact(DisplayName = "lifecycle.profile-drift")]
    [Trait("SecurityCampaignCase", "lifecycle.profile-drift")]
    public async Task SecurityCampaignProfileDriftRejectsApprovedCallBeforeConsumeAsync()
    {
        var markerRoot = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-profile-drift-" + Guid.NewGuid().ToString("N"));
        var startedPath = markerRoot + ".started";
        var closedPath = markerRoot + ".closed";
        var calledPath = markerRoot + ".called";
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "lifecycle-marker",
                hostArguments:
                [
                    startedPath,
                    closedPath,
                    calledPath,
                ],
                shutdownGracePeriod: TimeSpan.FromSeconds(2));
            await using (fixture)
            {
                var tool = Assert.Single((await fixture.OpenAsync()).Tools);
                var action = fixture.Prepare(
                    tool,
                    """{"value":"must-not-dispatch"}""");
                var authorizationId = await fixture.AuthorizeAsync(action);

                PublishProfileChange(fixture, McpProfileChange.Edit);
                await WaitForFileAsync(closedPath);
                var result = Assert.IsType<
                    AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure>(
                    await fixture.Host.RunToolAsync(
                        authorizationId,
                        action,
                        default));

                Assert.Equal("mcp_run_not_found", result.Error.StableCode);
                Assert.False(File.Exists(calledPath));
                Assert.Equal(
                    [AuditOutcome.Requested, AuditOutcome.Approved],
                    fixture.Audit.Events.Where(item => string.Equals(
                        item.CorrelationId,
                        action.Proposal.Id.Value,
                        StringComparison.Ordinal)).Select(item => item.Outcome));
            }
        }
        finally
        {
            File.Delete(startedPath);
            File.Delete(closedPath);
            File.Delete(calledPath);
        }
    }

    [Fact]
    public async Task CatalogEventWithSameAuthorityFingerprintKeepsRunOpen()
    {
        var markerRoot = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-catalog-same-"
            + Guid.NewGuid().ToString("N"));
        var startedPath = markerRoot + ".started";
        var closedPath = markerRoot + ".closed";
        var calledPath = markerRoot + ".called";
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "lifecycle-marker",
                hostArguments:
                [
                    startedPath,
                    closedPath,
                    calledPath,
                ],
                shutdownGracePeriod: TimeSpan.FromSeconds(2));
            await using (fixture)
            {
                var first = await fixture.OpenAsync();

                PublishProfileChange(
                    fixture,
                    McpProfileChange.SameFingerprintEdit);
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                var reopened = await fixture.OpenAsync();

                Assert.False(File.Exists(closedPath));
                Assert.Equal(
                    Assert.Single(first.Tools).ProviderAlias,
                    Assert.Single(reopened.Tools).ProviderAlias);
            }
        }
        finally
        {
            File.Delete(startedPath);
            File.Delete(closedPath);
            File.Delete(calledPath);
        }
    }

    [Fact]
    public async Task EditingDisabledProfileDoesNotCancelUnrelatedActiveRun()
    {
        var markerRoot = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-disabled-isolation-"
            + Guid.NewGuid().ToString("N"));
        var startedPath = markerRoot + ".started";
        var closedPath = markerRoot + ".closed";
        var calledPath = markerRoot + ".called";
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "lifecycle-marker",
                hostArguments:
                [
                    startedPath,
                    closedPath,
                    calledPath,
                ],
                profileCount: 2,
                shutdownGracePeriod: TimeSpan.FromSeconds(2));
            await using (fixture)
            {
                PublishDisabledSecondProfile(
                    fixture,
                    advanceRevision: false);
                var first = await fixture.OpenAsync();

                PublishDisabledSecondProfile(
                    fixture,
                    advanceRevision: true);
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                var reopened = await fixture.OpenAsync();

                Assert.False(File.Exists(closedPath));
                Assert.Equal(
                    Assert.Single(first.Tools).ProviderAlias,
                    Assert.Single(reopened.Tools).ProviderAlias);
            }
        }
        finally
        {
            File.Delete(startedPath);
            File.Delete(closedPath);
            File.Delete(calledPath);
        }
    }

    [Theory]
    [InlineData(McpProfileChange.Delete)]
    [InlineData(McpProfileChange.Edit)]
    public async Task CatalogChangeCancelsBlockedSettingsTestAsRevisionDrift(
        McpProfileChange change)
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        fixture.Vault.BlockResolution = true;
        await using (fixture)
        {
            var testing = fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None).AsTask();
            await fixture.Vault.ResolutionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            PublishProfileChange(fixture, change);

            Assert.Equal(
                "mcp_profile_revision_mismatch",
                Assert.IsType<
                    McpServerTestResult.Failure>(
                    await testing).Error.StableCode);
        }
    }

    [Fact]
    public async Task OpenRunRejectsUnregisteredAndForgedActorsBeforeSecrets()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var absent = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    new AgentRunId("unregistered-run"),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);
            var forged = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent() with
                    {
                        DisplayName = "Forged agent",
                    },
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);
            Assert.Equal(
                "mcp_run_not_authorized",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    absent).Error.StableCode);
            Assert.Equal(
                "mcp_run_not_authorized",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    forged).Error.StableCode);
            Assert.Empty(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task WorkspaceKillSwitchRejectsMcpBeforeResolvingSecrets()
    {
        var routes = new FixedWorkspaceNetworkRoutes(
            new FixedWorkspaceNetworkConnector(WorkspaceNetworkEgress.Blocked));
        var fixture = await HostFixture.CreateAsync(
            mode: "normal",
            workspaceNetworkRoutes: routes);
        await using (fixture)
        {
            var result = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);

            Assert.Equal(
                "workspace_network_kill_switch_blocked",
                Assert.IsType<
                    AgentMcpHostResult<AgentMcpRunManifest>.Failure>(
                    result).Error.StableCode);
            Assert.Equal(HostFixture.WorkspaceId(), routes.LastWorkspaceId);
            Assert.Empty(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task IsolatedWorkspacePlansMcpServerInsideWorkspaceRuntime()
    {
        var commandRuntime = new RecordingWorkspaceCommandRuntime();
        var routes = new FixedWorkspaceNetworkRoutes(
            new FixedWorkspaceNetworkConnector(WorkspaceNetworkEgress.Direct),
            commandRuntime);
        var fixture = await HostFixture.CreateAsync(
            mode: "normal",
            workspaceNetworkRoutes: routes);
        await using (fixture)
        {
            _ = await fixture.OpenAsync();

            Assert.Equal(1, commandRuntime.DuplexPlanCount);
            Assert.Equal(
                HostFixture.SecretCanary,
                commandRuntime.LastConnection!.Startup.Environment.Single(
                    variable => string.Equals(
                        variable.Name,
                        "GHOSTSHELL_ALLOWED",
                        StringComparison.Ordinal)).Value
                    is ConnectionEnvironmentValue.PlainText value
                        ? value.Value
                        : null);
        }
    }

    [Fact(DisplayName = "authority.mcp.call broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.mcp.call")]
    public async Task SecurityCampaignMcpCallRequiresExactOneUseAuthorityAsync()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var tool = Assert.Single((await fixture.OpenAsync()).Tools);
            var action = fixture.Prepare(tool, """{"value":"campaign"}""");

            var forged = Assert.IsType<
                AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure>(
                await fixture.Host.RunToolAsync(
                    new AgentAuthorizationId("forged-authorization"),
                    action,
                    default));

            Assert.Equal("authorization_rejected", forged.Error.StableCode);
            Assert.DoesNotContain(
                fixture.Audit.Events,
                item => string.Equals(
                    item.CorrelationId,
                    action.Proposal.Id.Value,
                    StringComparison.Ordinal)
                    && item.Outcome == AuditOutcome.Started);

            var authorizationId = await fixture.AuthorizeAsync(action);
            Assert.IsType<AgentMcpHostResult<AgentMcpToolCallReceipt>.Success>(
                await fixture.Host.RunToolAsync(
                    authorizationId,
                    action,
                    default));
            var replay = Assert.IsType<
                AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure>(
                await fixture.Host.RunToolAsync(
                    authorizationId,
                    action,
                    default));

            Assert.Equal("authorization_rejected", replay.Error.StableCode);
            Assert.Single(
                fixture.Audit.Events,
                item => string.Equals(
                    item.CorrelationId,
                    action.Proposal.Id.Value,
                    StringComparison.Ordinal)
                    && item.Outcome == AuditOutcome.Started);
            Assert.Single(
                fixture.Audit.Events,
                item => string.Equals(
                    item.CorrelationId,
                    action.Proposal.Id.Value,
                    StringComparison.Ordinal)
                    && item.Outcome == AuditOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingHostGateIsSafe()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var manifest = Assert.Single(
                (await fixture.OpenAsync()).Tools);
            var action = fixture.Prepare(
                manifest,
                """{"value":"hello"}""");
            var gate = GetPrivateSemaphore(
                fixture.Host,
                "_gate");
            await gate.WaitAsync();
            try
            {
                using var cancellation =
                    new CancellationTokenSource();
                var running = fixture.Host.RunToolAsync(
                    new AgentAuthorizationId("unused-authorization"),
                    action,
                    cancellation.Token).AsTask();
                Assert.False(running.IsCompleted);

                cancellation.Cancel();

                Assert.Equal(
                    "caller_cancelled",
                    Assert.IsType<
                        AgentMcpHostResult<
                            AgentMcpToolCallReceipt>.Failure>(
                        await running).Error.StableCode);
                Assert.Empty(fixture.Audit.Events);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingRunEntryIsSafe()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var manifest = Assert.Single(
                (await fixture.OpenAsync()).Tools);
            var action = fixture.Prepare(
                manifest,
                """{"value":"hello"}""");
            var operationGate =
                GetOnlyRunOperationGate(fixture.Host);
            await operationGate.WaitAsync();
            try
            {
                using var cancellation =
                    new CancellationTokenSource();
                var running = fixture.Host.RunToolAsync(
                    new AgentAuthorizationId("unused-authorization"),
                    action,
                    cancellation.Token).AsTask();
                await Task.Delay(TimeSpan.FromMilliseconds(20));
                Assert.False(running.IsCompleted);

                cancellation.Cancel();

                Assert.Equal(
                    "caller_cancelled",
                    Assert.IsType<
                        AgentMcpHostResult<
                            AgentMcpToolCallReceipt>.Failure>(
                        await running).Error.StableCode);
                Assert.Empty(fixture.Audit.Events);
            }
            finally
            {
                operationGate.Release();
            }
        }
    }

    [Fact]
    public async Task IdempotentOpenRequiresExactActorAndPolicyGeneration()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var first = await fixture.OpenAsync();
            var secretResolutionCount = fixture.Vault.Purposes.Count;

            var reopened = await fixture.OpenAsync();
            var wrongWorkspace = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    new WorkspaceInstanceId("another-workspace"),
                    HostFixture.Now),
                CancellationToken.None);
            var forged = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent() with
                    {
                        DisplayName = "Forged agent",
                    },
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);
            Assert.Null(await fixture.Broker.UpdateRunPolicyAsync(
                new AgentRunPolicyUpdate(
                    HostFixture.RunId(),
                    HostFixture.McpPolicy(AgentPermission.Ask),
                    policyGeneration: 2,
                    HostFixture.Human()),
                CancellationToken.None));
            var changedGeneration = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);

            Assert.Equal(
                Assert.Single(first.Tools).ProviderAlias,
                Assert.Single(reopened.Tools).ProviderAlias);
            Assert.Equal(
                "mcp_run_not_authorized",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    forged).Error.StableCode);
            Assert.Equal(
                "mcp_run_not_authorized",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    wrongWorkspace).Error.StableCode);
            Assert.Equal(
                "mcp_run_not_authorized",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    changedGeneration).Error.StableCode);
            Assert.Equal(
                secretResolutionCount,
                fixture.Vault.Purposes.Count);
        }
    }

    [Fact]
    public async Task PolicyRevocationCancelsDiscoveryBeforeProcessLaunch()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        fixture.Vault.BlockResolution = true;
        await using (fixture)
        {
            var opening = fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None).AsTask();
            await fixture.Vault.ResolutionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.Null(await fixture.Broker.UpdateRunPolicyAsync(
                new AgentRunPolicyUpdate(
                    HostFixture.RunId(),
                    AgentPolicy.Default,
                    policyGeneration: 2,
                    HostFixture.Human()),
                CancellationToken.None));

            var failure = Assert.IsType<
                AgentMcpHostResult<
                    AgentMcpRunManifest>.Failure>(await opening);
            Assert.Equal(
                "mcp_run_authority_revoked",
                failure.Error.StableCode);
            Assert.Single(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task FrozenAliasesAreStableWithinRunAndUnlinkableAcrossRuns()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var first = await fixture.OpenAsync();
            var reopened = await fixture.OpenAsync();
            var secondRunId = new AgentRunId("mcp-run-2");
            Assert.Null(await fixture.RegisterRunAsync(secondRunId));
            var second = await fixture.OpenAsync(secondRunId);

            var firstAlias = Assert.Single(first.Tools).ProviderAlias;
            Assert.Equal(
                firstAlias,
                Assert.Single(reopened.Tools).ProviderAlias);
            Assert.NotEqual(
                firstAlias,
                Assert.Single(second.Tools).ProviderAlias, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task RunToolCapacityCountsEveryDiscoveredTool()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "many-unselected-tools",
            profileCount: 2);
        await using (fixture)
        {
            var result = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);

            Assert.Equal(
                "mcp_tool_capacity_exceeded",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    result).Error.StableCode);
        }
    }

    [Fact]
    public async Task RunRejectsIndividuallyValidSchemasOverAggregateBudget()
    {
        var enabledTools = new[] { "control" }
            .Concat(
                Enumerable.Range(1, 9)
                    .Select(index => $"schema-{index}"))
            .ToArray();
        var fixture = await HostFixture.CreateAsync(
            mode: "aggregate-schema-limit",
            enabledTools: enabledTools);
        await using (fixture)
        {
            var result = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);

            Assert.Equal(
                "mcp_schema_capacity_exceeded",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    result).Error.StableCode);
        }
    }

    [Fact]
    public async Task DefaultWorkingDirectoryIsFrozenAsExecutableDirectory()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "normal",
            omitWorkingDirectory: true);
        await using (fixture)
        {
            var manifest = Assert.Single(
                (await fixture.OpenAsync()).Tools);
            var expected = Path.GetDirectoryName(
                Environment.ProcessPath!);

            Assert.Equal(expected, manifest.WorkingDirectory);
            Assert.Equal(
                expected,
                fixture.Prepare(
                    manifest,
                    """{"value":"hello"}""")
                    .Proposal.Presentation.WorkingDirectory);
        }
    }

    [Fact]
    public async Task EnvironmentRejectsWindowsBlockOverflowWithinUtf8Budget()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "windows-environment-limit");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            Assert.Equal(
                "mcp_secret_limit_exceeded",
                Assert.IsType<
                    McpServerTestResult.Failure>(
                    result).Error.StableCode);
            Assert.Equal(4, fixture.Vault.Purposes.Count);
        }
    }

    [Fact]
    public async Task StickyCleanupUncertaintyBlocksTestAndOpenBeforeSecrets()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            typeof(AgentMcpSessionHost)
                .GetMethod(
                    "MarkCleanupUncertain",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Host, null);

            Assert.True(await fixture.Host.ClearHistoryAsync(
                OperationContext.ForHuman(HostFixture.ClientId()),
                CancellationToken.None));
            Assert.True(fixture.Host.Snapshot.CleanupUncertain);

            var tested = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);
            var opened = await fixture.Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    HostFixture.RunId(),
                    HostFixture.Agent(),
                    HostFixture.WorkspaceId(),
                    HostFixture.Now),
                CancellationToken.None);

            Assert.Equal(
                "mcp_cleanup_uncertain",
                Assert.IsType<
                    McpServerTestResult.Failure>(
                    tested).Error.StableCode);
            Assert.Equal(
                "mcp_cleanup_uncertain",
                Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpRunManifest>.Failure>(
                    opened).Error.StableCode);
            Assert.Empty(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task CredentialInvalidationClosesAffectedRunBeforeReturning()
    {
        var markerRoot = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-credential-" + Guid.NewGuid().ToString("N"));
        var startedPath = markerRoot + ".started";
        var closedPath = markerRoot + ".closed";
        var calledPath = markerRoot + ".called";
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "lifecycle-marker",
                hostArguments:
                [
                    startedPath,
                    closedPath,
                    calledPath,
                ],
                shutdownGracePeriod: TimeSpan.FromSeconds(2));
            await using (fixture)
            {
                var manifest = Assert.Single(
                    (await fixture.OpenAsync()).Tools);
                Assert.True(File.Exists(startedPath));
                var action = fixture.Prepare(
                    manifest,
                    """{"value":"hello"}""");
                var authorizationId =
                    await fixture.AuthorizeAsync(action);

                await fixture.Host.InvalidateAsync(
                    new SecretRef("mcp-secret-canary"));
                await fixture.Host.InvalidateAsync(
                    new SecretRef("mcp-secret-canary"));
                var result = await fixture.Host.RunToolAsync(
                    authorizationId,
                    action,
                    CancellationToken.None);

                Assert.Equal(
                    "mcp_run_not_found",
                    Assert.IsType<
                        AgentMcpHostResult<
                            AgentMcpToolCallReceipt>.Failure>(
                        result).Error.StableCode);
                Assert.True(File.Exists(closedPath));
                Assert.False(File.Exists(calledPath));
            }
        }
        finally
        {
            File.Delete(startedPath);
            File.Delete(closedPath);
            File.Delete(calledPath);
        }
    }

    [Fact]
    public async Task CredentialInvalidationWaitsForActiveSettingsTestCleanup()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        fixture.Vault.BlockResolution = true;
        await using (fixture)
        {
            var testing = fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None).AsTask();
            await fixture.Vault.ResolutionStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var invalidation = fixture.Host.InvalidateAsync(
                new SecretRef("mcp-dotnet-root")).AsTask();

            Assert.False(invalidation.IsCompleted);
            fixture.Vault.ReleaseResolution.TrySetResult();
            Assert.IsType<McpServerTestResult.Success>(await testing);
            await invalidation;
        }
    }

    [Fact]
    public async Task AuthenticatedSettingsTestDiscoversWithoutCallingTools()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-mcp-call-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "call-marker",
                hostArguments: [markerPath]);
            await using (fixture)
            {
                var result = await fixture.Host.TestAsync(
                    new McpServerTestRequest(
                        HostFixture.ProfileId(),
                        HostFixture.ProfileRevision),
                    OperationContext.ForHuman(
                        HostFixture.ClientId(),
                        expectedRevision:
                            HostFixture.ProfileRevision,
                        deadlineUtc:
                            HostFixture.Now.AddSeconds(30)),
                    CancellationToken.None);

                var report = Assert.IsType<
                    McpServerTestResult.Success>(result).Report;
                Assert.Equal(
                    HostFixture.ProfileId(),
                    report.ProfileId);
                Assert.Equal(
                    HostFixture.ProfileRevision,
                    report.Revision);
                Assert.Equal(1, report.DiscoveredToolCount);
                Assert.Equal(1, report.EnabledToolCount);
                Assert.False(File.Exists(markerPath));
                Assert.Equal(
                    2,
                    fixture.Vault.Purposes.Count);
            }
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task SettingsTestRejectsNonHumanActorBeforeResolvingSecrets()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                new OperationContext(
                    RequestId.New(),
                    HostFixture.Agent(),
                    ExpectedRevision:
                        HostFixture.ProfileRevision,
                    DeadlineUtc:
                        HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var failure = Assert.IsType<
                McpServerTestResult.Failure>(result);
            Assert.Equal(
                "mcp_test_not_authenticated",
                failure.Error.StableCode);
            Assert.Empty(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task SettingsTestRejectsForgedHumanIdentityBeforeResolvingSecrets()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    new ClientId("forged-mcp-client"),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var failure = Assert.IsType<
                McpServerTestResult.Failure>(result);
            Assert.Equal(
                "mcp_test_not_authenticated",
                failure.Error.StableCode);
            Assert.Empty(fixture.Vault.Purposes);
        }
    }

    [Fact]
    public async Task DisabledTrustedProfileCanBeTestedButIsExcludedFromAgentRuns()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "normal",
            isEnabled: false);
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var success = Assert.IsType<
                McpServerTestResult.Success>(result);
            Assert.Equal(1, success.Report.DiscoveredToolCount);
            Assert.Empty((await fixture.OpenAsync()).Tools);
        }
    }

    [Fact]
    public async Task UntrustedProfileCannotBeTestedOrSelectedByAgentRuns()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "normal",
            isTrusted: false);
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var failure = Assert.IsType<McpServerTestResult.Failure>(result);
            Assert.Equal("mcp_profile_untrusted", failure.Error.StableCode);
            Assert.Empty(fixture.Vault.Purposes);
            Assert.Empty((await fixture.OpenAsync()).Tools);
        }
    }

    [Fact]
    public async Task SettingsTestMapsCallerCancellationAfterSdkTranslation()
    {
        var fixture = await HostFixture.CreateAsync(mode: "hang-list");
        await using (fixture)
        using (var cancellation = new CancellationTokenSource(
                   TimeSpan.FromMilliseconds(100)))
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                cancellation.Token);

            var failure = Assert.IsType<
                McpServerTestResult.Failure>(result);
            Assert.Equal(
                "mcp_test_cancelled",
                failure.Error.StableCode);
        }
    }

    [Fact]
    public async Task SettingsTestMapsDeadlineAfterSdkTranslation()
    {
        var fixture = await HostFixture.CreateAsync(mode: "hang-list");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc:
                        HostFixture.Now.AddMilliseconds(100)),
                CancellationToken.None);

            var failure = Assert.IsType<
                McpServerTestResult.Failure>(result);
            Assert.Equal(
                "mcp_test_timed_out",
                failure.Error.StableCode);
        }
    }

    [Fact]
    public async Task SettingsTestRejectsAggregateEnvironmentSecretOverflow()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "secret-aggregate-limit");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var failure = Assert.IsType<
                McpServerTestResult.Failure>(result);
            Assert.Equal(
                "mcp_secret_limit_exceeded",
                failure.Error.StableCode);
        }
    }

    [Fact]
    public async Task SettingsTestReturnsOnlyCountsForUntrustedToolDiscovery()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "secret-tool-name");
        await using (fixture)
        {
            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            var report = Assert.IsType<
                McpServerTestResult.Success>(result).Report;
            Assert.Equal(2, report.DiscoveredToolCount);
            Assert.DoesNotContain(
                HostFixture.ReflectedToolCanary,
                report.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                typeof(McpServerTestReport).GetProperties(),
                property => property.PropertyType
                    == typeof(IReadOnlyList<string>));
        }
    }

    [Fact]
    public async Task SettingsTestPublishesCorrelatedBoundedSecretSafeLifecycle()
    {
        var fixture = await HostFixture.CreateAsync(mode: "stderr");
        await using (fixture)
        {
            var snapshots = new List<McpServerDiagnosticsSnapshot>();
            fixture.Host.Changed += (_, eventArgs) =>
                snapshots.Add(eventArgs.Snapshot);

            var result = await fixture.Host.TestAsync(
                new McpServerTestRequest(
                    HostFixture.ProfileId(),
                    HostFixture.ProfileRevision),
                OperationContext.ForHuman(
                    HostFixture.ClientId(),
                    expectedRevision: HostFixture.ProfileRevision,
                    deadlineUtc: HostFixture.Now.AddSeconds(30)),
                CancellationToken.None);

            _ = Assert.IsType<McpServerTestResult.Success>(result);
            var summary = Assert.Single(fixture.Host.Snapshot.Summaries);
            Assert.Equal(McpServerSessionKind.Test, summary.SessionKind);
            Assert.Equal(McpServerLifecycleState.Stopped, summary.State);
            Assert.Equal(
                [
                    McpServerLifecycleState.Testing,
                    McpServerLifecycleState.Healthy,
                    McpServerLifecycleState.Stopped,
                ],
                summary.Events.Select(item => item.State));
            Assert.InRange(
                summary.Events.Count,
                1,
                McpServerDiagnosticSummary.MaximumRetainedEvents);
            Assert.True(summary.Events[^1].ObservedStderrBytes > 0);
            Assert.DoesNotContain(
                "LEAK-ME-NOT",
                summary.ToString(),
                StringComparison.Ordinal);
            Assert.All(
                snapshots.SelectMany(snapshot => snapshot.Summaries),
                item => Assert.Equal(summary.SessionId, item.SessionId));
        }
    }

    [Fact]
    public async Task FrozenManifestKeepsSensitiveProtocolToolNameInsideHostBinding()
    {
        var fixture = await HostFixture.CreateAsync(
            mode: "secret-tool-name",
            enabledTools: [HostFixture.ReflectedToolCanary]);
        await using (fixture)
        {
            var manifest = Assert.Single(
                (await fixture.OpenAsync()).Tools);

            Assert.True(manifest.ToolNameRedacted);
            Assert.StartsWith(
                "redacted_tool_",
                manifest.ToolName,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                HostFixture.ReflectedToolCanary,
                manifest.ToolName,
                StringComparison.Ordinal);
            var action = fixture.Prepare(
                manifest,
                """{"value":"hello"}""");
            Assert.DoesNotContain(
                HostFixture.ReflectedToolCanary,
                string.Join(
                    ",",
                    action.Proposal.Presentation.Arguments.Select(
                        argument => argument.DisplayValue)),
                StringComparison.Ordinal);
            var authorizationId = await fixture.AuthorizeAsync(action);

            var result = await fixture.Host.RunToolAsync(
                authorizationId,
                action,
                CancellationToken.None);

            Assert.IsType<
                AgentMcpHostResult<
                    AgentMcpToolCallReceipt>.Success>(result);
        }
    }

    [Fact]
    public async Task GovernedRemoteCallUsesVaultHeaderAndFrozenEndpoint()
    {
        var endpoint = new Uri("https://mcp.example.test/rpc");
        var handler = new McpStreamableHttpClientTests.FakeMcpHttpHandler(
            callResponseUsesSse: true);
        var fixture = await HostFixture.CreateAsync(
            mode: "unused-for-http",
            enabledTools: ["first"],
            transport: new McpServerTransport.StreamableHttp(
                endpoint,
                [
                    new McpServerHttpHeader(
                        "Authorization",
                        new SecretRef("mcp-http-authorization")),
                ]),
            streamableHttpHandlerFactory: uri =>
            {
                Assert.Equal(endpoint, uri);
                return handler;
            });
        fixture.Vault.Values["mcp-http-authorization"] =
            Encoding.UTF8.GetBytes("Bearer remote-vault-token");
        await using (fixture)
        {
            var manifest = Assert.Single(
                (await fixture.OpenAsync()).Tools);
            Assert.Equal(
                McpServerTransportKind.StreamableHttp,
                manifest.TransportKind);
            Assert.Equal(endpoint.AbsoluteUri, manifest.TransportTarget);
            Assert.Null(manifest.WorkingDirectory);
            Assert.Contains(
                fixture.Vault.Purposes,
                purpose => purpose.Kind
                    == SecretUseKind.McpServerHttpHeader);

            var action = fixture.Prepare(manifest, "{}");
            Assert.Equal(
                "Remote MCP Streamable HTTP server",
                action.Proposal.Presentation.Host);
            var authorizationId = await fixture.AuthorizeAsync(action);
            var result = await fixture.Host.RunToolAsync(
                authorizationId,
                action,
                CancellationToken.None);

            Assert.IsType<
                AgentMcpHostResult<
                    AgentMcpToolCallReceipt>.Success>(result);
            Assert.All(
                handler.Requests.Where(request =>
                    request.Method == HttpMethod.Post),
                request => Assert.Equal(
                    "Bearer remote-vault-token",
                    request.Authorization));
        }
    }

    [Fact]
    public async Task GovernedCallUsesFrozenManifestAndRedactsResolvedSecrets()
    {
        var ambientName =
            "GHOSTSHELL_MCP_AMBIENT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(
            ambientName,
            "must-not-cross-boundary");
        try
        {
            var fixture = await HostFixture.CreateAsync(
                mode: "environment",
                hostArguments: [ambientName]);
            await using (fixture)
            {
                var manifest = await fixture.OpenAsync();
                var tool = Assert.Single(manifest.Tools);
                var action = fixture.Prepare(
                    tool,
                    """{"value":"hello"}""");
                var authorizationId =
                    await fixture.AuthorizeAsync(action);

                var result = await fixture.Host.RunToolAsync(
                    authorizationId,
                    action,
                    CancellationToken.None);

                var success = Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpToolCallReceipt>.Success>(
                    result);
                Assert.False(success.Value.IsError);
                Assert.DoesNotContain(
                    HostFixture.SecretCanary,
                    success.Value.ProviderJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "must-not-cross-boundary",
                    success.Value.ProviderJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "[REDACTED MCP CONTENT]",
                    success.Value.ProviderJson,
                    StringComparison.Ordinal);
                using var providerJson = JsonDocument.Parse(
                    success.Value.ProviderJson);
                Assert.Equal(
                    AgentMcpToolCallReceipt.ContentOrigin,
                    providerJson.RootElement
                        .GetProperty("content_origin")
                        .GetString());
                Assert.Equal(
                    JsonValueKind.Null,
                    providerJson.RootElement
                        .GetProperty("structured_content")
                        .GetProperty("inherited")
                        .ValueKind);
                Assert.Contains(
                    fixture.Audit.Events,
                    auditEvent => string.Equals(auditEvent.Action, BuiltInAgentTools.McpCall
, StringComparison.Ordinal) && auditEvent.Outcome
                            == AuditOutcome.Succeeded);
                Assert.Equal(
                    [
                        SecretUseKind.McpServerEnvironment,
                        SecretUseKind.McpServerEnvironment,
                    ],
                    fixture.Vault.Purposes
                        .Select(purpose => purpose.Kind)
                        .ToArray());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ambientName, null);
        }
    }

    [Fact]
    public async Task CancellationAfterDispatchReturnsUnknownOutcomeAndAuditsFailure()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var tool = Assert.Single((await fixture.OpenAsync()).Tools);
            var action = fixture.Prepare(tool, """{"hang":true}""");
            var authorizationId = await fixture.AuthorizeAsync(action);
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(100));

            var result = await fixture.Host.RunToolAsync(
                authorizationId,
                action,
                cancellation.Token);

            var failure = Assert.IsType<
                AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure>(
                result);
            Assert.Equal(
                "mcp_tool_outcome_unknown",
                failure.Error.StableCode);
            Assert.True(failure.Error.OutcomeUnknown);
            Assert.Contains(
                fixture.Audit.Events,
                auditEvent => string.Equals(auditEvent.Action, BuiltInAgentTools.McpCall
, StringComparison.Ordinal) && auditEvent.Outcome == AuditOutcome.Failed
                    && auditEvent.Details
                        is AuditDetails.AgentActionDetails
                    {
                        ResultCode: "mcp_tool_outcome_unknown",
                    });
        }
    }

    [Fact]
    public async Task CancellationBeforeSdkDispatchAuditsCancelledNotUnknown()
    {
        var fixture = await HostFixture.CreateAsync(mode: "normal");
        await using (fixture)
        {
            var tool = Assert.Single(
                (await fixture.OpenAsync()).Tools);
            var action = fixture.Prepare(
                tool,
                """{"value":"hello"}""");
            var authorizationId =
                await fixture.AuthorizeAsync(action);
            var clientOperationGate =
                GetOnlyClientOperationGate(fixture.Host);
            await clientOperationGate.WaitAsync();
            try
            {
                using var cancellation =
                    new CancellationTokenSource();
                var running = fixture.Host.RunToolAsync(
                    authorizationId,
                    action,
                    cancellation.Token).AsTask();
                await WaitUntilAsync(
                    () => fixture.Audit.Events.Any(
                        auditEvent =>
                            auditEvent.Outcome
                                == AuditOutcome.Started));
                await Task.Delay(TimeSpan.FromMilliseconds(20));

                cancellation.Cancel();

                var failure = Assert.IsType<
                    AgentMcpHostResult<
                        AgentMcpToolCallReceipt>.Failure>(
                    await running);
                Assert.Equal(
                    "caller_cancelled",
                    failure.Error.StableCode);
                Assert.False(failure.Error.OutcomeUnknown);
                Assert.Contains(
                    fixture.Audit.Events,
                    auditEvent =>
                        auditEvent.Outcome == AuditOutcome.Cancelled
                        && auditEvent.Details
                            is AuditDetails.AgentActionDetails
                        {
                            ResultCode: "caller_cancelled",
                        });
            }
            finally
            {
                clientOperationGate.Release();
            }
        }
    }

    [Fact]
    public void SchemaSanitizerStripsAnnotationsWithoutDroppingArgumentNames()
    {
        var literal = HostFixture.SecretCanary.ToCharArray();
        using var redactor = new McpSecretRedactor([literal]);
        using var schema = JsonDocument.Parse(
            $$"""
            {
              "type": "object",
              "description": "{{HostFixture.SecretCanary}}",
              "properties": {
                "description": {
                  "type": "string",
                  "description": "{{HostFixture.SecretCanary}}",
                  "enum": ["safe", "{{HostFixture.SecretCanary}}"]
                }
              }
            }
            """);

        var sanitized = McpAgentSchemaSanitizer.Sanitize(
            schema.RootElement,
            redactor);

        Assert.False(sanitized.TryGetProperty("description", out _));
        var argument = sanitized
            .GetProperty("properties")
            .GetProperty("description");
        Assert.False(argument.TryGetProperty("description", out _));
        Assert.Equal(
            ["safe", "[REDACTED MCP CONTENT]"],
            argument.GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public void SecretRedactorReturnsOneFixedReplacementWithoutAmplification()
    {
        using var redactor = new McpSecretRedactor(
            ["R".ToCharArray(), "[".ToCharArray()]);

        var result = redactor.Redact(
            new string('R', 64 * 1024),
            out var redacted);

        Assert.True(redacted);
        Assert.Equal("[REDACTED MCP CONTENT]", result);
    }

    [Fact]
    public void SchemaAndStructuredResultRedactNonStringSecretScalars()
    {
        using var redactor = new McpSecretRedactor(
            [
                "12345".ToCharArray(),
                "true".ToCharArray(),
                "null".ToCharArray(),
            ]);
        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "12345": { "type": "string" },
              "properties": {
                "value": {
                  "enum": [12345, true, null, 7]
                }
              }
            }
            """);

        var schema = McpAgentSchemaSanitizer.Sanitize(
            schemaDocument.RootElement,
            redactor);
        var enumValues = schema
            .GetProperty("properties")
            .GetProperty("value")
            .GetProperty("enum")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            JsonValueKind.String,
            enumValues[0].ValueKind);
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            enumValues[0].GetString());
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            enumValues[1].GetString());
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            enumValues[2].GetString());
        Assert.Equal(7, enumValues[3].GetInt32());
        Assert.DoesNotContain(
            schema.EnumerateObject(),
            property => string.Equals(property.Name, "12345", StringComparison.Ordinal));

        using var structuredDocument = JsonDocument.Parse(
            """{"number":12345,"boolean":true,"nothing":null,"safe":7}""");
        var receipt = McpProviderResultProjection.Project(
            new McpToolCallResult(
                [],
                structuredDocument.RootElement.Clone(),
                IsError: false),
            redactor);

        Assert.DoesNotContain(
            "12345",
            receipt.ProviderJson,
            StringComparison.Ordinal);
        using var projected = JsonDocument.Parse(
            receipt.ProviderJson);
        var structured = projected.RootElement
            .GetProperty("structured_content");
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            structured.GetProperty("number").GetString());
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            structured.GetProperty("boolean").GetString());
        Assert.Equal(
            "[REDACTED MCP CONTENT]",
            structured.GetProperty("nothing").GetString());
        Assert.Equal(7, structured.GetProperty("safe").GetInt32());
    }

    [Fact]
    public void StructuredResultUsesCollisionFreeRedactedPropertyNames()
    {
        using var redactor = new McpSecretRedactor(
            ["secret_name".ToCharArray()]);
        using var structuredDocument = JsonDocument.Parse(
            """{"secret_name":1,"redacted_property_0":2}""");

        var receipt = McpProviderResultProjection.Project(
            new McpToolCallResult(
                [],
                structuredDocument.RootElement.Clone(),
                IsError: false),
            redactor);

        using var projected = JsonDocument.Parse(receipt.ProviderJson);
        var properties = projected.RootElement
            .GetProperty("structured_content")
            .EnumerateObject()
            .ToArray();
        Assert.Equal(2, properties.Length);
        Assert.Equal(
            2,
            properties.Select(property => property.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.DoesNotContain(
            properties,
            property => string.Equals(property.Name, "secret_name", StringComparison.Ordinal));
        Assert.Contains(
            properties,
            property => string.Equals(property.Name, "redacted_property_0", StringComparison.Ordinal));
    }

    private static void PublishProfileChange(
        HostFixture fixture,
        McpProfileChange change)
    {
        var snapshot = fixture.Catalog.SnapshotValue;
        var stored = Assert.Single(snapshot.McpServerProfiles);
        if (change == McpProfileChange.Delete)
        {
            fixture.Catalog.Publish(
                snapshot with
                {
                    McpServerProfiles = [],
                });
            return;
        }

        var profile = stored.Value;
        var changedProfile = new McpServerProfile(
            profile.Id,
            profile.SchemaVersion,
            change is McpProfileChange.Edit
                or McpProfileChange.SameFingerprintEdit
                    ? profile.Name + " edited"
                    : profile.Name,
            profile.Transport,
            profile.EnabledTools,
            isEnabled: change != McpProfileChange.Disable);
        var revision = change == McpProfileChange.Edit
            ? stored.Revision + 1
            : stored.Revision;
        fixture.Catalog.Publish(
            snapshot with
            {
                McpServerProfiles =
                [
                    new StoredDefinition<McpServerProfile>(
                        changedProfile,
                        revision,
                        stored.CreatedAt,
                        HostFixture.Now),
                ],
            });
    }

    private static void PublishDisabledSecondProfile(
        HostFixture fixture,
        bool advanceRevision)
    {
        var snapshot = fixture.Catalog.SnapshotValue;
        var profiles = snapshot.McpServerProfiles.ToArray();
        Assert.Equal(2, profiles.Length);
        var stored = profiles[1];
        var profile = stored.Value;
        var changedProfile = new McpServerProfile(
            profile.Id,
            profile.SchemaVersion,
            advanceRevision
                ? profile.Name + " edited"
                : profile.Name,
            profile.Transport,
            profile.EnabledTools,
            isEnabled: false);
        profiles[1] = new StoredDefinition<McpServerProfile>(
            changedProfile,
            advanceRevision
                ? stored.Revision + 1
                : stored.Revision,
            stored.CreatedAt,
            HostFixture.Now);
        fixture.Catalog.Publish(
            snapshot with
            {
                McpServerProfiles = profiles,
            });
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(3));
        while (!File.Exists(path))
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                timeout.Token);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeout.Token);
        }
    }

    private static SemaphoreSlim GetPrivateSemaphore(
        object instance,
        string fieldName) =>
        Assert.IsType<SemaphoreSlim>(
            instance.GetType()
                .GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instance));

    private static SemaphoreSlim GetOnlyRunOperationGate(
        AgentMcpSessionHost host)
        => GetPrivateSemaphore(
            GetOnlyRun(host),
            "_operationGate");

    private static SemaphoreSlim GetOnlyClientOperationGate(
        AgentMcpSessionHost host)
    {
        var run = GetOnlyRun(host);
        var profiles = Assert.IsAssignableFrom<
            System.Collections.IEnumerable>(
                run.GetType()
                    .GetField(
                        "_profiles",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(run));
        var profile = Assert.Single(
            profiles.Cast<object>());
        var client = profile.GetType()
            .GetProperty("Client")!
            .GetValue(profile)!;
        return GetPrivateSemaphore(
            client,
            "_operationLock");
    }

    private static object GetOnlyRun(
        AgentMcpSessionHost host)
    {
        var runs = host.GetType()
            .GetField(
                "_runs",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;
        var values = Assert.IsAssignableFrom<
            System.Collections.IEnumerable>(
                runs.GetType().GetProperty("Values")!
                    .GetValue(runs));
        return Assert.Single(values.Cast<object>());
    }

    public enum McpProfileChange
    {
        Disable,
        Delete,
        Edit,
        SameFingerprintEdit,
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        public const string SecretCanary =
            "api_key=ghostshell-mcp-secret-canary";
        public const string ReflectedToolCanary = "12345";
        public const long ProfileRevision = 7;

        public static readonly DateTimeOffset Now =
            new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

        private HostFixture(
            AgentMcpSessionHost host,
            AgentCapabilityBroker broker,
            CatalogProxy catalog,
            SecretVaultProxy vault,
            SessionHostProxy sessionHost,
            RecordingAuditStore audit)
        {
            Host = host;
            Broker = broker;
            Catalog = catalog;
            Vault = vault;
            SessionHost = sessionHost;
            Audit = audit;
        }

        public AgentMcpSessionHost Host { get; }

        public AgentCapabilityBroker Broker { get; }

        public CatalogProxy Catalog { get; }

        public SecretVaultProxy Vault { get; }

        public SessionHostProxy SessionHost { get; }

        public RecordingAuditStore Audit { get; }

        public static async Task<HostFixture> CreateAsync(
            string mode,
            string[]? hostArguments = null,
            IReadOnlyList<string>? enabledTools = null,
            bool isEnabled = true,
            bool isTrusted = true,
            int profileCount = 1,
            bool omitWorkingDirectory = false,
            TimeSpan? shutdownGracePeriod = null,
            McpServerTransport? transport = null,
            Func<Uri, HttpMessageHandler?>? streamableHttpHandlerFactory = null,
            IWorkspaceNetworkRouteResolver? workspaceNetworkRoutes = null)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var dotnetPath = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The .NET host path is unavailable.");
            var dotnetRoot = Path.GetDirectoryName(dotnetPath)
                ?? throw new InvalidOperationException(
                    "The .NET host directory is unavailable.");
            var environment = new List<McpServerEnvironmentVariable>
            {
                new(
                    "DOTNET_ROOT",
                    new SecretRef("mcp-dotnet-root")),
                new(
                    "GHOSTSHELL_ALLOWED",
                    new SecretRef("mcp-secret-canary")),
            };
            if (string.Equals(mode, "secret-tool-name", StringComparison.Ordinal))
            {
                environment.Add(new(
                    "GHOSTSHELL_REFLECTED_TOOL",
                    new SecretRef("mcp-reflected-tool")));
            }
            else if (string.Equals(mode, "secret-aggregate-limit", StringComparison.Ordinal))
            {
                for (var index = 0; index < 5; index++)
                {
                    environment.Add(new(
                        $"GHOSTSHELL_LIMIT_{index}",
                        new SecretRef($"mcp-limit-{index}")));
                }
            }
            else if (string.Equals(mode, "windows-environment-limit", StringComparison.Ordinal))
            {
                for (var index = 0; index < 2; index++)
                {
                    environment.Add(new(
                        $"GHOSTSHELL_WINDOWS_LIMIT_{index}",
                        new SecretRef($"mcp-windows-limit-{index}")));
                }
            }

            var storedProfiles = Enumerable.Range(0, profileCount)
                .Select(index =>
                {
                    var profile = transport is null
                        ? new McpServerProfile(
                            ProfileId(index),
                            McpServerProfile.CurrentSchemaVersion,
                            $"Test MCP {index + 1}",
                            new McpServerTransport.Stdio(
                                dotnetPath,
                                [
                                    assemblyPath,
                                    "--mcp-test-host",
                                    mode,
                                    .. (hostArguments ?? []),
                                ],
                                omitWorkingDirectory
                                    ? null
                                    : Path.GetDirectoryName(assemblyPath),
                                environment),
                            enabledTools ?? ["control"],
                            isEnabled,
                            isTrusted)
                        : new McpServerProfile(
                            ProfileId(index),
                            McpServerProfile.CurrentSchemaVersion,
                            $"Test MCP {index + 1}",
                            transport,
                            enabledTools ?? ["control"],
                            isEnabled,
                            isTrusted);
                    return new StoredDefinition<McpServerProfile>(
                        profile,
                        Revision: ProfileRevision,
                        Now,
                        Now);
                })
                .ToArray();
            var catalog = DispatchProxy.Create<
                IDefinitionCatalog,
                CatalogProxy>();
            var catalogProxy = (CatalogProxy)(object)catalog;
            catalogProxy.SnapshotValue =
                DefinitionCatalogSnapshot.Empty with
                {
                    McpServerProfiles = storedProfiles,
                };
            var vault = DispatchProxy.Create<
                ISecretVault,
                SecretVaultProxy>();
            var vaultProxy = (SecretVaultProxy)(object)vault;
            vaultProxy.ExpectedScopeOwnerIds = storedProfiles
                .Select(stored => stored.Value.Id.Value)
                .ToHashSet(StringComparer.Ordinal);
            vaultProxy.Values = new Dictionary<string, byte[]>(
                StringComparer.Ordinal)
            {
                ["mcp-dotnet-root"] = Encoding.UTF8.GetBytes(dotnetRoot),
                ["mcp-secret-canary"] =
                    Encoding.UTF8.GetBytes(SecretCanary),
            };
            if (string.Equals(mode, "secret-tool-name", StringComparison.Ordinal))
            {
                vaultProxy.Values["mcp-reflected-tool"] =
                    Encoding.UTF8.GetBytes(ReflectedToolCanary);
            }
            else if (string.Equals(mode, "secret-aggregate-limit", StringComparison.Ordinal))
            {
                for (var index = 0; index < 5; index++)
                {
                    vaultProxy.Values[$"mcp-limit-{index}"] =
                        [.. Enumerable.Repeat((byte)'s', 30 * 1024)];
                }
            }
            else if (string.Equals(mode, "windows-environment-limit", StringComparison.Ordinal))
            {
                for (var index = 0; index < 2; index++)
                {
                    vaultProxy.Values[$"mcp-windows-limit-{index}"] =
                        [.. Enumerable.Repeat((byte)'w', 16_380)];
                }
            }
            var sessionHost = DispatchProxy.Create<
                ISessionHostClient,
                SessionHostProxy>();
            var sessionHostProxy =
                (SessionHostProxy)(object)sessionHost;
            sessionHostProxy.Context = CreateContext();
            var audit = new RecordingAuditStore();
            var broker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                audit,
                new FixedTimeProvider(Now));
            var registrationError = await broker.RegisterRunAsync(
                new AgentRunRegistration(
                    RunId(),
                    Agent(),
                    ClientId(),
                    Target(),
                    McpPolicy(AgentPermission.Ask),
                    policyGeneration: 1),
                CancellationToken.None);
            Assert.Null(registrationError);
            var host = new AgentMcpSessionHost(
                catalog,
                vault,
                sessionHost,
                broker,
                broker,
                new AgentMcpToolCallActionComposer(),
                new FixedTimeProvider(Now),
                new FixedApprovalPrincipal(Human()),
                new McpSessionOptions
                {
                    MaxTools = 128,
                    MaxToolSchemaBytes =
                        AgentMcpToolManifest.MaximumInputSchemaBytes,
                    MaxToolArgumentsBytes =
                        AgentMcpToolCallRequest.MaximumArgumentsBytes,
                    MaxToolResultBytes =
                        AgentMcpToolCallReceipt.MaximumProviderJsonBytes,
                    ShutdownGracePeriod =
                        shutdownGracePeriod
                            ?? TimeSpan.FromMilliseconds(50),
                },
                streamableHttpHandlerFactory,
                workspaceNetworkRoutes: workspaceNetworkRoutes);
            return new HostFixture(
                host,
                broker,
                catalogProxy,
                vaultProxy,
                sessionHostProxy,
                audit);
        }

        public async Task<AgentMcpRunManifest> OpenAsync(
            AgentRunId? runId = null)
        {
            var result = await Host.OpenRunAsync(
                new AgentMcpOpenRunRequest(
                    runId ?? RunId(),
                    Agent(),
                    WorkspaceId(),
                    Now),
                CancellationToken.None);
            return Assert.IsType<
                AgentMcpHostResult<AgentMcpRunManifest>.Success>(
                result).Value;
        }

        public ValueTask<AgentAuthorizationError?> RegisterRunAsync(
            AgentRunId runId) =>
            Broker.RegisterRunAsync(
                new AgentRunRegistration(
                    runId,
                    Agent(),
                    ClientId(),
                    Target(),
                    McpPolicy(AgentPermission.Ask),
                    policyGeneration: 1),
                CancellationToken.None);

        public AgentMcpToolCallAction Prepare(
            AgentMcpToolManifest manifest,
            string arguments) =>
            new AgentMcpToolCallActionComposer().Prepare(
                new AgentActionEnvelope(
                    new AgentActionId("mcp-action"),
                    RunId(),
                    Agent(),
                    policyGeneration: 1,
                    Now,
                    Now.AddMinutes(1)),
                SessionHost.Context,
                new AgentMcpToolCallRequest(
                    manifest,
                    JsonDocument.Parse(arguments)
                        .RootElement.Clone()));

        public async Task<AgentAuthorizationId> AuthorizeAsync(
            AgentMcpToolCallAction action)
        {
            var requested = await Broker.RequestAsync(
                action.Proposal,
                CancellationToken.None);
            var approval = Assert.IsType<
                AgentAuthorizationResult.ApprovalRequired>(
                requested).Approval;
            var decided = await Broker.DecideAsync(
                new AgentApprovalDecision(
                    approval.Id,
                    Human(),
                    approved: true,
                    AgentApprovalDuration.Once,
                    Now),
                CancellationToken.None);
            return Assert.IsType<
                AgentAuthorizationResult.Authorized>(
                decided).Authorization.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Broker.DisposeAsync();
            foreach (var value in Vault.Values.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }

        private static AgentContextSnapshot CreateContext()
        {
            var panel = new PanelInstance(
                PanelId(),
                PanelKind.Terminal,
                "Test terminal",
                SessionId());
            var tab = new TabInstance(
                TabId(),
                "Test",
                [panel],
                panel.Id);
            var graph = new WorkspaceGraphSnapshot(
                WindowId(),
                new WorkspaceInstance(
                    WorkspaceId(),
                    "Test",
                    [tab],
                    tab.Id),
                revision: 1,
                lastSequence: 1);
            var descriptor = new SessionDescriptor(
                SessionId(),
                PanelKind.Terminal,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId(),
                    WorkspaceId(),
                    TabId(),
                    PanelId()),
                CapabilitySet.Empty,
                Revision: 1,
                HasActiveWork: false,
                StatusDetail: "Ready");
            return new AgentContextSnapshot(
                Target(),
                [
                    AgentContextPanel.ForGraphPanel(
                        graph,
                        TabId(),
                        PanelId(),
                        descriptor),
                ],
                Now);
        }

        public static McpServerProfileId ProfileId(int index = 0) =>
            new(index == 0 ? "mcp.test" : $"mcp.test.{index + 1}");

        public static AgentRunId RunId() => new("mcp-run");

        public static AgentPolicy McpPolicy(AgentPermission permission) =>
            AgentPolicy.Default with
            {
                Permissions = AgentPolicy.Default.Permissions.SetItem(
                    AgentCapability.McpTools,
                    permission),
            };

        public static ClientId ClientId() => new("mcp-client");

        public static ActorDescriptor Agent() =>
            new(
                new ActorId("mcp-agent"),
                ActorKind.Agent,
                "MCP agent");

        public static ActorDescriptor Human() =>
            new(
                new ActorId(ClientId().Value),
                ActorKind.Human,
                "Local user",
                ClientId());

        private sealed class FixedApprovalPrincipal(
            ActorDescriptor actor) : IAgentApprovalPrincipal
        {
            public ActorDescriptor Actor { get; } = actor;
        }

        private static AgentTarget.Panel Target() =>
            new(WindowId(), WorkspaceId(), TabId(), PanelId());

        private static WindowInstanceId WindowId() =>
            new("mcp-window");

        public static WorkspaceInstanceId WorkspaceId() =>
            new("mcp-workspace");

        private static TabInstanceId TabId() => new("mcp-tab");

        private static PanelInstanceId PanelId() => new("mcp-panel");

        private static SessionId SessionId() => new("mcp-session");
    }

    private sealed class FixedWorkspaceNetworkRoutes(
        IWorkspaceNetworkConnector connector,
        IConnectionCommandRuntime? commandRuntime = null) :
        IWorkspaceNetworkRouteResolver
    {
        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public IWorkspaceNetworkConnector? ConnectorFor(
            WorkspaceInstanceId workspaceId)
        {
            LastWorkspaceId = workspaceId;
            return connector;
        }

        public IConnectionCommandRuntime? IsolatedCommandRuntimeFor(
            WorkspaceInstanceId workspaceId)
        {
            LastWorkspaceId = workspaceId;
            return commandRuntime;
        }
    }

    private sealed class FixedWorkspaceNetworkConnector(
        WorkspaceNetworkEgress egress) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress { get; } = egress;

        public Uri LocalProxyEndpoint { get; } =
            new("socks5://127.0.0.1:45678", UriKind.Absolute);

        public ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWorkspaceCommandRuntime : IConnectionCommandRuntime
    {
        public int DuplexPlanCount { get; private set; }

        public ConnectionProfile? LastConnection { get; private set; }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>>
            PlanDuplexCommandAsync(
                ConnectionProfile connection,
                string executable,
                IReadOnlyList<string> arguments,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DuplexPlanCount++;
            LastConnection = connection;
            var environment = connection.Startup.Environment.ToDictionary(
                variable => variable.Name,
                variable => Assert.IsType<ConnectionEnvironmentValue.PlainText>(
                    variable.Value).Value,
                StringComparer.Ordinal);
            return ValueTask.FromResult(
                ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(
                    new TerminalLaunchRequest(
                        connection.Startup.Directory,
                        executable,
                        arguments,
                        environment)));
        }
    }

    public class CatalogProxy : DispatchProxy
    {
        private EventHandler? _changed;

        public DefinitionCatalogSnapshot SnapshotValue { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public void Publish(DefinitionCatalogSnapshot snapshot)
        {
            SnapshotValue = snapshot;
            _changed?.Invoke(this, EventArgs.Empty);
        }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments)
        {
            switch (targetMethod?.Name)
            {
                case "get_Snapshot":
                    return SnapshotValue;
                case "add_Changed":
                    _changed += (EventHandler)arguments![0]!;
                    return null;
                case "remove_Changed":
                    _changed -= (EventHandler)arguments![0]!;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    public class SecretVaultProxy : DispatchProxy
    {
        public Dictionary<string, byte[]> Values { get; set; } =
            new(StringComparer.Ordinal);

        public IReadOnlySet<string> ExpectedScopeOwnerIds { get; set; } =
            new HashSet<string>(StringComparer.Ordinal);

        public ConcurrentQueue<SecretUsePurpose> Purposes { get; } =
            [];

        public bool BlockResolution { get; set; }

        public TaskCompletionSource ResolutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseResolution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.Resolve,
                    "test",
                    "test_available",
                    "Test vault is available."),
                nameof(ISecretVault.ResolveAsync)
                    when arguments is
                    [
                        ResolveSecretRequest request,
                        CancellationToken cancellationToken,
                    ] => Resolve(request, cancellationToken),
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private async ValueTask<
            SecretVaultResult<SecretMaterial>> Resolve(
            ResolveSecretRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Purposes.Enqueue(request.Purpose);
            Assert.Equal(SecretScopeKind.McpServer, request.Scope.Kind);
            Assert.NotNull(request.Scope.OwnerId);
            Assert.Contains(
                request.Scope.OwnerId!,
                ExpectedScopeOwnerIds);
            if (BlockResolution)
            {
                ResolutionStarted.TrySetResult();
                await ReleaseResolution.Task.WaitAsync(
                    cancellationToken);
            }

            return Values.TryGetValue(
                request.Reference.Value,
                out var value)
                ? SecretVaultResult<SecretMaterial>.Succeed(
                    SecretMaterial.CopyFrom(value))
                : SecretVaultResult<SecretMaterial>.Fail(
                    SecretVaultError.Create(
                        SecretVaultErrorCode.NotFound));
        }
    }

    public class SessionHostProxy : DispatchProxy
    {
        public AgentContextSnapshot Context { get; set; } = null!;

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? arguments) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.InspectAgentContextAsync)
                    when arguments is
                    [
                        AgentContextRequest request,
                        OperationContext _,
                        CancellationToken cancellationToken,
                    ] => Inspect(request, cancellationToken),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private ValueTask<HostResult<AgentContextSnapshot>> Inspect(
            AgentContextRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                request.Target == Context.Target
                    ? HostResult<AgentContextSnapshot>.Succeed(
                        Context,
                        Context.Revision)
                    : HostResult<AgentContextSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.NotFound,
                            "Target unavailable."),
                        Context.Revision));
        }
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        private readonly ConcurrentQueue<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events =>
            [.. _events];

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue(auditEvent);
            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> values = [.. Events.Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))];
            return ValueTask.FromResult(
                AuditStoreResult<
                    IReadOnlyList<AuditEventRecord>>.Success(values));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
