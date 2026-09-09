using Asura.Application;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using AvaloniaEdit.CodeCompletion;

namespace Asura.App.Views.Components;

public sealed partial class CodeEditBox
{
    private const int MaximumVisibleCompletionItems = 2000;
    private static readonly TimeSpan DefaultCompletionDebounce =
        TimeSpan.FromMilliseconds(20);

    private CancellationTokenSource? _completionDebounceCancellation;
    private ISqlLanguageSession? _completionTransportSession;
    private Task<SqlCompletionResult>? _completionTransport;
    private CompletionWindow? _completionWindow;
    private Task _pendingCompletion = Task.CompletedTask;
    private long _completionGeneration;

    internal TimeSpan CompletionDebounceForTesting { get; set; } =
        DefaultCompletionDebounce;

    internal Task PendingCompletionForTesting => _pendingCompletion;

    internal CompletionWindow? ActiveCompletionWindowForTesting => _completionWindow;

    private void OnEditorTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        // The workspace owns Cmd/Ctrl+Enter. Giving consumers first refusal is
        // what prevents an open completion popup from accepting that Enter.
        EditorKeyDown?.Invoke(this, e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape
            && (_completionWindow is not null || !_pendingCompletion.IsCompleted))
        {
            e.Handled = true;
            CancelCompletionAndCloseWindow();
            return;
        }

        if (IsCompletionShortcut(e))
        {
            e.Handled = true;
            StartCompletionRequest(TimeSpan.Zero);
        }
    }

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        _ = sender;
        if (string.Equals(e.Text, ".", StringComparison.Ordinal))
        {
            if (HasPlausibleDotQualifier())
            {
                StartCompletionRequest(TimeSpan.Zero);
            }
            else
            {
                CancelCompletionAndCloseWindow();
            }

            return;
        }

        if (IsIdentifierInput(e.Text))
        {
            StartCompletionRequest(CompletionDebounceForTesting);
            return;
        }

        CancelCompletionAndCloseWindow();
    }

    private static bool IsCompletionShortcut(KeyEventArgs e) =>
        e.Key == Key.Space
        && (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta));

    private void StartCompletionRequest(TimeSpan debounce)
    {
        CancelCompletionRequest();
        var session = SqlLanguageSession;
        var context = SqlCompletionContext;
        if (!_sqlIntelligenceAttached
            || session is null
            || (!session.IsAvailable && !session.CanRetry))
        {
            CloseCompletionWindow();
            _pendingCompletion = Task.CompletedTask;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _completionDebounceCancellation = cancellation;
        var generation = _completionGeneration;
        var sql = Editor.Document.Text;
        var cursorOffset = Editor.CaretOffset;
        _pendingCompletion = CompleteAfterDebounceAsync(
            session,
            context,
            sql,
            cursorOffset,
            generation,
            debounce,
            cancellation);
    }

    private async Task CompleteAfterDebounceAsync(
        ISqlLanguageSession session,
        SqlCompletionContext context,
        string sql,
        int cursorOffset,
        long generation,
        TimeSpan debounce,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(debounce, cancellation.Token);
            if (!IsCurrentCompletionRequest(
                session,
                context,
                sql,
                cursorOffset,
                generation))
            {
                return;
            }

            var activeTransport = _completionTransport;
            if (ReferenceEquals(_completionTransportSession, session)
                && activeTransport is { IsCompleted: false })
            {
                try
                {
                    // Cancels only this superseded UI waiter. The framed worker
                    // exchange remains alive and is never given this token.
                    await activeTransport.WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Its owning request reports the failure if it is still current.
                }

                if (!IsCurrentCompletionRequest(
                    session,
                    context,
                    sql,
                    cursorOffset,
                    generation))
                {
                    return;
                }
            }

            ReleaseCompletionDebounce(cancellation);
            // Cancelling an in-flight framed exchange restarts the native worker;
            // coalesce behind one exchange and generation-fence superseded results.
            var transport = session.CompleteAsync(
                sql,
                cursorOffset,
                context,
                CancellationToken.None);
            _completionTransportSession = session;
            _completionTransport = transport;
            try
            {
                var result = await transport;
                if (IsCurrentCompletionRequest(
                    session,
                    context,
                    sql,
                    cursorOffset,
                    generation))
                {
                    ApplySqlLanguageSessionStatus(session);
                    ShowCompletion(result, cursorOffset);
                }
            }
            finally
            {
                if (ReferenceEquals(_completionTransport, transport))
                {
                    _completionTransport = null;
                    _completionTransportSession = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer edit superseded this request before its debounce elapsed.
        }
        catch
        {
            if (IsCurrentCompletionRequest(
                session,
                context,
                sql,
                cursorOffset,
                generation))
            {
                ApplySqlLanguageSessionStatus(session);
                CloseCompletionWindow();
            }
        }
        finally
        {
            ReleaseCompletionDebounce(cancellation);
            cancellation.Dispose();
        }
    }

    private bool IsCurrentCompletionRequest(
        ISqlLanguageSession session,
        SqlCompletionContext context,
        string sql,
        int cursorOffset,
        long generation) =>
        generation == _completionGeneration
        && ReferenceEquals(session, SqlLanguageSession)
        && Equals(context, SqlCompletionContext)
        && string.Equals(sql, Editor.Document.Text, StringComparison.Ordinal)
        && cursorOffset == Editor.CaretOffset
        && _sqlIntelligenceAttached;

    private void ShowCompletion(SqlCompletionResult result, int cursorOffset)
    {
        CloseCompletionWindow();
        if (result.Items.Count == 0)
        {
            return;
        }

        var (start, end) = ClampReplacementRange(result, Editor.Document.TextLength);
        if (cursorOffset < start || cursorOffset > end)
        {
            return;
        }

        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = start,
            EndOffset = end,
            CloseWhenCaretAtBeginning = false,
        };
        foreach (var item in result.Items.Take(MaximumVisibleCompletionItems))
        {
            window.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window))
            {
                _completionWindow = null;
            }
        };
        _completionWindow = window;
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];
        window.Show();
    }

    private static (int Start, int End) ClampReplacementRange(
        SqlCompletionResult result,
        int textLength)
    {
        var start = Math.Clamp(result.ReplacementStart, 0, textLength);
        var rawEnd = (long)result.ReplacementStart
            + Math.Max(0, result.ReplacementLength);
        var end = (int)Math.Clamp(rawEnd, start, textLength);
        return (start, end);
    }

    private void CancelCompletionRequest()
    {
        _completionGeneration++;
        _completionDebounceCancellation?.Cancel();
        _completionDebounceCancellation = null;
    }

    private void ReleaseCompletionDebounce(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_completionDebounceCancellation, cancellation))
        {
            _completionDebounceCancellation = null;
        }
    }

    private void CancelCompletionAndCloseWindow()
    {
        CancelCompletionRequest();
        CloseCompletionWindow();
        _pendingCompletion = Task.CompletedTask;
    }

    private void CloseCompletionWindow()
    {
        var window = _completionWindow;
        _completionWindow = null;
        window?.Hide();
    }

    internal Task RequestCompletionForTestingAsync()
    {
        StartCompletionRequest(TimeSpan.Zero);
        return _pendingCompletion;
    }
}
