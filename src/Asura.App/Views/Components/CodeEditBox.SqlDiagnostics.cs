using Asura.Application;
using Avalonia;

namespace Asura.App.Views.Components;

public sealed partial class CodeEditBox
{
    private static readonly TimeSpan DefaultDiagnosticDebounce =
        TimeSpan.FromMilliseconds(300);

    public static readonly DirectProperty<CodeEditBox, IReadOnlyList<SqlDiagnostic>>
        DiagnosticsProperty = AvaloniaProperty.RegisterDirect<
            CodeEditBox,
            IReadOnlyList<SqlDiagnostic>>(
            nameof(Diagnostics),
            editor => editor.Diagnostics);

    public static readonly DirectProperty<CodeEditBox, string> DiagnosticStatusProperty =
        AvaloniaProperty.RegisterDirect<CodeEditBox, string>(
            nameof(DiagnosticStatus),
            editor => editor.DiagnosticStatus);

    private readonly SqlDiagnosticBackgroundRenderer _sqlDiagnosticRenderer = new();
    private IReadOnlyList<SqlDiagnostic> _diagnostics = [];
    private string _diagnosticStatus = string.Empty;
    private CancellationTokenSource? _diagnosticDebounceCancellation;
    private Task _pendingDiagnostics = Task.CompletedTask;
    private long _diagnosticGeneration;

    /// <summary>Diagnostics for the exact text currently displayed.</summary>
    public IReadOnlyList<SqlDiagnostic> Diagnostics
    {
        get => _diagnostics;
        private set => SetAndRaise(DiagnosticsProperty, ref _diagnostics, value);
    }

    /// <summary>A short live-region reading such as “1 error · 2 warnings”.</summary>
    public string DiagnosticStatus
    {
        get => _diagnosticStatus;
        private set => SetAndRaise(
            DiagnosticStatusProperty,
            ref _diagnosticStatus,
            value);
    }

    internal TimeSpan DiagnosticDebounceForTesting { get; set; } =
        DefaultDiagnosticDebounce;

    internal Task PendingDiagnosticsForTesting => _pendingDiagnostics;

    internal bool IsDiagnosticRendererAttachedForTesting =>
        Editor.TextArea.TextView.BackgroundRenderers.Contains(_sqlDiagnosticRenderer);

    private void AttachDiagnosticRenderer()
    {
        if (!IsDiagnosticRendererAttachedForTesting)
        {
            Editor.TextArea.TextView.BackgroundRenderers.Add(_sqlDiagnosticRenderer);
        }
    }

    private void DetachDiagnosticRenderer() =>
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_sqlDiagnosticRenderer);

    private void ScheduleSqlDiagnostics()
    {
        CancelDiagnosticRequest();
        var session = SqlLanguageSession;
        if (!_sqlIntelligenceAttached
            || session is null
            || (!session.IsAvailable && !session.CanRetry))
        {
            _pendingDiagnostics = Task.CompletedTask;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _diagnosticDebounceCancellation = cancellation;
        var generation = _diagnosticGeneration;
        var sql = Editor.Document.Text;
        _pendingDiagnostics = DiagnoseAfterDebounceAsync(
            session,
            sql,
            generation,
            cancellation);
    }

    private async Task DiagnoseAfterDebounceAsync(
        ISqlLanguageSession session,
        string sql,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(DiagnosticDebounceForTesting, cancellation.Token);
            if (!IsCurrentDiagnosticRequest(session, sql, generation))
            {
                return;
            }

            ReleaseDiagnosticDebounce(cancellation);
            // Cancelling an in-flight framed exchange restarts the native worker;
            // newer editor snapshots supersede this one through generation fencing.
            var diagnostics = await session.DiagnoseAsync(sql, CancellationToken.None);
            if (IsCurrentDiagnosticRequest(session, sql, generation))
            {
                ApplySqlLanguageSessionStatus(session);
                SetDiagnostics(diagnostics);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer snapshot superseded this request before its debounce elapsed.
        }
        catch
        {
            if (IsCurrentDiagnosticRequest(session, sql, generation))
            {
                ApplySqlLanguageSessionStatus(session);
                SetDiagnostics([]);
            }
        }
        finally
        {
            ReleaseDiagnosticDebounce(cancellation);
            cancellation.Dispose();
        }
    }

    private bool IsCurrentDiagnosticRequest(
        ISqlLanguageSession session,
        string sql,
        long generation) =>
        generation == _diagnosticGeneration
        && ReferenceEquals(session, SqlLanguageSession)
        && string.Equals(sql, Editor.Document.Text, StringComparison.Ordinal)
        && _sqlIntelligenceAttached;

    private void CancelDiagnosticRequest()
    {
        _diagnosticGeneration++;
        _diagnosticDebounceCancellation?.Cancel();
        _diagnosticDebounceCancellation = null;
    }

    private void ReleaseDiagnosticDebounce(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_diagnosticDebounceCancellation, cancellation))
        {
            _diagnosticDebounceCancellation = null;
        }
    }
}
