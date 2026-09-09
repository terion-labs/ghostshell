using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class ProcessMonitorRuntimePanelView : UserControl
{
    public ProcessMonitorRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(sender, e);
    }

    private void OnConnectionSelected(object? sender, PanelConnectionSelectedEventArgs e) =>
        ConnectionSelected?.Invoke(this, e);

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e) =>
        NewConnectionRequested?.Invoke(this, e);

    private void OnSortCpuClick(object? sender, RoutedEventArgs e) =>
        ApplySort(ProcessMonitorSort.CpuDescending);

    private void OnSortMemoryClick(object? sender, RoutedEventArgs e) =>
        ApplySort(ProcessMonitorSort.MemoryDescending);

    private void OnSortNameClick(object? sender, RoutedEventArgs e) =>
        ApplySort(ProcessMonitorSort.NameAscending);

    private void OnSortProcessIdClick(object? sender, RoutedEventArgs e) =>
        ApplySort(ProcessMonitorSort.ProcessIdAscending);

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void ApplySort(ProcessMonitorSort sort)
    {
        if (DataContext is ProcessMonitorRuntimePanelViewModel panel)
        {
            panel.ChangeSort(sort);
        }
    }
}
