using System.Globalization;
using Asura.App.ViewModels;
using Asura.Application;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Asura.App.Views.Components;

/// <summary>
/// Builds result columns from runtime database metadata. The ordinal is closed
/// over by each template, so rows remain ordinary typed view models rather than
/// reflection-backed dictionaries or generated CLR types.
/// </summary>
internal static class DatabaseDataGridColumnFactory
{
    public static IReadOnlyList<DataGridColumn> Create(
        IReadOnlyList<DatabaseResultColumnViewModel> columns,
        bool canSort)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return [.. columns.Select((column, ordinal) => CreateColumn(column, ordinal, canSort))];
    }

    public static bool CanReuse(
        DataGridColumn existing,
        DatabaseResultColumnViewModel replacement) =>
        existing is DataGridTemplateColumn
        && existing.Tag is DatabaseResultColumnViewModel current
        && string.Equals(current.Name, replacement.Name, StringComparison.Ordinal)
        && current.ValueKind == replacement.ValueKind
        && current.IsEditable == replacement.IsEditable;

    public static void Refresh(
        DataGridColumn existing,
        DatabaseResultColumnViewModel replacement,
        bool canSort)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!CanReuse(existing, replacement))
        {
            throw new ArgumentException(
                "The database column template is not compatible with the replacement.",
                nameof(replacement));
        }

        // Keep the realized cell templates and any user-resized width. Their
        // closures capture only the ordinal, so a same-shape page can update
        // metadata and sort chrome without tearing down the DataGrid.
        existing.Tag = replacement;
        existing.Header = CreateHeader(replacement);
        existing.CanUserSort = canSort;
        existing.IsReadOnly = !replacement.IsEditable;
    }

    private static DataGridColumn CreateColumn(
        DatabaseResultColumnViewModel column,
        int ordinal,
        bool canSort)
    {
        var result = new DataGridTemplateColumn
        {
            Header = CreateHeader(column),
            Width = new DataGridLength(column.Width),
            MinWidth = 76,
            MaxWidth = 340,
            CanUserResize = true,
            CanUserSort = canSort,
            IsReadOnly = !column.IsEditable,
            Tag = column,
            CellTemplate = new FuncDataTemplate<DatabaseResultRowViewModel>(
                (row, _) => CreateDisplayCell(row.Cells[ordinal]),
                supportsRecycling: false),
        };

        if (column.IsEditable)
        {
            result.CellEditingTemplate = new FuncDataTemplate<DatabaseResultRowViewModel>(
                (row, _) => CreateEditor(row.Cells[ordinal]),
                supportsRecycling: false);
        }

        return result;
    }

    private static Control CreateHeader(DatabaseResultColumnViewModel column)
    {
        var label = new TextBlock
        {
            Text = column.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                label,
            },
        };

        if (column.SortDescending is { } descending)
        {
            header.Children.Add(new SymbolIcon
            {
                Symbol = descending ? Symbol.ArrowDown : Symbol.ArrowUp,
                FontSize = 10,
            });
        }

        var sortState = column.SortDescending switch
        {
            false => "ascending",
            true => "descending",
            null => "not sorted",
        };
        AutomationProperties.SetName(
            header,
            $"Sort database column {column.Name}, {sortState}");
        AutomationProperties.SetHelpText(
            header,
            $"{column.DataTypeName}; {sortState}");
        ToolTip.SetTip(header, column.DataTypeName);
        return header;
    }

    private static Control CreateDisplayCell(DatabaseResultCellViewModel cell)
    {
        var text = new TextBlock
        {
            DataContext = cell,
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.Bind(
            TextBlock.TextProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.Text));
        text.Bind(
            Visual.OpacityProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.State,
                converter: EditStateOpacityConverter.Instance));
        return AddValidationState(cell, text);
    }

    private static Control CreateEditor(DatabaseResultCellViewModel cell)
    {
        if (cell.UsesBooleanEditor)
        {
            return CreateBooleanEditor(cell);
        }

        if (cell.UsesTextEditor)
        {
            return CreateTextEditor(cell);
        }

        return CreateDisplayCell(cell);
    }

    private static Control CreateBooleanEditor(DatabaseResultCellViewModel cell)
    {
        var editor = new CheckBox
        {
            DataContext = cell,
            IsThreeState = cell.Column.IsNullable != false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        editor.Bind(
            CheckBox.IsCheckedProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.BooleanValue,
                mode: BindingMode.TwoWay,
                updateSourceTrigger: UpdateSourceTrigger.PropertyChanged));
        AutomationProperties.SetName(editor, $"Edit {cell.Column.Name}");
        return AddValidationState(cell, editor);
    }

    private static Control CreateTextEditor(DatabaseResultCellViewModel cell)
    {
        var editor = new TextBox
        {
            DataContext = cell,
            Padding = new Thickness(8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // The editor sits inside a square grid cell; rounded corners would
            // draw a pill floating in a rectangle.
            CornerRadius = new CornerRadius(0),
        };
        editor.Bind(
            TextBox.TextProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.EditText,
                mode: BindingMode.TwoWay,
                updateSourceTrigger: UpdateSourceTrigger.PropertyChanged));
        AutomationProperties.SetName(editor, $"Edit {cell.Column.Name}");
        return AddValidationState(cell, editor);
    }

    private static Control AddValidationState(
        DatabaseResultCellViewModel cell,
        Control content)
    {
        var border = new Border
        {
            DataContext = cell,
            Child = content,
        };
        border.Classes.Add("DatabaseCellValidation");
        border.Bind(
            Border.BorderThicknessProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.ValidationError,
                converter: ValidationBorderThicknessConverter.Instance));
        BindValidationMetadata(border);
        BindValidationMetadata(content);
        return border;
    }

    private static void BindValidationMetadata(Control control)
    {
        control.Bind(
            ToolTip.TipProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.ValidationError));
        control.Bind(
            AutomationProperties.HelpTextProperty,
            CompiledBinding.Create(
                (DatabaseResultCellViewModel viewModel) => viewModel.ValidationError));
        ToolTip.SetShowDelay(control, 250);
        ToolTip.SetShowOnDisabled(control, true);
    }

    private sealed class ValidationBorderThicknessConverter : IValueConverter
    {
        public static ValidationBorderThicknessConverter Instance { get; } = new();

        public object Convert(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) => string.IsNullOrWhiteSpace(value as string)
            ? new Thickness(0)
            : new Thickness(0, 0, 0, 2);

        public object ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class EditStateOpacityConverter : IValueConverter
    {
        public static EditStateOpacityConverter Instance { get; } = new();

        public object Convert(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) => value is DatabaseEditValueState.Null
            or DatabaseEditValueState.Default
                ? 0.62
                : 1d;

        public object ConvertBack(
            object? value,
            Type targetType,
            object? parameter,
            CultureInfo culture) => throw new NotSupportedException();
    }
}
