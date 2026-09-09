using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class NetworkCredentialSelectorTests
{
    [Theory]
    [InlineData(NetworkCredentialTarget.ProxyPassword, NetworkConnectionKind.Proxy, SecretKind.Password, "Password")]
    [InlineData(NetworkCredentialTarget.AnyConnectPassword, NetworkConnectionKind.AnyConnect, SecretKind.Password, "Password")]
    [InlineData(NetworkCredentialTarget.OpenVpnPassword, NetworkConnectionKind.OpenVpn, SecretKind.Password, "Password")]
    [InlineData(NetworkCredentialTarget.WireGuardConfiguration, NetworkConnectionKind.WireGuard, SecretKind.Other, "Configuration")]
    [InlineData(NetworkCredentialTarget.OpenVpnConfiguration, NetworkConnectionKind.OpenVpn, SecretKind.Other, "Configuration")]
    [InlineData(NetworkCredentialTarget.AnyConnectClientCertificate, NetworkConnectionKind.AnyConnect, SecretKind.Certificate, "ClientCertificate")]
    [InlineData(NetworkCredentialTarget.TailscaleAuthKey, NetworkConnectionKind.Tailscale, SecretKind.ApiKey, "AuthKey")]
    public async Task Bound_selector_retains_new_and_replacement_credentials(
        NetworkCredentialTarget target,
        NetworkConnectionKind connectionKind,
        SecretKind secretKind,
        string field)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        await session.Dispatch(async () =>
        {
            var editor = new NetworkConnectionProfileEditorViewModel();
            editor.SelectedKind = editor.KindOptions.Single(option => option.Kind == connectionKind);
            var selector = new ComboBox { DataContext = editor };
            selector.Bind(ItemsControl.ItemsSourceProperty, new Binding($"{field}CredentialOptions"));
            selector.Bind(ComboBox.SelectedItemProperty, new Binding($"Selected{field}Credential")
            {
                Mode = BindingMode.TwoWay,
            });
            var window = new Window { Width = 600, Height = 400, Content = selector };
            try
            {
                window.Show();
                foreach (var reference in new[] { new SecretRef("first"), new SecretRef("replacement") })
                {
                    editor.ApplyCredential(target, reference, "Stored credential", secretKind);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Contains(selector.Items.Cast<NetworkCredentialOption>(), option => option.Reference == reference);
                    var selected = field switch
                    {
                        "Password" => editor.SelectedPasswordCredential,
                        "Configuration" => editor.SelectedConfigurationCredential,
                        "ClientCertificate" => editor.SelectedClientCertificateCredential,
                        _ => editor.SelectedAuthKeyCredential,
                    };
                    Assert.Equal(reference, selected?.Reference);
                    Assert.Equal(reference, Assert.IsType<NetworkCredentialOption>(selector.SelectedItem).Reference);
                    selector.IsDropDownOpen = true;
                    window.UpdateLayout();
                    Assert.Contains(selector.Items.Cast<NetworkCredentialOption>(), option => option.Reference == reference);
                    selector.IsDropDownOpen = false;
                    editor.BeginCredentialMetadataLoad();
                    editor.MarkCredentialMetadataUnavailable();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Equal(reference, Assert.IsType<NetworkCredentialOption>(selector.SelectedItem).Reference);
                }

                var stored = new SecretMetadata(
                    new SecretRef("vault-credential"), "Vault credential", secretKind,
                    new SecretScope(SecretScopeKind.NetworkConnection, editor.Id.Value),
                    SecretVaultPersistenceKind.OsProtectedPersistent,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                editor.ApplyCredentialMetadata([stored]);
                selector.SelectedItem = selector.Items.Cast<NetworkCredentialOption>()
                    .Single(option => option.Reference == stored.Reference);
                editor.ApplyCredentialMetadata([stored with { Label = "Renamed credential" }]);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var storedSelection = Assert.IsType<NetworkCredentialOption>(selector.SelectedItem);
                Assert.Equal(stored.Reference, storedSelection.Reference);
                Assert.Equal("Renamed credential", storedSelection.Label);
                Assert.Equal(NetworkCredentialOptionState.Available, storedSelection.State);

                // Clearing is an explicit option, not the transient null emitted
                // by the control when an ItemsSource replacement resets selection.
                var empty = selector.Items.Cast<NetworkCredentialOption>().SingleOrDefault(option => option.Reference is null);
                if (empty is not null)
                {
                    selector.SelectedItem = empty;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Null(Assert.IsType<NetworkCredentialOption>(selector.SelectedItem).Reference);
                }
            }
            finally
            {
                window.Close();
            }
            return true;
        }, timeout.Token);
    }
}
