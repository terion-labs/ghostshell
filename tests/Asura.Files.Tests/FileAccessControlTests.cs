using System.Text;

using Asura.Application;

namespace Asura.Files.Tests;

/// <summary>
/// Who can do what, read and written through the provider seam.
///
/// The two connections that answer at all answer differently, and neither is
/// converted into the other: a filesystem has nine bits and no notion of a
/// named account, an object store has named accounts and no notion of a group.
/// A seam that flattened them would have to invent one of the two.
/// </summary>
public sealed class FileAccessControlTests
{
    [Fact]
    public async Task Native_chmod_rejects_a_link_even_if_it_appears_after_path_validation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var root = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var target = Path.Combine(outside.Path, "private");
        await File.WriteAllTextAsync(target, "private");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(Path.Combine(root.Path, "raced"), target);
        var provider = new PosixLocalFileProvider(new LocalFileProviderOptions(LocalProfile, LocalAuthority, root.Path));
        // Exercise the mutation sink directly, after the normal validation boundary.
        _ = provider.SetModeNoFollow(FilePath.Root.Append(new FilePathSegment("raced")), 0x1FF);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task Owner_can_chmod_an_unreadable_leaf_in_a_search_only_parent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var root = TemporaryDirectory.Create();
        var directory = Directory.CreateDirectory(Path.Combine(root.Path, "search-only")).FullName;
        var leaf = Path.Combine(directory, "unreadable");
        await File.WriteAllTextAsync(leaf, "data");
        File.SetUnixFileMode(leaf, UnixFileMode.None);
        File.SetUnixFileMode(directory, UnixFileMode.UserExecute);
        try
        {
            IFileProvider provider = new PosixLocalFileProvider(new LocalFileProviderOptions(LocalProfile, LocalAuthority, root.Path));
            var location = new FileLocation(LocalProfile, LocalAuthority, FilePath.Root)
                .Child(new FilePathSegment("search-only")).Child(new FilePathSegment("unreadable"));
            var changed = await provider.SetAccessControlAsync(new FileSetAccessControlRequest(location,
                new FilePanelPosixMode(0b110_000_000)), CancellationToken.None);
            Assert.True(changed.IsSuccess, changed.Error?.Message);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(leaf));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_listed_file_replaced_with_a_link_cannot_redirect_chmod()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var root = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var target = Path.Combine(outside.Path, "target");
        await File.WriteAllTextAsync(target, "private");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var path = Path.Combine(root.Path, "listed");
        await File.WriteAllTextAsync(path, "ordinary");
        IFileProvider provider = LocalFileProvider.CreateForCurrentPlatform(new LocalFileProviderOptions(LocalProfile, LocalAuthority, root.Path));
        var rootLocation = new FileLocation(LocalProfile, LocalAuthority, FilePath.Root);
        Assert.True((await provider.ListAsync(new FileListRequest(rootLocation, 10), CancellationToken.None)).IsSuccess);
        File.Delete(path);
        File.CreateSymbolicLink(path, target);
        var location = rootLocation.Child(new FilePathSegment("listed"));
        var read = await provider.GetAccessControlAsync(new FileAccessControlRequest(location), CancellationToken.None);
        var write = await provider.SetAccessControlAsync(new FileSetAccessControlRequest(location, new FilePanelPosixMode(0b111_111_111)), CancellationToken.None);
        Assert.False(read.IsSuccess);
        Assert.False(write.IsSuccess);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }

    private static readonly FileProviderProfileId LocalProfile = new("access-local");
    private static readonly FileAuthority LocalAuthority = new("local");

    [Fact]
    public async Task A_local_posix_filesystem_reads_and_writes_its_permission_bits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TemporaryDirectory.Create();
        IFileProvider provider = LocalFileProvider.CreateForCurrentPlatform(
            new LocalFileProviderOptions(LocalProfile, LocalAuthority, root.Path));
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.Permissions));

        var path = Path.Combine(root.Path, "script.sh");
        await File.WriteAllTextAsync(path, "echo hi", Encoding.UTF8);
        File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);
        var location = new FileLocation(
            LocalProfile,
            LocalAuthority,
            FilePath.Root.Append(new FilePathSegment("script.sh")));

        var read = await provider.GetAccessControlAsync(
            new FileAccessControlRequest(location),
            CancellationToken.None);

        Assert.True(read.IsSuccess);
        Assert.Equal("644", read.Value!.Mode!.Octal);

        var written = await provider.SetAccessControlAsync(
            new FileSetAccessControlRequest(location, mode: new FilePanelPosixMode(0b111_101_101)),
            CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.Equal("755", written.Value!.Mode!.Octal);
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(path));
    }

    /// <summary>
    /// A filesystem is not asked for a list of grants, and it says which of the
    /// two it speaks rather than doing its best with the wrong one.
    /// </summary>
    [Fact]
    public async Task A_filesystem_refuses_a_list_of_grants()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TemporaryDirectory.Create();
        IFileProvider provider = LocalFileProvider.CreateForCurrentPlatform(
            new LocalFileProviderOptions(LocalProfile, LocalAuthority, root.Path));
        var path = Path.Combine(root.Path, "notes.txt");
        await File.WriteAllTextAsync(path, "hello", Encoding.UTF8);

        var result = await provider.SetAccessControlAsync(
            new FileSetAccessControlRequest(
                new FileLocation(
                    LocalProfile,
                    LocalAuthority,
                    FilePath.Root.Append(new FilePathSegment("notes.txt"))),
                grants: [new FilePanelAccessGrant(
                    new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                    FilePanelAccessRight.Read)]),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, result.Error!.Code);
    }

    /// <summary>
    /// An object store answers with parties. The service repeats a grantee once
    /// per permission and the provider folds them back into one row each, which
    /// is what anybody reading a list of who has access actually wants.
    /// </summary>
    [Fact]
    public async Task An_object_store_reads_its_grants_one_row_per_party()
    {
        var (provider, store) = CreateS3();
        await WriteObjectAsync(provider, "report.csv");
        store.Acls["report.csv"] = new S3ObjectAcl(
            "owner-canonical-id",
            "owner",
            [
                new S3ObjectGrant("CanonicalUser", "owner-canonical-id", "owner", null, "FULL_CONTROL"),
                new S3ObjectGrant("Group", null, null, "http://acs.amazonaws.com/groups/global/AllUsers", "READ"),
                new S3ObjectGrant("CanonicalUser", "p3179430", "p3179430", null, "READ"),
                new S3ObjectGrant("CanonicalUser", "p3179430", "p3179430", null, "WRITE"),
            ]);

        var read = await provider.GetAccessControlAsync(
            new FileAccessControlRequest(S3Location("report.csv")),
            CancellationToken.None);

        Assert.True(read.IsSuccess);
        Assert.Null(read.Value!.Mode);
        var grants = read.Value.Grants;
        Assert.Equal(3, grants.Count);
        Assert.Equal(
            FilePanelAccessRight.FullControl,
            grants.Single(grant => grant.Grantee.Kind == FilePanelGranteeKind.Owner).Rights);
        Assert.Equal(
            FilePanelAccessRight.Read,
            grants.Single(grant => grant.Grantee.Kind == FilePanelGranteeKind.Everyone).Rights);
        Assert.Equal(
            FilePanelAccessRight.Read | FilePanelAccessRight.Write,
            grants.Single(grant => string.Equals(grant.Grantee.Id, "p3179430", StringComparison.Ordinal)).Rights);
    }

    /// <summary>
    /// And writing them back unfolds one row per permission again — and keeps
    /// the owner. An ACL sent to S3 without an owner is how an object changes
    /// hands by accident.
    /// </summary>
    [Fact]
    public async Task Writing_grants_keeps_the_owner_and_unfolds_the_permissions()
    {
        var (provider, store) = CreateS3();
        await WriteObjectAsync(provider, "report.csv");

        var written = await provider.SetAccessControlAsync(
            new FileSetAccessControlRequest(
                S3Location("report.csv"),
                grants:
                [
                    new FilePanelAccessGrant(
                        new FilePanelGrantee(FilePanelGranteeKind.Everyone),
                        FilePanelAccessRight.Read),
                    new FilePanelAccessGrant(
                        new FilePanelGrantee(FilePanelGranteeKind.User, "p3179430"),
                        FilePanelAccessRight.Read | FilePanelAccessRight.Write),
                ]),
            CancellationToken.None);

        Assert.True(written.IsSuccess);
        var stored = store.Acls["report.csv"];
        Assert.Equal("owner-canonical-id", stored.OwnerId);
        Assert.Equal(3, stored.Grants.Count);
        Assert.Contains(
            stored.Grants,
            grant => string.Equals(grant.GranteeUri, "http://acs.amazonaws.com/groups/global/AllUsers"
, StringComparison.Ordinal) && string.Equals(grant.Permission, "READ", StringComparison.Ordinal));
        Assert.Equal(
            ["READ", "WRITE"],
            stored.Grants
                .Where(grant => string.Equals(grant.GranteeId, "p3179430", StringComparison.Ordinal))
                .Select(grant => grant.Permission)
                .Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_object_store_refuses_permission_bits()
    {
        var (provider, _) = CreateS3();
        await WriteObjectAsync(provider, "report.csv");

        var result = await provider.SetAccessControlAsync(
            new FileSetAccessControlRequest(
                S3Location("report.csv"),
                mode: new FilePanelPosixMode(0b111_101_101)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, result.Error!.Code);
    }

    private static readonly FileProviderProfileId S3Profile = new("access-s3");
    private static readonly FileAuthority S3Authority = new("test-bucket");

    private static (S3FileProvider Provider, FakeS3ObjectStore Store) CreateS3()
    {
        var store = new FakeS3ObjectStore();
        var provider = new S3FileProvider(
            store,
            new S3FileProviderOptions(S3Profile, S3Authority, "test-bucket"));
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.AccessControlLists));
        return (provider, store);
    }

    private static FileLocation S3Location(string key) =>
        new(S3Profile, S3Authority, FilePath.Root.Append(new FilePathSegment(key)));

    private static async Task WriteObjectAsync(S3FileProvider provider, string key)
    {
        using var content = new MemoryStream("a,b\n1,2\n"u8.ToArray());
        var write = await provider.WriteAsync(
            new FileWriteRequest(
                S3Location(key),
                content.Length,
                bufferSize: 8192,
                new FileMutationPrecondition.Any()),
            content,
            progress: null,
            CancellationToken.None);
        Assert.True(write.IsSuccess);
    }

}
