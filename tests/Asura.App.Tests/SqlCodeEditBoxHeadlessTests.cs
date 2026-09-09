using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Application;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class SqlCodeEditBoxHeadlessTests
{
    [Fact]
    public Task Completion_applies_the_worker_utf16_range_and_keeps_item_metadata() =>
        RunHeadlessAsync(async fixture =>
        {
            const string sql = "SELECT '😀', na FROM people";
            var replacementStart = sql.IndexOf("na", StringComparison.Ordinal);
            var language = new FakeSqlLanguageSession
            {
                Complete = (_, _, _) => Task.FromResult(new SqlCompletionResult(
                    replacementStart,
                    2,
                    [new SqlCompletionItem(
                        "name",
                        SqlCompletionItemKind.Column,
                        "people.name · varchar",
                        "name")]))
            };
            fixture.Editor.Text = sql;
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.EditorForTesting.CaretOffset = replacementStart + 2;

            await fixture.Editor.RequestCompletionForTestingAsync();

            var window = Assert.IsType<CompletionWindow>(
                fixture.Editor.ActiveCompletionWindowForTesting);
            var item = Assert.IsType<SqlCompletionData>(
                Assert.Single(window.CompletionList.CompletionData));
            Assert.Equal("name", item.Label);
            Assert.Equal(SqlCompletionItemKind.Column, item.Kind);
            Assert.Equal("people.name · varchar", item.Detail);
            Assert.Equal(replacementStart, window.StartOffset);
            Assert.Equal(replacementStart + 2, window.EndOffset);

            window.CompletionList.RequestInsertion(EventArgs.Empty);

            Assert.Equal("SELECT '😀', name FROM people", fixture.Editor.Text);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Empty_editor_and_dot_send_the_exact_text_and_utf16_caret() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.SqlLanguageSession = language;

            await fixture.Editor.RequestCompletionForTestingAsync();
            Assert.Equal((string.Empty, 0), Assert.Single(language.CompletionRequests));

            fixture.Editor.Text = "SELECT p";
            fixture.Editor.FocusEditor(caretToEnd: true);
            fixture.Window.KeyTextInput(".");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Equal(("SELECT p.", 9), language.CompletionRequests[^1]);
        });

    [Fact]
    public Task Completion_context_changes_are_forwarded_and_close_stale_suggestions() =>
        RunHeadlessAsync(async fixture =>
        {
            var first = new SqlCompletionContext(
                new DatabaseObjectId(null, "main", "articles"));
            var second = new SqlCompletionContext(
                new DatabaseObjectId(null, "main", "authors"));
            var language = FakeSqlLanguageSession.WithCompletion("id");
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.SqlCompletionContext = first;

            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Equal(first, Assert.Single(language.CompletionContexts));
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);

            fixture.Editor.SqlCompletionContext = second;

            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
            await fixture.Editor.RequestCompletionForTestingAsync();

            fixture.Editor.SqlCompletionContext = SqlCompletionContext.Empty;
            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Equal(
                [first, second, SqlCompletionContext.Empty],
                language.CompletionContexts);
        });

    [Fact]
    public Task Dot_completion_accepts_plain_and_quoted_identifier_qualifiers() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.SqlLanguageSession = language;

            foreach (var qualifier in new[] { "schema", "\"alias\"", "`alias`", "[alias]" })
            {
                fixture.Editor.Text = $"SELECT {qualifier}";
                fixture.Editor.FocusEditor(caretToEnd: true);
                fixture.Window.KeyTextInput(".");
                await fixture.Editor.PendingCompletionForTesting;

                Assert.Equal(
                    ($"SELECT {qualifier}.", $"SELECT {qualifier}.".Length),
                    language.CompletionRequests[^1]);
            }

            Assert.Equal(4, language.CompletionRequests.Count);
        });

    [Fact]
    public Task Dot_completion_ignores_numeric_literals() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.Text = "SELECT 1";
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyTextInput(".");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Equal("SELECT 1.", fixture.Editor.Text);
            Assert.Empty(language.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);

            fixture.Editor.Text = "SELECT ";
            fixture.Editor.FocusEditor(caretToEnd: true);
            fixture.Window.KeyTextInput(".");
            fixture.Window.KeyTextInput("5");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Equal("SELECT .5", fixture.Editor.Text);
            Assert.Empty(language.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Dot_completion_ignores_strings_comments_and_open_quoted_content() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.SqlLanguageSession = language;
            var noisyPrefixes = new[]
            {
                "SELECT 'alias'",
                "SELECT 'alias",
                "SELECT -- alias",
                "SELECT /* alias",
                "SELECT /* outer /* nested */ alias",
                "SELECT \"alias",
                "SELECT `alias",
                "SELECT [alias",
                "SELECT $tag$alias",
                "SELECT $$alias",
            };

            foreach (var prefix in noisyPrefixes)
            {
                fixture.Editor.Text = prefix;
                fixture.Editor.FocusEditor(caretToEnd: true);
                fixture.Window.KeyTextInput(".");
                await fixture.Editor.PendingCompletionForTesting;

                Assert.Equal($"{prefix}.", fixture.Editor.Text);
            }

            Assert.Empty(language.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Typing_a_keyword_prefix_opens_completion_and_enter_accepts_where() =>
        RunHeadlessAsync(async fixture =>
        {
            const string prefix = "SELECT * FROM people ";
            var language = new FakeSqlLanguageSession
            {
                Complete = (sql, cursor, _) => Task.FromResult(new SqlCompletionResult(
                    prefix.Length,
                    cursor - prefix.Length,
                    [new SqlCompletionItem(
                        "WHERE",
                        SqlCompletionItemKind.Keyword,
                        "SQL keyword",
                        "WHERE")])),
            };
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = prefix;
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyTextInput("W");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Equal(($"{prefix}W", prefix.Length + 1),
                Assert.Single(language.CompletionRequests));
            var window = Assert.IsType<CompletionWindow>(
                fixture.Editor.ActiveCompletionWindowForTesting);
            Assert.Equal(
                "WHERE",
                Assert.IsType<SqlCompletionData>(
                    Assert.Single(window.CompletionList.CompletionData)).Label);

            fixture.Window.KeyPress(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                null);

            Assert.Equal($"{prefix}WHERE", fixture.Editor.Text);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Value_keyword_and_function_completion_replace_only_the_typed_prefix() =>
        RunHeadlessAsync(async fixture =>
        {
            const string expressionPrefix =
                "SELECT * FROM people WHERE created_at < ";
            var language = new FakeSqlLanguageSession
            {
                Complete = (sql, cursor, _) =>
                {
                    var item = sql switch
                    {
                        _ when sql.EndsWith("cur", StringComparison.Ordinal) =>
                            new SqlCompletionItem(
                                "CURRENT_TIMESTAMP",
                                SqlCompletionItemKind.Keyword,
                                "SQL value keyword",
                                "CURRENT_TIMESTAMP"),
                        _ when sql.EndsWith("cou", StringComparison.Ordinal) =>
                            new SqlCompletionItem(
                                "COUNT",
                                SqlCompletionItemKind.Function,
                                "SQL function",
                                "COUNT("),
                        _ => new SqlCompletionItem(
                            "COALESCE",
                            SqlCompletionItemKind.Function,
                            "SQL function",
                            "COALESCE("),
                    };
                    var replacementLength = sql.EndsWith("co", StringComparison.Ordinal)
                        ? 2
                        : 3;
                    return Task.FromResult(new SqlCompletionResult(
                        cursor - replacementLength,
                        replacementLength,
                        [item]));
                },
            };
            fixture.Editor.SqlLanguageSession = language;
            Assert.Equal(
                TimeSpan.FromMilliseconds(20),
                fixture.Editor.CompletionDebounceForTesting);

            fixture.Editor.Text = $"{expressionPrefix}cu";
            fixture.Editor.FocusEditor(caretToEnd: true);
            fixture.Window.KeyTextInput("r");
            await fixture.Editor.PendingCompletionForTesting;
            AcceptCompletion(
                fixture,
                fixture.Editor,
                expressionPrefix.Length,
                replacementLength: 3,
                "CURRENT_TIMESTAMP",
                SqlCompletionItemKind.Keyword,
                "CURRENT_TIMESTAMP");
            Assert.Equal(
                $"{expressionPrefix}CURRENT_TIMESTAMP",
                fixture.Editor.Text);
            Assert.Equal(fixture.Editor.Text.Length, fixture.Editor.EditorForTesting.CaretOffset);

            fixture.Editor.Text = $"{expressionPrefix}c";
            fixture.Editor.FocusEditor(caretToEnd: true);
            fixture.Window.KeyTextInput("o");
            await fixture.Editor.PendingCompletionForTesting;
            AcceptCompletion(
                fixture,
                fixture.Editor,
                expressionPrefix.Length,
                replacementLength: 2,
                "COALESCE",
                SqlCompletionItemKind.Function,
                "COALESCE(");
            Assert.Equal($"{expressionPrefix}COALESCE(", fixture.Editor.Text);
            Assert.Equal(fixture.Editor.Text.Length, fixture.Editor.EditorForTesting.CaretOffset);

            const string projectionPrefix = "SELECT ";
            fixture.Editor.Text = $"{projectionPrefix}co";
            fixture.Editor.FocusEditor(caretToEnd: true);
            fixture.Window.KeyTextInput("u");
            await fixture.Editor.PendingCompletionForTesting;
            AcceptCompletion(
                fixture,
                fixture.Editor,
                projectionPrefix.Length,
                replacementLength: 3,
                "COUNT",
                SqlCompletionItemKind.Function,
                "COUNT(");
            Assert.Equal($"{projectionPrefix}COUNT(", fixture.Editor.Text);
            Assert.Equal(fixture.Editor.Text.Length, fixture.Editor.EditorForTesting.CaretOffset);
        });

    [Fact]
    public Task Default_identifier_completion_debounce_stays_within_the_fluent_budget() =>
        RunHeadlessAsync(fixture =>
        {
            Assert.InRange(
                fixture.Editor.CompletionDebounceForTesting,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20));
            return Task.CompletedTask;
        });

    [Fact]
    public Task Identifier_completion_ignores_literals_comments_and_numeric_tokens() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.SqlLanguageSession = language;
            var noisyInputs = new[]
            {
                (Prefix: "SELECT 'alia", Input: "s"),
                (Prefix: "SELECT -- alia", Input: "s"),
                (Prefix: "SELECT /* alia", Input: "s"),
                (Prefix: "SELECT /* outer /* nested */ alia", Input: "s"),
                (Prefix: "SELECT $tag$alia", Input: "s"),
                (Prefix: "SELECT $$alia", Input: "s"),
                (Prefix: "SELECT 'co", Input: "u"),
                (Prefix: "SELECT -- co", Input: "u"),
                (Prefix: "SELECT /* co", Input: "u"),
                (Prefix: "SELECT 12", Input: "a"),
            };

            foreach (var (prefix, input) in noisyInputs)
            {
                fixture.Editor.Text = prefix;
                fixture.Editor.FocusEditor(caretToEnd: true);
                fixture.Window.KeyTextInput(input);
                await fixture.Editor.PendingCompletionForTesting;
            }

            Assert.Empty(language.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Identifier_completion_remains_available_inside_delimited_identifiers() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession();
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.SqlLanguageSession = language;
            var prefixes = new[] { "SELECT \"na", "SELECT `na", "SELECT [na" };

            foreach (var prefix in prefixes)
            {
                fixture.Editor.Text = prefix;
                fixture.Editor.FocusEditor(caretToEnd: true);
                fixture.Window.KeyTextInput("m");
                await fixture.Editor.PendingCompletionForTesting;
            }

            Assert.Equal(3, language.CompletionRequests.Count);
        });

    [Fact]
    public Task Typing_a_column_prefix_after_where_opens_completion_and_escape_closes_it() =>
        RunHeadlessAsync(async fixture =>
        {
            const string prefix = "SELECT * FROM people WHERE ";
            var language = new FakeSqlLanguageSession
            {
                Complete = (_, cursor, _) => Task.FromResult(new SqlCompletionResult(
                    prefix.Length,
                    cursor - prefix.Length,
                    [new SqlCompletionItem(
                        "name",
                        SqlCompletionItemKind.Column,
                        "people.name · text",
                        "name")])),
            };
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = prefix;
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyTextInput("n");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Equal(($"{prefix}n", prefix.Length + 1),
                Assert.Single(language.CompletionRequests));
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);

            fixture.Window.KeyPress(
                Key.Escape,
                RawInputModifiers.None,
                PhysicalKey.Escape,
                null);

            Assert.Equal($"{prefix}n", fixture.Editor.Text);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);

            fixture.Window.KeyTextInput(" ");
            fixture.Window.KeyTextInput(";");
            await fixture.Editor.PendingCompletionForTesting;

            Assert.Single(language.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task New_identifier_input_cancels_a_pending_completion_debounce() =>
        RunHeadlessAsync(async fixture =>
        {
            const string prefix = "SELECT * FROM people ";
            var language = FakeSqlLanguageSession.WithCompletion("WHERE");
            fixture.Editor.CompletionDebounceForTesting = Timeout.InfiniteTimeSpan;
            fixture.Editor.Text = prefix;
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyTextInput("W");
            var cancelledDebounce = fixture.Editor.PendingCompletionForTesting;
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Window.KeyTextInput("H");
            var currentCompletion = fixture.Editor.PendingCompletionForTesting;

            await cancelledDebounce;
            await currentCompletion;

            Assert.Equal(($"{prefix}WH", prefix.Length + 2),
                Assert.Single(language.CompletionRequests));
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task Hung_completion_keeps_only_the_latest_waiter_without_cancelling_transport() =>
        RunHeadlessAsync(async fixture =>
        {
            const string prefix = "SELECT * FROM people ";
            var firstStarted = NewSignal<CancellationToken>();
            var firstResult = NewSignal<SqlCompletionResult>();
            var calls = 0;
            var language = new FakeSqlLanguageSession
            {
                Complete = async (_, cursor, cancellationToken) =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        firstStarted.TrySetResult(cancellationToken);
                        return await firstResult.Task;
                    }

                    return new SqlCompletionResult(
                        prefix.Length,
                        cursor - prefix.Length,
                        [new SqlCompletionItem(
                            "WHERE",
                            SqlCompletionItemKind.Keyword,
                            null,
                            "WHERE")]);
                },
            };
            fixture.Editor.CompletionDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = prefix;
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyTextInput("W");
            var staleCompletion = fixture.Editor.PendingCompletionForTesting;
            var staleToken = await firstStarted.Task;
            var suffix = new string('x', 64);
            var supersededWaiters = new List<Task>();
            Task? currentCompletion = null;
            foreach (var character in suffix)
            {
                if (currentCompletion is not null)
                {
                    supersededWaiters.Add(currentCompletion);
                }

                fixture.Window.KeyTextInput(character.ToString());
                currentCompletion = fixture.Editor.PendingCompletionForTesting;
            }

            await Task.WhenAll(supersededWaiters).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(staleToken.CanBeCanceled);
            Assert.False(staleToken.IsCancellationRequested);
            Assert.Equal(1, calls);
            Assert.NotNull(currentCompletion);
            Assert.False(currentCompletion.IsCompleted);

            firstResult.TrySetResult(new SqlCompletionResult(
                prefix.Length,
                1,
                [new SqlCompletionItem(
                    "WRONG",
                    SqlCompletionItemKind.Keyword,
                    null,
                    "WRONG")]));
            await staleCompletion;
            await currentCompletion;

            Assert.Equal(2, calls);
            Assert.Equal(($"{prefix}W{suffix}", prefix.Length + suffix.Length + 1),
                language.CompletionRequests[^1]);
            Assert.Equal(
                "WHERE",
                Assert.IsType<SqlCompletionData>(Assert.Single(
                    fixture.Editor.ActiveCompletionWindowForTesting!
                        .CompletionList.CompletionData)).Label);
        });

    [Fact]
    public Task Control_and_command_space_open_completion_without_inserting_a_space() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = FakeSqlLanguageSession.WithCompletion("people");
            fixture.Editor.Text = "SELECT * FROM pe";
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);

            fixture.Window.KeyPress(
                Key.Space,
                RawInputModifiers.Control,
                PhysicalKey.Space,
                " ");
            await fixture.Editor.PendingCompletionForTesting;
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);
            Assert.Equal("SELECT * FROM pe", fixture.Editor.Text);

            fixture.Window.KeyPress(
                Key.Space,
                RawInputModifiers.Meta,
                PhysicalKey.Space,
                " ");
            await fixture.Editor.PendingCompletionForTesting;
            Assert.Equal(2, language.CompletionRequests.Count);
            Assert.Equal("SELECT * FROM pe", fixture.Editor.Text);
        });

    [Fact]
    public Task Command_enter_reaches_the_query_shortcut_before_an_open_popup() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = FakeSqlLanguageSession.WithCompletion("SELECT");
            fixture.Editor.Text = "SEL";
            fixture.Editor.SqlLanguageSession = language;
            fixture.Editor.FocusEditor(caretToEnd: true);
            await fixture.Editor.RequestCompletionForTestingAsync();
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);
            var runs = 0;
            fixture.Editor.EditorKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter
                    && (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                        || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                {
                    runs++;
                    e.Handled = true;
                }
            };

            fixture.Window.KeyPress(
                Key.Enter,
                RawInputModifiers.Meta,
                PhysicalKey.Enter,
                null);
            fixture.Window.KeyPress(
                Key.Enter,
                RawInputModifiers.Control,
                PhysicalKey.Enter,
                null);

            Assert.Equal(2, runs);
            Assert.Equal("SEL", fixture.Editor.Text);
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);
        });

    [Fact]
    public Task New_text_cancels_a_pending_diagnostic_debounce() =>
        RunHeadlessAsync(async fixture =>
        {
            var diagnosticRequests = new List<string>();
            var language = new FakeSqlLanguageSession
            {
                Diagnose = (sql, _) =>
                {
                    diagnosticRequests.Add(sql);
                    return Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);
                },
            };
            fixture.Editor.DiagnosticDebounceForTesting = Timeout.InfiniteTimeSpan;
            fixture.Editor.Text = "old";
            fixture.Editor.SqlLanguageSession = language;
            var cancelledDebounce = fixture.Editor.PendingDiagnosticsForTesting;

            fixture.Editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = "new";
            var currentDiagnostics = fixture.Editor.PendingDiagnosticsForTesting;

            await cancelledDebounce;
            await currentDiagnostics;

            Assert.Equal("new", Assert.Single(diagnosticRequests));
        });

    [Fact]
    public Task An_in_flight_diagnostic_is_not_cancelled_and_cannot_repaint_new_text() =>
        RunHeadlessAsync(async fixture =>
        {
            var firstStarted = NewSignal<CancellationToken>();
            var firstResult = NewSignal<IReadOnlyList<SqlDiagnostic>>();
            var calls = 0;
            var language = new FakeSqlLanguageSession
            {
                Diagnose = async (_, cancellationToken) =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        firstStarted.TrySetResult(cancellationToken);
                        return await firstResult.Task;
                    }

                    return [new SqlDiagnostic(
                        "new text diagnostic",
                        SqlDiagnosticSeverity.Warning,
                        0,
                        3)];
                }
            };
            fixture.Editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = "old";
            fixture.Editor.SqlLanguageSession = language;
            var staleTask = fixture.Editor.PendingDiagnosticsForTesting;
            var staleToken = await firstStarted.Task;

            fixture.Editor.Text = "new";
            await fixture.Editor.PendingDiagnosticsForTesting;

            Assert.False(staleToken.CanBeCanceled);
            Assert.False(staleToken.IsCancellationRequested);
            Assert.Equal("new text diagnostic", Assert.Single(fixture.Editor.Diagnostics).Message);

            firstResult.TrySetResult([
                new SqlDiagnostic("stale", SqlDiagnosticSeverity.Error, 0, 3),
            ]);
            await staleTask;
            Assert.Equal("new text diagnostic", Assert.Single(fixture.Editor.Diagnostics).Message);
        });

    [Fact]
    public Task Diagnostics_have_inline_ranges_a_live_summary_and_bounded_messages() =>
        RunHeadlessAsync(async fixture =>
        {
            var diagnostics = Enumerable.Range(0, 7)
                .Select(index => new SqlDiagnostic(
                    index == 0 ? "Unknown   column\nmissing" : $"warning {index}",
                    index == 0
                        ? SqlDiagnosticSeverity.Error
                        : SqlDiagnosticSeverity.Warning,
                    index == 0 ? 7 : 0,
                    index == 0 ? 2 : 1,
                    index == 0 ? "COLUMN_NOT_FOUND" : null))
                .ToArray();
            var language = new FakeSqlLanguageSession
            {
                Diagnose = (_, _) => Task.FromResult<IReadOnlyList<SqlDiagnostic>>(diagnostics),
            };
            fixture.Editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = "SELECT xx";
            fixture.Editor.SqlLanguageSession = language;
            await fixture.Editor.PendingDiagnosticsForTesting;

            Assert.Equal("1 error · 6 warnings", fixture.Editor.DiagnosticStatus);
            Assert.True(fixture.Editor.IsDiagnosticRendererAttachedForTesting);
            var segment = SqlDiagnosticBackgroundRenderer.CreateVisibleSegment(
                fixture.Editor.Diagnostics[0],
                fixture.Editor.Text!.Length);
            Assert.NotNull(segment);
            Assert.Equal(7, segment!.StartOffset);
            Assert.Equal(2, segment.Length);

            var status = fixture.Editor.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(control => string.Equals(
                    AutomationProperties.GetName(control),
                    "SQL diagnostics",
                    StringComparison.Ordinal));
            var tooltip = Assert.IsType<string>(ToolTip.GetTip(status));
            Assert.Contains("Error: Unknown column missing", tooltip, StringComparison.Ordinal);
            Assert.Contains("… 2 more", tooltip, StringComparison.Ordinal);
            Assert.DoesNotContain("warning 6", tooltip, StringComparison.Ordinal);
            Assert.Equal(tooltip, AutomationProperties.GetHelpText(status));
        });

    [Fact]
    public Task Swapping_or_disabling_the_session_fences_and_clears_old_state() =>
        RunHeadlessAsync(async fixture =>
        {
            var oldStarted = NewSignal<CancellationToken>();
            var oldResult = NewSignal<IReadOnlyList<SqlDiagnostic>>();
            var oldSession = new FakeSqlLanguageSession
            {
                Diagnose = async (_, token) =>
                {
                    oldStarted.TrySetResult(token);
                    return await oldResult.Task;
                }
            };
            var newSession = new FakeSqlLanguageSession
            {
                Diagnose = (_, _) => Task.FromResult<IReadOnlyList<SqlDiagnostic>>([
                    new SqlDiagnostic("new session", SqlDiagnosticSeverity.Information, 0, 1),
                ]),
            };
            fixture.Editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = "x";
            fixture.Editor.SqlLanguageSession = oldSession;
            var oldPending = fixture.Editor.PendingDiagnosticsForTesting;
            var oldToken = await oldStarted.Task;

            fixture.Editor.SqlLanguageSession = newSession;
            await fixture.Editor.PendingDiagnosticsForTesting;

            Assert.False(oldToken.CanBeCanceled);
            Assert.False(oldToken.IsCancellationRequested);
            Assert.Equal("new session", Assert.Single(fixture.Editor.Diagnostics).Message);
            Assert.Equal(0, oldSession.DisposeCount);

            oldResult.TrySetResult([
                new SqlDiagnostic("old session", SqlDiagnosticSeverity.Error, 0, 1),
            ]);
            await oldPending;
            Assert.Equal("new session", Assert.Single(fixture.Editor.Diagnostics).Message);

            var unavailable = new FakeSqlLanguageSession
            {
                IsAvailable = false,
                UnavailableReason = "worker stopped",
            };
            fixture.Editor.SqlLanguageSession = unavailable;
            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Empty(fixture.Editor.Diagnostics);
            Assert.Equal(string.Empty, fixture.Editor.DiagnosticStatus);
            Assert.Empty(unavailable.CompletionRequests);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);

            unavailable.CanRetry = true;
            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Single(unavailable.CompletionRequests);
            Assert.Equal("worker stopped", ToolTip.GetTip(fixture.Editor));
        });

    [Fact]
    public Task Recovering_an_initial_failure_does_not_keep_the_stale_error_status() =>
        RunHeadlessAsync(async fixture =>
        {
            var language = new FakeSqlLanguageSession
            {
                IsAvailable = false,
                CanRetry = true,
                UnavailableReason = "initial worker failure",
            };
            language.Complete = (_, cursor, _) =>
            {
                language.IsAvailable = true;
                language.UnavailableReason = null;
                return Task.FromResult(new SqlCompletionResult(cursor, 0, []));
            };
            fixture.Editor.SqlLanguageStatus = "SQL intelligence is unavailable: initial worker failure";
            fixture.Editor.SqlLanguageSession = language;

            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Equal(
                "SQL completion and validation are available.",
                ToolTip.GetTip(fixture.Editor));
        });

    [Fact]
    public Task Detaching_closes_completion_and_fences_work_without_owning_the_session() =>
        RunHeadlessAsync(async fixture =>
        {
            var diagnosisStarted = NewSignal<CancellationToken>();
            var diagnosisResult = NewSignal<IReadOnlyList<SqlDiagnostic>>();
            var language = FakeSqlLanguageSession.WithCompletion("people");
            language.Diagnose = async (_, token) =>
            {
                diagnosisStarted.TrySetResult(token);
                return await diagnosisResult.Task;
            };
            fixture.Editor.DiagnosticDebounceForTesting = TimeSpan.Zero;
            fixture.Editor.Text = "pe";
            fixture.Editor.SqlLanguageSession = language;
            var pendingDiagnosis = fixture.Editor.PendingDiagnosticsForTesting;
            var token = await diagnosisStarted.Task;
            await fixture.Editor.RequestCompletionForTestingAsync();
            Assert.NotNull(fixture.Editor.ActiveCompletionWindowForTesting);

            fixture.Window.Content = null;
            fixture.Window.UpdateLayout();

            Assert.False(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            Assert.Null(fixture.Editor.ActiveCompletionWindowForTesting);
            Assert.False(fixture.Editor.IsDiagnosticRendererAttachedForTesting);
            Assert.Equal(0, language.DisposeCount);

            diagnosisResult.TrySetResult([
                new SqlDiagnostic("detached", SqlDiagnosticSeverity.Error, 0, 1),
            ]);
            await pendingDiagnosis;
            Assert.Empty(fixture.Editor.Diagnostics);
        });

    [Fact]
    public Task Completion_popup_is_bounded_to_two_thousand_items() =>
        RunHeadlessAsync(async fixture =>
        {
            var items = Enumerable.Range(0, 2001)
                .Select(index => new SqlCompletionItem(
                    $"column_{index}",
                    SqlCompletionItemKind.Column,
                    null,
                    $"column_{index}"))
                .ToArray();
            var language = new FakeSqlLanguageSession
            {
                Complete = (_, _, _) => Task.FromResult(new SqlCompletionResult(
                    0,
                    0,
                    items)),
            };
            fixture.Editor.SqlLanguageSession = language;

            await fixture.Editor.RequestCompletionForTestingAsync();

            Assert.Equal(
                2000,
                fixture.Editor.ActiveCompletionWindowForTesting!
                    .CompletionList.CompletionData.Count);
        });

    [Fact]
    public Task Database_workspace_binds_a_bounded_worker_status_to_the_query_editor() =>
        RunHeadlessAsync(fixture =>
        {
            var status = $"Worker\n unavailable   {new string('x', 400)}";
            var workspace = new DatabaseWorkspaceView
            {
                DataContext = new QueryEditorStatusSource(status),
            };
            fixture.Window.Content = workspace;
            fixture.Window.UpdateLayout();

            var editor = workspace.FindControl<CodeEditBox>("QueryEditorBox");
            Assert.NotNull(editor);
            Assert.Equal(status, editor!.SqlLanguageStatus);
            var tooltip = Assert.IsType<string>(ToolTip.GetTip(editor));
            Assert.DoesNotContain('\n', tooltip);
            Assert.EndsWith("…", tooltip, StringComparison.Ordinal);
            Assert.Equal(321, tooltip.Length);
            Assert.Equal(tooltip, AutomationProperties.GetHelpText(editor));
            return Task.CompletedTask;
        });

    private static async Task RunHeadlessAsync(Func<SqlEditorFixture, Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    using var fixture = SqlEditorFixture.Create();
                    await assertion(fixture);
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AcceptCompletion(
        SqlEditorFixture fixture,
        CodeEditBox editor,
        int replacementStart,
        int replacementLength,
        string label,
        SqlCompletionItemKind kind,
        string insertText)
    {
        var completionWindow = Assert.IsType<CompletionWindow>(
            editor.ActiveCompletionWindowForTesting);
        Assert.Equal(replacementStart, completionWindow.StartOffset);
        Assert.Equal(replacementStart + replacementLength, completionWindow.EndOffset);
        var item = Assert.Single(
            completionWindow.CompletionList.CompletionData.Cast<SqlCompletionData>(),
            candidate => string.Equals(candidate.Label, label
, StringComparison.Ordinal) && candidate.Kind == kind
                && string.Equals(candidate.InsertText, insertText, StringComparison.Ordinal));
        completionWindow.CompletionList.SelectedItem = item;
        fixture.Window.KeyPress(
            Key.Enter,
            RawInputModifiers.None,
            PhysicalKey.Enter,
            keySymbol: null);
        Assert.Null(editor.ActiveCompletionWindowForTesting);
    }

    private sealed class SqlEditorFixture : IDisposable
    {
        private SqlEditorFixture(Window window, CodeEditBox editor)
        {
            Window = window;
            Editor = editor;
        }

        internal Window Window { get; }

        internal CodeEditBox Editor { get; }

        internal static SqlEditorFixture Create()
        {
            var editor = new CodeEditBox();
            var window = new Window
            {
                Width = 700,
                Height = 240,
                Content = editor,
            };
            window.Show();
            window.UpdateLayout();
            editor.FocusEditor();
            return new SqlEditorFixture(window, editor);
        }

        public void Dispose() => Window.Close();
    }

    private sealed class FakeSqlLanguageSession : ISqlLanguageSession
    {
        internal Func<string, int, CancellationToken, Task<SqlCompletionResult>> Complete
        {
            get;
            set;
        } = (_, _, _) => Task.FromResult(SqlCompletionResult.Empty);

        internal Func<string, CancellationToken, Task<IReadOnlyList<SqlDiagnostic>>> Diagnose
        {
            get;
            set;
        } = (_, _) => Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

        public bool IsAvailable { get; set; } = true;

        public bool CanRetry { get; set; }

        public string? UnavailableReason { get; set; }

        internal List<(string Sql, int CursorOffset)> CompletionRequests { get; } = [];

        internal List<SqlCompletionContext> CompletionContexts { get; } = [];

        internal int DisposeCount { get; private set; }

        public Task<SqlCompletionResult> CompleteAsync(
            string sql,
            int cursorOffset,
            CancellationToken cancellationToken) =>
            CompleteAsync(
                sql,
                cursorOffset,
                SqlCompletionContext.Empty,
                cancellationToken);

        public Task<SqlCompletionResult> CompleteAsync(
            string sql,
            int cursorOffset,
            SqlCompletionContext context,
            CancellationToken cancellationToken)
        {
            CompletionRequests.Add((sql, cursorOffset));
            CompletionContexts.Add(context);
            return Complete(sql, cursorOffset, cancellationToken);
        }

        public Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(
            string sql,
            CancellationToken cancellationToken) => Diagnose(sql, cancellationToken);

        public Task UpdateCatalogAsync(
            SqlCatalogSnapshot catalog,
            CancellationToken cancellationToken)
        {
            _ = catalog;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal static FakeSqlLanguageSession WithCompletion(string insertText) => new()
        {
            Complete = (_, cursor, _) => Task.FromResult(new SqlCompletionResult(
                cursor,
                0,
                [new SqlCompletionItem(
                    insertText,
                    SqlCompletionItemKind.Keyword,
                    "completion detail",
                    insertText)])),
        };
    }

    private sealed class QueryEditorStatusSource(string status) : ISqlQueryEditorHost
    {
        public SqlCompletionContext SqlLanguageCompletionContext { get; } = new(null);

        public string SqlLanguageStatus { get; } = status;

        public ISqlLanguageSession? SqlLanguageSession => null;

        public string QueryText { get; set; } = string.Empty;

        public bool ShowQueryEditor => true;
    }
}

public static class SqlEditorHeadlessApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Asura.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
