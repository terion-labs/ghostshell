using System.Runtime.InteropServices;

namespace Asura.Application;

public enum TerminalKittyPlacementLayer
{
    BelowBackground,
    BelowText,
    AboveText,
}

[StructLayout(LayoutKind.Auto)]
public readonly record struct TerminalKittySourceRectangle
{
    public TerminalKittySourceRectangle(int X, int Y, int Width, int Height)
    {
        if (X < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(X));
        }

        if (Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Y));
        }

        if (Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width));
        }

        if (Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Height));
        }

        _ = checked(X + Width);
        _ = checked(Y + Height);
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>Viewport geometry for a visible Kitty placement instance.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TerminalKittyPlacementGeometry
{
    public TerminalKittyPlacementGeometry(
        int ViewportColumn,
        int ViewportRow,
        int GridColumns,
        int GridRows,
        double PixelWidth,
        double PixelHeight)
    {
        if (GridColumns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GridColumns));
        }

        if (GridRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GridRows));
        }

        if (!double.IsFinite(PixelWidth) || PixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelWidth));
        }

        if (!double.IsFinite(PixelHeight) || PixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelHeight));
        }

        this.ViewportColumn = ViewportColumn;
        this.ViewportRow = ViewportRow;
        this.GridColumns = GridColumns;
        this.GridRows = GridRows;
        this.PixelWidth = PixelWidth;
        this.PixelHeight = PixelHeight;
    }

    /// <summary>May be negative when the placement is partially scrolled off-screen.</summary>
    public int ViewportColumn { get; }

    public int ViewportRow { get; }

    public int GridColumns { get; }

    public int GridRows { get; }

    /// <summary>Destination width in Avalonia logical pixels.</summary>
    public double PixelWidth { get; }

    /// <summary>Destination height in Avalonia logical pixels.</summary>
    public double PixelHeight { get; }
}

public sealed record TerminalKittyPlacement
{
    public TerminalKittyPlacement(
        TerminalKittyImageKey Image,
        uint PlacementId,
        bool IsVirtual,
        int ZIndex,
        double PixelOffsetX,
        double PixelOffsetY,
        TerminalKittySourceRectangle Source,
        TerminalKittyPlacementGeometry? Geometry)
    {
        if (Image.ImageId == 0 || Image.Generation == 0)
        {
            throw new ArgumentException("A Kitty placement image key must be initialized.", nameof(Image));
        }

        if (!double.IsFinite(PixelOffsetX) || PixelOffsetX < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelOffsetX));
        }

        if (!double.IsFinite(PixelOffsetY) || PixelOffsetY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelOffsetY));
        }

        if (Source.Width <= 0 || Source.Height <= 0)
        {
            throw new ArgumentException("A Kitty placement source rectangle must be initialized.", nameof(Source));
        }

        if (Geometry is { } geometry
            && (geometry.GridColumns <= 0
                || geometry.GridRows <= 0
                || geometry.PixelWidth <= 0
                || geometry.PixelHeight <= 0))
        {
            throw new ArgumentException("Kitty placement geometry must be initialized.", nameof(Geometry));
        }

        this.Image = Image;
        this.PlacementId = PlacementId;
        this.IsVirtual = IsVirtual;
        this.ZIndex = ZIndex;
        this.PixelOffsetX = PixelOffsetX;
        this.PixelOffsetY = PixelOffsetY;
        this.Source = Source;
        this.Geometry = Geometry;
    }

    public TerminalKittyImageKey Image { get; }

    public uint PlacementId { get; }

    public bool IsVirtual { get; }

    public int ZIndex { get; }

    public TerminalKittyPlacementLayer Layer => ZIndex switch
    {
        < int.MinValue / 2 => TerminalKittyPlacementLayer.BelowBackground,
        < 0 => TerminalKittyPlacementLayer.BelowText,
        _ => TerminalKittyPlacementLayer.AboveText,
    };

    /// <summary>Horizontal cell offset in Avalonia logical pixels.</summary>
    public double PixelOffsetX { get; }

    /// <summary>Vertical cell offset in Avalonia logical pixels.</summary>
    public double PixelOffsetY { get; }

    public TerminalKittySourceRectangle Source { get; }

    /// <summary>
    /// Null for fully off-screen placements. Virtual placement instances carry
    /// geometry resolved by Ghostty's canonical Unicode-placeholder iterator.
    /// </summary>
    public TerminalKittyPlacementGeometry? Geometry { get; }
}
