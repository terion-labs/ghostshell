using Asura.Core;

namespace Asura.App.ViewModels;

public enum LayoutDesignerCancelDisposition
{
    Close,
    ConfirmDiscard,
}

/// <summary>The two split gestures a slot offers, matching the runtime workspace.</summary>
public enum LayoutDesignerSplitDirection
{
    Right,
    Down,
}

public sealed record LayoutDesignerOperationResult
{
    private LayoutDesignerOperationResult(
        bool isSuccess,
        DefinitionValidationIssue? issue)
    {
        IsSuccess = isSuccess;
        Issue = issue;
    }

    public static LayoutDesignerOperationResult Applied { get; } = new(true, null);

    public bool IsSuccess { get; }

    public DefinitionValidationIssue? Issue { get; }

    internal static LayoutDesignerOperationResult Rejected(DefinitionValidationIssue issue) =>
        new(false, issue ?? throw new ArgumentNullException(nameof(issue)));
}

public sealed record LayoutDesignerSaveRequest(
    LayoutDefinition Definition,
    long? ExpectedRevision);

/// <summary>
/// Presentation state for one designer slot. The instance is also the Dock
/// document's Context, so it is mutable: the document keeps one identity while
/// its order, share of the canvas, and selection change under editing.
/// </summary>
public sealed class LayoutDesignerSlotViewModel : ObservableObject
{
    private int _order;
    private double _widthShare;
    private double _heightShare;
    private bool _isSelected;

    public LayoutDesignerSlotViewModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The layout slot id, which is also the Dock document id.</summary>
    public string Id { get; }

    /// <summary>The keyboard and accessibility traversal position, 1-based.</summary>
    public int Order
    {
        get => _order;
        internal set
        {
            if (SetProperty(ref _order, value))
            {
                OnPropertyChanged(nameof(OrderLabel));
                OnPropertyChanged(nameof(UsesOrangePalette));
                OnPropertyChanged(nameof(UsesBluePalette));
                OnPropertyChanged(nameof(UsesGreenPalette));
                OnPropertyChanged(nameof(UsesPinkPalette));
            }
        }
    }

    /// <summary>The slot's share of the canvas width, 0..1.</summary>
    public double WidthShare
    {
        get => _widthShare;
        internal set
        {
            if (SetProperty(ref _widthShare, value))
            {
                OnPropertyChanged(nameof(SizeLabel));
            }
        }
    }

    /// <summary>The slot's share of the canvas height, 0..1.</summary>
    public double HeightShare
    {
        get => _heightShare;
        internal set
        {
            if (SetProperty(ref _heightShare, value))
            {
                OnPropertyChanged(nameof(SizeLabel));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public string OrderLabel => $"Panel {Order}";

    /// <summary>
    /// The slot's size as the share of the screen it will actually occupy —
    /// "50% × 33%" — because proportions, not grid coordinates, are what the
    /// dock-based designer edits.
    /// </summary>
    public string SizeLabel =>
        $"{Math.Round(WidthShare * 100)}% × {Math.Round(HeightShare * 100)}%";

    public bool UsesOrangePalette => PaletteIndex == 0;

    public bool UsesBluePalette => PaletteIndex == 1;

    public bool UsesGreenPalette => PaletteIndex == 2;

    public bool UsesPinkPalette => PaletteIndex == 3;

    private int PaletteIndex => (Order - 1) % 4;
}
