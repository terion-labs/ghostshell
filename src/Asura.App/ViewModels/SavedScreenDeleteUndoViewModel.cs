using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record SavedScreenDeleteUndoReceipt(
    ScreenId ScreenId,
    string ScreenName);

public sealed class SavedScreenDeleteUndoViewModel : ObservableObject
{
    private const string InitialStatus =
        "No saved-screen deletion is available to undo. Running instances are not changed by saved-definition deletion.";

    private readonly IDefinitionCatalog _catalog;
    private PendingSavedScreenDeleteState? _pending;
    private bool _isRestoring;
    private string _status = InitialStatus;

    public SavedScreenDeleteUndoViewModel(IDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public SavedScreenDeleteUndoReceipt? Pending => _pending?.Receipt;

    public bool HasPending => Pending is not null;

    public bool CanUndo => HasPending && !IsRestoring;

    public bool IsRestoring
    {
        get => _isRestoring;
        private set
        {
            if (SetProperty(ref _isRestoring, value))
            {
                OnPropertyChanged(nameof(CanUndo));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    internal void Publish(StoredDefinition<ScreenDefinition> deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        var receipt = new SavedScreenDeleteUndoReceipt(
            deleted.Value.Id,
            deleted.Value.Name);
        _pending = new PendingSavedScreenDeleteState(deleted, receipt);
        Status =
            $"Deleted “{receipt.ScreenName}”. Running instances were not changed. Undo restores this saved screen.";
        NotifyPendingChanged();
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
        UndoAsync(CancellationToken cancellationToken)
    {
        if (_pending is not { } pending)
        {
            return Failure("No saved-screen deletion is available to undo.");
        }

        if (IsRestoring)
        {
            return Failure("The saved screen is already being restored.");
        }

        IsRestoring = true;
        if (ReferenceEquals(_pending, pending))
        {
            Status = $"Restoring “{pending.Receipt.ScreenName}”…";
        }

        try
        {
            var result = await _catalog.SaveScreenAsync(
                pending.Stored.Value,
                expectedRevision: null,
                cancellationToken);
            if (result.IsSuccess)
            {
                ClearPending(
                    pending,
                    $"Restored “{pending.Receipt.ScreenName}”.");
            }
            else if (ReferenceEquals(_pending, pending))
            {
                Status =
                    $"Could not restore “{pending.Receipt.ScreenName}”. Retry or dismiss this undo.";
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(_pending, pending))
            {
                Status =
                    $"Restore cancelled for “{pending.Receipt.ScreenName}”. Retry or dismiss this undo.";
            }

            throw;
        }
        finally
        {
            IsRestoring = false;
        }
    }

    public void Dismiss()
    {
        if (IsRestoring)
        {
            return;
        }

        _pending = null;
        Status = "Saved-screen delete undo dismissed.";
        NotifyPendingChanged();
    }

    private void ClearPending(
        PendingSavedScreenDeleteState expected,
        string status)
    {
        if (!ReferenceEquals(_pending, expected))
        {
            return;
        }

        _pending = null;
        Status = status;
        NotifyPendingChanged();
    }

    private void NotifyPendingChanged()
    {
        OnPropertyChanged(nameof(Pending));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(CanUndo));
    }

    private static DefinitionStoreResult<StoredDefinition<ScreenDefinition>>
        Failure(string message) =>
        DefinitionStoreResult<StoredDefinition<ScreenDefinition>>.Failure(
            new DefinitionStoreError(
                DefinitionStoreErrorCode.InvalidDefinition,
                message));

    private sealed record PendingSavedScreenDeleteState(
        StoredDefinition<ScreenDefinition> Stored,
        SavedScreenDeleteUndoReceipt Receipt);
}
