using Asura.Application;

namespace Asura.Application.Tests;

/// <summary>
/// What a file connection can be asked to do, decided once.
///
/// The rules used to be written out at each of the places an action appeared —
/// a toolbar button's enabled state, a menu entry's, and again in the guard
/// that ran when it was clicked. Three copies of a rule are three chances for
/// a connection to be offered something it cannot do.
/// </summary>
public sealed class FilePanelActionCatalogTests
{
    private static readonly FilePanelActionContext Ready = new()
    {
        Capabilities = FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.StreamingWrite
            | FilePanelCapability.CreateDirectory
            | FilePanelCapability.Rename
            | FilePanelCapability.Delete,
        HasLocation = true,
        IsHierarchicalLocation = true,
        SelectionCount = 1,
        SelectionIsSingleFile = true,
        SelectionIsTransferable = true,
        HasTransferQueue = true,
        HasLocalProvider = true,
    };

    private static FilePanelActionState State(
        FilePanelActionContext context,
        FilePanelAction action) =>
        FilePanelActionCatalog.Resolve(context).Single(state => state.Action == action);

    /// <summary>
    /// Not offered and offered-but-impossible are different answers. An object
    /// store cannot rename anything, ever, and a permanently greyed Rename in
    /// its menu reads as a fault in the shell rather than a fact about S3.
    /// </summary>
    [Fact]
    public void What_a_connection_cannot_do_is_not_offered_at_all()
    {
        var store = Ready with { Capabilities = FilePanelCapability.List };

        var rename = State(store, FilePanelAction.Rename);

        Assert.False(rename.IsAvailable);
        Assert.False(rename.IsEnabled);
    }

    /// <summary>
    /// While what it can do but has nothing to do it to stays on show, greyed:
    /// there the way to enable it is obvious.
    /// </summary>
    [Fact]
    public void What_a_connection_can_do_stays_on_show_with_nothing_selected()
    {
        var nothingSelected = Ready with
        {
            SelectionCount = 0,
            SelectionIsSingleFile = false,
            SelectionIsTransferable = false,
        };

        var rename = State(nothingSelected, FilePanelAction.Rename);

        Assert.True(rename.IsAvailable);
        Assert.False(rename.IsEnabled);
    }

    [Fact]
    public void Nothing_is_enabled_while_the_panel_is_busy()
    {
        var busy = Ready with { IsBusy = true };

        Assert.All(
            FilePanelActionCatalog.Resolve(busy),
            state => Assert.False(state.IsEnabled));
    }

    /// <summary>
    /// And nothing while a saved session is still binding: a panel that has not
    /// yet reached its provider cannot be told to delete something on it.
    /// </summary>
    [Fact]
    public void Nothing_is_enabled_before_a_saved_session_has_bound()
    {
        var binding = Ready with { IsBindingSavedSession = true };

        Assert.All(
            FilePanelActionCatalog.Resolve(binding),
            state => Assert.False(state.IsEnabled));
    }

    /// <summary>
    /// A flat namespace of keys has nowhere to put a folder. The capability is
    /// declared by the provider; the location is what decides whether there is
    /// a place for it right now.
    /// </summary>
    [Fact]
    public void A_folder_cannot_be_created_where_there_are_no_folders()
    {
        var objectStore = Ready with { IsHierarchicalLocation = false };

        var newFolder = State(objectStore, FilePanelAction.NewFolder);

        Assert.True(newFolder.IsAvailable);
        Assert.False(newFolder.IsEnabled);
    }

    /// <summary>
    /// Uploading is sending a file from this machine to the other end. On the
    /// machine itself there is no other end, and the file picker is the way in.
    /// </summary>
    [Fact]
    public void Uploading_is_not_offered_on_the_machine_the_shell_is_running_on()
    {
        var local = Ready with { IsLocalProvider = true };

        Assert.False(State(local, FilePanelAction.Upload).IsAvailable);
        Assert.False(State(Ready, FilePanelAction.OpenExternally).IsAvailable);
        Assert.True(State(local, FilePanelAction.OpenExternally).IsAvailable);
    }

    /// <summary>
    /// Downloading needs somewhere to download to. A session with no reach onto
    /// this machine has none.
    /// </summary>
    [Fact]
    public void Downloading_is_not_offered_without_a_way_back_to_this_machine()
    {
        var unreachable = Ready with { HasLocalProvider = false };

        Assert.False(State(unreachable, FilePanelAction.Download).IsAvailable);
        Assert.True(State(Ready, FilePanelAction.Download).IsEnabled);
    }

