using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// The three families of saved connections the shell manages through one UI:
/// terminal endpoints, file-transfer providers, and database connections.
/// </summary>
public enum SavedConnectionFamily
{
    Terminal,
    Files,
    Database,
}

/// <summary>
/// One entry in the unified connection-type selector. Exactly one of the
/// family-specific payloads is set. The Git group belongs to the terminal
/// family: it reuses the Local and SSH endpoint editors, adds a repository
/// path, and saves a profile whose preferred panel is Git.
/// </summary>
public sealed record UnifiedConnectionTypeOption(
    SavedConnectionFamily Family,
    string Group,
    string Label,
    ConnectionKind? TerminalKind = null,
    FileProviderKind? FileKind = null,
    string? DatabaseDriverId = null,
    bool GitRepository = false)
{
    public string DisplayName => $"{Group} · {Label}";
}

/// <summary>What the unified editor produced, discriminated by family.</summary>
public abstract record UnifiedConnectionEditorResult
{
    private UnifiedConnectionEditorResult()
    {
    }

    /// <summary>
    /// A terminal profile. <paramref name="SaveConnection"/> is false only for
    /// the connect purpose with its save checkbox cleared: the profile opens a
    /// session but is not persisted.
    /// </summary>
    public sealed record Terminal(
        ConnectionEditorSaveRequest Request,
        bool SaveConnection) : UnifiedConnectionEditorResult;

    public sealed record Files(
        FileProviderProfileSaveRequest Request) : UnifiedConnectionEditorResult;

    /// <summary>
    /// A database profile. <paramref name="SaveConnection"/> mirrors the
    /// terminal purpose: false connects once without persisting.
    /// </summary>
    public sealed record Database(
        DatabaseConnectionSaveRequest Request,
        bool SaveConnection = true) : UnifiedConnectionEditorResult;
}

/// <summary>
/// Hosts the three family editors behind one name field and one grouped
/// connection-type selector. Families whose runtime is unavailable in the
/// current build simply do not contribute options.
/// </summary>
public sealed class UnifiedConnectionEditorViewModel : ObservableObject
{
    private UnifiedConnectionTypeOption _selectedType;
    private bool _hasTestFeedback;

    public UnifiedConnectionEditorViewModel(
        ConnectionEditorViewModel terminal,
        FileProviderProfileEditorViewModel? files,
        DatabaseConnectionEditorViewModel? database,
        SavedConnectionFamily? lockedFamily = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        Files = files;
        Database = database;
        LockedFamily = lockedFamily;
        TypeOptions = BuildTypeOptions(lockedFamily);
        if (TypeOptions.Count == 0)
        {
            throw new InvalidOperationException(
                "No connection family is available for this editor.");
        }

        _selectedType = InitialOption(lockedFamily ?? initialFamily);
        Terminal.PropertyChanged += OnFamilyEditorPropertyChanged;
        Files?.PropertyChanged += OnFamilyEditorPropertyChanged;

        Database?.PropertyChanged += OnFamilyEditorPropertyChanged;
    }

    public ConnectionEditorViewModel Terminal { get; }

    public FileProviderProfileEditorViewModel? Files { get; }

    public DatabaseConnectionEditorViewModel? Database { get; }

    /// <summary>Editing an existing definition pins the editor to its family.</summary>
    public SavedConnectionFamily? LockedFamily { get; }

    public IReadOnlyList<UnifiedConnectionTypeOption> TypeOptions { get; }

