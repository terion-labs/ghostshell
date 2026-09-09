using System.Data.Common;
using System.Diagnostics;
using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Databases;

namespace Asura.Architecture.Tests;

public sealed class DatabaseConnectionMaterialsTests
{
    [Theory]
    [InlineData("postgres", "Host=db.private;SSL Mode=VerifyFull", "Root Certificate")]
    [InlineData("cockroach", "Host=db.private;SSL Mode=VerifyFull", "SSL Certificate")]
    [InlineData("redshift", "Host=db.private;SSL Mode=VerifyFull", "SSL Key")]
    [InlineData("mysql", "Server=db.private;SslMode=VerifyFull", "SslCa")]
    [InlineData("mariadb", "Server=db.private;SslMode=VerifyFull", "CertificateFile")]
    [InlineData("mysql", "Server=db.private;SslMode=VerifyFull", "SslCert")]
    [InlineData("mysql", "Server=db.private;SslMode=VerifyFull", "SslKey")]
    [InlineData("sqlserver", "Server=db.private,1433;Encrypt=Strict;HostNameInCertificate=db.private", "ServerCertificate")]
    public async Task Explicit_certificate_bytes_round_trip_without_changing_origin_or_tls_policy(string driver, string connection, string option)
    {
        using var files = new Fixture();
        var certificate = Path.Combine(files.Root, "host-certificate.pem");
        byte[] bytes = [0, 1, 2, 127, 255];
        await File.WriteAllBytesAsync(certificate, bytes, CancellationToken.None);
        var builder = new DbConnectionStringBuilder { ConnectionString = connection };
        builder[option] = certificate;
        var original = new DatabaseWorkerConnection(driver, builder.ConnectionString);
        using var material = await DatabaseConnectionMaterials.ReadHostAsync(original, CancellationToken.None);
        Assert.DoesNotContain(files.Root, material.Connection.ConnectionString, StringComparison.Ordinal);
        Assert.Single(material.Descriptors);
        using var wire = new MemoryStream();
        await material.WriteAsync(wire, CancellationToken.None);
        wire.Position = 0;
        var imported = await DatabaseConnectionMaterials.ReceiveAsync(material.Connection, material.Descriptors,
            wire, files.Scratch, CancellationToken.None);
        using var providerConnection = BuiltInDatabaseDrivers.All.Single(item => string.Equals(item.Descriptor.Id, driver, StringComparison.Ordinal))
            .CreateConnection(imported.ConnectionString);
        Assert.Contains("material", providerConnection.ConnectionString, StringComparison.Ordinal);
        var restored = new DbConnectionStringBuilder { ConnectionString = imported.ConnectionString };
        var guestPath = Assert.IsType<string>(restored[option]);
        Assert.StartsWith(Path.Combine(files.Scratch, "material") + Path.DirectorySeparatorChar, guestPath, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(guestPath, CancellationToken.None));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(guestPath));
        }
        restored.Remove(option);
        builder.Remove(option);
        Assert.Equal(builder.ConnectionString, restored.ConnectionString);
    }

    [Fact]
    public async Task Oracle_copies_only_known_self_contained_files_and_rejects_external_references()
    {
        using var files = new Fixture();
        var wallet = Directory.CreateDirectory(Path.Combine(files.Root, "wallet")).FullName;
        await File.WriteAllTextAsync(Path.Combine(wallet, "cwallet.sso"), "wallet", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wallet, "tnsnames.ora"), "db=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.private)(PORT=1522)))", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(wallet, "unrelated.secret"), "not imported", CancellationToken.None);
        var builder = new DbConnectionStringBuilder { ["Data Source"] = "db", ["Tns_Admin"] = wallet };
        using var material = await DatabaseConnectionMaterials.ReadHostAsync(new("oracle", builder.ConnectionString), CancellationToken.None);
        Assert.Equal(["cwallet.sso", "tnsnames.ora"], material.Descriptors.Select(static file => file.Name), StringComparer.Ordinal);
        using var wire = new MemoryStream();
        await material.WriteAsync(wire, CancellationToken.None);
        wire.Position = 0;
        var imported = await DatabaseConnectionMaterials.ReceiveAsync(material.Connection, material.Descriptors, wire, files.Scratch, CancellationToken.None);
        using var oracle = BuiltInDatabaseDrivers.All.Single(item => string.Equals(item.Descriptor.Id, "oracle", StringComparison.Ordinal))
            .CreateConnection(imported.ConnectionString);
        Assert.Contains("material", oracle.ConnectionString, StringComparison.Ordinal);
        var directory = Assert.IsType<string>(new DbConnectionStringBuilder { ConnectionString = imported.ConnectionString }["Tns_Admin"]);
        Assert.False(File.Exists(Path.Combine(directory, "unrelated.secret")));
        await File.WriteAllTextAsync(Path.Combine(wallet, "sqlnet.ora"), "WALLET_LOCATION=(SOURCE=(METHOD=FILE)(METHOD_DATA=(DIRECTORY=/private/other)))", CancellationToken.None);
        await Assert.ThrowsAsync<NotSupportedException>(() => DatabaseConnectionMaterials.ReadHostAsync(new("oracle", builder.ConnectionString), CancellationToken.None));
    }

    [Fact]
    public async Task Missing_large_linked_and_relative_host_files_fail_before_launch()
    {
        using var files = new Fixture();
        var large = Path.Combine(files.Root, "large.pem");
        await using (var file = File.Create(large)) { file.SetLength(DatabaseConnectionMaterials.MaximumFileBytes + 1); }
        string[] paths = ["relative.pem", Path.Combine(files.Root, "missing.pem"), large];
        foreach (var path in paths)
        {
            var launched = false;
            using var worker = new DatabaseOperationWorker(() => throw new InvalidOperationException("No result expected."),
                workspaceLaunch: _ => { launched = true; return Task.FromResult(new DatabaseWorkspaceOperationLaunch(new ProcessStartInfo("must-not-start"), static () => Task.CompletedTask)); },
                importHostConnectionFiles: true);
            await Assert.ThrowsAsync<NotSupportedException>(() => worker.ListTablesAsync(new("postgres", $"Host=db.private;Root Certificate={path}"), CancellationToken.None));
            Assert.False(launched);
        }
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(files.Root, "linked.pem");
            File.CreateSymbolicLink(link, large);
            await Assert.ThrowsAsync<NotSupportedException>(() => DatabaseConnectionMaterials.ReadHostAsync(new("postgres", $"Host=db.private;Root Certificate={link}"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Ordinary_workspace_keeps_guest_paths_and_does_not_read_them_on_the_host()
    {
        var reachedLaunch = new IOException("Fixture reached the guest launch boundary.");
        using var worker = new DatabaseOperationWorker(() => throw new InvalidOperationException("No result expected."),
            workspaceLaunch: _ => Task.FromException<DatabaseWorkspaceOperationLaunch>(reachedLaunch));
        var failure = await Assert.ThrowsAsync<IOException>(() => worker.ListTablesAsync(
            new("postgres", "Host=db.private;Root Certificate=/guest-only/certificate.pem"), CancellationToken.None));
        Assert.Same(reachedLaunch, failure);
    }

    [Fact]
    public async Task Unrecognized_options_and_traversal_descriptors_cannot_write_guest_files()
    {
        using var files = new Fixture();
        DatabaseConnectionMaterial[] descriptors = [new("Host", "certificate.pem", 1), new("Root Certificate", "../escape", 1)];
        foreach (var descriptor in descriptors)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseConnectionMaterials.ReceiveAsync(
                new("postgres", "Host=@asura-private-material;Root Certificate=@asura-private-material"), [descriptor],
                Stream.Null, files.Scratch, CancellationToken.None));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Scratch));
    }

    [Fact]
    public async Task Truncated_import_is_removed_by_the_existing_operation_cleanup_lease()
    {
        var id = Guid.NewGuid().ToString("N");
        DatabaseWorkspaceScratch.Prepare(id);
        string path;
        try
        {
            using var lease = DatabaseWorkspaceScratch.Acquire(id);
            path = lease.DirectoryPath;
            using var input = new MemoryStream();
            await DatabaseOperationProtocol.WriteFrameAsync(input, new byte[2], CancellationToken.None);
            input.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseConnectionMaterials.ReceiveAsync(
                new("postgres", "Host=db.private;Root Certificate=@asura-private-material"),
                [new("Root Certificate", "certificate.pem", 3)], input, path, CancellationToken.None));
        }
        finally { await DatabaseWorkspaceScratch.CleanupAsync(id, CancellationToken.None); }
        Assert.False(Directory.Exists(path));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("asura-material-test-");
        public Fixture()
        {
            Scratch = Path.Combine(Root, "scratch");
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(Scratch); }
            else { Directory.CreateDirectory(Scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        internal string Root => _root.FullName;
        internal string Scratch { get; }
        public void Dispose() => _root.Delete(recursive: true);
    }
}
