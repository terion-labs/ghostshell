using System.ComponentModel;
using System.Text;
using Asura.App.ViewModels;
using Asura.Application;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views.Components;

public sealed partial class DatabaseWorkspaceView
{
    private const int MaximumContextFileBytes = 16 * 1024 * 1024;
    private const int MaximumQuickFilterLabelCharacters = 48;
    private const double QuickLookPreferredWidth = 620;
    private const double QuickLookPreferredHeight = 420;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly FilePickerFileType CsvFileType = new("CSV data")
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"],
        AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"],
    };

    private static readonly FilePickerFileType JsonFileType = new("JSON data")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    internal static IReadOnlyList<FilePickerFileType> PageExportFileTypes { get; } =
        [CsvFileType, JsonFileType];

    private DatabaseGridCellTarget? _contextCellTarget;
    private bool _pointerContextMiss;

    private void InitializeDatabaseContextMenu()
    {
        ResultDataGrid.AddHandler(
            PointerPressedEvent,
            OnResultGridPointerPressedTunnel,
            RoutingStrategies.Tunnel);

        var accelerator = AcceleratorModifier;
        QuickLookMenuItem.InputGesture = new KeyGesture(Key.Enter, accelerator);
        RefreshTableMenuItem.InputGesture = new KeyGesture(Key.R, RefreshModifier);
        PasteCellMenuItem.InputGesture = new KeyGesture(Key.V, accelerator);
        AddRowMenuItem.InputGesture = new KeyGesture(Key.I, accelerator);
        DuplicateRowMenuItem.InputGesture = new KeyGesture(Key.D, accelerator);
        CopyRowMenuItem.InputGesture = new KeyGesture(Key.C, accelerator);
        CopyCellMenuItem.InputGesture = new KeyGesture(
            Key.C,
            accelerator | KeyModifiers.Shift);
        DeleteRowMenuItem.InputGesture = new KeyGesture(Key.Delete);
    }

    private static KeyModifiers AcceleratorModifier => OperatingSystem.IsMacOS()
        ? KeyModifiers.Meta
        : KeyModifiers.Control;

    private static KeyModifiers RefreshModifier => OperatingSystem.IsMacOS()
        ? KeyModifiers.Meta | KeyModifiers.Alt
        : KeyModifiers.Control;

    /// <summary>
    /// Clears the previous pointer target before DataGrid identifies the cell.
    /// Without the tunnel phase, a right-click below the last row can act on the
    /// cell from the previous menu invocation.
    /// </summary>
    private void OnResultGridPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (e.GetCurrentPoint(ResultDataGrid).Properties.IsRightButtonPressed)
        {
            _contextCellTarget = null;
            _pointerContextMiss = true;
        }
    }

    private void OnResultCellPointerPressed(
        object? sender,
        DataGridCellPointerPressedEventArgs e)
    {
        _ = sender;
        var properties = e.PointerPressedEventArgs
            .GetCurrentPoint(ResultDataGrid)
            .Properties;
        if (!properties.IsRightButtonPressed && !properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (!TryCreateCellTarget(e.Row, e.Column, out var target))
        {
            return;
        }

        CommitGridEdit();
        ResultDataGrid.SelectedItem = target.Row;
        ResultDataGrid.CurrentColumn = e.Column;
        _contextCellTarget = target;
        _pointerContextMiss = false;

        if (properties.IsMiddleButtonPressed)
        {
            e.PointerPressedEventArgs.Handled = true;
            ShowQuickLook(target);
        }
    }

    private void OnResultContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _ = sender;
        if (!e.TryGetPosition(ResultDataGrid, out _))
        {
            // The keyboard context-menu gesture has no pointer position. Its
            // target is the current DataGrid cell, not the last right-click.
            _pointerContextMiss = false;
            _contextCellTarget = ResolveCurrentCellTarget();
        }
    }

    private void OnResultContextMenuOpening(object? sender, CancelEventArgs e)
    {
        _ = sender;
        var target = _pointerContextMiss
            ? null
            : _contextCellTarget ?? ResolveCurrentCellTarget();
        if (Panel is not { } panel
            || target is null
            || !IsTargetCurrent(panel, target))
        {
            e.Cancel = true;
            InvalidateDatabaseContextMenu(closePopup: false);
            return;
        }

        _contextCellTarget = target;
        RefreshContextMenu(panel, target);
    }

    private void OnResultContextMenuClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        _ = e;
        // Do not mutate MenuItem visuals while the popup is tearing down.
        // BuildQuickFilterMenu replaces them at the next Opening, before the
        // popup is laid out again. Their click closures carry no row payload.
        if (QuickFilterMenuItem.IsSubMenuOpen)
        {
            QuickFilterMenuItem.Close();
        }

        ClearDatabaseContextMenuTarget();
    }

    private void InvalidateDatabaseContextMenu(bool closePopup)
    {
        if (closePopup && ResultContextMenu.IsOpen)
        {
            ResultContextMenu.Close();
        }

        ClearDatabaseContextMenuTarget();
    }

    private void ClearDatabaseContextMenuTarget()
    {
        _contextCellTarget = null;
        _pointerContextMiss = false;
    }

    private bool TryCreateCellTarget(
        DataGridRow rowContainer,
        DataGridColumn gridColumn,
        out DatabaseGridCellTarget target)
    {
        target = null!;
        var ordinal = gridColumn.DisplayIndex;
        if (rowContainer.DataContext is not DatabaseResultRowViewModel row
            || gridColumn.Tag is not DatabaseResultColumnViewModel column
            || ordinal < 0
            || ordinal >= row.Cells.Count)
        {
            return false;
        }

        target = new DatabaseGridCellTarget(row, column, row.Cells[ordinal], ordinal);
        return true;
    }

    private DatabaseGridCellTarget? ResolveCurrentCellTarget()
    {
        var gridColumn = ResultDataGrid.CurrentColumn;
        var ordinal = gridColumn?.DisplayIndex ?? -1;
        if (ResultDataGrid.SelectedItem is not DatabaseResultRowViewModel row
            || gridColumn?.Tag is not DatabaseResultColumnViewModel column
            || ordinal < 0
            || ordinal >= row.Cells.Count)
        {
            return null;
        }

        return new DatabaseGridCellTarget(row, column, row.Cells[ordinal], ordinal);
    }

    private bool IsTargetCurrent(
        DatabaseRuntimePanelViewModel panel,
        DatabaseGridCellTarget target)
    {
        var current = ResolveCurrentCellTarget();
        return current is not null
            && ReferenceEquals(panel.SelectedRow, target.Row)
            && panel.ResultRows.Any(row => ReferenceEquals(row, target.Row))
            && target.Ordinal >= 0
            && target.Ordinal < target.Row.Cells.Count
            && target.Ordinal < panel.ResultColumns.Count
            && ReferenceEquals(target.Row.Cells[target.Ordinal], target.Cell)
            && ReferenceEquals(panel.ResultColumns[target.Ordinal], target.Column)
            && ReferenceEquals(current.Row, target.Row)
            && ReferenceEquals(current.Cell, target.Cell)
            && current.Ordinal == target.Ordinal;
    }

    private static bool IsRowCurrent(
        DatabaseRuntimePanelViewModel panel,
        DatabaseResultRowViewModel row) =>
        ReferenceEquals(panel.SelectedRow, row)
        && panel.ResultRows.Any(candidate => ReferenceEquals(candidate, row));

    private void RefreshContextMenu(
        DatabaseRuntimePanelViewModel panel,
        DatabaseGridCellTarget target)
    {
        var canMutateObject = panel.CanEditRows;
        var showMutationActions = panel.HasResults;
        var mutationUnavailableReason = canMutateObject
            ? null
            : panel.ReadOnlyReason;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        var isOracle = string.Equals(
            panel.SelectedDriver.Id,
            "oracle",
            StringComparison.Ordinal);

        QuickLookMenuItem.IsEnabled = true;
        RefreshTableMenuItem.IsEnabled = panel.CanRefreshTable;

        MutationMenuSeparator.IsVisible = showMutationActions;
        PasteCellMenuItem.IsVisible = showMutationActions;
        PasteCellMenuItem.IsEnabled = panel.CanMutateRows && target.Cell.CanSetText;
        AddRowMenuItem.IsVisible = showMutationActions;
        AddRowMenuItem.IsEnabled = panel.CanMutateRows;
        DuplicateRowMenuItem.IsVisible = showMutationActions;
        DuplicateRowMenuItem.IsEnabled = panel.CanDuplicateSelectedRow;
        ToolTip.SetTip(PasteCellMenuItem, mutationUnavailableReason);
        ToolTip.SetTip(AddRowMenuItem, mutationUnavailableReason);
        ToolTip.SetTip(DuplicateRowMenuItem, mutationUnavailableReason);

        SetEmptyMenuItem.IsVisible = target.Cell.CanSetEmpty && !isOracle;
        SetEmptyMenuItem.IsEnabled = panel.CanSetSelectedCellEmpty(target.Ordinal);
        SetNullContextMenuItem.IsVisible = target.Cell.CanSetNull;
        SetNullContextMenuItem.IsEnabled = panel.CanMutateRows && target.Cell.CanSetNull;
        SetDefaultContextMenuItem.IsVisible = target.Cell.CanSetDefault;
        SetDefaultContextMenuItem.IsEnabled = panel.CanMutateRows
            && target.Row.IsNew
            && target.Cell.CanSetDefault;
        SetFileMenuItem.IsVisible = target.Cell.CanSetBinary;
        SetFileMenuItem.IsEnabled = panel.CanMutateRows
            && target.Cell.CanSetBinary
            && storage?.CanOpen == true;
        var hasNullOrDefaultAction = SetNullContextMenuItem.IsVisible
            || SetDefaultContextMenuItem.IsVisible;
        SetNullMenuSeparator.IsVisible = SetEmptyMenuItem.IsVisible
            && hasNullOrDefaultAction;
        SetFileMenuSeparator.IsVisible = hasNullOrDefaultAction
            && SetFileMenuItem.IsVisible;
        var hasSetValueAction = SetEmptyMenuItem.IsVisible
            || SetNullContextMenuItem.IsVisible
            || SetDefaultContextMenuItem.IsVisible
            || SetFileMenuItem.IsVisible;
        SetValueMenuSeparator.IsVisible = showMutationActions;
        SetValueMenuItem.IsVisible = showMutationActions;
        SetValueMenuItem.IsEnabled = SetEmptyMenuItem.IsEnabled
            || SetNullContextMenuItem.IsEnabled
            || SetDefaultContextMenuItem.IsEnabled
            || SetFileMenuItem.IsEnabled;
        ToolTip.SetTip(SetValueMenuItem, mutationUnavailableReason);

        CopyRowMenuItem.IsEnabled = true;
        CopyCellMenuItem.IsEnabled = true;
        CopyColumnMenuItem.IsEnabled = panel.ResultRows.Count > 0;
        CopyRowAsMenuItem.IsEnabled = true;
        CopyRowInsertMenuItem.IsEnabled = panel.CanCopySelectedRowAsInsert;
        ToolTip.SetTip(
            CopyRowInsertMenuItem,
            panel.CanCopySelectedRowAsInsert
                ? "Copy an executable INSERT for the active database."
                : "INSERT copy requires a physical table result.");

        BuildQuickFilterMenu(panel, target);
        QuickFilterMenuSeparator.IsVisible = panel.CanFilterTable;
        QuickFilterMenuItem.IsVisible = panel.CanFilterTable;

        ImportMenuItem.IsVisible = showMutationActions;
        ImportCsvMenuItem.IsEnabled = panel.CanMutateRows && storage?.CanOpen == true;
        ImportMenuItem.IsEnabled = ImportCsvMenuItem.IsEnabled;
        ToolTip.SetTip(ImportMenuItem, mutationUnavailableReason);
        ExportPageMenuItem.IsEnabled = panel.HasResults && storage?.CanSave == true;

        DeleteMenuSeparator.IsVisible = showMutationActions;
        DeleteRowMenuItem.IsVisible = showMutationActions;
        DeleteRowMenuItem.IsEnabled = panel.CanDeleteSelectedRow;
        ToolTip.SetTip(DeleteRowMenuItem, mutationUnavailableReason);
    }

    private void BuildQuickFilterMenu(
        DatabaseRuntimePanelViewModel panel,
        DatabaseGridCellTarget target)
    {
        var operators = panel.GetQuickFilterOperators(target.Ordinal);
        var items = new List<object>(operators.Count + 4);
        var previousGroup = -1;
        foreach (var option in operators)
        {
            var group = QuickFilterGroup(option.Operator);
            if (items.Count > 0 && group != previousGroup)
            {
                items.Add(new Separator());
            }

            previousGroup = group;
            var item = new MenuItem
            {
                Header = BuildQuickFilterLabel(target, option.Operator),
                IsEnabled = panel.CanFilterTable,
            };
            AutomationProperties.SetName(
                item,
                $"Quick-filter {target.Column.Name} using {option.Label}");
            var filterOperator = option.Operator;
            item.Click += async (_, _) =>
            {
                var liveTarget = _contextCellTarget ?? ResolveCurrentCellTarget();
                var livePanel = Panel;
                if (liveTarget is null || livePanel is null)
                {
                    return;
                }

                CommitGridEdit();
                if (IsTargetCurrent(livePanel, liveTarget)
                    && livePanel.GetQuickFilterOperators(liveTarget.Ordinal).Any(candidate =>
                        candidate.Operator == filterOperator))
                {
                    await livePanel.ApplyQuickFilterAsync(
                        liveTarget.Ordinal,
                        filterOperator);
                }
            };
            items.Add(item);
        }

        // MenuItem's late-bound ItemsSource does not reliably update
        // HasSubMenu in every popup host. Populate its owned collection so both
        // the chevron and keyboard submenu opening are available immediately.
        QuickFilterMenuItem.Items.Clear();
        foreach (var item in items)
        {
            QuickFilterMenuItem.Items.Add(item);
        }

        QuickFilterMenuItem.IsEnabled = panel.CanFilterTable && items.Count > 0;
    }

    private static int QuickFilterGroup(DatabaseFilterOperator filterOperator) =>
        filterOperator switch
        {
            DatabaseFilterOperator.Equal
                or DatabaseFilterOperator.NotEqual
                or DatabaseFilterOperator.LessThan
                or DatabaseFilterOperator.GreaterThan
                or DatabaseFilterOperator.LessThanOrEqual
                or DatabaseFilterOperator.GreaterThanOrEqual => 0,
            DatabaseFilterOperator.Contains or DatabaseFilterOperator.NotContains => 1,
            DatabaseFilterOperator.StartsWith or DatabaseFilterOperator.EndsWith => 2,
            DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn => 3,
            DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull => 4,
            _ => 5,
        };

    private static string BuildQuickFilterLabel(
        DatabaseGridCellTarget target,
        DatabaseFilterOperator filterOperator)
    {
        var column = target.Column.Name;
        if (filterOperator == DatabaseFilterOperator.IsNull)
        {
            return $"{column} IS NULL";
        }

        if (filterOperator == DatabaseFilterOperator.IsNotNull)
        {
            return $"{column} IS NOT NULL";
        }

        var value = QuoteQuickFilterValue(target.Cell.Text);
        return filterOperator switch
        {
            DatabaseFilterOperator.Equal => $"{column} = {value}",
            DatabaseFilterOperator.NotEqual => $"{column} <> {value}",
            DatabaseFilterOperator.LessThan => $"{column} < {value}",
            DatabaseFilterOperator.GreaterThan => $"{column} > {value}",
            DatabaseFilterOperator.LessThanOrEqual => $"{column} <= {value}",
            DatabaseFilterOperator.GreaterThanOrEqual => $"{column} >= {value}",
            DatabaseFilterOperator.Contains => $"{column} Contains {value}",
            DatabaseFilterOperator.NotContains => $"{column} Not contains {value}",
            DatabaseFilterOperator.StartsWith => $"{column} Has prefix {value}",
            DatabaseFilterOperator.EndsWith => $"{column} Has suffix {value}",
            DatabaseFilterOperator.In => $"{column} IN ({value})",
            DatabaseFilterOperator.NotIn => $"{column} NOT IN ({value})",
            _ => $"{column} {filterOperator} {value}",
        };
    }

    private static string QuoteQuickFilterValue(string value)
    {
        var oneLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Replace("'", "''", StringComparison.Ordinal);
        if (oneLine.Length > MaximumQuickFilterLabelCharacters)
        {
            oneLine = oneLine[..(MaximumQuickFilterLabelCharacters - 1)] + "…";
        }

        return $"'{oneLine}'";
    }

    private void OnQuickLookClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if ((_contextCellTarget ?? ResolveCurrentCellTarget()) is { } target)
        {
            ShowQuickLook(target);
        }
    }

    private void ShowQuickLook(DatabaseGridCellTarget target) => _ = ShowQuickLookAsync(target);

    private async Task ShowQuickLookAsync(DatabaseGridCellTarget target)
    {
        if (Panel is not { } panel || !IsTargetCurrent(panel, target))
        {
            return;
        }

        if (target.Cell.NeedsFullTextForEditing
            && !await panel.PrepareCellForEditingAsync(target.Cell))
        {
            return;
        }

        if (ReferenceEquals(Panel, panel) && IsTargetCurrent(panel, target))
        {
            ShowLoadedQuickLook(target);
        }
    }

    private void ShowLoadedQuickLook(DatabaseGridCellTarget target)
    {
        var canEdit = Panel is { CanMutateRows: true }
            && target.Cell.CanSetText;
        var initialState = target.Cell.State;
        var initialEditText = target.Cell.EditText;
        var smallSpace = QuickLookThemeSpace("ShellSpaceSm", fallback: 8);
        var mediumSpace = QuickLookThemeSpace("ShellSpaceMd", fallback: 12);
        var editor = new TextBox
        {
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            AcceptsReturn = true,
            // Display text is intentionally compact and may contain an
            // ellipsis. Only read-only Quick Look uses it; editable cells must
            // start with the complete provider-neutral edit representation.
            Text = canEdit ? initialEditText : target.Cell.Text,
            IsReadOnly = !canEdit,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            editor,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(
            editor,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        AutomationProperties.SetName(editor, $"Quick Look value for {target.Column.Name}");

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = "Quick Look Editor",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{target.Column.Name} · {target.Column.DataTypeName}",
            Opacity = 0.7,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            ItemSpacing = smallSpace,
            LineSpacing = smallSpace,
        };
        var close = new Button { Content = canEdit ? "Cancel" : "Close" };
        AutomationProperties.SetName(close, "Close the database cell Quick Look");
        var flyout = new Flyout
        {
            Placement = PlacementMode.Center,
        };
        close.Click += (_, _) => flyout.Hide();
        actions.Children.Add(close);

        void ApplyAndClose()
        {
            var nextText = editor.Text ?? string.Empty;
            var textChanged = !string.Equals(
                nextText,
                initialEditText,
                StringComparison.Ordinal);
            if (textChanged
                && Panel is { } panel
                && target.Cell.State == initialState
                && IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellText(target.Ordinal, nextText);
            }

            flyout.Hide();
        }

        if (canEdit)
        {
            var apply = new Button { Content = "Apply" };
            apply.Classes.Add("PrimaryButton");
            AutomationProperties.SetName(apply, "Apply the Quick Look database cell value");
            apply.Click += (_, _) => ApplyAndClose();
            actions.Children.Add(apply);
        }

        void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            _ = sender;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                flyout.Hide();
                return;
            }

            if (canEdit
                && e.Key == Key.Enter
                && e.KeyModifiers == AcceleratorModifier)
            {
                e.Handled = true;
                ApplyAndClose();
            }
        }

        // TextBox consumes Enter when AcceptsReturn is enabled. Listening in
        // the tunnel (including already-handled events) keeps Cmd/Ctrl+Enter a
        // dependable Apply gesture without changing normal multiline input.
        editor.AddHandler(
            InputElement.KeyDownEvent,
            OnEditorKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = mediumSpace,
        };
        AutomationProperties.SetName(content, "Database cell Quick Look dialog");
        content.Children.Add(header);
        Grid.SetRow(editor, 1);
        content.Children.Add(editor);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        flyout.Content = content;

        // Opening a second popup while the context menu is closing can be
        // ignored by native popup hosts. Posting starts Quick Look afterward.
        Dispatcher.UIThread.Post(() =>
        {
            if (VisualRoot is null
                || Panel is not { } panel
                || !IsTargetCurrent(panel, target))
            {
                return;
            }

            var contentSize = QuickLookContentSize(mediumSpace);
            content.Width = contentSize.Width;
            content.Height = contentSize.Height;
            content.MaxWidth = contentSize.Width;
            content.MaxHeight = contentSize.Height;
            flyout.ShowAt(ResultDataGrid);
            editor.Focus();
            editor.SelectAll();
        });
    }

    private Size QuickLookContentSize(double viewportInset)
    {
        var width = SmallestPositive(
            ResultDataGrid.Bounds.Width,
            Bounds.Width,
            TopLevel.GetTopLevel(this)?.Bounds.Width ?? 0);
        var height = SmallestPositive(
            ResultDataGrid.Bounds.Height,
            Bounds.Height,
            TopLevel.GetTopLevel(this)?.Bounds.Height ?? 0);
        var contentWidth = Math.Max(
            1,
            Math.Min(QuickLookPreferredWidth, width - (viewportInset * 2)));
        var contentHeight = Math.Max(
            1,
            Math.Min(QuickLookPreferredHeight, height - (viewportInset * 2)));
        return new Size(contentWidth, contentHeight);
    }

    private double QuickLookThemeSpace(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double space
            ? space
            : fallback;

    private static double SmallestPositive(params double[] values)
    {
        var result = double.PositiveInfinity;
        foreach (var value in values)
        {
            if (double.IsFinite(value) && value > 0)
            {
                result = Math.Min(result, value);
            }
        }

        return double.IsPositiveInfinity(result) ? 1 : result;
    }

    private async void OnRefreshTableClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            CommitGridEdit();
            await panel.RefreshTableAsync();
        }
    }

    private async void OnPasteCellClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        await PasteCellFromClipboardAsync(target);
    }

    private async Task PasteCellFromClipboardAsync(DatabaseGridCellTarget? target)
    {
        if (target is null
            || Panel is not { } panel
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            CommitGridEdit();
            if (!IsTargetCurrent(panel, target))
            {
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            if (text is null)
            {
                panel.ReportInteractionError("The clipboard does not contain text.");
                return;
            }

            if (Encoding.UTF8.GetByteCount(text) > MaximumContextFileBytes)
            {
                panel.ReportInteractionError("The clipboard value is larger than 16 MiB.");
                return;
            }

            if (IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellText(target.Ordinal, text);
            }
        }
        catch (OperationCanceledException)
        {
            // A native clipboard provider can surface cancellation while its
            // owner is closing. Treat it like an empty clipboard interaction.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not paste from the clipboard: {exception.Message}");
        }
    }

    private void OnContextAddRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CommitGridEdit();
        Panel?.AddRow();
    }

    private void OnDuplicateRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        var panel = Panel;
        CommitGridEdit();
        if (target is not null
            && panel is not null
            && IsTargetCurrent(panel, target))
        {
            panel.DuplicateSelectedRow();
        }
    }

    private void OnSetEmptyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        if (target is not null && Panel is { } panel)
        {
            CommitGridEdit();
            if (IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellEmpty(target.Ordinal);
            }
        }
    }

    private void OnSetNullContextClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        if (target is not null && Panel is { } panel)
        {
            CommitGridEdit();
            if (IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellNull(target.Ordinal);
            }
        }
    }

    private void OnSetDefaultContextClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        if (target is not null && Panel is { } panel)
        {
            CommitGridEdit();
            if (IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellDefault(target.Ordinal);
            }
        }
    }

    private async void OnSetFileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        var panel = Panel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (target is null || panel is null || storage?.CanOpen != true)
        {
            return;
        }

        try
        {
            CommitGridEdit();
            if (!IsTargetCurrent(panel, target))
            {
                return;
            }

            var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Use a file as {target.Column.Name}",
                AllowMultiple = false,
            });
            if (selected.Count != 1)
            {
                return;
            }

            var bytes = await ReadStorageFileAsync(selected[0]);
            if (IsTargetCurrent(panel, target))
            {
                panel.SetSelectedCellBinary(target.Ordinal, bytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Native pickers may report cancellation either as an empty result
            // or as an exception, depending on the platform backend.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not add the selected file: {exception.Message}");
        }
    }

    private async void OnCopyRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        await CopyTargetTextAsync(
            target,
            static (panel, cellTarget) => panel.BuildRowTsvAsync(cellTarget.Row));
    }

    private async void OnCopyCellClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        await CopyCellAsync(target);
    }

    private async Task CopyCellAsync(DatabaseGridCellTarget? target)
        => await CopyTargetTextAsync(
            target,
            static (panel, cellTarget) => panel.BuildCellValueAsync(
                cellTarget.Row,
                cellTarget.Ordinal));

    private async void OnCopyColumnClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        await CopyTargetTextAsync(
            target,
            static (panel, cellTarget) => panel.BuildColumnValuesAsync(cellTarget.Ordinal));
    }

    private async Task CopyTargetTextAsync(
        DatabaseGridCellTarget? target,
        Func<DatabaseRuntimePanelViewModel, DatabaseGridCellTarget, Task<(string Text, long Revision)>> build)
    {
        var panel = Panel;
        if (target is null || panel is null)
        {
            return;
        }

        try
        {
            CommitGridEdit();
            if (!IsTargetCurrent(panel, target))
            {
                return;
            }

            var snapshot = await build(panel, target);
            if (ReferenceEquals(Panel, panel) && panel.IsClipboardRequestCurrent(snapshot.Revision))
            {
                await CopyTextAsync(snapshot.Text, panel);
            }
        }
        catch (OperationCanceledException)
        {
            // Clipboard ownership can disappear during shutdown.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError(
                $"Could not format the selected database value: {exception.Message}");
        }
    }

    /// <summary>True when the text actually reached the clipboard.</summary>
    private async Task<bool> CopyTextAsync(
        string text,
        DatabaseRuntimePanelViewModel panel)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Clipboard ownership can disappear during shutdown.
            return false;
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not copy to the clipboard: {exception.Message}");
            return false;
        }
    }

    private async void OnImportCsvClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var panel = Panel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (panel is null || storage?.CanOpen != true)
        {
            return;
        }

        try
        {
            CommitGridEdit();
            var sourcePage = CaptureGridPage(panel);
            var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import CSV rows",
                AllowMultiple = false,
                FileTypeFilter = [CsvFileType],
            });
            if (selected.Count != 1)
            {
                return;
            }

            if (!IsGridPageCurrent(panel, sourcePage) || !panel.CanMutateRows)
            {
                panel.ReportInteractionError(
                    "The database page changed while the CSV file was open. Start the import again.");
                return;
            }

            var bytes = await ReadStorageFileAsync(selected[0]);
            if (!IsGridPageCurrent(panel, sourcePage) || !panel.CanMutateRows)
            {
                panel.ReportInteractionError(
                    "The database page changed while the CSV file was being read. Start the import again.");
                return;
            }

            var text = StrictUtf8.GetString(bytes);
            panel.ImportCsv(text.TrimStart('\uFEFF'));
        }
        catch (OperationCanceledException)
        {
            // See OnSetFileClick: cancellation varies by native picker backend.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not import the CSV file: {exception.Message}");
        }
    }

    private async void OnExportPageClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var panel = Panel;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (panel is null || storage?.CanSave != true)
        {
            return;
        }

        try
        {
            CommitGridEdit();
            var sourcePage = CaptureGridPage(panel);
            var selected = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export the current database page",
                SuggestedFileName = ExportFileStem(panel) + ".csv",
                DefaultExtension = "csv",
                FileTypeChoices = PageExportFileTypes,
                ShowOverwritePrompt = true,
            });
            if (selected is null)
            {
                return;
            }

            if (!IsGridPageCurrent(panel, sourcePage))
            {
                panel.ReportInteractionError(
                    "The database page changed while the export destination was open. Start the export again.");
                return;
            }

            var format = ResolvePageExportFormat(selected.Name);
            await WriteStorageFileAsync(
                selected,
                destination => panel.WriteCurrentPageExportAsync(destination, format));
        }
        catch (OperationCanceledException)
        {
            // See OnSetFileClick: cancellation varies by native picker backend.
        }
        catch (Exception exception)
        {
            panel.ReportInteractionError($"Could not export the database page: {exception.Message}");
        }
    }

    internal static DatabaseGridExportFormat ResolvePageExportFormat(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => DatabaseGridExportFormat.Json,
            ".csv" or "" => DatabaseGridExportFormat.Csv,
            var extension => throw new InvalidDataException(
                $"Unsupported database export extension '{extension}'. Use .csv or .json."),
        };
    }

    private static string ExportFileStem(DatabaseRuntimePanelViewModel panel)
    {
        var name = panel.SelectedObject?.Descriptor.Name ?? "database-page";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(character => invalid.Contains(character) ? '-' : character)]);
        return string.IsNullOrWhiteSpace(safe) ? "database-page" : safe;
    }

    private static DatabaseGridPageTarget CaptureGridPage(
        DatabaseRuntimePanelViewModel panel) => new(
        panel.SelectedObject?.Descriptor.Id,
        panel.ResultColumns,
        panel.ResultRows);

    private static bool IsGridPageCurrent(
        DatabaseRuntimePanelViewModel panel,
        DatabaseGridPageTarget target) =>
        Equals(panel.SelectedObject?.Descriptor.Id, target.ObjectId)
        && ReferenceEquals(panel.ResultColumns, target.Columns)
        && ReferenceEquals(panel.ResultRows, target.Rows);

    private void OnContextDeleteRowClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var target = _contextCellTarget ?? ResolveCurrentCellTarget();
        var panel = Panel;
        CommitGridEdit();
        if (target is not null
            && panel is not null
            && IsTargetCurrent(panel, target))
        {
            panel.DeleteSelectedRow();
        }
    }

    private async void OnResultGridKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (IsInsideGridEditor(e.Source))
        {
            return;
        }

        var target = ResolveCurrentCellTarget();
        var accelerator = AcceleratorModifier;
        if (e.KeyModifiers == accelerator && e.Key == Key.Enter && target is not null)
        {
            e.Handled = true;
            ShowQuickLook(target);
            return;
        }

        if (e.KeyModifiers == RefreshModifier
            && e.Key == Key.R
            && Panel is { CanRefreshTable: true } refreshPanel)
        {
            e.Handled = true;
            CommitGridEdit();
            await refreshPanel.RefreshTableAsync();
            return;
        }

        if (e.KeyModifiers == accelerator
            && e.Key == Key.V
            && target is { Cell.CanSetText: true }
            && Panel is { CanMutateRows: true })
        {
            e.Handled = true;
            await PasteCellFromClipboardAsync(target);
            return;
        }

        if (e.KeyModifiers == accelerator
            && e.Key == Key.I
            && Panel is { CanMutateRows: true } addPanel)
        {
            e.Handled = true;
            CommitGridEdit();
            addPanel.AddRow();
            return;
        }

        if (e.KeyModifiers == accelerator
            && e.Key == Key.D
            && target is not null
            && Panel is { CanDuplicateSelectedRow: true } duplicatePanel)
        {
            e.Handled = true;
            CommitGridEdit();
            if (IsTargetCurrent(duplicatePanel, target))
            {
                duplicatePanel.DuplicateSelectedRow();
            }

            return;
        }

        var isCopyGesture = e.Key == Key.C
            && (e.KeyModifiers == accelerator
                || e.KeyModifiers == (accelerator | KeyModifiers.Shift));
        if (isCopyGesture && target is not null)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                await CopyCellAsync(target);
            }
            else
            {
                await CopyTargetTextAsync(
                    target,
                    static (panel, cellTarget) => panel.BuildRowTsvAsync(cellTarget.Row));
            }

            return;
        }

        var deleteKey = e.Key is Key.Delete or Key.Back;
        if (deleteKey
            && e.KeyModifiers == KeyModifiers.None
            && target is not null
            && Panel is { CanDeleteSelectedRow: true } deletePanel)
        {
            e.Handled = true;
            CommitGridEdit();
            if (IsTargetCurrent(deletePanel, target))
            {
                deletePanel.DeleteSelectedRow();
            }
        }
    }

    private static bool IsInsideGridEditor(object? source)
    {
        if (source is not Control control)
        {
            return false;
        }

        return control is TextBox or CheckBox
            || control.FindAncestorOfType<TextBox>() is not null
            || control.FindAncestorOfType<CheckBox>() is not null;
    }

    private static async Task<byte[]> ReadStorageFileAsync(IStorageFile file)
    {
        await using var source = await file.OpenReadAsync();
        if (source.CanSeek && source.Length > MaximumContextFileBytes)
        {
            throw new InvalidDataException("The selected file is larger than 16 MiB.");
        }

        using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer);
            if (count == 0)
            {
                break;
            }

            if (content.Length + count > MaximumContextFileBytes)
            {
                throw new InvalidDataException("The selected file is larger than 16 MiB.");
            }

            await content.WriteAsync(buffer.AsMemory(0, count));
        }

        return content.ToArray();
    }

    private static async Task WriteStorageFileAsync(
        IStorageFile file,
        Func<Stream, Task> write)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(write);
        var localPath = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await WriteLocalStorageFileAtomicallyAsync(localPath, write);
            return;
        }

        throw new InvalidOperationException(
            "This storage provider cannot replace an export atomically. "
            + "Choose a local filesystem destination so an interrupted export cannot damage an existing file.");
    }

    internal static async Task WriteLocalStorageFileAtomicallyAsync(
        string localPath,
        Func<Stream, Task> write)
    {
        var target = Path.GetFullPath(localPath);
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("The export destination has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.asura-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var temporary = OpenExportTemporaryFile(temporaryPath))
            {
                await write(temporary);
                await temporary.FlushAsync();
            }

            File.Move(temporaryPath, target, overwrite: true);
        }
        finally
        {
            TryDeleteExportTemporaryFile(temporaryPath);
        }
    }

    private static FileStream OpenExportTemporaryFile(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    private static void TryDeleteExportTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the original export failure. A best-effort hidden temp
            // cleanup must never replace the actionable provider or I/O error.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    private sealed record DatabaseGridCellTarget(
        DatabaseResultRowViewModel Row,
        DatabaseResultColumnViewModel Column,
        DatabaseResultCellViewModel Cell,
        int Ordinal);

    private sealed record DatabaseGridPageTarget(
        DatabaseObjectId? ObjectId,
        IReadOnlyList<DatabaseResultColumnViewModel> Columns,
        IReadOnlyList<DatabaseResultRowViewModel> Rows);
}