    public UnifiedConnectionTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value) || value is null)
            {
                return;
            }

            ApplyOption(value);
            OnPropertyChanged(nameof(IsTerminal));
            OnPropertyChanged(nameof(IsFiles));
            OnPropertyChanged(nameof(IsDatabase));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(TestLabel));
            _hasTestFeedback = false;
            OnPropertyChanged(nameof(HasTestFeedback));
            OnPropertyChanged(nameof(TestStatus));
            OnPropertyChanged(nameof(TestDetail));
            OnPropertyChanged(nameof(TestFooterHint));
            OnPropertyChanged(nameof(IsTesting));
            OnPropertyChanged(nameof(Name));
        }
    }

    public SavedConnectionFamily Family => SelectedType.Family;

    public bool IsTerminal => Family == SavedConnectionFamily.Terminal;

    public bool IsFiles => Family == SavedConnectionFamily.Files;

    public bool IsDatabase => Family == SavedConnectionFamily.Database;

    public bool CanTest => true;

    public bool IsTesting => Family switch
    {
        SavedConnectionFamily.Terminal => Terminal.IsTesting,
        SavedConnectionFamily.Files => Files?.IsTesting == true,
        SavedConnectionFamily.Database => Database?.IsTesting == true,
        _ => false,
    };

    public bool HasTestFeedback => _hasTestFeedback;

    public string TestStatus => Family switch
    {
        SavedConnectionFamily.Terminal => Terminal.TestStatus,
        SavedConnectionFamily.Files => Files?.TestStatus ?? string.Empty,
        SavedConnectionFamily.Database => Database?.TestStatus ?? string.Empty,
        _ => string.Empty,
    };

    public string TestDetail => Family switch
    {
        SavedConnectionFamily.Terminal => Terminal.TestDetail,
        SavedConnectionFamily.Files => Files?.TestDetail ?? string.Empty,
        SavedConnectionFamily.Database => Database?.TestDetail ?? string.Empty,
        _ => string.Empty,
    };

    public string TestFooterHint => HasTestFeedback
        ? $"{TestStatus}: {TestDetail}"
        : "Opening revalidates runtime, credentials, and platform support.";

    public string TestLabel => IsTesting
        ? "Testing…"
        : IsTerminal ? "Run diagnostics" : "Test";

    /// <summary>
    /// Families whose connect purpose can open without persisting. Files
    /// panels bind to a stored profile, so they always save.
    /// </summary>
    public bool SupportsUnsavedConnect => IsTerminal || IsDatabase;

    public bool IsEditing => LockedFamily switch
    {
        SavedConnectionFamily.Terminal => Terminal.IsEditing,
        SavedConnectionFamily.Files => Files?.IsEditing == true,
        SavedConnectionFamily.Database => Database?.IsEditing == true,
        _ => false,
    };

    public string EditorTitle => IsEditing ? "Edit connection" : "New connection";

    /// <summary>
    /// One name for whichever definition the dialog produces. Writing through
    /// to every family keeps the typed name when the type selection switches
    /// families.
    /// </summary>
    public string Name
    {
        get => Family switch
        {
            SavedConnectionFamily.Files => Files?.Name ?? string.Empty,
            SavedConnectionFamily.Database => Database?.Name ?? string.Empty,
            _ => Terminal.Name,
        };
        set
        {
            Terminal.Name = value;
            Files?.Name = value;

            Database?.Name = value;

            OnPropertyChanged();
        }
    }

    public UnifiedConnectionEditorResult CreateSaveResult(bool saveConnection = true) =>
        Family switch
        {
            SavedConnectionFamily.Terminal => new UnifiedConnectionEditorResult.Terminal(
                Terminal.CreateSaveRequest(),
                saveConnection),
            SavedConnectionFamily.Files => new UnifiedConnectionEditorResult.Files(
                Files!.CreateSaveRequest()),
            SavedConnectionFamily.Database => new UnifiedConnectionEditorResult.Database(
                Database!.CreateSaveRequest(),
                saveConnection),
            _ => throw new ArgumentOutOfRangeException(nameof(Family), Family, null),
        };

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
        {
            return;
        }

        _hasTestFeedback = true;
        OnTestStateChanged();
        try
        {
            if (IsTerminal)
            {
                await Terminal.TestAsync(cancellationToken);
            }
            else if (IsFiles && Files is not null)
            {
                await Files.TestAsync(cancellationToken);
            }
            else if (IsDatabase && Database is not null)
            {
                await Database.TestAsync(cancellationToken);
            }
        }
        finally
        {
            OnTestStateChanged();
        }
    }

    private void ApplyOption(UnifiedConnectionTypeOption option)
    {
        switch (option.Family)
        {
            case SavedConnectionFamily.Terminal when option.TerminalKind is { } kind:
                Terminal.Kind = kind;
                Terminal.OpensGitRepository = option.GitRepository;
                break;
            case SavedConnectionFamily.Files when Files is not null
                && option.FileKind is { } kind:
                Files.Kind = kind;
                break;
            case SavedConnectionFamily.Database when Database is not null
                && option.DatabaseDriverId is { } driverId:
                Database.SelectedDriver = Database.Drivers
                    .First(item => string.Equals(item.Id, driverId, StringComparison.Ordinal));
                break;
        }
    }

    private void OnFamilyEditorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is nameof(ConnectionEditorViewModel.IsTesting)
            or nameof(ConnectionEditorViewModel.TestStatus)
            or nameof(ConnectionEditorViewModel.TestDetail))
        {
            OnTestStateChanged();
        }
    }

    private void OnTestStateChanged()
    {
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(HasTestFeedback));
        OnPropertyChanged(nameof(TestStatus));
        OnPropertyChanged(nameof(TestDetail));
        OnPropertyChanged(nameof(TestFooterHint));
        OnPropertyChanged(nameof(TestLabel));
    }

    private UnifiedConnectionTypeOption InitialOption(SavedConnectionFamily family)
    {
        var candidates = TypeOptions.Where(item => item.Family == family).ToArray();
        if (candidates.Length == 0)
        {
            return TypeOptions[0];
        }

        return family switch
        {
            SavedConnectionFamily.Terminal => candidates
                .FirstOrDefault(item => item.TerminalKind == Terminal.Kind
                    && item.GitRepository == Terminal.OpensGitRepository)
                ?? candidates[0],
            SavedConnectionFamily.Files => candidates
                .FirstOrDefault(item => item.FileKind == Files!.Kind)
                ?? candidates[0],
            SavedConnectionFamily.Database => candidates
                .FirstOrDefault(item => string.Equals(item.DatabaseDriverId, Database!.SelectedDriver.Id, StringComparison.Ordinal))
                ?? candidates[0],
            _ => candidates[0],
        };
    }

    private IReadOnlyList<UnifiedConnectionTypeOption> BuildTypeOptions(
        SavedConnectionFamily? lockedFamily)
    {
        var options = new List<UnifiedConnectionTypeOption>();
        if (lockedFamily is null or SavedConnectionFamily.Terminal)
        {
            options.AddRange(
            [
                new(SavedConnectionFamily.Terminal, "Terminal", "Local", ConnectionKind.Local),
                new(SavedConnectionFamily.Terminal, "Terminal", "SSH", ConnectionKind.Ssh),
                new(SavedConnectionFamily.Terminal, "Terminal", "Docker", ConnectionKind.Docker),
                new(SavedConnectionFamily.Terminal, "Terminal", "WSL", ConnectionKind.Wsl),
                new(SavedConnectionFamily.Terminal, "Git", "Local repository", ConnectionKind.Local, GitRepository: true),
                new(SavedConnectionFamily.Terminal, "Git", "SSH", ConnectionKind.Ssh, GitRepository: true),
            ]);
        }

        if (Files is not null && lockedFamily is null or SavedConnectionFamily.Files)
        {
            options.AddRange(
            [
                new(SavedConnectionFamily.Files, "Files", "Local folder", FileKind: FileProviderKind.Local),
                new(SavedConnectionFamily.Files, "Files", "SFTP", FileKind: FileProviderKind.Sftp),
                new(SavedConnectionFamily.Files, "Files", "FTP / FTPS", FileKind: FileProviderKind.Ftp),
                new(SavedConnectionFamily.Files, "Files", "S3", FileKind: FileProviderKind.S3),
                new(SavedConnectionFamily.Files, "Files", "SMB", FileKind: FileProviderKind.Smb),
                new(SavedConnectionFamily.Files, "Files", "WebDAV", FileKind: FileProviderKind.WebDav),
            ]);
        }

        if (Database is not null && lockedFamily is null or SavedConnectionFamily.Database)
        {
            options.AddRange(Database.Drivers.Select(driver =>
                new UnifiedConnectionTypeOption(
                    SavedConnectionFamily.Database,
                    "Database",
                    driver.DisplayName,
                    DatabaseDriverId: driver.Id)));
        }

        return options.AsReadOnly();
    }
}
