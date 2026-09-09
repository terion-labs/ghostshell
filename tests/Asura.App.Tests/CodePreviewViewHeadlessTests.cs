using Asura.App.Views.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class CodePreviewViewHeadlessTests
{
    [Fact]
    public Task DeferredGrammarReportsWhenItsPresentationIsReady() =>
        RunHeadlessAsync(async () =>
        {
            var preview = new CodePreviewView
            {
                FileName = "controller.py",
                Text = "def step(target, actual):\n    return target - actual",
            };
            var window = new Window
            {
                Width = 800,
                Height = 500,
                Content = preview,
            };

            try
            {
                Assert.False(preview.IsPresentationReady);
                window.Show();
                Assert.False(preview.IsPresentationReady);

                for (var attempt = 0;
                     attempt < 100 && !preview.IsPresentationReady;
                     attempt++)
                {
                    await Task.Delay(10);
                }

                Assert.True(preview.IsPresentationReady);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task FullDocumentPreviewCannotScrollBelowItsLastLine() =>
        RunHeadlessAsync(async () =>
        {
            var preview = new CodePreviewView
            {
                FileName = "inspection.json",
                Text = string.Join('\n', Enumerable.Range(1, 77).Select(index => $"line {index}")),
                WordWrap = false,
            };
            var window = new Window
            {
                Width = 800,
                Height = 500,
                Content = preview,
            };

            try
            {
                window.Show();
                await Task.Delay(50);
                window.UpdateLayout();

                var editor = Assert.Single(
                    preview.GetVisualDescendants().OfType<TextEditor>());
                Assert.False(editor.Options.AllowScrollBelowDocument);

                var scrollViewer = Assert.Single(
                    editor.GetVisualDescendants().OfType<ScrollViewer>());
                var maximumOffset = Math.Max(
                    0,
                    scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                var documentEndOffset = Math.Max(
                    0,
                    editor.TextArea.TextView.DocumentHeight - scrollViewer.Viewport.Height);

                Assert.InRange(maximumOffset, documentEndOffset - 1, documentEndOffset + 2);
            }
            finally
            {
                window.Close();
            }
        });

    private static async Task RunHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
