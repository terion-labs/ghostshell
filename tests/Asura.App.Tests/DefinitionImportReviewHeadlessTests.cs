using Asura.App.Views;
using Asura.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class DefinitionImportReviewHeadlessTests
{
    [Fact]
    public async Task Execution_review_starts_unchecked_and_does_not_carry_approval_to_another_dialog()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        await session.Dispatch(async () =>
        {
            var plan = new DefinitionBundleImportPlan("/tmp/review.json", new(new(1, DateTimeOffset.UtcNow, []),
                DefinitionImportMode.ReplaceExisting, [])
            {
                ExecutionReview = [new("Host mount", "WRITABLE /tmp/host → /work"), new("Commands", "echo reviewed")],
            });
            var dialog = new DefinitionImportPreflightDialog(plan);
            try
            {
                dialog.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var checkbox = dialog.FindControl<CheckBox>("ExecutionAcknowledgement")!;
                var apply = dialog.FindControl<Button>("ImportButton")!;
                Assert.False(checkbox.IsChecked);
                Assert.False(apply.IsEnabled);
                checkbox.IsChecked = true;
                Assert.True(dialog.CanApply);
                Assert.True(apply.IsEnabled);
                checkbox.IsChecked = false;
                Assert.False(apply.IsEnabled);
                Assert.True(apply.Bounds.Height > 0);
                var buttonBottom = Assert.NotNull(apply.TranslatePoint(new(0, apply.Bounds.Height), dialog));
                Assert.InRange(buttonBottom.Y, 0, dialog.Bounds.Height);
                var another = new DefinitionImportPreflightDialog(plan);
                Assert.False(another.CanApply);
                another.Close();
            }
            finally
            {
                dialog.Close();
            }
        }, timeout.Token);
    }
}
