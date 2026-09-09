using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;

namespace Asura.Mcp.Server;

/// <summary>Serializes listener changes, persists preferences, and reports failures to Settings.</summary>
public sealed class LocalMcpServerControl(
    WorkspaceMcpServer server,
    string profileDirectory) : ILocalMcpServerControl, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath = Path.Combine(profileDirectory, "mcp-server.json");
    private readonly LocalMcpServerToken _token = new(profileDirectory);
    private bool _disposed;

    public LocalMcpServerState State { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
                var settings = JsonSerializer.Deserialize(json, LocalMcpSettingsJson.Default.LocalMcpPreferences)
                    ?? throw new JsonException("Missing MCP preferences.");
                ValidatePort(settings.Port);
                State = new(settings.Enabled, settings.Port);
            }

            await StartIfEnabledAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            State = State with { Error = "MCP server could not start. Check its settings and try again." };
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ConfigureAsync(bool enabled, int port)
    {
        ValidatePort(port);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(profileDirectory);
            var json = JsonSerializer.Serialize(new LocalMcpPreferences(enabled, port),
                LocalMcpSettingsJson.Default.LocalMcpPreferences);
            var temporaryPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            // The saved choice survives a failed bind and can be retried from Settings.
            await server.StopAsync().ConfigureAwait(false);
            State = new(enabled, port);
            await StartIfEnabledAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            State = State with { Error = "MCP settings could not be applied. Check the port and try again." };
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<string> ReadTokenAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _token.ReadAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RotateTokenAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Stop authenticated sessions before replacing the credential.
            await server.StopAsync().ConfigureAwait(false);
            State = State with { IsRunning = false, Error = null };
            await _token.RotateAsync().ConfigureAwait(false);
            await StartIfEnabledAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            State = State with { Error = "The MCP token could not be replaced. Try again." };
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await server.StopAsync().ConfigureAwait(false);
            State = State with { IsRunning = false };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartIfEnabledAsync()
    {
        if (!State.Enabled)
        {
            return;
        }

        try
        {
            var token = await _token.ReadAsync().ConfigureAwait(false);
            await server.StartAsync(State.Port, token, CancellationToken.None).ConfigureAwait(false);
            State = State with { IsRunning = true, Error = null };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            State = State with
            {
                IsRunning = false,
                Error = "MCP server could not start. The port may be in use or its token may be unreadable. Choose another port or replace the token, then retry.",
            };
        }
    }

    private static void ValidatePort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
    }
}

internal sealed record LocalMcpPreferences(bool Enabled, int Port);

[JsonSerializable(typeof(LocalMcpPreferences))]
internal sealed partial class LocalMcpSettingsJson : JsonSerializerContext;
