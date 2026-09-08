using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class SqliteAgentPolicyPreferenceStoreTests
{
    [Theory]
    [InlineData("""{"Provider":"private-provider","Model":"private-model","Permissions":{"TerminalRead":0,"RunCommands":0,"ReadFiles":1}}""")]
    [InlineData("""{"provider":"private-provider","model":"private-model","permissions":{"TerminalRead":"Off","RunCommands":"Off","ReadFiles":"Ask"},"compactionModel":{"provider":"private-provider","model":"private-model"},"titleModel":{"provider":"private-provider","model":"private-model"}}""")]
    public async Task HistoricalStoredPermissionsArePreservedAndNewCapabilitiesDefaultToOff(string payload)
    {
        await using var temporary = TemporaryDatabase.Create();
        await using var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_policy_preference SET policy_json = $json WHERE singleton_id = 1;";
        command.Parameters.AddWithValue("$json", payload);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var result = await new SqliteAgentPolicyPreferenceStore(temporary.Database).ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var policy = Assert.IsType<AgentPolicy>(result.Value);
        Assert.Equal("private-provider", policy.Provider);
        Assert.Equal("private-model", policy.Model);
        Assert.Equal(new AgentModelSelection(policy.Provider, policy.Model), policy.CompactionModel);
        Assert.Equal(new AgentModelSelection(policy.Provider, policy.Model), policy.TitleModel);
        Assert.Equal(AgentPermission.Off, policy.GetPermission(AgentCapability.TerminalRead));
        Assert.Equal(AgentPermission.Off, policy.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(AgentPermission.Ask, policy.GetPermission(AgentCapability.ReadFiles));
        Assert.Equal(AgentPermission.Off, policy.GetPermission(AgentCapability.WorkspaceLayout));
        Assert.Equal(AgentPolicy.Capabilities.Length, policy.Permissions.Count);
        command.CommandText = "SELECT policy_json FROM agent_policy_preference WHERE singleton_id = 1;";
        Assert.Equal(payload, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("""{"Provider":"p","Model":"m","Permissions":{"RunCommands":3}}""")]
    [InlineData("""{"Provider":"p","Model":"m","Permissions":{"RunCommands":42}}""")]
    [InlineData("""{"Provider":"p","Model":"m","Permissions":{"Unknown":0}}""")]
    [InlineData("""{"Provider":"p","Provider":"other","Model":"m","Permissions":{"RunCommands":0}}""")]
    [InlineData("""{"Provider":"p","Model":"m","Permissions":{"RunCommands":0},"Unknown":true}""")]
    public async Task MalformedLegacyPolicyCannotGainPermissions(string payload)
    {
        await using var temporary = TemporaryDatabase.Create();
        await using var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_policy_preference SET policy_json = $json WHERE singleton_id = 1;";
        command.Parameters.AddWithValue("$json", payload);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var result = await new SqliteAgentPolicyPreferenceStore(temporary.Database).ReadAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        command.CommandText = "SELECT policy_json FROM agent_policy_preference WHERE singleton_id = 1;";
        Assert.Equal(payload, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DefaultPolicySurvivesDatabaseReopen()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentPolicyPreferenceStore(temporary.Database);
        var policy = new AgentPolicy(
            "provider-openai",
            "gpt-5.6-terra",
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                _ => AgentPermission.Ask))
        {
            CompactionModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-terra"),
            TitleModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-sol"),
            SystemPrompt = "Follow this workspace's repository conventions.",
        };

        Assert.True((await store.WriteAsync(policy, CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteAgentPolicyPreferenceStore(temporary.Database);

        var result = await store.ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(policy.Provider, result.Value?.Provider);
        Assert.Equal(policy.Model, result.Value?.Model);
        Assert.Equal(policy.CompactionModel, result.Value?.CompactionModel);
        Assert.Equal(policy.TitleModel, result.Value?.TitleModel);
        Assert.Equal(policy.SystemPrompt, result.Value?.SystemPrompt);
        Assert.Equal(
            policy.Permissions.OrderBy(pair => pair.Key),
            result.Value?.Permissions.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task StoredPolicyWithoutExplicitMaintenanceRoutesIsRejected()
    {
        await using var temporary = TemporaryDatabase.Create();
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE agent_policy_preference
                SET policy_json = $policyJson
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue(
                "$policyJson",
                """
                {"Provider":"provider-openai","Model":"gpt-5.6-terra","Permissions":{}}
                """);
            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        var store = new SqliteAgentPolicyPreferenceStore(temporary.Database);
        var result = await store.ReadAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
    }
}
