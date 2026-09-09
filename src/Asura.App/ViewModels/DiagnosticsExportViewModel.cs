using System.Globalization;
using Asura.Application;

namespace Asura.App.ViewModels;

public enum DiagnosticsExportStatus
{
    Idle,
    ChoosingDestination,
    Collecting,
    Exporting,
    Success,
    Cancelled,
    Error,
}

public sealed record DiagnosticsRedactionOption(
    DiagnosticsRedactionLevel Level,
    string Name,
    string Description);

/// <summary>
/// Runs the user-owned diagnostics export workflow without knowing about native dialogs, host file
/// managers, or concrete artifact collectors. Every external boundary is injected and failures are
/// mapped to sanitized presentation state.
/// </summary>
public sealed class DiagnosticsExportViewModel : ObservableObject
{
    private readonly IDiagnosticsBundleExporter _exporter;
    private readonly IDiagnosticsBundleRequestSource _requestSource;
    private readonly IDiagnosticsBundleDestinationPicker _destinationPicker;
    private readonly IDiagnosticsArtifactPresenter _artifactPresenter;
    private readonly TimeProvider _timeProvider;
    private readonly object _exportSync = new();
    private DiagnosticsRedactionOption _selectedRedactionOption;
    private CancellationTokenSource? _activeExport;
    private DiagnosticsExportStatus _status;
    private string _statusMessage = string.Empty;
    private DiagnosticsBundleReceipt? _receipt;
    private DiagnosticsGeneratedArtifact? _lastArtifact;
    private bool _isExporting;
    private bool _isArtifactActionInProgress;

    public DiagnosticsExportViewModel(
        IDiagnosticsBundleExporter exporter,
        IDiagnosticsBundleRequestSource requestSource,
        IDiagnosticsBundleDestinationPicker destinationPicker,
        IDiagnosticsArtifactPresenter artifactPresenter,
        TimeProvider? timeProvider = null)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _requestSource = requestSource ?? throw new ArgumentNullException(nameof(requestSource));
        _destinationPicker = destinationPicker
            ?? throw new ArgumentNullException(nameof(destinationPicker));
        _artifactPresenter = artifactPresenter
            ?? throw new ArgumentNullException(nameof(artifactPresenter));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var supported = requestSource.SupportedRedactionLevels
            ?? throw new ArgumentException(
                "The diagnostics source did not declare supported redaction levels.",
                nameof(requestSource));
        var supportedSet = supported
            .Where(Enum.IsDefined)
            .ToHashSet();
        if (!supportedSet.Contains(DiagnosticsRedactionLevel.Safe))
        {
            throw new ArgumentException(
                "Diagnostics export must support the safe redaction level.",
                nameof(requestSource));
        }

        var options = new List<DiagnosticsRedactionOption>
        {
            new(
                DiagnosticsRedactionLevel.Safe,
                "Safe summary",
                "Exports component status and performance summaries with the least diagnostic detail."),
        };
        if (supportedSet.Contains(DiagnosticsRedactionLevel.Full))
        {
            options.Add(new DiagnosticsRedactionOption(
                DiagnosticsRedactionLevel.Full,
                "Full diagnostics",
                "Adds eligible application logs and crash reports; mandatory secret filtering still applies."));
        }

