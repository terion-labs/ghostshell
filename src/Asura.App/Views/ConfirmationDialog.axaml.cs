using Asura.App.Controls;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

/// <summary>
/// What the confirm button does when pressed, which decides how it dresses:
/// a destructive action wears the danger label and is deliberately not the
/// default, so Enter cannot destroy anything; an acknowledgement is default.
/// </summary>
internal enum ConfirmationIntent
{
    Destructive,
    Acknowledge,
}

/// <summary>
/// Everything a confirmation can vary in. The window owns everything else.
/// </summary>
internal sealed record ConfirmationDialogOptions
{
    public required string Title { get; init; }

    public required string Heading { get; init; }

    public required string Detail { get; init; }

    /// <summary>The blast radius: what the action does and does not touch.</summary>
    public string? Notice { get; init; }

    /// <summary>The notice card's tone; a session list reads Warning, a scope note Notice.</summary>
    public SurfaceTone NoticeTone { get; init; } = SurfaceTone.Notice;

    public required string ConfirmLabel { get; init; }

    public string CancelLabel { get; init; } = "Cancel";

    /// <summary>An error acknowledgement has nothing to cancel.</summary>
    public bool ShowsCancel { get; init; } = true;

    public ConfirmationIntent Intent { get; init; } = ConfirmationIntent.Destructive;

    public string? ConfirmAutomationName { get; init; }

    public string? CancelAutomationName { get; init; }
}

/// <summary>
/// The one confirmation window. Callers state what is being asked via
/// <see cref="ConfirmationDialogOptions"/> and await <c>ShowDialog&lt;bool&gt;</c>;
/// true means the action was confirmed.
/// </summary>
public sealed partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
        : this(new ConfirmationDialogOptions
        {
            Title = "Confirm",
            Heading = "Are you sure?",
            Detail = "This action needs a second look before it runs.",
            ConfirmLabel = "Confirm",
        })
    {
    }

    internal ConfirmationDialog(ConfirmationDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Heading = options.Heading.Trim();
        Detail = options.Detail.Trim();
        Notice = string.IsNullOrWhiteSpace(options.Notice) ? null : options.Notice.Trim();
        ConfirmLabel = options.ConfirmLabel.Trim();
        CancelLabel = options.CancelLabel.Trim();
        ShowsCancel = options.ShowsCancel;
        InitializeComponent();
        DataContext = this;
        Title = options.Title;

        var confirm = this.FindControl<Button>("ConfirmButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        this.FindControl<StateOverlay>("ConfirmationState")!.Kind =
            options.Intent == ConfirmationIntent.Destructive
                ? StateOverlayKind.DestructiveAction
                : StateOverlayKind.TerminalError;
        this.FindControl<SurfaceCard>("NoticeCard")!.Tone = options.NoticeTone;
        if (options.Intent == ConfirmationIntent.Destructive)
        {
            confirm.Classes.Add("DestructiveButton");
        }
        else
        {
            // Only an acknowledgement answers to Enter. A destructive confirm
            // must be pressed on purpose.
            confirm.IsDefault = true;
        }

        AutomationProperties.SetName(confirm, options.ConfirmAutomationName ?? ConfirmLabel);
        AutomationProperties.SetName(cancel, options.CancelAutomationName ?? CancelLabel);

        // Tunnelled, so Escape closes even while focus sits in a child that
        // would otherwise swallow the key.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public string Heading { get; }

    public string Detail { get; }

    public string? Notice { get; }

    public bool HasNotice => Notice is not null;

    public string ConfirmLabel { get; }

    public string CancelLabel { get; }

    public bool ShowsCancel { get; }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close(false);
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
