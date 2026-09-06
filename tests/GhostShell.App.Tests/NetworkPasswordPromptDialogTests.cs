using Avalonia.Controls;
using Avalonia.Headless;
using GhostShell.App.Views;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class NetworkPasswordPromptDialogTests
{
    [Fact]
    public async Task Replacement_prompt_masks_password_and_does_not_offer_premature_persistence()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        await session.Dispatch(() =>
        {
            const string description = "The stored password was rejected. Enter a replacement to retry.";
            var dialog = new DatabasePasswordPromptDialog("Office VPN", description: description);
            Assert.Equal(description, dialog.FindControl<TextBlock>("PromptDescription")!.Text);
            Assert.Equal('•', dialog.FindControl<TextBox>("PasswordInput")!.PasswordChar);
            Assert.False(dialog.FindControl<CheckBox>("SavePasswordCheckBox")!.IsVisible);
            return true;
        }, timeout.Token);
    }
}
