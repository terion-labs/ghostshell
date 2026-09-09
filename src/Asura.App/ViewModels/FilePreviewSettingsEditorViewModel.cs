using System.Windows.Input;
using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// The file-preview settings as the settings page edits them. Every change
/// applies the moment the control moves — through the shared preferences, so
/// each open file panel reads the new value on its next preview — and there
/// is no save step anywhere.
/// </summary>
public sealed class FilePreviewSettingsEditorViewModel : ObservableObject
{
    private readonly IFilePreviewPreferences _preferences;
    private readonly IPreviewCacheControl? _cache;

    public FilePreviewSettingsEditorViewModel(
        IFilePreviewPreferences preferences,
        IPreviewCacheControl? cache = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _cache = cache;
        ClearCacheCommand = new AsyncActionCommand(ClearCacheAsync, () => _cache is not null);
        RefreshCacheUsage();
    }

    public ICommand ClearCacheCommand { get; }

    /// <summary>
    /// Remote files up to this size preview the moment they are selected;
    /// larger ones wait for the explicit download action in the preview.
    /// </summary>
    public decimal AutoLoadThresholdMegabytes
    {
        get => (decimal)Current.AutoLoadThresholdBytes / (1024 * 1024);
        set
        {
            if (value != AutoLoadThresholdMegabytes)
            {
                Apply(Current with { AutoLoadThresholdBytes = (long)(value * 1024 * 1024) });
                OnPropertyChanged();
            }
        }
    }

    public bool KeepPreviewsBetweenRuns
    {
        get => Current.KeepPreviewsBetweenRuns;
        set
        {
            if (value != Current.KeepPreviewsBetweenRuns)
            {
                Apply(Current with { KeepPreviewsBetweenRuns = value });
                OnPropertyChanged();
                RefreshCacheUsage();
            }
        }
    }

    public decimal CacheBudgetMegabytes
    {
        get => (decimal)Current.CacheBudgetBytes / (1024 * 1024);
        set
        {
            if (value != CacheBudgetMegabytes)
            {
                Apply(Current with { CacheBudgetBytes = (long)(value * 1024 * 1024) });
                OnPropertyChanged();
            }
        }
    }

    public string CacheUsageText { get; private set; } = string.Empty;

    /// <summary>
    /// Reads the cache's current footprint. Called when the page is shown and
    /// after anything that changes it, rather than on a timer: a settings page
    /// is looked at, not watched.
    /// </summary>
    public void RefreshCacheUsage()
    {
        CacheUsageText = _cache is null
            ? "No preview cache is available in this build."
            : $"{ByteSize.Format(_cache.CachedBytes)} on disk right now.";
        OnPropertyChanged(nameof(CacheUsageText));
    }

    private FilePreviewSettings Current => _preferences.Current;

    private void Apply(FilePreviewSettings settings) =>
        _ = _preferences.ApplyAsync(settings, CancellationToken.None).AsTask();

    private async Task ClearCacheAsync()
    {
        if (_cache is not null)
        {
            await _cache.ClearAsync(CancellationToken.None);
        }

        RefreshCacheUsage();
    }
}

/// <summary>
/// Preferences that live only in this process, for hosts composed without
/// settings storage — tests and the capture harness. Behavior is identical;
/// only persistence is missing.
/// </summary>
public sealed class InMemoryFilePreviewPreferences : IFilePreviewPreferences
{
    private FilePreviewSettings _current = FilePreviewSettings.Default;

    public FilePreviewSettings Current => _current;

    public event EventHandler? Changed;

    public ValueTask ApplyAsync(FilePreviewSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}
