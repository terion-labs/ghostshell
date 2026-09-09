using Asura.Application;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Asura.App.Views.Components;

public sealed partial class CodeEditBox
{
    private const int MaximumDiagnosticMessagesInTooltip = 5;
    private const int MaximumDiagnosticMessageCharacters = 240;

    private void SetDiagnostics(IReadOnlyList<SqlDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics.Count == 0 ? [] : diagnostics.ToArray();
        DiagnosticStatus = FormatDiagnosticStatus(Diagnostics);
        DiagnosticStatusText.Text = DiagnosticStatus;
        DiagnosticStatusBadge.IsVisible = Diagnostics.Count > 0;
        var details = FormatDiagnosticDetails(Diagnostics);
        ToolTip.SetTip(DiagnosticStatusBadge, details);
        ToolTip.SetTip(DiagnosticStatusText, details);
        AutomationProperties.SetHelpText(DiagnosticStatusText, details);
        ApplySqlDiagnosticTheme();
        Editor.TextArea.TextView.InvalidateLayer(_sqlDiagnosticRenderer.Layer);
    }

    private void ApplySqlDiagnosticTheme()
    {
        var error = ResourceBrush("ShellDangerBrush");
        var warning = ResourceBrush("ShellWarningBrush");
        var information = ResourceBrush("ShellAccentBrush");
        _sqlDiagnosticRenderer.Update(Diagnostics, error, warning, information);
        DiagnosticStatusText.Foreground = HighestSeverity(Diagnostics) switch
        {
            SqlDiagnosticSeverity.Error => error,
            SqlDiagnosticSeverity.Warning => warning,
            _ => information,
        };
        Editor.TextArea.TextView.InvalidateLayer(_sqlDiagnosticRenderer.Layer);
    }

    private IBrush? ResourceBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value)
            ? value as IBrush
            : null;

    private static SqlDiagnosticSeverity HighestSeverity(
        IReadOnlyList<SqlDiagnostic> diagnostics)
    {
        if (diagnostics.Any(item => item.Severity == SqlDiagnosticSeverity.Error))
        {
            return SqlDiagnosticSeverity.Error;
        }

        return diagnostics.Any(item => item.Severity == SqlDiagnosticSeverity.Warning)
            ? SqlDiagnosticSeverity.Warning
            : SqlDiagnosticSeverity.Information;
    }

    private static string FormatDiagnosticStatus(IReadOnlyList<SqlDiagnostic> diagnostics)
    {
        var errors = diagnostics.Count(item => item.Severity == SqlDiagnosticSeverity.Error);
        var warnings = diagnostics.Count(item => item.Severity == SqlDiagnosticSeverity.Warning);
        var information = diagnostics.Count - errors - warnings;
        var parts = new List<string>(3);
        AddDiagnosticCount(parts, errors, "error");
        AddDiagnosticCount(parts, warnings, "warning");
        AddDiagnosticCount(parts, information, "note");
        return string.Join(" · ", parts);
    }

    private static string? FormatDiagnosticDetails(
        IReadOnlyList<SqlDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return null;
        }

        var messages = diagnostics
            .Take(MaximumDiagnosticMessagesInTooltip)
            .Select(item =>
            {
                var message = string.Join(
                    " ",
                    item.Message.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries));
                if (message.Length > MaximumDiagnosticMessageCharacters)
                {
                    message = $"{message[..MaximumDiagnosticMessageCharacters]}…";
                }

                return $"{item.Severity}: {message}";
            })
            .ToList();
        if (diagnostics.Count > MaximumDiagnosticMessagesInTooltip)
        {
            messages.Add(
                $"… {diagnostics.Count - MaximumDiagnosticMessagesInTooltip} more");
        }

        return string.Join(Environment.NewLine, messages);
    }

    private static void AddDiagnosticCount(List<string> parts, int count, string label)
    {
        if (count > 0)
        {
            parts.Add($"{count} {label}{(count == 1 ? string.Empty : "s")}");
        }
    }
}
