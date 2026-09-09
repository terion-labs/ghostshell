using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class TerminalSettingsViewModelTests
{
    [Fact]
    public void Applying_the_same_catalog_preserves_both_editor_instances()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var terminal = Assert.IsType<TerminalProfileEditorViewModel>(viewModel.TerminalEditor);
        var quickTerminal = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalEditor);

        viewModel.ApplyCatalog(fixture.Proxy.CurrentSnapshot);

        Assert.Same(terminal, viewModel.TerminalEditor);
        Assert.Same(quickTerminal, viewModel.QuickTerminalEditor);
    }

    [Fact]
    public void A_new_revision_replaces_only_the_changed_editor()
    {
        var snapshot = Snapshot();
        var fixture = CreateCatalog(snapshot);
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var terminal = Assert.IsType<TerminalProfileEditorViewModel>(viewModel.TerminalEditor);
        var quickTerminal = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalEditor);
        var revisedTerminal = snapshot.TerminalProfiles.Single() with { Revision = TerminalRevision + 1 };

        viewModel.ApplyCatalog(snapshot with { TerminalProfiles = [revisedTerminal] });

        Assert.NotSame(terminal, viewModel.TerminalEditor);
        Assert.Equal(TerminalRevision + 1, viewModel.TerminalEditor?.ExpectedRevision);
        Assert.Same(quickTerminal, viewModel.QuickTerminalEditor);
    }

    [Fact]
    public void Forced_discard_rebuilds_dirty_fields_even_at_the_same_revision()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var dirty = Assert.IsType<TerminalProfileEditorViewModel>(viewModel.TerminalEditor);
        dirty.FontFamily = "Unsaved preview";

        viewModel.DiscardTerminalDraft();

        var restored = Assert.IsType<TerminalProfileEditorViewModel>(viewModel.TerminalEditor);
        Assert.NotSame(dirty, restored);
        Assert.Equal("JetBrains Mono", restored.FontFamily);
        Assert.Equal(TerminalRevision, restored.ExpectedRevision);
    }

    [Fact]
    public async Task Terminal_save_forwards_the_editor_revision_and_conflict_keeps_the_draft()
    {
        var fixture = CreateCatalog(Snapshot());
        fixture.Proxy.RejectTerminalSave = true;
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var editor = Assert.IsType<TerminalProfileEditorViewModel>(viewModel.TerminalEditor);
        editor.FontFamily = "Unsaved conflict draft";

        var result = await viewModel.SaveTerminalProfileAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(TerminalRevision, fixture.Proxy.LastExpectedTerminalRevision);
        Assert.Same(editor, viewModel.TerminalEditor);
        Assert.Equal("Unsaved conflict draft", editor.FontFamily);
    }

    [Fact]
    public async Task Unchanged_terminal_save_does_not_write_to_the_catalog()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);

        var result = await viewModel.SaveTerminalProfileAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TerminalRevision, result.Value?.Revision);
        Assert.Null(fixture.Proxy.LastExpectedTerminalRevision);
    }

    [Fact]
    public async Task Quick_terminal_save_forwards_the_editor_revision_and_conflict_keeps_the_draft()
    {
        var fixture = CreateCatalog(Snapshot());
        fixture.Proxy.RejectQuickTerminalSave = true;
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var editor = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalEditor);
        editor.HeightPercent = 73;

        var result = await viewModel.SaveQuickTerminalSettingsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, result.Error?.Code);
        Assert.Equal(QuickTerminalRevision, fixture.Proxy.LastExpectedQuickTerminalRevision);
        Assert.Same(editor, viewModel.QuickTerminalEditor);
        Assert.Equal(73, editor.HeightPercent);
    }

    [Fact]
    public void Registration_result_is_forwarded_to_the_live_quick_terminal_editor()
    {
        var fixture = CreateCatalog(Snapshot());
        using var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        var configured = new KeyStroke("K", KeyModifiers.Meta);
        var fallback = QuickTerminalSettings.Default.Hotkey;
        var failure = new GlobalHotkeyRegistrationResult.Failure(new(
            GlobalHotkeyRegistrationErrorCode.Conflict,
            "global_hotkey_conflict",
            "Another application owns the shortcut."));

        viewModel.ApplyQuickTerminalRegistration(configured, fallback, failure);

        var editor = Assert.IsType<QuickTerminalSettingsEditorViewModel>(
            viewModel.QuickTerminalEditor);
        Assert.Contains("Another application", editor.RegistrationStatus);
        Assert.Contains("remains active", editor.RegistrationStatus);
    }

    [Fact]
    public void Disposing_the_owner_releases_both_editor_references()
    {
        var fixture = CreateCatalog(Snapshot());
        var viewModel = new TerminalSettingsViewModel(fixture.Catalog);
        Assert.NotNull(viewModel.TerminalEditor);
        Assert.NotNull(viewModel.QuickTerminalEditor);

        viewModel.Dispose();
        viewModel.Dispose();

        Assert.Null(viewModel.TerminalEditor);
        Assert.Null(viewModel.QuickTerminalEditor);
    }

    private const long TerminalRevision = 42;
    private const long QuickTerminalRevision = 43;

    private static DefinitionCatalogSnapshot Snapshot() =>
        DefinitionCatalogSnapshot.Empty with
        {
            TerminalProfiles = [Store(DefaultTerminalProfile(), TerminalRevision)],
            QuickTerminalSettings =
                [Store(QuickTerminalSettings.Default, QuickTerminalRevision)],
            Keymaps = [Store(BuiltInKeymaps.LinuxTerminal, 2)],
        };

    private static TerminalProfile DefaultTerminalProfile() =>
        new(
            new TerminalProfileId("builtin.terminal.settings-owner"),
            "Settings terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.AsuraDark,
            BuiltInKeymaps.LinuxTerminalId);

    private static StoredDefinition<T> Store<T>(T value, long revision)
        where T : IDurableDefinition =>
        new(value, revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CatalogFixture CreateCatalog(DefinitionCatalogSnapshot snapshot)
    {
        var catalog = DispatchProxy.Create<IDefinitionCatalog, RecordingCatalogProxy>();
        var proxy = (RecordingCatalogProxy)(object)catalog;
        proxy.CurrentSnapshot = snapshot;
        return new(catalog, proxy);
    }

    private sealed record CatalogFixture(
        IDefinitionCatalog Catalog,
        RecordingCatalogProxy Proxy);

    public class RecordingCatalogProxy : DispatchProxy
    {
        public DefinitionCatalogSnapshot CurrentSnapshot { get; set; } =
            DefinitionCatalogSnapshot.Empty;

        public bool RejectTerminalSave { get; set; }

        public bool RejectQuickTerminalSave { get; set; }

        public long? LastExpectedTerminalRevision { get; private set; }

        public long? LastExpectedQuickTerminalRevision { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            return targetMethod.Name switch
            {
                "get_Snapshot" => CurrentSnapshot,
                nameof(IDefinitionCatalog.SaveTerminalProfileAsync) => SaveTerminal(
                    (TerminalProfile)args[0]!,
                    (long?)args[1]),
                nameof(IDefinitionCatalog.SaveQuickTerminalSettingsAsync) => SaveQuickTerminal(
                    (QuickTerminalSettings)args[0]!,
                    (long?)args[1]),
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminal(
            TerminalProfile definition,
            long? expectedRevision)
        {
            LastExpectedTerminalRevision = expectedRevision;
            return Complete(definition, expectedRevision, RejectTerminalSave);
        }

        private ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
            SaveQuickTerminal(
                QuickTerminalSettings definition,
                long? expectedRevision)
        {
            LastExpectedQuickTerminalRevision = expectedRevision;
            return Complete(definition, expectedRevision, RejectQuickTerminalSave);
        }

        private static ValueTask<DefinitionStoreResult<StoredDefinition<T>>> Complete<T>(
            T definition,
            long? expectedRevision,
            bool reject)
            where T : IDurableDefinition =>
            ValueTask.FromResult(reject
                ? DefinitionStoreResult<StoredDefinition<T>>.Failure(new(
                    DefinitionStoreErrorCode.RevisionConflict,
                    "The settings changed before they could be saved.",
                    (expectedRevision ?? 0) + 1))
                : DefinitionStoreResult<StoredDefinition<T>>.Success(
                    Store(definition, (expectedRevision ?? 0) + 1)));
    }
}
