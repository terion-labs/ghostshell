using Asura.Application;
using Asura.Databases;

namespace Asura.Databases.Tests;

/// <summary>
/// The session probe and database enumeration behind the connection status
/// bar and the database selector. Server engines cannot run in a unit test,
/// so they are held to their declared facts; the file engines prove the
/// end-to-end path.
/// </summary>
public sealed class DatabaseSessionProbeTests
{
    /// <summary>
    /// The descriptor flag is what the UI reads; the SQL is what the client
    /// runs. A driver whose two answers disagree ships a database selector
    /// that either never appears or never works.
    /// </summary>
    [Fact]
    public void A_drivers_selector_flag_agrees_with_its_enumeration_statement()
    {
        Assert.All(BuiltInDatabaseDrivers.All, driver =>
            Assert.Equal(
                driver.ListDatabasesSql is not null,
                driver.Descriptor.CanListDatabases));
    }

    /// <summary>
    /// Server engines state the port their editor placeholder shows; file
    /// engines have no port to state.
    /// </summary>
    [Fact]
    public void Every_engine_states_a_default_port_exactly_when_it_listens_on_one()
    {
        Assert.All(BuiltInDatabaseDrivers.All, driver =>
            Assert.Equal(
                !driver.Descriptor.IsFileBased,
                driver.Descriptor.DefaultPort is not null));
    }

    [Fact]
    public async Task A_file_engine_answers_the_probe_with_its_version_and_no_tls()
    {
        var client = new DatabasePanelClient();

        var info = await client.DescribeSessionAsync(
            "sqlite",
            "Data Source=:memory:",
            tunnel: null,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(info.ServerVersion));
        Assert.Null(info.TlsProtocol);
    }

    [Fact]
    public async Task A_file_engine_has_no_databases_to_enumerate()
    {
        var client = new DatabasePanelClient();

        Assert.Empty(await client.ListDatabasesAsync(
            "sqlite",
            "Data Source=:memory:",
            tunnel: null,
            CancellationToken.None));
    }
}
