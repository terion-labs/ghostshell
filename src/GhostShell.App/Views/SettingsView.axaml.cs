using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.SettingsPages;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Views;

internal sealed record KeybindingPrefixOptionsSelection(
    double TimeoutMilliseconds,
    bool Repeatable,
    FailedSequenceBehavior FailureBehavior);

internal sealed record ConnectionSecretFormInput(
    LauncherConnectionViewModel? Connection,
    SecretKind? Kind,
    string Label,
    string Value);

internal sealed record FileProviderSecretFormInput(
    FileProviderProfileItemViewModel? Profile,
    SecretKind? Kind,
    string Label,
    string Value);

internal sealed record AiProviderSecretFormInput(
    AiProviderProfileItemViewModel? Profile,
    string Label,
    string Value);

internal sealed record McpServerSecretFormInput(
    McpServerSecretTargetViewModel? Target,
    SecretKind? Kind,
    string Label,
    string Value);

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AboutSettingsRequested;

    public event EventHandler<RoutedEventArgs>? AddAiProviderRequested;

    public event EventHandler<RoutedEventArgs>? AddFileProviderRequested;

    public event EventHandler<RoutedEventArgs>? AddMcpServerRequested;

    public event EventHandler<RoutedEventArgs>? AgentSettingsRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceSettingsRequested;

    public event EventHandler<RoutedEventArgs>? ClearKeybindingPrefixRequested;

    public event EventHandler<RoutedEventArgs>? CheckForUpdatesRequested;

    public event EventHandler<RoutedEventArgs>?
        ClearMcpServerDiagnosticHistoryRequested;

    public event EventHandler<RoutedEventArgs>? CloneKeybindingPresetRequested;

    public event EventHandler<RoutedEventArgs>? CreateAiProviderSecretRequested;

    public event EventHandler<RoutedEventArgs>? CreateConnectionSecretRequested;

    public event EventHandler<RoutedEventArgs>? CreateFileProviderSecretRequested;

    public event EventHandler<RoutedEventArgs>? CreateMcpServerSecretRequested;

    public event EventHandler<RoutedEventArgs>? DeleteAiProviderRequested;

    public event EventHandler<RoutedEventArgs>? DeleteFileProviderRequested;

    public event EventHandler<RoutedEventArgs>? DeleteMcpServerRequested;

    public event EventHandler<RoutedEventArgs>? DeleteScreenRequested;

    public event EventHandler<RoutedEventArgs>? DeleteSecretRequested;

    public event EventHandler<RoutedEventArgs>? DeleteWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? DiagnosticsSettingsRequested;

    public event EventHandler<RoutedEventArgs>? DownloadUpdateRequested;

    public event EventHandler<RoutedEventArgs>? DismissSavedScreenDeleteUndoRequested;

    public event EventHandler<RoutedEventArgs>? EditAiProviderRequested;

    public event EventHandler<RoutedEventArgs>? EditFileProviderRequested;

    public event EventHandler<RoutedEventArgs>? EditLayoutRequested;

    public event EventHandler<RoutedEventArgs>? EditMcpServerRequested;

    public event EventHandler<RoutedEventArgs>? EditScreenRequested;

    public event EventHandler<RoutedEventArgs>? EditSecretRequested;

    public event EventHandler<RoutedEventArgs>? CreateWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? EditWorkspaceRequested;

    public event EventHandler<RoutedEventArgs>? ExportDefinitionsRequested;

    public event EventHandler<RoutedEventArgs>? DisableStartupProtectionRequested;

    public event EventHandler<RoutedEventArgs>? EnableStartupProtectionRequested;

    public event EventHandler<RoutedEventArgs>? FilesSettingsRequested;

    public event EventHandler<RoutedEventArgs>? BrowserSettingsRequested;

    public event EventHandler<RoutedEventArgs>? LockNowRequested;

    public event EventHandler<RoutedEventArgs>? ImportDefinitionsRequested;

    public event EventHandler<RoutedEventArgs>? InstallWorkspaceIsolationRuntimeRequested;

    public event EventHandler<RoutedEventArgs>? KeybindingPrefixOptionsChangedRequested;

    public event EventHandler<SelectionChangedEventArgs>?
        KeybindingProfileSelectionChangedRequested;

    public event EventHandler<RoutedEventArgs>? KeybindingSettingsRequested;

    public event EventHandler<RoutedEventArgs>? McpSettingsRequested;

    public event EventHandler<RoutedEventArgs>? NetworkingSettingsRequested;

    public event EventHandler<RoutedEventArgs>? OpenThirdPartyNoticesRequested;

    public event EventHandler<RoutedEventArgs>? QuickTerminalSettingsRequested;

    public event EventHandler<RoutedEventArgs>? RecordKeybindingPrefixRequested;

    public event EventHandler<RoutedEventArgs>? RecordKeybindingRequested;

    public event EventHandler<RoutedEventArgs>? RecordQuickTerminalHotkeyRequested;

    public event EventHandler<RoutedEventArgs>? ResetAllKeybindingsRequested;

    public event EventHandler<RoutedEventArgs>? ResetKeybindingRequested;

    public event EventHandler<RoutedEventArgs>? ReviewHistoryPrivacyRequested;

    public event EventHandler<RoutedEventArgs>? ReviewOnboardingRequested;

    public event EventHandler<RoutedEventArgs>? RestartToApplyUpdateRequested;

    public event EventHandler<RoutedEventArgs>?
        RestoreSessionsOnStartChangedRequested;

    public event EventHandler<RoutedEventArgs>? TerminalMultiplexingChangedRequested;

    public event EventHandler<RoutedEventArgs>? TerminateManagedRemoteSessionRequested;

    public event EventHandler<RoutedEventArgs>? ForgetManagedRemoteSessionRequested;

    public event EventHandler<RoutedEventArgs>? ApplicationAppearanceChangedRequested;

    public event EventHandler<RoutedEventArgs>? TerminalAppearanceChangedRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceApplyRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceApplyTerminalRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceCancelRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceCancelTerminalRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceResetRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceImportRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceExportRequested;

    public event EventHandler<RoutedEventArgs>? AppearanceResetTerminalPaletteRequested;

    public event EventHandler<RoutedEventArgs>? PickColorRequested;

    public event EventHandler<RoutedEventArgs>? SaveKeybindingsRequested;

    public event EventHandler<RoutedEventArgs>? SaveQuickTerminalSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SaveTerminalProfileRequested;

    public event EventHandler<RoutedEventArgs>? SaveDefaultAgentPolicyRequested;

    public event EventHandler<RoutedEventArgs>? SelectTerminalPaletteRequested;

    public event EventHandler<RoutedEventArgs>? SecretsSettingsRequested;

    public event EventHandler<RoutedEventArgs>? SettingsBackRequested;

    public event EventHandler<RoutedEventArgs>? ShowCommandPaletteRequested;

    public event EventHandler<RoutedEventArgs>? ShowLayoutDesignerRequested;

    public event EventHandler<RoutedEventArgs>? SaveHistoryRetentionRequested;

    public event EventHandler<RoutedEventArgs>? RetryRecentSessionHistoryRequested;

    public event EventHandler<RoutedEventArgs>? ExportAllHistoryRequested;

    public event EventHandler<RoutedEventArgs>? CancelHistoryExportRequested;

    public event EventHandler<RoutedEventArgs>? ClearRecentSessionsRequested;

    public event EventHandler<RoutedEventArgs>? ResetRecentSessionHistoryRequested;

    public event EventHandler<RoutedEventArgs>? ShowNewItemRequested;

    public event EventHandler<RoutedEventArgs>? TerminalSettingsRequested;

    public event EventHandler<RoutedEventArgs>? TestMcpServerRequested;

    public event EventHandler<RoutedEventArgs>? UnbindKeybindingRequested;

    public event EventHandler<RoutedEventArgs>? UndoDeletedSavedScreenRequested;

    public event EventHandler<RoutedEventArgs>? WorkspaceSettingsRequested;

    internal void ConfigureAppearanceControls(
        IReadOnlyList<PlatformProfile> platformProfiles,
        IReadOnlyList<AppearanceTextScaleOption> textScaleOptions) =>
        AppearanceSettingsPage.ConfigureAppearanceControls(
            platformProfiles,
            textScaleOptions);

    internal void ApplyAppearance(
        ThemePreference theme,
        AppearanceTextScaleOption selectedTextScale) =>
        AppearanceSettingsPage.ApplyAppearance(theme, selectedTextScale);

    internal AppearanceSelection CaptureAppearance() =>
        AppearanceSettingsPage.CaptureAppearance();

    internal void SetCustomAccent(Avalonia.Media.Color color) =>
        AppearanceSettingsPage.SetCustomAccent(color);

    internal void SetAppearanceValidationStatus(string message, bool isWarning) =>
        AppearanceSettingsPage.SetValidationStatus(message, isWarning);

    internal void ResetApplicationAppearance(ThemePreference defaultTheme) =>
        AppearanceSettingsPage.ResetApplicationAppearance(defaultTheme);

    internal KeybindingPrefixOptionsSelection? CaptureKeybindingPrefixOptions()
    {
        if (KeybindingPrefixTimeout.Value is not { } timeout
            || KeybindingPrefixRepeatable.IsChecked is not { } repeatable
            || KeybindingPrefixFailure.SelectedItem is not FailedSequenceBehavior behavior)
        {
            return null;
        }

        return new((double)timeout, repeatable, behavior);
    }

    internal ConnectionSecretFormInput CaptureConnectionSecretForm() =>
        new(
            SecretConnectionPicker.SelectedItem as LauncherConnectionViewModel,
            SecretKindPicker.SelectedItem is SecretKind kind ? kind : null,
            SecretLabelInput.Text ?? string.Empty,
            SecretValueInput.Text ?? string.Empty);

    internal FileProviderSecretFormInput CaptureFileProviderSecretForm() =>
        new(
            SecretFileProviderPicker.SelectedItem as FileProviderProfileItemViewModel,
            FileProviderSecretKindPicker.SelectedItem is SecretKind kind ? kind : null,
            FileProviderSecretLabelInput.Text ?? string.Empty,
            FileProviderSecretValueInput.Text ?? string.Empty);

    internal AiProviderSecretFormInput CaptureAiProviderSecretForm() =>
        new(
            AiProviderSecretProfilePicker.SelectedItem as AiProviderProfileItemViewModel,
            AiProviderSecretLabelInput.Text ?? string.Empty,
            AiProviderSecretValueInput.Text ?? string.Empty);

    internal McpServerSecretFormInput CaptureMcpServerSecretForm() =>
        new(
            McpServerSecretTargetPicker.SelectedItem
                as McpServerSecretTargetViewModel,
            McpServerSecretKindPicker.SelectedItem is SecretKind kind ? kind : null,
            McpServerSecretLabelInput.Text ?? string.Empty,
            McpServerSecretValueInput.Text ?? string.Empty);

    internal void ClearConnectionSecretValue() =>
        SecretValueInput.Text = string.Empty;

    internal void ClearConnectionSecretLabel() =>
        SecretLabelInput.Text = string.Empty;

    internal void ClearFileProviderSecretValue() =>
        FileProviderSecretValueInput.Text = string.Empty;

    internal void ClearFileProviderSecretLabel() =>
        FileProviderSecretLabelInput.Text = string.Empty;

    internal void ClearAiProviderSecretValue() =>
        AiProviderSecretValueInput.Text = string.Empty;

    internal void ClearAiProviderSecretLabel() =>
        AiProviderSecretLabelInput.Text = string.Empty;

    internal void ClearMcpServerSecretValue() =>
        McpServerSecretValueInput.Text = string.Empty;

    internal void ClearMcpServerSecretLabel() =>
        McpServerSecretLabelInput.Text = string.Empty;

    internal void BindOperationalViewModels(
        RecoveryDataControlViewModel recoveryData,
        LocalArtifactControlViewModel localArtifact,
        DiagnosticsExportViewModel diagnosticsExport)
    {
        RecoveryDataControlView.DataContext = recoveryData
            ?? throw new ArgumentNullException(nameof(recoveryData));
        LocalArtifactControlView.DataContext = localArtifact
            ?? throw new ArgumentNullException(nameof(localArtifact));
        DiagnosticsExportView.DataContext = diagnosticsExport
            ?? throw new ArgumentNullException(nameof(diagnosticsExport));
    }

    internal void FocusBackButton() =>
        SettingsBackButton.Focus(NavigationMethod.Tab);

    internal void FocusSavedScreenUndo() =>
        UndoDeletedSavedScreenButton.Focus(NavigationMethod.Tab);

    private void OnAboutSettingsClick(object? sender, RoutedEventArgs e) =>
        AboutSettingsRequested?.Invoke(sender, e);

    private void OnApplicationAppearanceChangedRequested(
        object? sender,
        RoutedEventArgs e) =>
        ApplicationAppearanceChangedRequested?.Invoke(sender, e);

    private void OnTerminalAppearanceChangedRequested(
        object? sender,
        RoutedEventArgs e) =>
        TerminalAppearanceChangedRequested?.Invoke(sender, e);

    private void OnAppearanceApplyRequested(object? sender, RoutedEventArgs e) =>
        AppearanceApplyRequested?.Invoke(sender, e);

    private void OnAppearanceApplyTerminalRequested(object? sender, RoutedEventArgs e) =>
        AppearanceApplyTerminalRequested?.Invoke(sender, e);

    private void OnAppearanceCancelRequested(object? sender, RoutedEventArgs e) =>
        AppearanceCancelRequested?.Invoke(sender, e);

    private void OnAppearanceCancelTerminalRequested(object? sender, RoutedEventArgs e) =>
        AppearanceCancelTerminalRequested?.Invoke(sender, e);

    private void OnAppearanceResetRequested(object? sender, RoutedEventArgs e) =>
        AppearanceResetRequested?.Invoke(sender, e);

    private void OnAppearanceImportRequested(object? sender, RoutedEventArgs e) =>
        AppearanceImportRequested?.Invoke(sender, e);

    private void OnAppearanceExportRequested(object? sender, RoutedEventArgs e) =>
        AppearanceExportRequested?.Invoke(sender, e);

    private void OnAppearanceResetTerminalPaletteRequested(
        object? sender,
        RoutedEventArgs e) =>
        AppearanceResetTerminalPaletteRequested?.Invoke(sender, e);

    private void OnPickColorRequested(object? sender, RoutedEventArgs e) =>
        PickColorRequested?.Invoke(sender, e);

    private void OnAddAiProviderClick(object? sender, RoutedEventArgs e) =>
        AddAiProviderRequested?.Invoke(sender, e);

    private void OnAddFileProviderClick(object? sender, RoutedEventArgs e) =>
        AddFileProviderRequested?.Invoke(sender, e);

    private void OnAddMcpServerClick(object? sender, RoutedEventArgs e) =>
        AddMcpServerRequested?.Invoke(sender, e);

    private void OnAgentSettingsClick(object? sender, RoutedEventArgs e) =>
        AgentSettingsRequested?.Invoke(sender, e);

    private void OnSaveDefaultAgentPolicyClick(object? sender, RoutedEventArgs e) =>
        SaveDefaultAgentPolicyRequested?.Invoke(sender, e);

    private void OnAppearanceSettingsClick(object? sender, RoutedEventArgs e) =>
        AppearanceSettingsRequested?.Invoke(sender, e);

    private void OnClearKeybindingPrefixClick(object? sender, RoutedEventArgs e) =>
        ClearKeybindingPrefixRequested?.Invoke(sender, e);

    private void OnClearMcpServerDiagnosticHistoryClick(
        object? sender,
        RoutedEventArgs e) =>
        ClearMcpServerDiagnosticHistoryRequested?.Invoke(sender, e);

    private void OnCloneKeybindingPresetClick(object? sender, RoutedEventArgs e) =>
        CloneKeybindingPresetRequested?.Invoke(sender, e);

    private void OnCreateAiProviderSecretClick(object? sender, RoutedEventArgs e) =>
        CreateAiProviderSecretRequested?.Invoke(sender, e);

    private void OnCreateConnectionSecretClick(object? sender, RoutedEventArgs e) =>
        CreateConnectionSecretRequested?.Invoke(sender, e);

    private void OnCreateFileProviderSecretClick(object? sender, RoutedEventArgs e) =>
        CreateFileProviderSecretRequested?.Invoke(sender, e);

    private void OnCreateMcpServerSecretClick(object? sender, RoutedEventArgs e) =>
        CreateMcpServerSecretRequested?.Invoke(sender, e);

    private void OnDeleteAiProviderClick(object? sender, RoutedEventArgs e) =>
        DeleteAiProviderRequested?.Invoke(sender, e);

    private void OnDeleteFileProviderClick(object? sender, RoutedEventArgs e) =>
        DeleteFileProviderRequested?.Invoke(sender, e);

    private void OnDeleteMcpServerClick(object? sender, RoutedEventArgs e) =>
        DeleteMcpServerRequested?.Invoke(sender, e);

    private void OnDeleteScreenClick(object? sender, RoutedEventArgs e) =>
        DeleteScreenRequested?.Invoke(sender, e);

    private void OnDeleteSecretClick(object? sender, RoutedEventArgs e) =>
        DeleteSecretRequested?.Invoke(sender, e);

    private void OnDeleteWorkspaceClick(object? sender, RoutedEventArgs e) =>
        DeleteWorkspaceRequested?.Invoke(sender, e);

    private void OnDiagnosticsSettingsClick(object? sender, RoutedEventArgs e) =>
        DiagnosticsSettingsRequested?.Invoke(sender, e);

    private void OnDismissSavedScreenDeleteUndoClick(object? sender, RoutedEventArgs e) =>
        DismissSavedScreenDeleteUndoRequested?.Invoke(sender, e);

    private void OnEditAiProviderClick(object? sender, RoutedEventArgs e) =>
        EditAiProviderRequested?.Invoke(sender, e);

    private void OnEditFileProviderClick(object? sender, RoutedEventArgs e) =>
        EditFileProviderRequested?.Invoke(sender, e);

    private void OnEditLayoutClick(object? sender, RoutedEventArgs e) =>
        EditLayoutRequested?.Invoke(sender, e);

    private void OnEditMcpServerClick(object? sender, RoutedEventArgs e) =>
        EditMcpServerRequested?.Invoke(sender, e);

    private void OnEditScreenClick(object? sender, RoutedEventArgs e) =>
        EditScreenRequested?.Invoke(sender, e);

    private void OnEditSecretClick(object? sender, RoutedEventArgs e) =>
        EditSecretRequested?.Invoke(sender, e);

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e) =>
        CreateWorkspaceRequested?.Invoke(sender, e);

    private void OnEditWorkspaceClick(object? sender, RoutedEventArgs e) =>
        EditWorkspaceRequested?.Invoke(sender, e);

    private void OnExportDefinitionsClick(object? sender, RoutedEventArgs e) =>
        ExportDefinitionsRequested?.Invoke(sender, e);

    private void OnDisableStartupProtectionClick(object? sender, RoutedEventArgs e) =>
        DisableStartupProtectionRequested?.Invoke(sender, e);

    private void OnEnableStartupProtectionClick(object? sender, RoutedEventArgs e) =>
        EnableStartupProtectionRequested?.Invoke(sender, e);

    private void OnFilesSettingsClick(object? sender, RoutedEventArgs e) =>
        FilesSettingsRequested?.Invoke(sender, e);

    private void OnBrowserSettingsClick(object? sender, RoutedEventArgs e) =>
        BrowserSettingsRequested?.Invoke(sender, e);

    public event EventHandler<RoutedEventArgs>? BrowserProfileEnabledChangedRequested;

    public event EventHandler<RoutedEventArgs>? DeleteBrowserProfileRequested;

    private void OnBrowserProfileEnabledChanged(object? sender, RoutedEventArgs e) =>
        BrowserProfileEnabledChangedRequested?.Invoke(sender, e);

    private void OnDeleteBrowserProfileClick(object? sender, RoutedEventArgs e) =>
        DeleteBrowserProfileRequested?.Invoke(sender, e);

    private void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(sender, e);

    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e) =>
        DownloadUpdateRequested?.Invoke(sender, e);

    private void OnLockNowClick(object? sender, RoutedEventArgs e) =>
        LockNowRequested?.Invoke(sender, e);

    private void OnImportDefinitionsClick(object? sender, RoutedEventArgs e) =>
        ImportDefinitionsRequested?.Invoke(sender, e);

    private void OnInstallWorkspaceIsolationRuntimeClick(object? sender, RoutedEventArgs e) =>
        InstallWorkspaceIsolationRuntimeRequested?.Invoke(sender, e);

    private void OnKeybindingPrefixOptionsChanged(object? sender, RoutedEventArgs e) =>
        KeybindingPrefixOptionsChangedRequested?.Invoke(sender, e);

    private void OnKeybindingProfileSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e) =>
        KeybindingProfileSelectionChangedRequested?.Invoke(sender, e);

    private void OnKeybindingSettingsClick(object? sender, RoutedEventArgs e) =>
        KeybindingSettingsRequested?.Invoke(sender, e);

    private void OnMcpSettingsClick(object? sender, RoutedEventArgs e) =>
        McpSettingsRequested?.Invoke(sender, e);

    private void OnOpenThirdPartyNoticesClick(object? sender, RoutedEventArgs e) =>
        OpenThirdPartyNoticesRequested?.Invoke(sender, e);

    private void OnQuickTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        QuickTerminalSettingsRequested?.Invoke(sender, e);

    private void OnRecordKeybindingPrefixClick(object? sender, RoutedEventArgs e) =>
        RecordKeybindingPrefixRequested?.Invoke(sender, e);

    private void OnRecordKeybindingClick(object? sender, RoutedEventArgs e) =>
        RecordKeybindingRequested?.Invoke(sender, e);

    private void OnResetAllKeybindingsClick(object? sender, RoutedEventArgs e) =>
        ResetAllKeybindingsRequested?.Invoke(sender, e);

    private void OnResetKeybindingClick(object? sender, RoutedEventArgs e) =>
        ResetKeybindingRequested?.Invoke(sender, e);

    private void OnReviewHistoryPrivacyClick(object? sender, RoutedEventArgs e) =>
        ReviewHistoryPrivacyRequested?.Invoke(sender, e);

    private void OnReviewOnboardingClick(object? sender, RoutedEventArgs e) =>
        ReviewOnboardingRequested?.Invoke(sender, e);

    private void OnRestartToApplyUpdateClick(object? sender, RoutedEventArgs e) =>
        RestartToApplyUpdateRequested?.Invoke(sender, e);

    private void OnRestoreSessionsOnStartChanged(object? sender, RoutedEventArgs e) =>
        RestoreSessionsOnStartChangedRequested?.Invoke(sender, e);

    private void OnTerminalMultiplexingChanged(object? sender, RoutedEventArgs e) =>
        TerminalMultiplexingChangedRequested?.Invoke(sender, e);

    private void OnTerminateManagedRemoteSessionClick(object? sender, RoutedEventArgs e) =>
        TerminateManagedRemoteSessionRequested?.Invoke(sender, e);

    private void OnForgetManagedRemoteSessionClick(object? sender, RoutedEventArgs e) =>
        ForgetManagedRemoteSessionRequested?.Invoke(sender, e);

    private void OnSaveKeybindingsClick(object? sender, RoutedEventArgs e) =>
        SaveKeybindingsRequested?.Invoke(sender, e);

    private void OnQuickTerminalSettingsSaveRequested(object? sender, RoutedEventArgs e) =>
        SaveQuickTerminalSettingsRequested?.Invoke(sender, e);

    private void OnQuickTerminalHotkeyRecordRequested(object? sender, RoutedEventArgs e) =>
        RecordQuickTerminalHotkeyRequested?.Invoke(sender, e);

    private void OnSaveTerminalProfileClick(object? sender, RoutedEventArgs e) =>
        SaveTerminalProfileRequested?.Invoke(sender, e);

    private void OnSelectTerminalPaletteClick(object? sender, RoutedEventArgs e) =>
        SelectTerminalPaletteRequested?.Invoke(sender, e);

    private void OnSecretsSettingsClick(object? sender, RoutedEventArgs e) =>
        SecretsSettingsRequested?.Invoke(sender, e);

    private void OnSettingsBackClick(object? sender, RoutedEventArgs e) =>
        SettingsBackRequested?.Invoke(sender, e);

    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e) =>
        ShowCommandPaletteRequested?.Invoke(sender, e);

    private void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        ShowLayoutDesignerRequested?.Invoke(sender, e);

    private void OnShowNewItemClick(object? sender, RoutedEventArgs e) =>
        ShowNewItemRequested?.Invoke(sender, e);

    private void OnSaveHistoryRetentionClick(object? sender, RoutedEventArgs e) =>
        SaveHistoryRetentionRequested?.Invoke(sender, e);

    private void OnRetryRecentSessionHistoryClick(object? sender, RoutedEventArgs e) =>
        RetryRecentSessionHistoryRequested?.Invoke(sender, e);

    private void OnExportAllHistoryClick(object? sender, RoutedEventArgs e) =>
        ExportAllHistoryRequested?.Invoke(sender, e);

    private void OnCancelHistoryExportClick(object? sender, RoutedEventArgs e) =>
        CancelHistoryExportRequested?.Invoke(sender, e);

    private void OnClearRecentSessionsClick(object? sender, RoutedEventArgs e) =>
        ClearRecentSessionsRequested?.Invoke(sender, e);

    private void OnResetRecentSessionHistoryClick(object? sender, RoutedEventArgs e) =>
        ResetRecentSessionHistoryRequested?.Invoke(sender, e);

    private void OnTerminalSettingsClick(object? sender, RoutedEventArgs e) =>
        TerminalSettingsRequested?.Invoke(sender, e);

    private void OnTestMcpServerClick(object? sender, RoutedEventArgs e) =>
        TestMcpServerRequested?.Invoke(sender, e);

    private void OnUnbindKeybindingClick(object? sender, RoutedEventArgs e) =>
        UnbindKeybindingRequested?.Invoke(sender, e);

    private void OnUndoDeletedSavedScreenClick(object? sender, RoutedEventArgs e) =>
        UndoDeletedSavedScreenRequested?.Invoke(sender, e);

    private void OnWorkspaceSettingsClick(object? sender, RoutedEventArgs e) =>
        WorkspaceSettingsRequested?.Invoke(sender, e);

    private void OnNetworkingSettingsClick(object? sender, RoutedEventArgs e) =>
        NetworkingSettingsRequested?.Invoke(sender, e);
}
