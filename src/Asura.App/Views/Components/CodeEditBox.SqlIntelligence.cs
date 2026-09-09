using Asura.Application;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using AvaloniaEdit;

namespace Asura.App.Views.Components;

/// <summary>
/// Owns the optional SQL-language session boundary. Completion and diagnostics
/// are separate partials because they have independent cancellation lifetimes.
/// </summary>
public sealed partial class CodeEditBox
{
    private const int MaximumLanguageStatusCharacters = 320;

    public static readonly StyledProperty<ISqlLanguageSession?> SqlLanguageSessionProperty =
        AvaloniaProperty.Register<CodeEditBox, ISqlLanguageSession?>(
            nameof(SqlLanguageSession));

    public static readonly StyledProperty<string?> SqlLanguageStatusProperty =
        AvaloniaProperty.Register<CodeEditBox, string?>(nameof(SqlLanguageStatus));

    public static readonly StyledProperty<SqlCompletionContext> SqlCompletionContextProperty =
        AvaloniaProperty.Register<CodeEditBox, SqlCompletionContext>(
            nameof(SqlCompletionContext),
            SqlCompletionContext.Empty);

    private bool _sqlIntelligenceAttached;
    private string? _availableSqlLanguageStatus;

    public ISqlLanguageSession? SqlLanguageSession
    {
        get => GetValue(SqlLanguageSessionProperty);
        set => SetValue(SqlLanguageSessionProperty, value);
    }

    /// <summary>Optional worker/catalog lifecycle status supplied by the owner.</summary>
    public string? SqlLanguageStatus
    {
        get => GetValue(SqlLanguageStatusProperty);
        set => SetValue(SqlLanguageStatusProperty, value);
    }

    /// <summary>
    /// The currently selected database object, captured independently for
    /// every completion request so a stale popup cannot cross selections.
    /// </summary>
    public SqlCompletionContext SqlCompletionContext
    {
        get => GetValue(SqlCompletionContextProperty);
        set => SetValue(SqlCompletionContextProperty, value);
    }

    internal TextEditor EditorForTesting => Editor;

    private void InitializeSqlIntelligence() =>
        Editor.TextArea.TextEntered += OnEditorTextEntered;

    private void AttachSqlIntelligence()
    {
        _sqlIntelligenceAttached = true;
        AttachDiagnosticRenderer();
        ApplySqlDiagnosticTheme();
        ScheduleSqlDiagnostics();
    }

    private void DetachSqlIntelligence()
    {
        _sqlIntelligenceAttached = false;
        StopSqlRequests();
        CloseCompletionWindow();
        SetDiagnostics([]);
        DetachDiagnosticRenderer();
    }

    private void RestartSqlIntelligence()
    {
        StopSqlRequests();
        CloseCompletionWindow();
        SetDiagnostics([]);
        _availableSqlLanguageStatus = SqlLanguageSession?.IsAvailable == true
            ? SqlLanguageStatus
            : null;
        if (_sqlIntelligenceAttached)
        {
            ScheduleSqlDiagnostics();
        }
    }

    private void RestartSqlCompletionContext() =>
        CancelCompletionAndCloseWindow();

    private void OnSqlDocumentChanged()
    {
        CancelCompletionRequest();
        SetDiagnostics([]);
        ScheduleSqlDiagnostics();
    }

    private void StopSqlRequests()
    {
        CancelDiagnosticRequest();
        CancelCompletionRequest();
    }

    private void ApplySqlLanguageStatus()
    {
        if (SqlLanguageSession?.IsAvailable == true)
        {
            _availableSqlLanguageStatus = SqlLanguageStatus;
        }

        ApplySqlLanguageStatus(SqlLanguageStatus);
    }

    private void ApplySqlLanguageSessionStatus(ISqlLanguageSession session)
    {
        if (session.IsAvailable)
        {
            ApplySqlLanguageStatus(
                _availableSqlLanguageStatus
                ?? "SQL completion and validation are available.");
            return;
        }

        ApplySqlLanguageStatus(
            session.UnavailableReason ?? "SQL intelligence worker is unavailable.");
    }

    private void ApplySqlLanguageStatus(string? value)
    {
        var status = string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (status.Length > MaximumLanguageStatusCharacters)
        {
            status = $"{status[..MaximumLanguageStatusCharacters]}…";
        }

        var boundedStatus = string.IsNullOrEmpty(status) ? null : status;
        ToolTip.SetTip(this, boundedStatus);
        AutomationProperties.SetHelpText(this, boundedStatus);
    }
}
