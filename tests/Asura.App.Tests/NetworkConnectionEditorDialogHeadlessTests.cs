using System.Reflection;
using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.App.Views.Components;
using Asura.Application;
using Asura.Core;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class NetworkConnectionEditorDialogHeadlessTests
{
    [Fact]
    public async Task A_credential_slot_opens_its_own_draft_and_takes_the_stored_result()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        await session.Dispatch(async () =>
        {
            var catalog = DispatchProxy.Create<
                IDefinitionCatalog,
                NetworkSettingsViewModelTests.RecordingCatalogProxy>();
            var vault = DispatchProxy.Create<
                ISecretVault,
                NetworkSettingsViewModelTests.RecordingVaultProxy>();
            using var settings = new NetworkSettingsViewModel(catalog, vault);
            settings.BeginCreateProfile();
            var editor = settings.ProfileEditor!;
            var dialog = new NetworkConnectionEditorDialog(settings);
            try
            {
                dialog.Show();
                await Idle();

                // A new connection is a proxy, so the one slot in view is its
                // password; the other kinds' slots exist but are not shown.
                var picker = Assert.Single(
                    dialog.GetVisualDescendants().OfType<NetworkCredentialPicker>(),
                    candidate => candidate.IsEffectivelyVisible);
                Assert.Equal("Password", picker.Label);
                Assert.Equal(NetworkCredentialTarget.ProxyPassword, picker.Target);

                var add = Assert.Single(
                    picker.GetVisualDescendants().OfType<Button>(),
                    candidate => string.Equals(
                        AutomationProperties.GetName(candidate),
                        "Add a new Password",
                        StringComparison.Ordinal));
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle();

                Assert.True(editor.IsCredentialDraftOpen);
                Assert.Equal(
                    NetworkCredentialTarget.ProxyPassword,
                    editor.Credential.SelectedTarget?.Target);
                Assert.False(editor.Credential.IsDocument);
                Assert.Contains("password", editor.Credential.Title, StringComparison.Ordinal);

                editor.Username = "alice";
                editor.Credential.Label = "Office proxy password";
                editor.Credential.Value = "hunter2";
                Assert.True(await settings.StoreCredentialAsync(CancellationToken.None));
                await Idle();

                // The draft closes and the slot it was opened from now holds
                // the pending credential, with nothing left in the value box.
                Assert.False(editor.IsCredentialDraftOpen);
                Assert.Equal(string.Empty, editor.Credential.Value);
                var selected = Assert.IsType<NetworkCredentialOption>(picker.SelectedItem);
                Assert.Equal(NetworkCredentialOptionState.Pending, selected.State);
                Assert.Equal("Office proxy password", selected.Label);

                // The dialog is the draft's frame: discarding the draft closes it.
                await settings.CancelProfileEditAsync(CancellationToken.None);
                await Idle();
                Assert.False(dialog.IsVisible);
            }
            finally
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }

            return true;
        }, timeout.Token);
    }

    private static Task Idle() =>
        Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
}
