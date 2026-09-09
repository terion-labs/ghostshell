using Asura.App.Views;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class ShellClosePrivacyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_prompts_preserve_decisions_but_hide_protected_details_while_locked(bool locked)
    {
        var discards = new List<(string Title, string Detail)>();
        var scopes = new List<CloseScopeResult.ConfirmationRequired>();
        var errors = new List<string>();
        var layoutPrompts = 0;
        var nativeCloses = 0;
        var presentation = new ShellClosePresentation(
            () => { layoutPrompts++; return Task.FromResult(false); },
            (title, detail) => { discards.Add((title, detail)); return Task.FromResult(false); },
            scope => { scopes.Add(scope); return Task.FromResult(true); },
            message => { errors.Add(message); return Task.CompletedTask; },
            () => { }, () => { }, () => nativeCloses++);
        var guarded = presentation.WithPrivacyGuard(() => locked);
        var confirmation = new CloseScopeResult.ConfirmationRequired(
            CloseScopeKind.Window, "sensitive-workspace",
            [new(new SessionId("session"), new PanelInstanceId("panel"), "production database", "private shell command", 1)]);

        Assert.False(await guarded.ConfirmDiscardAsync("Discard database changes?", "Changes to payroll will be lost."));
        Assert.False(await guarded.ConfirmLayoutDiscardAsync());
        Assert.True(await guarded.ConfirmScopeAsync(confirmation));
        await guarded.ShowErrorAsync("Failed to close payroll database.");
        guarded.CloseWindow();

        Assert.Equal(1, nativeCloses);
        if (locked)
        {
            Assert.Equal(0, layoutPrompts);
            Assert.Equal(2, discards.Count);
            Assert.All(discards, prompt => Assert.Equal(
                ("Discard unsaved changes?", "Unsaved changes will be lost."), prompt));
            Assert.Empty(Assert.Single(scopes).Sessions);
            Assert.Empty(scopes[0].TargetId);
            Assert.Equal("The application could not finish closing. Unlock it to review the details.", Assert.Single(errors));
        }
        else
        {
            Assert.Equal(1, layoutPrompts);
            Assert.Equal(("Discard database changes?", "Changes to payroll will be lost."), Assert.Single(discards));
            Assert.Same(confirmation, Assert.Single(scopes));
            Assert.Equal("Failed to close payroll database.", Assert.Single(errors));
        }
    }

    [Fact]
    public async Task Privacy_guard_reads_lock_state_when_the_prompt_is_requested()
    {
        var locked = false;
        var details = new List<string>();
        var presentation = new ShellClosePresentation(
            () => Task.FromResult(true),
            (_, detail) => { details.Add(detail); return Task.FromResult(true); },
            _ => Task.FromResult(true), _ => Task.CompletedTask,
            () => { }, () => { }, () => { }).WithPrivacyGuard(() => locked);

        locked = true;
        await presentation.ConfirmDiscardAsync("Database", "private name");
        locked = false;
        await presentation.ConfirmDiscardAsync("Database", "private name");

        Assert.Equal(["Unsaved changes will be lost.", "private name"], details);
    }
}
