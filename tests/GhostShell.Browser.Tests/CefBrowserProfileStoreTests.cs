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

    [Fact]
    public void AuthenticatedWorkspaceRoutePassesItsCredentialsToEveryCreatedView()
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
                "basic")));
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
    public void DurableProfileSealsAndRestoresTheCompleteRuntimeTree()
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
                Assert.True(first.SealRuntimeStateAfterEngineShutdown());
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
                Assert.True(first.SealRuntimeStateAfterEngineShutdown());
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
    public void HostProfileRestoresAfterRuntimeInstanceChangesWithoutSharingLiveRoutes(bool isolated)
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
                Assert.True(first.SealRuntimeStateAfterEngineShutdown());
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

    [Fact]
    public void StartupRecoversAnOrphanedDurableRuntimeTree()
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
            Directory.CreateDirectory(Path.Combine(entry, "cache"));
            File.WriteAllText(Path.Combine(entry, "cache", "Cookies"), "partial");

            using var recovery = new CefBrowserProfileStore(
                null,
                state,
                root,
                new RecordingRequestContextFactory().Create);
            Assert.True(recovery.RecoverOrphanedRuntimeState());
            Assert.Equal(0, state.SealCount);
            Assert.False(Directory.Exists(entry));
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
