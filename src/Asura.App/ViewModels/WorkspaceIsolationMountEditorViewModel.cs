using Asura.Core;

namespace Asura.App.ViewModels;

public sealed class WorkspaceIsolationMountEditorViewModel : ObservableObject
{
    private string _hostPath;
    private string _guestPath;
    private bool _isReadOnly;

    internal WorkspaceIsolationMountEditorViewModel(
        string hostPath,
        string guestPath,
        bool isReadOnly)
    {
        _hostPath = hostPath;
        _guestPath = guestPath;
        _isReadOnly = isReadOnly;
    }

    public string HostPath
    {
        get => _hostPath;
        set => SetProperty(ref _hostPath, value ?? string.Empty);
    }

    public string GuestPath
    {
        get => _guestPath;
        set
        {
            if (SetProperty(ref _guestPath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(RemoveAccessibleName));
            }
        }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (SetProperty(ref _isReadOnly, value))
            {
                OnPropertyChanged(nameof(IsReadWrite));
            }
        }
    }

    /// <summary>The same access level from the other segment of the picker.</summary>
    public bool IsReadWrite
    {
        get => !_isReadOnly;
        set => IsReadOnly = !value;
    }

    public string RemoveAccessibleName => string.IsNullOrWhiteSpace(GuestPath)
        ? "Remove host mount"
        : $"Remove host mount at {GuestPath}";

    internal WorkspaceIsolationMountDefinition Build() => new(
        HostPath.Trim(),
        GuestPath.Trim(),
        IsReadOnly);
}
