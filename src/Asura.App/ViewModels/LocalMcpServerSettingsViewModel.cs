using System.Globalization;
using Asura.Application;

namespace Asura.App.ViewModels;

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

    /// <summary>The state as one word, for the chip beside the switch.</summary>
    public string StatusLabel => _control is null
        ? "Unavailable"
        : _isBusy ? "Applying"
        : _state.Error is not null ? "Error"
        : _state.IsRunning ? "Running" : "Off";

    /// <summary>The sentence under the switch: what the state means, or what went wrong.</summary>
    public string StatusDetail => _control is null
        ? "The MCP server is unavailable in this host."
        : _isBusy ? "Applying the settings…"
        : _state.Error ?? (_state.IsRunning
            ? "Accepting connections from agents on this computer."
            : "Not listening. Turn it on to let an agent on this computer call Asura's tools.");

    public bool IsRunning => _control is not null && !_isBusy && _state.Error is null && _state.IsRunning;

    public bool IsOff => _control is null || (!_isBusy && _state.Error is null && !_state.IsRunning);

    public bool HasError => _control is not null && !_isBusy && _state.Error is not null;

    public bool IsBusy => _isBusy;

    /// <summary>Apply a new port, or retry the current one after it failed to bind.</summary>
    public string ApplyLabel => _state.Error is not null ? "Retry" : "Apply";

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
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
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(Endpoint));
    }
}
