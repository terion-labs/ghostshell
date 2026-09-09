using Asura.Application;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

internal sealed record SshHostKeyReviewPrompt(SshHostKeyReview Review)
{
    public string Title => Review.RequiresExplicitReplacement
        ? "The SSH host key changed"
        : "Trust this SSH host key?";

    public string Explanation => Review.RequiresExplicitReplacement
        ? "The server presented a different identity from the one you previously trusted. This can indicate a rebuilt server, an endpoint change, or an interception attempt."
        : "This is the first host key Asura has seen for this saved connection.";

    public string ConfirmLabel => Review.RequiresExplicitReplacement
        ? "Replace trusted key"
        : "Trust host key";
}

public sealed partial class SshHostKeyReviewDialog : Window
{
    public SshHostKeyReviewDialog()
    {
        InitializeComponent();
    }

    public SshHostKeyReviewDialog(SshHostKeyReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        InitializeComponent();
        DataContext = new SshHostKeyReviewPrompt(review);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

}
