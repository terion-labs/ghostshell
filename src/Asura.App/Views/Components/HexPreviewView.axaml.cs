using Avalonia.Controls;

namespace Asura.App.Views.Components;

/// <summary>
/// A file's bytes, drawn a screenful at a time. The rows are uniform and the
/// list realises only what is visible, so a dump of any size appears at once
/// rather than after every one of its lines has been measured.
/// </summary>
public sealed partial class HexPreviewView : UserControl
{
    public HexPreviewView()
    {
        InitializeComponent();
    }
}
