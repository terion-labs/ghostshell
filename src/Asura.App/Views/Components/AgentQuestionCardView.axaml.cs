using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class AgentQuestionCardView : UserControl
{
    public AgentQuestionCardView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? DeclineRequested;

    public event EventHandler<KeyEventArgs>? ResponseKeyDownRequested;

    public event EventHandler<RoutedEventArgs>? SubmitRequested;

    private void OnAgentQuestionResponseKeyDown(object? sender, KeyEventArgs e) =>
        ResponseKeyDownRequested?.Invoke(sender, e);

    private void OnDeclineAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        DeclineRequested?.Invoke(sender, e);

    private void OnSubmitAgentQuestionClick(object? sender, RoutedEventArgs e) =>
        SubmitRequested?.Invoke(sender, e);
}
