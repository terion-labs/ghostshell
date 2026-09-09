using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class BrowserProfileRuntimeTests
{
    [Fact]
    public void PinCapturesTheExactCatalogRevisionAndDefinition()
    {
        var profile = Profile("browser.work", isEnabled: true);
        var proxy = Catalog(Store(profile, 12));
        var runtime = new CatalogBrowserProfileRuntime(proxy.Catalog);

        var result = runtime.Pin(profile.Id, BrowserProfileKey.ForNamed(profile.Id.Value));
        proxy.Snapshot = DefinitionCatalogSnapshot.Empty;

        Assert.True(result.IsSuccess);
        Assert.Same(profile, result.Binding!.Definition);
        Assert.Equal(12, result.Binding.Revision);
        Assert.Equal(profile.Id, result.Binding.Selection.ProfileId);
    }

    [Fact]
    public void MissingAndDisabledProfilesFailWithoutFallback()
    {
        var disabled = Profile("browser.disabled", isEnabled: false);
        var proxy = Catalog(Store(disabled, 5));
        var runtime = new CatalogBrowserProfileRuntime(proxy.Catalog);

        var missing = runtime.Pin(
            new BrowserProfileId("browser.missing"),
            BrowserProfileKey.ForNamed("browser.missing"));
        var blocked = runtime.Pin(
            disabled.Id,
            BrowserProfileKey.ForNamed(disabled.Id.Value));

        Assert.False(missing.IsSuccess);
        Assert.Equal(BrowserProfilePinFailure.Missing, missing.Failure);
        Assert.Null(missing.Binding);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(BrowserProfilePinFailure.Disabled, blocked.Failure);
        Assert.Null(blocked.Binding);
    }

    [Fact]
    public void PrivatePanelsGetDistinctSessionPartitionsWhileDurablePanelsShareTheName()
    {
        var durable = Profile("browser.durable", isEnabled: true);
        var privateProfile = Profile(
            "browser.private",
            isEnabled: true,
            BrowserProfilePersistence.PrivateSession);
        var proxy = Catalog(Store(durable, 2), Store(privateProfile, 3));
        var runtime = new CatalogBrowserProfileRuntime(proxy.Catalog);

        var durableOne = runtime.PinNewPanel(
            durable.Id,
            BrowserProfileKey.Global,
            new PanelInstanceId("panel.one"));
        var durableTwo = runtime.PinNewPanel(
            durable.Id,
            BrowserProfileKey.ForWorkspace("workspace.two"),
            new PanelInstanceId("panel.two"));
        var privateOne = runtime.PinNewPanel(
            privateProfile.Id,
            BrowserProfileKey.Global,
            new PanelInstanceId("panel.one"));
        var privateTwo = runtime.PinNewPanel(
            privateProfile.Id,
            BrowserProfileKey.Global,
            new PanelInstanceId("panel.two"));

        Assert.Equal(durableOne.Binding!.Selection, durableTwo.Binding!.Selection);
        Assert.NotEqual(privateOne.Binding!.Selection, privateTwo.Binding!.Selection);
        Assert.Equal(BrowserProfileKind.Session, privateOne.Binding.Selection.Partition.Kind);
    }

    private static BrowserProfileDefinition Profile(
        string id,
        bool isEnabled,
        BrowserProfilePersistence persistence = BrowserProfilePersistence.DurableMetadata) =>
        new(
            new BrowserProfileId(id),
            BrowserProfileDefinition.CurrentSchemaVersion,
            id,
            persistence,
            BrowserProfilePrivacyPolicy.Strict,
            isEnabled: isEnabled);

    private static StoredDefinition<T> Store<T>(T definition, long revision)
        where T : IDurableDefinition =>
        new(definition, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogProxy Catalog(
        params StoredDefinition<BrowserProfileDefinition>[] profiles)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, CatalogDispatchProxy>();
        var dispatch = (CatalogDispatchProxy)(object)catalog;
        dispatch.CurrentSnapshot = DefinitionCatalogSnapshot.Empty with
        {
            BrowserProfiles = profiles,
        };
        return new CatalogProxy(catalog, dispatch);
    }

    private sealed record CatalogProxy(
        IDefinitionCatalog Catalog,
        CatalogDispatchProxy Dispatch)
    {
        public DefinitionCatalogSnapshot Snapshot
        {
            set => Dispatch.CurrentSnapshot = value;
        }
    }

    public class CatalogDispatchProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }
    }
}
