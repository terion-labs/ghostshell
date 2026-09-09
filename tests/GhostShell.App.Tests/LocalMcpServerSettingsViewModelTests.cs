using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.SettingsPages;
using GhostShell.Application;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class LocalMcpServerSettingsViewModelTests
{
    [Fact]
    public async Task SettingsToggleChangesServerOnceAndReturnsToSavedStateAfterInvalidInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        await session.Dispatch(async () =>
        {
            var control = new Control();
            using var model = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
            var view = new LocalMcpServerSettingsView { DataContext = model };
            var window = new Window { Content = view, Width = 760, Height = 720 };
            try
            {
                window.Show();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var toggle = view.FindControl<ToggleSwitch>("EnableServerToggle")!;
                Assert.Equal(0, control.ConfigureCount);
                Assert.False(toggle.IsChecked);
                toggle.IsChecked = true;
                Assert.True(control.State.Enabled);
                Assert.Equal(1, control.ConfigureCount);
                model.Port = "invalid";
                toggle.IsChecked = false;
                Assert.False(control.State.Enabled);
                Assert.Equal(2, control.ConfigureCount);
                model.Port = "invalid";
                toggle.IsChecked = true;
                Assert.False(toggle.IsChecked);
                Assert.False(control.State.Enabled);
                Assert.Equal(2, control.ConfigureCount);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, timeout.Token);
    }

    [Fact]
    public async Task InvalidPortCannotEnableButCannotPreventDisabling()
    {
        var control = new Control();
        using var model = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
        model.Port = "invalid";
        await model.SetEnabledAsync(true);
        Assert.Equal(0, control.ConfigureCount);
        Assert.Contains("1024", model.Message, StringComparison.Ordinal);
        model.Port = "18766";
        await model.SetEnabledAsync(true);
        Assert.True(model.Enabled);
        Assert.Equal(18766, control.State.Port);
        model.Port = "invalid";
        await model.SetEnabledAsync(false);
        Assert.False(model.Enabled);
        Assert.Equal(18766, control.State.Port);
        model.Port = "1";
        await model.ApplyPortAsync();
        Assert.Equal(2, control.ConfigureCount);
        Assert.Contains("1024", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsObserveSavedChangesAndDisplayStartupFailures()
    {
        var control = new Control();
        using var first = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
        using var second = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
        first.Port = "18767";
        await first.SetEnabledAsync(true);
        Assert.True(second.Enabled);
        Assert.Equal("http://127.0.0.1:18767/mcp", second.Endpoint);
        control.ReportFailure("Port unavailable");
        Assert.Equal("Port unavailable", second.Status);
        Assert.True(second.CanEdit);
        await second.ApplyPortAsync();
        Assert.Contains("Running", first.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenGoesOnlyToExplicitClipboardActionAndRotationUpdatesStatus()
    {
        var control = new Control();
        using var model = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
        string? copied = null;
        await model.CopyTokenAsync(text =>
        {
            copied = text;
            return Task.CompletedTask;
        });
        Assert.Equal("test-token", copied);
        Assert.DoesNotContain("test-token", model.Message, StringComparison.Ordinal);
        await model.RotateTokenAsync();
        Assert.Equal(1, control.RotationCount);
        Assert.Contains("Token replaced", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClipboardFailureDoesNotClaimTokenWasCopied()
    {
        var control = new Control();
        using var model = new LocalMcpServerSettingsViewModel(control, new Dispatcher());
        await model.CopyTokenAsync(_ => throw new InvalidOperationException("clipboard missing"));
        Assert.Contains("could not", model.Message, StringComparison.Ordinal);
        Assert.True(model.CanEdit);
    }

    private sealed class Control : ILocalMcpServerControl
    {
        public LocalMcpServerState State { get; private set; } = new();

        public event EventHandler? Changed;

        public int ConfigureCount { get; private set; }

        public int RotationCount { get; private set; }

        public Task ConfigureAsync(bool enabled, int port)
        {
            ConfigureCount++;
            State = new(enabled, port, enabled);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<string> ReadTokenAsync() => Task.FromResult("test-token");

        public Task RotateTokenAsync()
        {
            RotationCount++;
            return Task.CompletedTask;
        }

        public void ReportFailure(string error)
        {
            State = State with { IsRunning = false, Error = error };
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Dispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
