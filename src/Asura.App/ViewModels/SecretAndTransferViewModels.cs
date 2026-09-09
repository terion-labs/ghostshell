using System.Collections.ObjectModel;
using Asura.App;
using Asura.Application;
using Asura.Core;
using Asura.Docker;
using FluentIcons.Common;

namespace Asura.App.ViewModels;

public sealed record SecretMetadataViewModel(
    SecretRef Reference,
    string Label,
    string Kind,
    string Scope,
    string Updated,
    string LastUsed,
    SecretScope SecretScope,
    string Dependencies,
    int DependencyCount);

public sealed class FileTransferItemViewModel : ObservableObject
{
    private string _source;
    private string _destination;
    private string _operation;
    private string _state;
    private string _stage;
    private string _progress;
    private string? _error;
    private bool _hasError;
    private bool _canCancel;
    private bool _canRetry;
    private bool _isActive;
    private bool _hasKnownProgress;
    private double _progressPercent;

    public FileTransferItemViewModel(
        FilePanelTransferId id,
        string source,
        string destination,
        string operation,
        string state,
        string stage,
        string progress,
        string? error,
        bool hasError,
        bool canCancel,
        bool canRetry,
        bool isActive,
        bool hasKnownProgress,
        double progressPercent,
        DateTimeOffset queuedAt)
    {
        Id = id;
        _source = source;
        _destination = destination;
        _operation = operation;
        _state = state;
        _stage = stage;
        _progress = progress;
        _error = error;
        _hasError = hasError;
        _canCancel = canCancel;
        _canRetry = canRetry;
        _isActive = isActive;
        _hasKnownProgress = hasKnownProgress;
        _progressPercent = progressPercent;
        QueuedAt = queuedAt;
    }

    public FilePanelTransferId Id { get; }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string Destination
    {
        get => _destination;
        private set => SetProperty(ref _destination, value);
    }

    public string Operation
    {
        get => _operation;
        private set => SetProperty(ref _operation, value);
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public string Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set => SetProperty(ref _canCancel, value);
    }

    public bool CanRetry
    {
        get => _canRetry;
        private set => SetProperty(ref _canRetry, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool HasKnownProgress
    {
        get => _hasKnownProgress;
        private set => SetProperty(ref _hasKnownProgress, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public DateTimeOffset QueuedAt { get; }

    public void UpdateFrom(FileTransferItemViewModel latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (latest.Id != Id)
        {
            throw new ArgumentException(
                "A transfer row can only be updated from the same transfer identity.",
                nameof(latest));
        }

        Source = latest.Source;
        Destination = latest.Destination;
        Operation = latest.Operation;
        State = latest.State;
        Stage = latest.Stage;
        Progress = latest.Progress;
        Error = latest.Error;
        HasError = latest.HasError;
        CanCancel = latest.CanCancel;
        CanRetry = latest.CanRetry;
        IsActive = latest.IsActive;
        HasKnownProgress = latest.HasKnownProgress;
        ProgressPercent = latest.ProgressPercent;
    }
}

