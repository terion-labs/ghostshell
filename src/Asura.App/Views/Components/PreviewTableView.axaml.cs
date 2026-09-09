using Avalonia.Controls;

namespace Asura.App.Views.Components;

/// <summary>
/// A delimited file shown as rows and columns, reading the way the database
/// results grid does so that a table is a table wherever the shell shows one.
/// </summary>
public sealed partial class PreviewTableView : UserControl
{
    public PreviewTableView()
    {
        InitializeComponent();
    }
}
