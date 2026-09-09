using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.Application;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class WorkspaceEditorStateHeadlessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Rebuilding_policy_and_switching_providers_keeps_a_selected_model(bool multipleModels) => RunAsync(async () =>
    {
        using var editor = CreateEditor(multipleModels);
        var view = new WorkspaceEditorView(editor);
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        window.UpdateLayout();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(editor.IsDirty);
            editor.RunAgentInIsolation = true;
            editor.AgentPolicy.IsEnabled = true;
            editor.AgentPolicy.SelectedProvider = editor.AgentPolicy.ProviderOptions[^1];
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.NotNull(editor.AgentPolicy.SelectedModel);
            Assert.Equal(multipleModels ? 2 : 1, editor.AgentPolicy.ModelOptions.Count);
            var expectedModel = multipleModels ? "second-fast" : "second-model";
            Assert.Contains(editor.AgentPolicy.AgentTaskModelOptions, option => string.Equals(option.Selection?.Model, expectedModel, StringComparison.Ordinal));
            Assert.Contains(editor.AgentPolicy.TitleModelOptions, option => string.Equals(option.Selection?.Model, expectedModel, StringComparison.Ordinal));
            editor.AgentPolicy.SelectedModel = editor.AgentPolicy.ModelOptions[^1];
            Assert.True(editor.CanSave, editor.ValidationSummary);
            editor.Reset();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(editor.IsDirty);
            Assert.Equal(WorkspaceEditorCancelDisposition.Close, editor.RequestCancel());
        }
        finally { window.Close(); }
    });

    private static WorkspaceEditorViewModel CreateEditor(bool multipleModels) => new(
        new WorkspaceDefinition(new WorkspaceId("workspace"), WorkspaceDefinition.CurrentSchemaVersion,
            "Workspace", null, null, [], isIsolated: true),
        1, [], [], [], [],
        [
            new(new AiProviderProfileId("first"), "First", AiProviderKind.OpenAi,
                new Uri("https://first.example/v1/"), "first-model", 0, true, true),
            new(new AiProviderProfileId("second"), "Second", AiProviderKind.OpenAi,
                new Uri("https://second.example/v1/"), "second-model", 1, true, true,
                Models: multipleModels
                    ? [new("second-model", "Second model"), new("second-fast", "Second fast")]
                    : [new("second-model", "Second model")]),
        ]);

    private static async Task RunAsync(Func<Task> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(async () => { await action(); return true; }, timeout.Token));
        }
        finally { await session.DisposeAsync(); }
    }
}
