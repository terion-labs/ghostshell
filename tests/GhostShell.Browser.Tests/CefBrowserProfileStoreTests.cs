using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Browser.Tests;

public sealed class CefBrowserProfileStoreTests
{
    [Fact]
    public void ConstructionDoesNotCreatePersistentProfileStorage()
    {
        var parent = TemporaryRoot();
        var root = Path.Combine(parent, "profiles");
        try
        {
            using var store = new CefBrowserProfileStore();

            Assert.False(Directory.Exists(root));
            var state = store.ReadState(Selection("profile.one"), expectedRevision: 7);
            Assert.Equal(0, state.ActiveContexts);
            Assert.Equal(0, state.ActiveLeases);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingAnEmptyExactRevisionIsSuccessfulAndIdempotent()
    {
        var root = TemporaryRoot();
        try
        {
            using var store = new CefBrowserProfileStore();
            var request = new BrowserProfileClearRequest(
                Selection("profile.empty"),
                expectedRevision: 11,
                BrowserProfileDataCategory.AllEphemeralWebContent);

            var first = await store.ClearAsync(request, CancellationToken.None);
            var second = await store.ClearAsync(request, CancellationToken.None);

            Assert.Equal(BrowserProfileClearStatus.Cleared, first.Status);
            Assert.Equal(0, first.ClearedBytes);
            Assert.Equal(BrowserProfileClearStatus.Cleared, second.Status);
            Assert.Equal(0, second.ClearedBytes);
            Assert.Contains("encrypted browser state", first.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearingHonorsCancellationBeforeTouchingAProfile()
    {
        var root = TemporaryRoot();
        try
        {
            using var store = new CefBrowserProfileStore();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await store.ClearAsync(
                new BrowserProfileClearRequest(
                    Selection("profile.cancelled"),
                    expectedRevision: 3,
                    BrowserProfileDataCategory.Cookies),
                cancellation.Token);

            Assert.Equal(BrowserProfileClearStatus.Cancelled, result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocalLeasesShareOneExactContextAndTheFinalLeaseDestroysIt()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.shared", revision: 7);

        var first = store.AcquireLocal(binding);
        var second = store.AcquireLocal(binding);

        var context = Assert.Single(contexts.Created);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 1, 2),
            store.ReadState(binding.Selection, expectedRevision: 7));

        first.Dispose();

        Assert.Equal(0, context.DisposeCount);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 1, 1),
            store.ReadState(binding.Selection, expectedRevision: 7));

        second.Dispose();

        Assert.Equal(1, context.DisposeCount);
        Assert.Equal(
            new BrowserProfileDataState(binding.Selection, 7, 0, 0),
            store.ReadState(binding.Selection, expectedRevision: 7));
    }

    [Fact]
    public async Task Routed_surface_cannot_be_created_until_all_preferences_are_accepted()
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RecordingRequestContext(null) { PreferenceCompletion = accepted.Task };
        using var store = new CefBrowserProfileStore(null, _ => context);
        using var lease = store.AcquireRouted(Binding("profile.pending", revision: 1), "route", 41001);
        Assert.False(lease.Ready.IsCompleted);
        Assert.Empty(context.Preferences);
        Assert.Throws<InvalidOperationException>(() => lease.CreateView());
        accepted.SetResult(true);
        await lease.Ready;
        Assert.Equal(2, context.Preferences.Count);
        Assert.Throws<NotSupportedException>(() => lease.CreateView());
    }

    [Fact]
    public async Task Rejected_preference_never_publishes_a_surface()
    {
        var context = new RecordingRequestContext(null) { PreferenceCompletion = Task.FromResult(false) };
        using var store = new CefBrowserProfileStore(null, _ => context);
        using var lease = store.AcquireRouted(Binding("profile.rejected", revision: 1), "route", 41001);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.Ready);
        Assert.Throws<InvalidOperationException>(() => lease.CreateView());
    }

    [Fact]
    public async Task Canceled_owner_can_release_pending_context_without_late_surface_publication()
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RecordingRequestContext(null) { PreferenceCompletion = accepted.Task };
        using var store = new CefBrowserProfileStore(null, _ => context);
        var lease = store.AcquireRouted(Binding("profile.canceled", revision: 1), "route", 41001);
        using var cancellation = new CancellationTokenSource();
        var wait = lease.Ready.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        lease.Dispose();
        accepted.SetResult(true);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.Ready);
        Assert.Empty(context.Preferences);
        Assert.Equal(1, context.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => lease.CreateView());
    }

    [Fact]
    public void RoutedLeasesShareOnlyTheExactRevisionRouteAndProxyEndpoint()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.routed", revision: 9);

        var first = store.AcquireRouted(binding, "connection.one", 41001);
        var second = store.AcquireRouted(binding, "connection.one", 41001);

        var context = Assert.Single(contexts.Created);
        Assert.NotEmpty(context.Preferences);
        Assert.Throws<InvalidOperationException>(() =>
            store.AcquireRouted(binding, "connection.one", 41002));
        Assert.Single(contexts.Created);

        first.Dispose();
        Assert.Equal(0, context.DisposeCount);
        second.Dispose();
        Assert.Equal(1, context.DisposeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://destination.example/private")]
    public void AuthenticatedWorkspaceRoutePassesItsCredentialsToEveryCreatedView(string? origin)
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var credentials = new WorkspaceNetworkProxyCredentials("workspace", "password");
        var connector = new AuthenticatedConnector(credentials);
        using var lease = store.AcquireRouted(
            Binding("profile.proxy-auth", revision: 1),
            "workspace.route",
            connector);

        Assert.Throws<NotSupportedException>(() => lease.CreateView());

        var resolver = Assert.Single(contexts.Created).ProxyAuthenticationResolver;
        Assert.NotNull(resolver);
        Assert.Equal(
            new BrowserAuthenticationCredentials("workspace", "password"),
            resolver.Resolve(new BrowserAuthenticationChallenge(
                true,
                "127.0.0.1",
                connector.BrowserProxyEndpoint.Port,
                "GhostSHELL workspace",
                "basic",
                origin)));
        Assert.Null(resolver.Resolve(new BrowserAuthenticationChallenge(
            false, "127.0.0.1", connector.BrowserProxyEndpoint.Port,
            "GhostSHELL workspace", "basic", origin)));
        Assert.Null(resolver.Resolve(new BrowserAuthenticationChallenge(
            true, "destination.example", connector.BrowserProxyEndpoint.Port,
            "GhostSHELL workspace", "basic", origin)));
        Assert.Null(resolver.Resolve(new BrowserAuthenticationChallenge(
            true, "127.0.0.1", connector.BrowserProxyEndpoint.Port + 1,
            "GhostSHELL workspace", "basic", origin)));
    }

    [Theory]
    [InlineData(false, "local")]
    [InlineData(true, "ssh-selected-authority")]
    public async Task Server_authentication_uses_the_owned_route_not_caller_supplied_route(bool routed, string expectedRoute)
    {
        var contexts = new RecordingRequestContextFactory();
        var resolver = new RecordingAuthenticationResolver();
        using var store = new CefBrowserProfileStore(resolver, contexts.Create);
        var binding = Binding("profile.route-auth", revision: 1);
        using var lease = routed
            ? store.AcquireRouted(binding, "persistent-route", 41001, expectedRoute)
            : store.AcquireLocal(binding);
        Assert.Throws<NotSupportedException>(() => lease.CreateView());
        var bound = Assert.Single(contexts.Created).AuthenticationResolver;
        Assert.NotNull(bound);

        await bound.ResolveAsync(binding, new(false, "internal.example", 443, "realm", "basic",
            "https://internal.example/private", "forged-route"), CancellationToken.None);

        Assert.Equal(expectedRoute, resolver.Challenge?.RouteIdentity);
        Assert.Equal("https://internal.example/private", resolver.Challenge?.OriginUrl);
    }

    private sealed class RecordingAuthenticationResolver : IBrowserProfileAuthenticationResolver
    {
        public BrowserAuthenticationChallenge? Challenge { get; private set; }
        public TaskCompletionSource<BrowserAuthenticationCredentials?>? Completion { get; set; }

        public ValueTask<BrowserAuthenticationCredentials?> ResolveAsync(BrowserProfileBinding profile,
            BrowserAuthenticationChallenge challenge, CancellationToken cancellationToken)
        {
            Challenge = challenge;
            return Completion is null
                ? ValueTask.FromResult<BrowserAuthenticationCredentials?>(null)
                : new ValueTask<BrowserAuthenticationCredentials?>(Completion.Task);
        }
    }

    [Fact]
    public async Task Workspace_authority_switch_clears_http_credentials_without_deleting_cookies_and_rechecks_inflight_resolution()
    {
        var contexts = new RecordingRequestContextFactory();
        var resolver = new RecordingAuthenticationResolver
        {
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var store = new CefBrowserProfileStore(resolver, contexts.Create);
        var connector = new AuthenticatedConnector(new("workspace", "password"));
        var binding = Binding("profile.network-auth", revision: 1);
        using var lease = store.AcquireRouted(binding, "stable-cookie-route", connector);
        Assert.Throws<NotSupportedException>(() => lease.CreateView());
        var context = Assert.Single(contexts.Created);
        var pending = context.AuthenticationResolver!.ResolveAsync(binding,
            new(false, "internal.example", 443, "realm", "basic", "https://internal.example"), CancellationToken.None);

        await connector.ChangeAuthorityAsync("network-one", CancellationToken.None);
        resolver.Completion.SetResult(new("operator", "old-direct-password"));

        Assert.Null(await pending);
        Assert.Equal(1, context.ClearHttpAuthCredentialsCount);
        Assert.Equal(1, context.CloseAllConnectionsCount);
        Assert.Equal(0, context.DeleteCookiesCount);
        Assert.Equal(0, context.DisposeCount);
        using var reacquired = store.AcquireRouted(binding, "stable-cookie-route", connector);
        Assert.Single(contexts.Created);
    }

    [Fact]
    public void RoutedConnectionNamedLocalDoesNotShareTheLocalContext()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.route-namespace", revision: 1);

        using var local = store.AcquireLocal(binding);
        using var routed = store.AcquireRouted(binding, "local", 41501);

        Assert.Equal(2, contexts.Created.Count);
        Assert.Empty(contexts.Created[0].Preferences);
        Assert.NotEmpty(contexts.Created[1].Preferences);
        Assert.Equal(
            2,
            store.ReadState(binding.Selection, binding.Revision).ActiveContexts);
    }

    [Fact]
    public void DefinitionRevisionsShareStateWhileRoutesRemainIsolated()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var firstRevision = Binding("profile.isolated", revision: 3);
        var secondRevision = Binding("profile.isolated", revision: 4);
        var otherProfile = Binding("profile.other", revision: 3);

        using var firstLocal = store.AcquireLocal(firstRevision);
        using var secondLocal = store.AcquireLocal(secondRevision);
        using var otherLocal = store.AcquireLocal(otherProfile);
        using var firstRoute = store.AcquireRouted(
            firstRevision,
            "connection.one",
            42001);
        using var secondRoute = store.AcquireRouted(
            firstRevision,
            "connection.two",
            42002);

        Assert.Equal(4, contexts.Created.Count);
        Assert.Equal(3, store.ReadState(
            firstRevision.Selection,
            firstRevision.Revision).ActiveContexts);
        Assert.Equal(1, store.ReadState(
            secondRevision.Selection,
            secondRevision.Revision).ActiveContexts);
        Assert.Equal(1, store.ReadState(
            otherProfile.Selection,
            otherProfile.Revision).ActiveContexts);
    }

    [Fact]
    public async Task ExactCategoryClearsTouchOnlyTheSelectedProfileRevision()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var selected = Binding("profile.selected", revision: 12);
        var other = Binding("profile.other", revision: 12);
        using var selectedLocal = store.AcquireLocal(selected);
        using var selectedRouted = store.AcquireRouted(
            selected,
            "connection.selected",
            43001);
        using var otherLocal = store.AcquireLocal(other);

        var cookies = await store.ClearAsync(
            new BrowserProfileClearRequest(
                selected.Selection,
                selected.Revision,
                BrowserProfileDataCategory.Cookies),
            CancellationToken.None);
        var authentication = await store.ClearAsync(
            new BrowserProfileClearRequest(
                selected.Selection,
                selected.Revision,
                BrowserProfileDataCategory.HttpAuthentication),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.Cleared, cookies.Status);
        Assert.Equal(BrowserProfileClearStatus.Cleared, authentication.Status);
        Assert.All(contexts.Created.Take(2), context =>
        {
            Assert.Equal(1, context.DeleteCookiesCount);
            Assert.Equal(1, context.FlushCookieStoreCount);
            Assert.Equal(1, context.ClearHttpAuthCredentialsCount);
            Assert.Equal(1, context.CloseAllConnectionsCount);
        });
        Assert.Equal(0, contexts.Created[2].DeleteCookiesCount);
        Assert.Equal(0, contexts.Created[2].ClearHttpAuthCredentialsCount);
        Assert.Equal(0, contexts.Created[2].CloseAllConnectionsCount);
    }

    [Fact]
    public async Task FullResetRefusesAnExactRevisionWhileItHasAnOwner()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.in-use", revision: 5);
        using var lease = store.AcquireLocal(binding);

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                binding.Selection,
                binding.Revision,
                BrowserProfileDataCategory.AllEphemeralWebContent),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.InUse, result.Status);
        Assert.Equal(0, Assert.Single(contexts.Created).DeleteCookiesCount);
    }

    [Fact]
    public async Task ClearFailsClosedWhenAnotherRevisionOwnsThePartition()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var current = Binding("profile.revision", revision: 8);
        using var lease = store.AcquireLocal(current);

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                current.Selection,
                expectedRevision: 7,
                BrowserProfileDataCategory.Cookies),
            CancellationToken.None);

        Assert.Equal(BrowserProfileClearStatus.RevisionMismatch, result.Status);
        Assert.Equal(0, Assert.Single(contexts.Created).DeleteCookiesCount);
    }

    [Fact]
    public async Task ClearCancellationStopsBeforeTheNextMatchingRoute()
    {
        using var cancellation = new CancellationTokenSource();
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.cancel-between-routes", revision: 6);
        using var local = store.AcquireLocal(binding);
        using var routed = store.AcquireRouted(
            binding,
            "connection.cancelled",
            44001);
        contexts.Created[0].CookiesDeleted = cancellation.Cancel;

        var result = await store.ClearAsync(
            new BrowserProfileClearRequest(
                binding.Selection,
                binding.Revision,
                BrowserProfileDataCategory.Cookies),
            cancellation.Token);

        Assert.Equal(BrowserProfileClearStatus.Cancelled, result.Status);
        Assert.Equal(1, contexts.Created[0].DeleteCookiesCount);
        Assert.Equal(0, contexts.Created[1].DeleteCookiesCount);
    }

    [Fact]
    public async Task CookieClearWaitsForDeletionAndDurableStoreFlush()
    {
        var contexts = new RecordingRequestContextFactory();
        using var store = new CefBrowserProfileStore(null, contexts.Create);
        var binding = Binding("profile.acknowledged", revision: 2);
        using var lease = store.AcquireLocal(binding);
        var context = Assert.Single(contexts.Created);
        var deleted = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var flushed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.DeleteCookiesCompletion = deleted.Task;
        context.FlushCookieStoreCompletion = flushed.Task;

        var clear = store.ClearAsync(
            new BrowserProfileClearRequest(
                binding.Selection,
                binding.Revision,
                BrowserProfileDataCategory.Cookies),
            CancellationToken.None).AsTask();

        Assert.False(clear.IsCompleted);
        deleted.SetResult(1);
        await Task.Yield();
        Assert.False(clear.IsCompleted);
        flushed.SetResult();

        Assert.Equal(BrowserProfileClearStatus.Cleared, (await clear).Status);
    }

    [Fact]
    public void LegacyProfileCleanupRefusesToFollowARootLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = TemporaryRoot();
        var outside = TemporaryRoot();
        var link = Path.Combine(parent, "profiles");
        try
        {
            var marker = Path.Combine(outside, "must-survive");
            File.WriteAllText(marker, "legacy");
            Directory.CreateSymbolicLink(link, outside);

            Assert.Throws<IOException>(() =>
                CefBrowserProfileStore.DeleteOwnedDirectory(link));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(parent, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task DurableProfileSealsAndRestoresTheCompleteRuntimeTree()
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore();
        var binding = Binding(
            "profile.persisted",
            revision: 7,
            BrowserProfilePersistence.DurableMetadata);
        try
        {
            var firstContexts = new RecordingRequestContextFactory();
            using (var first = new CefBrowserProfileStore(
                       null,
                       state,
                       root,
                       firstContexts.Create))
            {
                using (first.AcquireLocal(binding))
                {
                    var cachePath = Assert.Single(firstContexts.Created).CachePath;
                    Assert.NotNull(cachePath);
                    Assert.Equal(root, Path.GetDirectoryName(cachePath));
                    if (!OperatingSystem.IsWindows())
                    {
                        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            File.GetUnixFileMode(cachePath));
                    }
                    Directory.CreateDirectory(Path.Combine(cachePath, "Default"));
                    File.WriteAllText(
                        Path.Combine(cachePath, "Default", "Cookies"),
                        "signed-in");
                    File.WriteAllText(
                        Path.Combine(root, "Local State"),
                        "os-crypt-metadata");
                }

                Assert.Equal(0, Assert.Single(firstContexts.Created).DisposeCount);
                first.ReleaseContextsForEngineShutdown();
                Assert.True(await first.SealRuntimeStateAfterEngineShutdownAsync());
            }

            var secondContexts = new RecordingRequestContextFactory();
            using var second = new CefBrowserProfileStore(
                null,
                state,
                root,
                secondContexts.Create);
            Assert.True(second.RecoverOrphanedRuntimeState());
            Assert.Equal(
                "os-crypt-metadata",
                File.ReadAllText(Path.Combine(root, "Local State")));
            using var restored = second.AcquireLocal(binding);
            var restoredPath = Assert.Single(secondContexts.Created).CachePath;
            Assert.Equal(
                "signed-in",
                File.ReadAllText(Path.Combine(restoredPath!, "Default", "Cookies")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DurableWorkspaceProfileRestoresAcrossLoopbackPortChanges()
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore();
        var binding = Binding("profile.workspace", 1, BrowserProfilePersistence.DurableMetadata);
        try
        {
            var firstContexts = new RecordingRequestContextFactory();
            using (var first = new CefBrowserProfileStore(null, state, root, firstContexts.Create))
            {
                var connector = new AuthenticatedConnector(
                    new WorkspaceNetworkProxyCredentials("workspace", "first"), 42001, "workspace:saved");
                using (first.AcquireRouted(binding, connector.LocalProxyEndpoint.AbsoluteUri, connector))
                {
                    File.WriteAllText(Path.Combine(firstContexts.Created[0].CachePath!, "Cookies"), "signed-in");
                }

                first.ReleaseContextsForEngineShutdown();
                Assert.True(await first.SealRuntimeStateAfterEngineShutdownAsync());
            }

            var secondContexts = new RecordingRequestContextFactory();
            using var second = new CefBrowserProfileStore(null, state, root, secondContexts.Create);
            var restoredConnector = new AuthenticatedConnector(
                new WorkspaceNetworkProxyCredentials("workspace", "second"), 42002, "workspace:saved");
            using var restored = second.AcquireRouted(
                binding, restoredConnector.LocalProxyEndpoint.AbsoluteUri, restoredConnector);
            Assert.Equal("signed-in", File.ReadAllText(Path.Combine(secondContexts.Created[0].CachePath!, "Cookies")));

            var otherConnector = new AuthenticatedConnector(
                new WorkspaceNetworkProxyCredentials("workspace", "other"), 42003, "workspace:other");
            using var other = second.AcquireRouted(binding, otherConnector.LocalProxyEndpoint.AbsoluteUri, otherConnector);
            Assert.False(File.Exists(Path.Combine(secondContexts.Created[1].CachePath!, "Cookies")));

            var cleared = await second.ClearAsync(
                new BrowserProfileClearRequest(binding.Selection, 1, BrowserProfileDataCategory.Cookies),
                CancellationToken.None);
            Assert.Equal(BrowserProfileClearStatus.Cleared, cleared.Status);
            Assert.Equal(2, secondContexts.Created.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostProfileRestoresAfterRuntimeInstanceChangesWithoutSharingLiveRoutes(bool isolated)
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore();
        var template = Binding("profile.host", 1, BrowserProfilePersistence.DurableMetadata);
        var selection = new BrowserProfileSelection(template.Selection.ProfileId,
            isolated ? BrowserProfileKey.ForWorkspace("saved-workspace") : BrowserProfileKey.Global);
        var binding = new BrowserProfileBinding(selection, template.Definition, template.Revision);
        try
        {
            var firstContexts = new RecordingRequestContextFactory();
            using (var first = new CefBrowserProfileStore(null, state, root, firstContexts.Create))
            {
                var connector = new AuthenticatedConnector(
                    new WorkspaceNetworkProxyCredentials("workspace", "first"), 42101, "host-workspace");
                using (first.AcquireRouted(binding, "runtime:first-instance", connector))
                {
                    File.WriteAllText(Path.Combine(firstContexts.Created[0].CachePath!, "Cookies"), "signed-in");
                }

                first.ReleaseContextsForEngineShutdown();
                Assert.True(await first.SealRuntimeStateAfterEngineShutdownAsync());
            }

            var contexts = new RecordingRequestContextFactory();
            using var second = new CefBrowserProfileStore(null, state, root, contexts.Create);
            var restoredConnector = new AuthenticatedConnector(
                new WorkspaceNetworkProxyCredentials("workspace", "second"), 42102, "host-workspace");
            using var restored = second.AcquireRouted(binding, "runtime:recreated-instance", restoredConnector);
            Assert.Equal("signed-in", File.ReadAllText(Path.Combine(contexts.Created[0].CachePath!, "Cookies")));

            var otherSelection = new BrowserProfileSelection(template.Selection.ProfileId,
                isolated ? BrowserProfileKey.ForWorkspace("another-saved-workspace") : BrowserProfileKey.Global);
            var otherBinding = new BrowserProfileBinding(otherSelection, template.Definition, template.Revision);
            var otherConnector = new AuthenticatedConnector(
                new WorkspaceNetworkProxyCredentials("workspace", "other"), 42103, "host-workspace");
            using var other = second.AcquireRouted(otherBinding, "runtime:another-instance", otherConnector);
            Assert.Equal(2, contexts.Created.Count);
            Assert.NotSame(contexts.Created[0], contexts.Created[1]);
            Assert.Equal(!isolated, File.Exists(Path.Combine(contexts.Created[1].CachePath!, "Cookies")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRecoversAnOrphanedDurableRuntimeTree(bool previousNestedLayout)
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore();
        var binding = Binding(
            "profile.orphan",
            revision: 3,
            BrowserProfilePersistence.DurableMetadata);
        try
        {
            var contexts = new RecordingRequestContextFactory();
            using (var crashed = new CefBrowserProfileStore(
                       null,
                       state,
                       root,
                       contexts.Create))
            {
                using var lease = crashed.AcquireLocal(binding);
                File.WriteAllText(
                    Path.Combine(Assert.Single(contexts.Created).CachePath!, "Cookies"),
                    "recover-me");
            }

            if (previousNestedLayout)
            {
                var entry = Assert.Single(Directory.GetDirectories(Path.Combine(root, "contexts")));
                Directory.Move(contexts.Created[0].CachePath!, Path.Combine(entry, "cache"));
            }

            using var recovery = new CefBrowserProfileStore(
                null,
                state,
                root,
                new RecordingRequestContextFactory().Create);
            Assert.True(recovery.RecoverOrphanedRuntimeState());
            Assert.True(state.Inspect(binding.Selection).Exists);
            Assert.False(Directory.Exists(Path.Combine(root, "contexts")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupDiscardsAPartialRestoreWithoutReplacingLastGoodState()
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore();
        var key = new BrowserProfileStateKey(
            Selection("profile.partial"),
            "local");
        var entry = Path.Combine(root, "contexts", Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(entry);
            BrowserProfileRuntimeManifest.Write(entry, key);
            var cache = Path.Combine(root, "profile-" + Path.GetFileName(entry));
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "Cookies"), "partial");

            using var recovery = new CefBrowserProfileStore(
                null,
                state,
                root,
                new RecordingRequestContextFactory().Create);
            Assert.True(recovery.RecoverOrphanedRuntimeState());
            Assert.Equal(0, state.SealCount);
            Assert.False(Directory.Exists(entry));
            Assert.False(Directory.Exists(cache));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupDiscardsAPartialEngineRestoreWithoutReplacingLastGoodState()
    {
        var parent = TemporaryRoot();
        var root = Path.Combine(parent, "runtime");
        var seed = Path.Combine(parent, "seed");
        var state = new RecordingStateStore();
        var engineKey = new BrowserProfileStateKey(
            new BrowserProfileSelection(
                new BrowserProfileId("builtin.browser.internal-runtime-state"),
                BrowserProfileKey.Global),
            "engine");
        try
        {
            Directory.CreateDirectory(seed);
            File.WriteAllText(Path.Combine(seed, "Local State"), "last-good");
            _ = state.Seal(engineKey, seed);

            Directory.CreateDirectory(root + ".restore");
            File.WriteAllText(Path.Combine(root + ".restore", "Local State"), "partial");

            using var recovery = new CefBrowserProfileStore(
                null,
                state,
                root,
                new RecordingRequestContextFactory().Create);
            Assert.True(recovery.RecoverOrphanedRuntimeState());
            Assert.Equal(1, state.SealCount);
            Assert.Equal(
                "last-good",
                File.ReadAllText(Path.Combine(root, "Local State")));
            Assert.False(Directory.Exists(root + ".restore"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void DurableSelectionRunsEphemerallyWhenEncryptionIsDisabled()
    {
        var root = TemporaryRoot();
        var state = new RecordingStateStore
        {
            RetentionEnabled = false,
            Available = false,
        };
        var contexts = new RecordingRequestContextFactory();
        try
        {
            using var store = new CefBrowserProfileStore(
                null,
                state,
                root,
                contexts.Create);
            using (store.AcquireLocal(Binding(
                       "profile.opted-out",
                       revision: 1,
                       BrowserProfilePersistence.DurableMetadata)))
            {
                Assert.Null(Assert.Single(contexts.Created).CachePath);
            }

            Assert.Equal(1, Assert.Single(contexts.Created).DisposeCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BrowserProfileSelection Selection(string id) => new(
        new BrowserProfileId(id),
        BrowserProfileKey.ForNamed(id));

    private static BrowserProfileBinding Binding(
        string id,
        long revision,
        BrowserProfilePersistence persistence =
            BrowserProfilePersistence.PrivateSession)
    {
        var selection = Selection(id);
        return new BrowserProfileBinding(
            selection,
            new BrowserProfileDefinition(
                selection.ProfileId,
                BrowserProfileDefinition.CurrentSchemaVersion,
                id,
                persistence,
                persistence == BrowserProfilePersistence.PrivateSession
                    ? BrowserProfilePrivacyPolicy.PrivateSession
                    : BrowserProfilePrivacyPolicy.Strict),
            revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_engine_snapshot_preserves_source_and_cleans_owned_destination(bool cancel)
    {
        var root = TemporaryRoot();
        string? snapshot = null;
        var state = new RecordingStateStore();
        try
        {
            File.WriteAllText(Path.Combine(root, "first_party_sets.db"), "retain");
            using var store = new CefBrowserProfileStore(null, state, root, new RecordingRequestContextFactory().Create);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SealRuntimeStateAfterEngineShutdownAsync());
            store.ReleaseContextsForEngineShutdown();
            Task Copy(string source, string destination, CancellationToken token)
            {
                Assert.Equal(root, source);
                snapshot = destination;
                File.WriteAllText(Path.Combine(destination, "partial"), "partial");
                return cancel
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.FromException(new IOException("synthetic copy failure"));
            }

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SealRuntimeStateAfterEngineShutdownAsync(Copy));
            }
            else
            {
                Assert.False(await store.SealRuntimeStateAfterEngineShutdownAsync(Copy));
            }
            Assert.NotNull(snapshot);
            Assert.False(Directory.Exists(snapshot));
            Assert.Equal("retain", File.ReadAllText(Path.Combine(root, "first_party_sets.db")));
            Assert.Equal(0, state.SealCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Engine_archive_reads_complete_snapshot_after_context_archives_finish()
    {
        var root = TemporaryRoot();
        string? snapshot = null;
        var state = new RecordingStateStore();
        try
        {
            using var store = new CefBrowserProfileStore(null, state, root, new RecordingRequestContextFactory().Create);
            using (store.AcquireLocal(Binding("snapshot", 1, BrowserProfilePersistence.DurableMetadata))) { }
            File.WriteAllText(Path.Combine(root, "first_party_sets.db"), "database");
            File.WriteAllText(Path.Combine(root, "first_party_sets.db-journal"), "journal");
            store.ReleaseContextsForEngineShutdown();
            Assert.True(await store.SealRuntimeStateAfterEngineShutdownAsync((source, destination, _) =>
            {
                Assert.Equal(1, state.SealCount);
                Assert.Empty(Directory.EnumerateDirectories(source));
                snapshot = destination;
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                }
                return Task.CompletedTask;
            }));
            Assert.Equal(2, state.SealCount);
            Assert.False(Directory.Exists(snapshot));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("during-copy")]
    [InlineData("after-copy")]
    [InlineData("after-seal")]
    public async Task Startup_discards_exact_crash_snapshot_without_restoring_it(string crashPoint)
    {
        var root = TemporaryRoot();
        var snapshot = root + ".shutdown-snapshot";
        var unrelated = root + ".shutdown-snapshot-unrelated";
        var state = new RecordingStateStore();
        try
        {
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "keep"), "unrelated");
            File.WriteAllText(Path.Combine(root, "Local State"), "authoritative");
            if (string.Equals(crashPoint, "after-seal", StringComparison.Ordinal))
            {
                using var first = new CefBrowserProfileStore(null, state, root, new RecordingRequestContextFactory().Create);
                first.ReleaseContextsForEngineShutdown();
                Assert.True(await first.SealRuntimeStateAfterEngineShutdownAsync());
            }
            Directory.CreateDirectory(snapshot);
            File.WriteAllText(Path.Combine(snapshot, "Local State"), "never-authoritative");
            if (!string.Equals(crashPoint, "during-copy", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(snapshot, "first_party_sets.db-journal"), "untrusted-copy");
            }
            using var recovered = new CefBrowserProfileStore(null, state, root, new RecordingRequestContextFactory().Create);
            Assert.True(recovered.RecoverOrphanedRuntimeState());
            Assert.False(Directory.Exists(snapshot));
            Assert.Equal("unrelated", File.ReadAllText(Path.Combine(unrelated, "keep")));
            Assert.Equal("authoritative", File.ReadAllText(Path.Combine(root, "Local State")));
            Assert.False(File.Exists(Path.Combine(root, "first_party_sets.db-journal")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(snapshot))
            {
                Directory.Delete(snapshot, recursive: true);
            }
            Directory.Delete(unrelated, recursive: true);
        }
    }

    [Fact]
    public void Startup_rejects_linked_crash_snapshot_without_touching_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = TemporaryRoot();
        var target = TemporaryRoot();
        var snapshot = root + ".shutdown-snapshot";
        try
        {
            File.WriteAllText(Path.Combine(target, "keep"), "unrelated");
            Directory.CreateSymbolicLink(snapshot, target);
            using var store = new CefBrowserProfileStore(null, new RecordingStateStore(), root, new RecordingRequestContextFactory().Create);
            Assert.Throws<IOException>(() => store.RecoverOrphanedRuntimeState());
            Assert.Equal("unrelated", File.ReadAllText(Path.Combine(target, "keep")));
            Assert.NotNull(new DirectoryInfo(snapshot).LinkTarget);
        }
        finally
        {
            Directory.Delete(snapshot);
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ghostshell-browser-profile-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingRequestContextFactory
    {
        public List<RecordingRequestContext> Created { get; } = [];

        public ICefBrowserRequestContext Create(string? cachePath)
        {
            var context = new RecordingRequestContext(cachePath);
            Created.Add(context);
            return context;
        }
    }

    private sealed class RecordingRequestContext(string? cachePath) :
        ICefBrowserRequestContext
    {
        public string? CachePath { get; } = cachePath;

        public Dictionary<string, string> Preferences { get; } = [];

        public Action? CookiesDeleted { get; set; }

        public Task<int>? DeleteCookiesCompletion { get; set; }

        public Task? FlushCookieStoreCompletion { get; set; }

        public int DeleteCookiesCount { get; private set; }

        public int ClearHttpAuthCredentialsCount { get; private set; }

        public int CloseAllConnectionsCount { get; private set; }

        public int FlushCookieStoreCount { get; private set; }

        public int DisposeCount { get; private set; }

        public IWorkspaceProxyAuthenticationResolver? ProxyAuthenticationResolver
        {
            get;
            private set;
        }

        public IBrowserProfileAuthenticationResolver? AuthenticationResolver { get; private set; }

        public Task<bool>? PreferenceCompletion { get; init; }

        public async Task<bool> SetPreferenceAsync(string name, string value)
        {
            var accepted = PreferenceCompletion is null || await PreferenceCompletion;
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            return accepted && SetPreference(name, value);
        }

        public bool SetPreference(string name, string value)
        {
            Preferences.Add(name, value);
            return true;
        }

        public Task<int> DeleteCookiesAsync()
        {
            DeleteCookiesCount++;
            CookiesDeleted?.Invoke();
            return DeleteCookiesCompletion ?? Task.FromResult(1);
        }

        public Task FlushCookieStoreAsync()
        {
            FlushCookieStoreCount++;
            return FlushCookieStoreCompletion ?? Task.CompletedTask;
        }

        public Task ClearHttpAuthCredentialsAsync()
        {
            ClearHttpAuthCredentialsCount++;
            return Task.CompletedTask;
        }

        public Task CloseAllConnectionsAsync()
        {
            CloseAllConnectionsCount++;
            return Task.CompletedTask;
        }

        public CefBrowserView CreateView(
            BrowserProfileBinding profile,
            IBrowserProfileAuthenticationResolver? authenticationResolver,
            IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver)
        {
            ProxyAuthenticationResolver = proxyAuthenticationResolver;
            AuthenticationResolver = authenticationResolver;
            throw new NotSupportedException(
                "The profile-store tests do not create native browser views.");
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class AuthenticatedConnector(
        WorkspaceNetworkProxyCredentials credentials,
        int port = 65367,
        string? profileRouteIdentity = null) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Direct;

        public Uri LocalProxyEndpoint { get; } = new(
            $"socks5://127.0.0.1:{port}",
            UriKind.Absolute);

        public WorkspaceNetworkProxyCredentials? LocalProxyCredentials { get; } = credentials;

        public Uri BrowserProxyEndpoint { get; } = new(
            $"http://127.0.0.1:{port}",
            UriKind.Absolute);

        public string? BrowserProfileRouteIdentity => profileRouteIdentity;

        public string? BrowserAuthenticationRouteIdentity { get; private set; } = "local";

        public event Func<CancellationToken, Task>? BrowserAuthenticationRouteChanging;

        public async Task ChangeAuthorityAsync(string identity, CancellationToken cancellationToken)
        {
            BrowserAuthenticationRouteIdentity = null;
            if (BrowserAuthenticationRouteChanging is { } callback)
            {
                await callback(cancellationToken);
            }
            BrowserAuthenticationRouteIdentity = identity;
        }

        public ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());
    }

    private sealed class RecordingStateStore : IBrowserProfileStateStore
    {
        private readonly Dictionary<BrowserProfileStateKey, Dictionary<string, byte[]>>
            _states = [];

        public bool RetentionEnabled { get; init; } = true;

        public bool Available { get; init; } = true;

        public int SealCount { get; private set; }

        public bool IsRetentionEnabled => RetentionEnabled;

        public bool IsAvailable => Available;

        public string? UnavailableReason => null;

        public BrowserProfileStoredState Inspect(BrowserProfileSelection selection)
        {
            var matching = _states
                .Where(item => item.Key.Selection == selection)
                .ToArray();
            return new BrowserProfileStoredState(
                matching.Length > 0,
                matching.SelectMany(item => item.Value.Values).Sum(bytes => bytes.LongLength));
        }

        public IReadOnlyList<BrowserProfileStateKey> ListKeys(
            BrowserProfileSelection selection) =>
            [.. _states.Keys.Where(key => key.Selection == selection)];

        public void Restore(BrowserProfileStateKey key, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            if (!_states.TryGetValue(key, out var files))
            {
                return;
            }

            foreach (var file in files)
            {
                var path = Path.Combine(destinationDirectory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Value);
            }
        }

        public long Seal(BrowserProfileStateKey key, string sourceDirectory)
        {
            SealCount++;
            var files = Directory.EnumerateFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(sourceDirectory, path),
                    File.ReadAllBytes,
                    StringComparer.Ordinal);
            _states[key] = files;
            return files.Values.Sum(bytes => bytes.LongLength);
        }

        public long Delete(BrowserProfileSelection selection)
        {
            var keys = _states.Keys
                .Where(key => key.Selection == selection)
                .ToArray();
            var bytes = keys.Sum(key =>
                _states[key].Values.Sum(value => value.LongLength));
            foreach (var key in keys)
            {
                _states.Remove(key);
            }

            return bytes;
        }
    }
}
