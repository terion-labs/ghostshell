namespace Asura.App.ViewModels;

public enum MainWindowRole
{
    Primary,
    Additional,
}

public delegate MainWindowViewModel MainWindowViewModelFactory();