    /// <summary>
    /// Renaming acts on one thing. Offering it for a selection of six, where it
    /// would quietly rename whichever the list calls the selected one, is worse
    /// than not offering it.
    /// </summary>
    [Fact]
    public void Renaming_is_refused_for_more_than_one_thing()
    {
        var several = Ready with { SelectionCount = 6, SelectionIsSingleFile = false };

        Assert.False(State(several, FilePanelAction.Rename).IsEnabled);
        Assert.True(State(several, FilePanelAction.Delete).IsEnabled);
    }

    /// <summary>
    /// A right-click on a file and a right-click on the space below the last of
    /// them are different questions. Offering "New folder" over a file, or
    /// "Delete" over the empty space, is the sort of menu people stop reading.
    /// </summary>
    [Fact]
    public void What_acts_on_a_file_and_what_acts_on_the_folder_are_kept_apart()
    {
        var scopes = FilePanelActionCatalog.Resolve(Ready)
            .ToDictionary(state => state.Action, state => state.Scope);

        Assert.Equal(FilePanelActionScope.Selection, scopes[FilePanelAction.Open]);
        Assert.Equal(FilePanelActionScope.Selection, scopes[FilePanelAction.Rename]);
        Assert.Equal(FilePanelActionScope.Selection, scopes[FilePanelAction.Delete]);
        Assert.Equal(FilePanelActionScope.Selection, scopes[FilePanelAction.Copy]);
        Assert.Equal(FilePanelActionScope.Folder, scopes[FilePanelAction.Paste]);
        Assert.Equal(FilePanelActionScope.Folder, scopes[FilePanelAction.NewFolder]);
        Assert.Equal(FilePanelActionScope.Folder, scopes[FilePanelAction.Upload]);
        Assert.Equal(FilePanelActionScope.Folder, scopes[FilePanelAction.Refresh]);
    }

    /// <summary>
    /// Pasting with nothing held is offered and refused rather than hidden: the
    /// folder can take a file, there just is not one waiting.
    /// </summary>
    [Fact]
    public void Pasting_waits_for_something_to_have_been_copied()
    {
        Assert.True(State(Ready, FilePanelAction.Paste).IsAvailable);
        Assert.False(State(Ready, FilePanelAction.Paste).IsEnabled);
        Assert.True(State(Ready with { HasClipboard = true }, FilePanelAction.Paste).IsEnabled);
    }

    /// <summary>
    /// A cut is a copy that takes the original away once it lands, so a
    /// connection that cannot delete cannot cut — while it can still copy.
    /// </summary>
    [Fact]
    public void Cutting_is_not_offered_where_nothing_can_be_removed()
    {
        var readOnlyish = Ready with
        {
            Capabilities = FilePanelCapability.List | FilePanelCapability.StreamingWrite,
        };

        Assert.False(State(readOnlyish, FilePanelAction.Cut).IsAvailable);
        Assert.True(State(readOnlyish, FilePanelAction.Copy).IsAvailable);
    }

    /// <summary>
    /// Copying a name or a path asks nothing of the connection: both are
    /// already in hand, and the clipboard belongs to this machine.
    /// </summary>
    [Fact]
    public void A_name_and_a_path_can_be_copied_from_any_connection()
    {
        var barelyAnything = Ready with { Capabilities = FilePanelCapability.List };

        Assert.True(State(barelyAnything, FilePanelAction.CopyName).IsEnabled);
        Assert.True(State(barelyAnything, FilePanelAction.CopyPath).IsEnabled);
        Assert.True(State(barelyAnything, FilePanelAction.Refresh).IsEnabled);
    }

    /// <summary>
    /// The one place, asked the one way: the guard that runs on a click reads
    /// the same answer the greying-out did.
    /// </summary>
    [Fact]
    public void Asking_about_one_action_agrees_with_asking_about_all_of_them()
    {
        foreach (var state in FilePanelActionCatalog.Resolve(Ready))
        {
            Assert.Equal(
                state.IsEnabled,
                FilePanelActionCatalog.IsEnabled(Ready, state.Action));
        }
    }

    /// <summary>
    /// Every action the enum names is answered. A new one that the catalog does
    /// not decide about would be invisible everywhere, which is a confusing way
    /// to find out it was never wired up.
    /// </summary>
    [Fact]
    public void Every_action_gets_an_answer()
    {
        var answered = FilePanelActionCatalog.Resolve(Ready)
            .Select(state => state.Action)
            .ToArray();

        Assert.Equal(Enum.GetValues<FilePanelAction>().Order(), answered.Order());
        Assert.Equal(answered.Length, answered.Distinct().Count());
    }
}
