using Asura.Application;

namespace Asura.App.ViewModels;

public sealed class FileTransferEditorViewModel : ObservableObject
{
    private FileProviderProfileDescriptor _selectedDestinationProfile;
    private string _destination;
    private FilePanelTransferOperation _operation = FilePanelTransferOperation.Copy;
    private FilePanelConflictPolicy _conflictPolicy = FilePanelConflictPolicy.Fail;

    public FileTransferEditorViewModel(
        FilePanelEntry source,
        IReadOnlyList<FileProviderProfileDescriptor> profiles,
        string? preferredProfileId = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one destination provider is required.", nameof(profiles));
        }

        Profiles = Array.AsReadOnly(profiles.ToArray());
        _selectedDestinationProfile = Profiles
            .FirstOrDefault(profile => string.Equals(profile.Id, preferredProfileId, StringComparison.Ordinal))
            ?? Profiles[0];
        _destination = FileLocationPresentation.ChildDisplay(
            _selectedDestinationProfile,
            source.Name);
    }

    public FilePanelEntry Source { get; }

    public string SourceLocation => FileLocationPresentation.Display(Source.Location);

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

    public IReadOnlyList<FilePanelTransferOperation> Operations { get; } =
        Enum.GetValues<FilePanelTransferOperation>();

    public IReadOnlyList<FilePanelConflictPolicy> ConflictPolicies { get; } =
        Enum.GetValues<FilePanelConflictPolicy>();

    public FileProviderProfileDescriptor SelectedDestinationProfile
    {
        get => _selectedDestinationProfile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Profiles.Any(profile => string.Equals(profile.Id, value.Id, StringComparison.Ordinal)))
            {
                throw new ArgumentException("The destination profile is not available.", nameof(value));
            }

            if (SetProperty(ref _selectedDestinationProfile, value))
            {
                Destination = FileLocationPresentation.ChildDisplay(value, Source.Name);
            }
        }
    }

    public string Destination
    {
        get => _destination;
        set => SetProperty(ref _destination, value);
    }

    public FilePanelTransferOperation Operation
    {
        get => _operation;
        set => SetProperty(ref _operation, value);
    }

    public FilePanelConflictPolicy ConflictPolicy
    {
        get => _conflictPolicy;
        set => SetProperty(ref _conflictPolicy, value);
    }

    public FilePanelTransferRequest CreateRequest()
    {
        var destination = FileLocationPresentation.Parse(
            SelectedDestinationProfile,
            Destination);
        if (destination == Source.Location.WithVersion(null))
        {
            throw new ArgumentException("Choose a destination different from the source.");
        }

        return new FilePanelTransferRequest(
            Source.Location,
            destination,
            Operation,
            ConflictPolicy);
    }
}
