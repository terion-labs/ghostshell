using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class AgentApprovalCardView : UserControl
{
    public AgentApprovalCardView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? ApproveRequested;

    public event EventHandler<RoutedEventArgs>? DenyRequested;

    private void OnApproveClick(object? sender, RoutedEventArgs e) =>
        ApproveRequested?.Invoke(sender, e);

    private void OnDenyClick(object? sender, RoutedEventArgs e) =>
        DenyRequested?.Invoke(sender, e);
}
