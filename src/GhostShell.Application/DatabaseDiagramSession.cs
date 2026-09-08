namespace GhostShell.Application;

/// <summary>Only bounded viewport images cross from the owned schema worker into the UI.</summary>
public interface IDatabaseDiagramSession : IAsyncDisposable
{
    Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken);

    Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken);
}

public sealed record DatabaseDiagramViewport(int Width, int Height, double Zoom, double PanX, double PanY, bool DarkTheme = true)
{
    public void Validate()
    {
        if (Width is < 1 or > 8192 || Height is < 1 or > 8192
            || (long)Width * Height > 16_777_216
            || !double.IsFinite(Zoom) || Zoom is < 0.01 or > 100
            || !double.IsFinite(PanX) || !double.IsFinite(PanY))
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "The diagram viewport is invalid.");
        }
    }
}

public enum DatabaseDiagramExport
{
    MermaidMarkdown,
    Svg,
}

public enum DatabaseDiagramPurpose
{
    Display,
    SourceExport,
}

/// <summary>The connection material is consumed only over private worker IPC, never command arguments.</summary>
public interface IDatabaseDiagramWorkerFactory
{
    Task<IDatabaseDiagramSession> OpenAsync(
        DatabaseWorkerConnection connection,
        CancellationToken cancellationToken,
        DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display);
}
