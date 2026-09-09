using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class AgentCapabilityRequestCardView : UserControl
{
    public AgentCapabilityRequestCardView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? EnableAskRequested;

    public event EventHandler<RoutedEventArgs>? KeepOffRequested;

    private void OnEnableAgentCapabilityAskClick(object? sender, RoutedEventArgs e) =>
        EnableAskRequested?.Invoke(sender, e);

    private void OnKeepAgentCapabilityOffClick(object? sender, RoutedEventArgs e) =>
        KeepOffRequested?.Invoke(sender, e);
}
