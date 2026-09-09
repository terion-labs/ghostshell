using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// The actions as the running shell builds them, rather than as a panel test
/// builds them.
///
/// A panel constructed directly in a test has no hosted session behind it. The
/// shell's panels do: they are handed a saved provider to bind to, and until
/// that binding lands the panel is deliberately inert. Everything the panel can
/// be asked to do reads that state, so a binding that never finishes leaves a
/// menu in which every entry is greyed out over a folder full of files.
/// </summary>
public sealed class FilePanelActionsThroughTheShellTests
{
    [Fact]
    public async Task A_bound_panel_with_a_file_selected_offers_what_the_connection_can_do()
    {
        var client = new HomeFilePanelClient();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            transferQueue: null,
            initialProfileId: new FileProviderProfileId(HomeFilePanelClient.HomeId),
            initialLocation: client.Root,
            deferInitialization: true);

        await panel.StartInitialization();
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "notes.md", StringComparison.Ordinal));
        panel.SetSelectedEntries([panel.SelectedEntry.Entry]);

        // Refresh asks nothing of the selection and nothing of the connection.
        // If it is greyed the panel believes it is busy or still binding, and
        // then every other entry is greyed for the same reason rather than for
        // one anybody could act on.
        Assert.True(
            panel.IsActionEnabled(FilePanelAction.Refresh),
            "The panel reports itself busy or unbound after a listing arrived.");
        Assert.True(panel.IsActionEnabled(FilePanelAction.Open));
        Assert.True(panel.IsActionEnabled(FilePanelAction.CopyName));
        Assert.True(panel.IsActionEnabled(FilePanelAction.CopyPath));
        Assert.True(panel.IsActionEnabled(FilePanelAction.Rename));
        Assert.True(panel.IsActionEnabled(FilePanelAction.Delete));
        Assert.True(panel.IsActionEnabled(FilePanelAction.AccessControl));

        // And the menus say the same, since they are the same list.
        Assert.All(
            panel.EntryMenuActions,
            action => Assert.True(
                action.IsEnabled,
                $"{action.Action} is offered but greyed out with a file selected."));
    }

    /// <summary>
    /// A provider profile is chosen from the connection selector rather than
    /// restored, which is the other way a panel arrives at a folder.
    /// </summary>
    [Fact]
    public async Task Choosing_a_connection_by_hand_leaves_the_actions_usable()
    {
        var client = new HomeFilePanelClient();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        await panel.StartInitialization();
        await panel.SelectProfileAsync(panel.Profiles[0]);
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "notes.md", StringComparison.Ordinal));

        Assert.True(panel.IsActionEnabled(FilePanelAction.Refresh));
        Assert.True(panel.IsActionEnabled(FilePanelAction.Rename));
    }

    /// <summary>
    /// A client that lists one folder with two things in it and declares what a
    /// local filesystem declares. Nothing here is hosted: the point is the
    /// panel's own binding state, not the session host's.
    /// </summary>
    private sealed class HomeFilePanelClient : IFilePanelClient
    {
        public const string HomeId = "builtin.files.home";

        public HomeFilePanelClient()
        {
            Root = new FilePanelLocation(
                HomeId,
                "local",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    HomeId,
                    "Home",
                    FileProviderFamily.Posix,
                    Root,
                    FilePanelCapability.List
                        | FilePanelCapability.Stat
                        | FilePanelCapability.RangedRead
                        | FilePanelCapability.StreamingWrite
                        | FilePanelCapability.CreateDirectory
                        | FilePanelCapability.Rename
                        | FilePanelCapability.Delete
                        | FilePanelCapability.Permissions,
                    500,
                    1024 * 1024),
            ];
        }

        public FilePanelLocation Root { get; }

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(
                new FilePanelPage(
                    [
                        Entry("notes.md", FilePanelEntryKind.File),
                        Entry("archive", FilePanelEntryKind.Directory),
                    ],
                    null)));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(
                Entry("notes.md", FilePanelEntryKind.File)));

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private FilePanelEntry Entry(string name, FilePanelEntryKind kind) => new(
            Root.Child(new FilePanelPathSegment(name)),
            name,
            kind,
            kind == FilePanelEntryKind.Directory ? null : 128,
            DateTimeOffset.UnixEpoch,
            IsHidden: false);
    }
}