        RedactionOptions = options.AsReadOnly();
        _selectedRedactionOption = RedactionOptions[0];
    }

    public IReadOnlyList<DiagnosticsRedactionOption> RedactionOptions { get; }

    public DiagnosticsRedactionOption SelectedRedactionOption
    {
        get => _selectedRedactionOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!RedactionOptions.Contains(value))
            {
                throw new ArgumentException(
                    "The selected diagnostics redaction level is not supported.",
                    nameof(value));
            }

            if (SetProperty(ref _selectedRedactionOption, value))
            {
                OnPropertyChanged(nameof(SelectedRedactionDescription));
            }
        }
    }

    public string SelectedRedactionDescription => SelectedRedactionOption.Description;

    public bool CanChooseRedactionLevel => RedactionOptions.Count > 1 && !IsExporting;

    public string RedactionAvailabilityMessage => RedactionOptions.Count > 1
        ? "Choose how much eligible diagnostic detail to include. Secret filtering is always enforced."
        : "This installation supports safe-summary export only. Secret filtering is always enforced.";

    public DiagnosticsExportStatus Status => _status;

    public string StatusLabel => Status switch
    {
        DiagnosticsExportStatus.ChoosingDestination => "Choosing",
        DiagnosticsExportStatus.Collecting => "Collecting",
        DiagnosticsExportStatus.Exporting => "Exporting",
        DiagnosticsExportStatus.Success => "Created",
        DiagnosticsExportStatus.Cancelled => "Cancelled",
        DiagnosticsExportStatus.Error => "Error",
        _ => string.Empty,
    };

    public string StatusMessage => _statusMessage;

    public string StatusAutomationName => string.IsNullOrEmpty(StatusMessage)
        ? "Diagnostics export status"
        : $"Diagnostics export status: {StatusMessage}";

    public bool HasStatus => Status != DiagnosticsExportStatus.Idle;

    public bool IsExporting => _isExporting;

    public bool IsError => Status == DiagnosticsExportStatus.Error;

    public bool IsSuccess => Status == DiagnosticsExportStatus.Success;

    public bool IsWarning => Status is DiagnosticsExportStatus.Error
        or DiagnosticsExportStatus.Cancelled;

    public bool CanExport => !IsExporting && !_isArtifactActionInProgress;

    public bool CanCancel
    {
        get
        {
            lock (_exportSync)
            {
                return _activeExport is not null;
            }
        }
    }

    public DiagnosticsBundleReceipt? Receipt => _receipt;

    public DiagnosticsGeneratedArtifact? LastArtifact => _lastArtifact;

    public bool HasArtifact => LastArtifact is not null;

    public string ArtifactDisplayName => LastArtifact?.DisplayName ?? string.Empty;

    public string ReceiptSummary => Receipt is null
        ? string.Empty
        : $"{Receipt.ArtifactCount} artifact{(Receipt.ArtifactCount == 1 ? string.Empty : "s")} · "
            + $"{FormatBytes(Receipt.ArchiveBytes)} archive";

    public string DigestSummary => Receipt is null ? string.Empty : $"SHA-256 {Receipt.Sha256}";

    public bool CanOpenArtifact => CanPresent(DiagnosticsArtifactPresentationCapabilities.Open);

    public bool CanRevealArtifact => CanPresent(DiagnosticsArtifactPresentationCapabilities.Reveal);

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_exportSync)
        {
            if (_isExporting)
            {
                return;
            }

            _isExporting = true;
        }

        PublishAvailability();
        try
        {
            SetStatus(
                DiagnosticsExportStatus.ChoosingDestination,
                "Complete or dismiss the system save dialog to continue.");
            operation.Token.ThrowIfCancellationRequested();
            var destination = await _destinationPicker.PickAsync(CreateSuggestedFileName());
            if (destination is null)
            {
                SetStatus(DiagnosticsExportStatus.Cancelled, "Diagnostics export was cancelled.");
                return;
            }

            await using (destination.ConfigureAwait(false))
            {
                operation.Token.ThrowIfCancellationRequested();
                lock (_exportSync)
                {
                    _activeExport = operation;
                }

                SetStatus(
                    DiagnosticsExportStatus.Collecting,
                    "Collecting the selected diagnostics.");
                var request = await _requestSource.CreateRequestAsync(
                    SelectedRedactionOption.Level,
                    operation.Token);

                SetStatus(
                    DiagnosticsExportStatus.Exporting,
                    "Reviewing and writing the diagnostics bundle.");
                var result = await _exporter.ExportAsync(
                    request,
                    destination.Content,
                    operation.Token);
                if (!result.IsSuccess)
                {
                    ApplyExportFailure(result.Error!);
                    return;
                }

                await destination.CompleteAsync(operation.Token);
                SetReceipt(result.Value!, destination.Artifact);
                SetStatus(
                    DiagnosticsExportStatus.Success,
                    $"Diagnostics bundle {destination.Artifact.DisplayName} was created.");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            SetStatus(DiagnosticsExportStatus.Cancelled, "Diagnostics export was cancelled.");
        }
        catch (Exception)
        {
            SetStatus(
                DiagnosticsExportStatus.Error,
                "The diagnostics bundle could not be created. No diagnostic details were exposed.");
        }
        finally
        {
            lock (_exportSync)
            {
                _activeExport = null;
                _isExporting = false;
            }

            PublishAvailability();
        }
    }

    public bool TryCancelExport()
    {
        lock (_exportSync)
        {
            if (_activeExport is null)
            {
                return false;
            }

            _activeExport.Cancel();
            return true;
        }
    }

    public Task OpenArtifactAsync(CancellationToken cancellationToken = default) =>
        PresentArtifactAsync(DiagnosticsArtifactPresentationAction.Open, cancellationToken);

    public Task RevealArtifactAsync(CancellationToken cancellationToken = default) =>
        PresentArtifactAsync(DiagnosticsArtifactPresentationAction.Reveal, cancellationToken);

    private async Task PresentArtifactAsync(
        DiagnosticsArtifactPresentationAction action,
        CancellationToken cancellationToken)
    {
        var requiredCapability = action == DiagnosticsArtifactPresentationAction.Open
            ? DiagnosticsArtifactPresentationCapabilities.Open
            : DiagnosticsArtifactPresentationCapabilities.Reveal;
        if (!CanPresent(requiredCapability) || LastArtifact is not { } artifact)
        {
            return;
        }

        _isArtifactActionInProgress = true;
        PublishAvailability();
        try
        {
            var result = await _artifactPresenter.PresentAsync(
                artifact,
                action,
                cancellationToken);
            SetStatus(result switch
            {
                DiagnosticsArtifactPresentationResult.Presented =>
                    DiagnosticsExportStatus.Success,
                DiagnosticsArtifactPresentationResult.Unsupported or
                    DiagnosticsArtifactPresentationResult.Failed =>
                    DiagnosticsExportStatus.Error,
                _ => DiagnosticsExportStatus.Error,
            }, result switch
            {
                DiagnosticsArtifactPresentationResult.Presented when
                    action == DiagnosticsArtifactPresentationAction.Open =>
                    "Opened the last diagnostics bundle.",
                DiagnosticsArtifactPresentationResult.Presented =>
                    "Revealed the last diagnostics bundle in the file manager.",
                DiagnosticsArtifactPresentationResult.Unsupported =>
                    "This platform cannot perform that diagnostics bundle action.",
                _ => "The diagnostics bundle could not be shown.",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(DiagnosticsExportStatus.Cancelled, "The bundle action was cancelled.");
        }
        catch (Exception)
        {
            SetStatus(DiagnosticsExportStatus.Error, "The diagnostics bundle could not be shown.");
        }
        finally
        {
            _isArtifactActionInProgress = false;
            PublishAvailability();
        }
    }

    private void ApplyExportFailure(DiagnosticsBundleError error)
    {
        if (error.Code == DiagnosticsBundleErrorCode.Cancelled)
        {
            SetStatus(DiagnosticsExportStatus.Cancelled, "Diagnostics export was cancelled.");
            return;
        }

        var message = error.Code switch
        {
            DiagnosticsBundleErrorCode.UnsafeContent =>
                "Export stopped because the diagnostics did not pass the mandatory safety review.",
            DiagnosticsBundleErrorCode.DestinationUnavailable =>
                "The selected diagnostics destination is unavailable or not writable.",
            DiagnosticsBundleErrorCode.ArtifactTooLarge or
                DiagnosticsBundleErrorCode.BundleTooLarge or
                DiagnosticsBundleErrorCode.TooManyArtifacts =>
                "The selected diagnostics exceed the safe export size limits.",
            DiagnosticsBundleErrorCode.InvalidRequest or
                DiagnosticsBundleErrorCode.InvalidPath or
                DiagnosticsBundleErrorCode.DuplicatePath =>
                "The diagnostics source produced an invalid export request.",
            _ => "The diagnostics bundle could not be created.",
        };
        SetStatus(DiagnosticsExportStatus.Error, message);
    }

    private bool CanPresent(DiagnosticsArtifactPresentationCapabilities capability) =>
        LastArtifact is not null
        && !IsExporting
        && !_isArtifactActionInProgress
        && (_artifactPresenter.Capabilities & capability) == capability;

    private void SetReceipt(
        DiagnosticsBundleReceipt receipt,
        DiagnosticsGeneratedArtifact artifact)
    {
        _receipt = receipt;
        _lastArtifact = artifact;
        OnPropertyChanged(nameof(Receipt));
        OnPropertyChanged(nameof(LastArtifact));
        OnPropertyChanged(nameof(HasArtifact));
        OnPropertyChanged(nameof(ArtifactDisplayName));
        OnPropertyChanged(nameof(ReceiptSummary));
        OnPropertyChanged(nameof(DigestSummary));
        OnPropertyChanged(nameof(CanOpenArtifact));
        OnPropertyChanged(nameof(CanRevealArtifact));
    }

    private void SetStatus(DiagnosticsExportStatus status, string message)
    {
        _status = status;
        _statusMessage = message;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(StatusAutomationName));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsExporting));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsWarning));
        PublishAvailability();
    }

    private void PublishAvailability()
    {
        OnPropertyChanged(nameof(IsExporting));
        OnPropertyChanged(nameof(CanChooseRedactionLevel));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanOpenArtifact));
        OnPropertyChanged(nameof(CanRevealArtifact));
    }

    private string CreateSuggestedFileName() =>
        $"asura-diagnostics-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.zip";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var kibibytes = bytes / 1024d;
        if (kibibytes < 1024)
        {
            return $"{kibibytes.ToString("0.#", CultureInfo.InvariantCulture)} KiB";
        }

        return $"{(kibibytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} MiB";
    }
}
