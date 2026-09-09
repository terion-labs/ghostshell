using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asura.Application.Previews;

namespace Asura.App.ViewModels;

/// <summary>
/// A switch the current format offers. Flipping it re-reads the bytes already
/// in hand rather than asking the provider for them again.
/// </summary>
public sealed class PreviewToggleViewModel : ObservableObject
{
    private readonly Action<string, bool> _changed;
    private bool _isOn;

    public PreviewToggleViewModel(FilePreviewToggle toggle, Action<string, bool> changed)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Id = toggle.Id;
        Label = toggle.Label;
        _isOn = toggle.IsOn;
    }

    public string Id { get; }

    public string Label { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                _changed(Id, value);
            }
        }
    }
}

/// <summary>
/// A delimited file shown as the table it describes. Column widths are
/// measured from the content once, so the header and every row line up without
/// each cell negotiating its own width.
/// </summary>
public sealed class PreviewTableViewModel
{
    /// <summary>Bounds a column measured from an unusually long value.</summary>
    private const double MinimumColumnWidth = 72;
    private const double MaximumColumnWidth = 320;
    private const double CharacterWidth = 7.2;

    private readonly IReadOnlyList<PreviewTableRowViewModel> _rows;

    public PreviewTableViewModel(TablePreviewRendering rendering)
    {
        ArgumentNullException.ThrowIfNull(rendering);
        Summary = rendering.Summary;
        var widths = MeasureColumns(rendering);
        Columns = [.. rendering.Columns
            .Select((name, index) => new PreviewTableColumnViewModel(
                name.Length == 0 ? $"Column {index + 1}" : name,
                widths[index]))];
        _rows = [.. rendering.Rows.Select((cells, index) => new PreviewTableRowViewModel(index + 1, cells, widths))];
    }

    public string Summary { get; }

    public IReadOnlyList<PreviewTableColumnViewModel> Columns { get; }

    /// <summary>
    /// Filled a handful of rows at a time, so a table arrives without the panel
    /// stopping to attach all of it.
    /// </summary>
    public ObservableCollection<PreviewTableRowViewModel> Rows { get; } = [];

    public Task FillAsync(CancellationToken cancellationToken) =>
        IncrementalFill.FillAsync(Rows, _rows, cancellationToken);

    private static double[] MeasureColumns(TablePreviewRendering rendering)
    {
        var widths = new double[rendering.Columns.Count];
        for (var index = 0; index < widths.Length; index++)
        {
            var longest = rendering.Columns[index].Length;
            foreach (var row in rendering.Rows)
            {
                if (index < row.Count && row[index].Length > longest)
                {
                    longest = row[index].Length;
                }
            }

            widths[index] = Math.Clamp(
                (longest * CharacterWidth) + 16,
                MinimumColumnWidth,
                MaximumColumnWidth);
        }

        return widths;
    }
}

public sealed record PreviewTableColumnViewModel(string Name, double Width);

public sealed class PreviewTableRowViewModel
{
    public PreviewTableRowViewModel(
        int number,
        IReadOnlyList<string> cells,
        IReadOnlyList<double> widths)
    {
        Number = number;
        Cells = [.. widths
            .Select((width, index) => new PreviewTableCellViewModel(
                index < cells.Count ? cells[index] : string.Empty,
                width))];
    }

    public int Number { get; }

    public bool IsEven => Number % 2 == 0;

    public IReadOnlyList<PreviewTableCellViewModel> Cells { get; }
}

public sealed record PreviewTableCellViewModel(string Text, double Width);

/// <summary>A file's bytes, arriving a handful of rows at a time.</summary>
public sealed class PreviewHexViewModel
{
    private readonly IReadOnlyList<HexPreviewRow> _rows;

    public PreviewHexViewModel(HexPreviewRendering rendering)
    {
        ArgumentNullException.ThrowIfNull(rendering);
        _rows = rendering.Rows;
        Summary = rendering.Summary;
    }

    public string Summary { get; }

    public ObservableCollection<HexPreviewRow> Rows { get; } = [];

    public Task FillAsync(CancellationToken cancellationToken) =>
        IncrementalFill.FillAsync(Rows, _rows, cancellationToken);
}

/// <summary>An archive's contents, as the folders its entry paths describe.</summary>
public sealed class PreviewTreeViewModel
{
    private readonly IReadOnlyList<PreviewTreeNodeViewModel> _nodes;

    public PreviewTreeViewModel(IReadOnlyList<PreviewTreeNode> roots, string summary)
    {
        ArgumentNullException.ThrowIfNull(roots);
        Summary = summary;
        // Opened at the top level: an archive with a single wrapping folder —
        // the common shape — should not need a click to show anything at all.
        _nodes = [.. roots.Select(node => new PreviewTreeNodeViewModel(node, roots.Count == 1))];
    }

    public string Summary { get; }

    public ObservableCollection<PreviewTreeNodeViewModel> Nodes { get; } = [];

    public Task FillAsync(CancellationToken cancellationToken) =>
        IncrementalFill.FillAsync(Nodes, _nodes, cancellationToken);
}

public sealed class PreviewTreeNodeViewModel : ObservableObject
{
    private bool _isExpanded;

    public PreviewTreeNodeViewModel(PreviewTreeNode node, bool expanded = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        Name = node.Name;
        Detail = node.Detail;
        IsContainer = node.IsContainer;
        _isExpanded = expanded && node.IsContainer;
        Children = [.. node.Children.Select(child => new PreviewTreeNodeViewModel(child))];
    }

    public string Name { get; }

    public string? Detail { get; }

    public bool IsContainer { get; }

    public IReadOnlyList<PreviewTreeNodeViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
