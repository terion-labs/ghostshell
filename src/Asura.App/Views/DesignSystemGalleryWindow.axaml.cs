using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

/// <summary>
/// A gallery of the shell's components, for looking at rather than shipping. It
/// holds no view model and reaches nothing: everything on it is declared inline,
/// so what it renders is the design system and only the design system.
/// </summary>
public sealed partial class DesignSystemGalleryWindow : Window
{
    public DesignSystemGalleryWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
