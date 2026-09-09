using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

/// <summary>
/// The permissions dialog: one table of parties for both models, because a
/// filesystem's owner-group-everyone and an object store's accounts are the
/// same question asked of different systems.
///
/// The one thing both must get right is that closing without changing anything
/// is not a write — a write bumps a version somebody else may be holding, and
/// on an object store it replaces the whole list.
/// </summary>
public sealed class FileAccessControlEditorViewModelTests
{
    private static readonly FilePanelLocation Location = new(
        "files.remote",
        "example",
        new FilePanelAddress.Hierarchical(
            FilePanelPath.Root.Append(new FilePanelPathSegment("script.sh"))));

    private static FileAccessControlEditorViewModel Posix(int mode, bool canEdit = true) =>
        new(
            "script.sh",
            "example",
            new FilePanelAccessControl(Location, new FilePanelPosixMode(mode)),
            canEdit);

    [Fact]
    public void A_filesystem_shows_its_three_parties_and_what_each_is_allowed()
    {
        var editor = Posix(0b111_101_100);

        Assert.True(editor.HasMode);
        Assert.Equal(["Owner", "Group", "Everyone"], editor.Rows.Select(row => row.Name), StringComparer.Ordinal);
        Assert.Equal("Read & Write", editor.Rows[0].Privilege);
        Assert.Equal("Read only", editor.Rows[1].Privilege);
        Assert.Equal("Read only", editor.Rows[2].Privilege);
        Assert.Contains("754", editor.ModeText, StringComparison.Ordinal);

        // The execute bit gets a column of its own. Finder hides it; a shell
        // cannot, because it is the difference between a script that runs and
        // one that does not.
        Assert.All(editor.Rows, row => Assert.True(row.ShowsExecute));
        Assert.True(editor.Rows[0].CanRun);
        Assert.True(editor.Rows[1].CanRun);
        Assert.False(editor.Rows[2].CanRun);
    }

    [Fact]
    public void Closing_without_changing_anything_is_not_a_write()
    {
        var editor = Posix(0b110_100_100);

        Assert.Null(editor.BuildRequest());

        // And setting a row back the way it was is still nothing.
        editor.Rows[0].Privilege = "Read only";
        editor.Rows[0].Privilege = "Read & Write";

        Assert.Null(editor.BuildRequest());
    }

    [Fact]
    public void Changing_a_party_is_sent_as_the_whole_mode()
    {
        var editor = Posix(0b110_100_100);

        editor.Rows[1].Privilege = "No access";
        editor.Rows[0].CanRun = true;

        var request = editor.BuildRequest();

        Assert.NotNull(request);
        Assert.Equal("704", request!.Mode!.Octal);
        Assert.Null(request.Grants);
        Assert.Equal(Location, request.Location);
    }

    /// <summary>
    /// Execute is separate from the privilege, so setting one must not disturb
    /// the other — a row switched to read-only keeps whether it can run.
    /// </summary>
    [Fact]
    public void The_run_column_and_the_privilege_do_not_reach_into_each_other()
    {
        var editor = Posix(0b111_101_101);

        editor.Rows[0].Privilege = "Read only";

        Assert.True(editor.Rows[0].CanRun);
        Assert.Equal("555", editor.BuildRequest()!.Mode!.Octal);
    }

    /// <summary>
    /// A connection that reports access without accepting changes gets a
    /// dialog that says so, and cannot be made to send one anyway.
    /// </summary>
    [Fact]
    public void A_reading_that_cannot_be_changed_never_becomes_a_write()
    {
        var editor = Posix(0b110_100_100, canEdit: false);

        editor.Rows[0].CanRun = true;

        Assert.False(editor.CanEdit);
        Assert.Null(editor.BuildRequest());
    }

    private static FileAccessControlEditorViewModel ObjectStore(
        params FilePanelAccessGrant[] grants) =>
        new(
            "report.csv",
            "bucket",
            new FilePanelAccessControl(Location, grants: grants),
            canEdit: true);

    [Fact]
    public void An_object_store_shows_one_row_per_party_and_no_run_column()
    {
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                FilePanelAccessRight.Read),
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                FilePanelAccessRight.FullControl));

        Assert.False(editor.HasMode);
        Assert.Equal(["Everyone", "p3179430"], editor.Rows.Select(row => row.Name), StringComparer.Ordinal);
        Assert.Equal("Read only", editor.Rows[0].Privilege);
        Assert.Equal("Full control", editor.Rows[1].Privilege);
        Assert.All(editor.Rows, row => Assert.False(row.ShowsExecute));
        Assert.Null(editor.BuildRequest());
    }

    [Fact]
    public void Changing_what_a_party_holds_sends_the_whole_list_back()
    {
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                FilePanelAccessRight.Read),
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                FilePanelAccessRight.FullControl));

        editor.Rows[0].Privilege = "No access";

        var request = editor.BuildRequest();

        // The whole list, minus the party that now holds nothing — which is how
        // the service is told to drop it, there being no way to remove one.
        Assert.NotNull(request);
        Assert.Null(request!.Mode);
        var grant = Assert.Single(request.Grants!);
        Assert.Equal("p3179430", grant.Grantee.Id);
        Assert.Equal(FilePanelAccessRight.FullControl, grant.Rights);
    }

    /// <summary>
    /// A grant the choices cannot express exactly shows as the smallest one
    /// that covers it, and keeps what it actually has until somebody picks
    /// something else. Leaving a row alone never quietly takes a right away.
    /// </summary>
    [Fact]
    public void A_grant_the_choices_cannot_express_is_shown_rounded_up_and_kept_exactly()
    {
        var exact = FilePanelAccessRight.Read | FilePanelAccessRight.ReadAcl;
        var editor = ObjectStore(
            new FilePanelAccessGrant(
                new FilePanelGrantee(FilePanelGranteeKind.User, "auditor"),
                exact));

        Assert.Equal("Full control", editor.Rows[0].Privilege);
        Assert.Null(editor.BuildRequest());

        editor.Rows[0].Privilege = "Read only";

        Assert.Equal(
            FilePanelAccessRight.Read,
            Assert.Single(editor.BuildRequest()!.Grants!).Rights);
    }
}
