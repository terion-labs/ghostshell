using System.Globalization;
using GhostShell.Application;

namespace GhostShell.App.ViewModels;

public sealed class LocalMcpServerSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ILocalMcpServerControl? _control;
    private readonly IUiThreadDispatcher _dispatcher;
    private LocalMcpServerState _state;
    private string _port;
    private string? _message;
    private bool _isBusy;
    private bool _disposed;

    public LocalMcpServerSettingsViewModel(ILocalMcpServerControl? control, IUiThreadDispatcher dispatcher)
    {
        _control = control;
        _dispatcher = dispatcher;
        _state = control?.State ?? new();
        _port = _state.Port.ToString(CultureInfo.InvariantCulture);
        control?.Changed += OnChanged;
    }

    public bool Enabled => _state.Enabled;

    public bool CanEdit => _control is not null && !_isBusy;

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Endpoint => $"http://127.0.0.1:{_state.Port.ToString(CultureInfo.InvariantCulture)}/mcp";

    public string Status => _control is null
        ? "The MCP server is unavailable in this host."
        : _isBusy ? "Applying MCP settings…"
        : _state.Error ?? (_state.IsRunning ? "Running · accepts connections on this computer" : "Off");

    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public Task SetEnabledAsync(bool enabled) => ConfigureAsync(enabled, disabling: !enabled);

    public Task ApplyPortAsync() => ConfigureAsync(Enabled);

    public async Task CopyEndpointAsync(Func<string, Task> copy)
    {
        await RunAsync(async () =>
        {
            await copy(Endpoint);
            Message = "Server URL copied.";
        });
    }

    public async Task CopyTokenAsync(Func<string, Task> copy)
    {
        await RunAsync(async () =>
        {
            var token = await _control!.ReadTokenAsync();
            await copy(token);
            Message = "Token copied. Paste it into your agent's MCP authentication settings.";
        });
    }

    public async Task RotateTokenAsync()
    {
        await RunAsync(async () =>
        {
            await _control!.RotateTokenAsync();
            ApplyState();
            if (_state.Error is null)
            {
                Message = "Token replaced. Copy the new token to reconnect your agents.";
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _control?.Changed -= OnChanged;
    }

    private async Task ConfigureAsync(bool enabled, bool disabling = false)
    {
        // Switching off always works, even while the port field contains an invalid draft.
        var port = _state.Port;
        if (!disabling && (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1024 or > 65535))
        {
            Message = "Enter a port from 1024 to 65535.";
            OnPropertyChanged(nameof(Enabled));
            return;
        }

        if (!enabled && int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var draftPort)
            && draftPort is >= 1024 and <= 65535)
        {
            port = draftPort;
        }

        await RunAsync(async () =>
        {
            await _control!.ConfigureAsync(enabled, port);
            ApplyState();
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (!CanEdit || _disposed)
        {
            return;
        }

        _isBusy = true;
        Message = null;
        Publish();
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            Message = "The operation could not be completed. Try again.";
        }
        finally
        {
            _isBusy = false;
            Publish();
        }
    }

    private async void OnChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await _dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ApplyState();
            }
        }, CancellationToken.None);
    }

    private void ApplyState()
    {
        _state = _control!.State;
        Port = _state.Port.ToString(CultureInfo.InvariantCulture);
        Publish();
    }

    private void Publish()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Endpoint));
    }
}
