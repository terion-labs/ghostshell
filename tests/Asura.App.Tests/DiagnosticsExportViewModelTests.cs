using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

public sealed class DiagnosticsExportViewModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 15, 30, 45, TimeSpan.Zero);

    [Fact]
    public void SafeRedactionIsMandatoryAndFullIsOfferedOnlyWhenSupported()
    {
        var safeOnly = CreateViewModel(new RecordingRequestSource(
            [DiagnosticsRedactionLevel.Safe]));
        var full = CreateViewModel(new RecordingRequestSource(
            [DiagnosticsRedactionLevel.Safe, DiagnosticsRedactionLevel.Full]));

        var safeOption = Assert.Single(safeOnly.RedactionOptions);
        Assert.Equal(DiagnosticsRedactionLevel.Safe, safeOption.Level);
        Assert.False(safeOnly.CanChooseRedactionLevel);
        Assert.Contains("safe-summary export only", safeOnly.RedactionAvailabilityMessage);
        Assert.Equal(2, full.RedactionOptions.Count);
        Assert.True(full.CanChooseRedactionLevel);
        Assert.Throws<ArgumentException>(() => CreateViewModel(
            new RecordingRequestSource([DiagnosticsRedactionLevel.Full])));
    }

    [Fact]
    public async Task SuccessfulExportUsesSelectedLevelCompletesDestinationAndEnablesPresentation()
    {
        var source = new RecordingRequestSource(
            [DiagnosticsRedactionLevel.Safe, DiagnosticsRedactionLevel.Full]);
        var destination = new RecordingDestination();
        var picker = new RecordingDestinationPicker(destination);
        var exporter = new RecordingExporter();
        var presenter = new RecordingPresenter(
            DiagnosticsArtifactPresentationCapabilities.Open
            | DiagnosticsArtifactPresentationCapabilities.Reveal);
        var viewModel = CreateViewModel(source, picker, exporter, presenter);
        viewModel.SelectedRedactionOption = viewModel.RedactionOptions.Single(option =>
            option.Level == DiagnosticsRedactionLevel.Full);

        await viewModel.ExportAsync();

        Assert.Equal([DiagnosticsRedactionLevel.Full], source.RequestedLevels);
        Assert.Same(source.Request, exporter.Request);
        Assert.Same(destination.Content, exporter.Destination);
        Assert.Equal("asura-diagnostics-20260722-153045.zip", picker.SuggestedFileName);
        Assert.True(destination.Completed);
        Assert.True(destination.Disposed);
        Assert.Equal(DiagnosticsExportStatus.Success, viewModel.Status);
        Assert.Same(destination.Artifact, viewModel.LastArtifact);
        Assert.True(viewModel.CanOpenArtifact);
        Assert.True(viewModel.CanRevealArtifact);
        Assert.Contains("2 artifacts", viewModel.ReceiptSummary);

        await viewModel.OpenArtifactAsync();
        await viewModel.RevealArtifactAsync();

        Assert.Equal(
            [
                DiagnosticsArtifactPresentationAction.Open,
                DiagnosticsArtifactPresentationAction.Reveal,
            ],
            presenter.Actions);
    }

    [Fact]
    public async Task ExportCompletionPublishesTheFinalBusyState()
    {
        var viewModel = CreateViewModel(
            new RecordingRequestSource([DiagnosticsRedactionLevel.Safe]));
        var busyStates = new List<bool>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.PropertyName, nameof(DiagnosticsExportViewModel.IsExporting), StringComparison.Ordinal))
            {
                busyStates.Add(viewModel.IsExporting);
            }
        };

        await viewModel.ExportAsync();

        Assert.Contains(true, busyStates);
        Assert.False(busyStates[^1]);
    }

    [Fact]
    public async Task DismissingDestinationPickerCancelsWithoutCollectingOrExporting()
    {
        var source = new RecordingRequestSource([DiagnosticsRedactionLevel.Safe]);
        var exporter = new RecordingExporter();
        var viewModel = CreateViewModel(
            source,
            new RecordingDestinationPicker(null),
            exporter);

        await viewModel.ExportAsync();

        Assert.Equal(DiagnosticsExportStatus.Cancelled, viewModel.Status);
        Assert.Empty(source.RequestedLevels);
        Assert.Equal(0, exporter.Calls);
        Assert.True(viewModel.CanExport);
    }

    [Fact]
    public async Task DestinationSelectionIsBusyButDoesNotOfferFalseCancellation()
    {
        var source = new RecordingRequestSource([DiagnosticsRedactionLevel.Safe])
        {
            WaitForCancellation = true,
        };
        var destination = new RecordingDestination();
        var picker = new DeferredDestinationPicker();
        var viewModel = CreateViewModel(source, picker);

        var export = viewModel.ExportAsync();
        await picker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DiagnosticsExportStatus.ChoosingDestination, viewModel.Status);
        Assert.True(viewModel.IsExporting);
        Assert.False(viewModel.CanExport);
        Assert.False(viewModel.CanCancel);
        Assert.False(viewModel.TryCancelExport());
        Assert.False(export.IsCompleted);

        picker.Complete(destination);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.CanCancel);
        Assert.True(viewModel.TryCancelExport());
        await export;

        Assert.Equal(DiagnosticsExportStatus.Cancelled, viewModel.Status);
        Assert.True(destination.Disposed);
    }

    [Fact]
    public async Task CallerCancellationDuringDestinationSelectionIsObservedAfterDialogCloses()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new RecordingRequestSource([DiagnosticsRedactionLevel.Safe]);
        var destination = new RecordingDestination();
        var picker = new DeferredDestinationPicker();
        var viewModel = CreateViewModel(source, picker);

        var export = viewModel.ExportAsync(cancellation.Token);
        await picker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.False(export.IsCompleted);
        Assert.False(viewModel.CanCancel);

        picker.Complete(destination);
        await export;

        Assert.Equal(DiagnosticsExportStatus.Cancelled, viewModel.Status);
        Assert.Empty(source.RequestedLevels);
        Assert.True(destination.Disposed);
    }

    [Fact]
    public async Task UnsafeExporterFailureIsMappedWithoutEchoingItsMessage()
    {
        const string unsafeMessage = "secret-canary-from-exporter";
        var destination = new RecordingDestination();
        var exporter = new RecordingExporter
        {
            Result = DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Failure(
                new DiagnosticsBundleError(
                    DiagnosticsBundleErrorCode.UnsafeContent,
                    unsafeMessage)),
        };
        var viewModel = CreateViewModel(
            new RecordingRequestSource([DiagnosticsRedactionLevel.Safe]),
            new RecordingDestinationPicker(destination),
            exporter);

        await viewModel.ExportAsync();

        Assert.Equal(DiagnosticsExportStatus.Error, viewModel.Status);
        Assert.Contains("mandatory safety review", viewModel.StatusMessage);
        Assert.DoesNotContain(unsafeMessage, viewModel.StatusMessage);
        Assert.False(destination.Completed);
        Assert.Null(viewModel.LastArtifact);
    }

    [Fact]
    public async Task CancelStopsCollectionAndReturnsToAnExportableState()
    {
        var source = new RecordingRequestSource([DiagnosticsRedactionLevel.Safe])
        {
            WaitForCancellation = true,
        };
        var viewModel = CreateViewModel(
            source,
            new RecordingDestinationPicker(new RecordingDestination()));

        var export = viewModel.ExportAsync();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.TryCancelExport());
        await export;

        Assert.Equal(DiagnosticsExportStatus.Cancelled, viewModel.Status);
        Assert.True(viewModel.CanExport);
        Assert.False(viewModel.CanCancel);
    }

    [Fact]
    public async Task BoundaryExceptionsBecomeSanitizedErrorState()
    {
        const string boundaryMessage = "private-path-canary";
        var source = new RecordingRequestSource([DiagnosticsRedactionLevel.Safe])
        {
            Exception = new IOException(boundaryMessage),
        };
        var viewModel = CreateViewModel(
            source,
            new RecordingDestinationPicker(new RecordingDestination()));

        await viewModel.ExportAsync();

        Assert.Equal(DiagnosticsExportStatus.Error, viewModel.Status);
        Assert.DoesNotContain(boundaryMessage, viewModel.StatusMessage);
        Assert.Contains("No diagnostic details were exposed", viewModel.StatusMessage);
    }

    private static DiagnosticsExportViewModel CreateViewModel(
        RecordingRequestSource source,
        IDiagnosticsBundleDestinationPicker? picker = null,
        RecordingExporter? exporter = null,
        RecordingPresenter? presenter = null) =>
        new(
            exporter ?? new RecordingExporter(),
            source,
            picker ?? new RecordingDestinationPicker(new RecordingDestination()),
            presenter ?? new RecordingPresenter(DiagnosticsArtifactPresentationCapabilities.None),
            new FixedTimeProvider(Now));

    private sealed class RecordingRequestSource(
        IReadOnlyList<DiagnosticsRedactionLevel> supported)
        : IDiagnosticsBundleRequestSource
    {
        public IReadOnlyList<DiagnosticsRedactionLevel> SupportedRedactionLevels { get; } = supported;

        public DiagnosticsBundleRequest Request { get; } = new(
            new DiagnosticsBundleMetadata(
                ProductIdentity.DisplayName,
                ProductIdentity.BundleIdentifier,
                ProductIdentity.ExecutableName,
                "0.1.0",
                ".NET 10",
                "Test OS",
                "arm64",
                Now),
            []);

        public List<DiagnosticsRedactionLevel> RequestedLevels { get; } = [];

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitForCancellation { get; init; }

        public Exception? Exception { get; init; }

        public async ValueTask<DiagnosticsBundleRequest> CreateRequestAsync(
            DiagnosticsRedactionLevel redactionLevel,
            CancellationToken cancellationToken)
        {
            RequestedLevels.Add(redactionLevel);
            Started.TrySetResult();
            if (Exception is not null)
            {
                throw Exception;
            }

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Request;
        }
    }

    private sealed class RecordingDestinationPicker(IDiagnosticsBundleDestination? destination)
        : IDiagnosticsBundleDestinationPicker
    {
        public string? SuggestedFileName { get; private set; }

        public ValueTask<IDiagnosticsBundleDestination?> PickAsync(string suggestedFileName)
        {
            SuggestedFileName = suggestedFileName;
            return ValueTask.FromResult(destination);
        }
    }

    private sealed class DeferredDestinationPicker : IDiagnosticsBundleDestinationPicker
    {
        private readonly TaskCompletionSource<IDiagnosticsBundleDestination?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IDiagnosticsBundleDestination?> PickAsync(string suggestedFileName)
        {
            _ = suggestedFileName;
            Started.TrySetResult();
            return new ValueTask<IDiagnosticsBundleDestination?>(_completion.Task);
        }

        public void Complete(IDiagnosticsBundleDestination? destination) =>
            _completion.TrySetResult(destination);
    }

    private sealed class RecordingDestination : IDiagnosticsBundleDestination
    {
        public DiagnosticsGeneratedArtifact Artifact { get; } =
            new("asura-diagnostics.zip", "opaque-artifact-locator");

        public Stream Content { get; } = new MemoryStream();

        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Content.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExporter : IDiagnosticsBundleExporter
    {
        public DiagnosticsBundleResult<DiagnosticsBundleReceipt> Result { get; init; } =
            DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Success(
                new DiagnosticsBundleReceipt(2, 512, 2048, new string('a', 64)));

        public int Calls { get; private set; }

        public DiagnosticsBundleRequest? Request { get; private set; }

        public Stream? Destination { get; private set; }

        public ValueTask<DiagnosticsBundleResult<DiagnosticsBundleReceipt>> ExportAsync(
            DiagnosticsBundleRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            Destination = destination;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingPresenter(
        DiagnosticsArtifactPresentationCapabilities capabilities)
        : IDiagnosticsArtifactPresenter
    {
        public DiagnosticsArtifactPresentationCapabilities Capabilities { get; } = capabilities;

        public List<DiagnosticsArtifactPresentationAction> Actions { get; } = [];

        public ValueTask<DiagnosticsArtifactPresentationResult> PresentAsync(
            DiagnosticsGeneratedArtifact artifact,
            DiagnosticsArtifactPresentationAction action,
            CancellationToken cancellationToken)
        {
            _ = artifact;
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(action);
            return ValueTask.FromResult(DiagnosticsArtifactPresentationResult.Presented);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
