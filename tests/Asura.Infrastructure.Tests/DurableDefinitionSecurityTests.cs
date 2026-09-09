using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class DurableDefinitionSecurityTests
{
    [Fact]
    public async Task ConnectionPersistsSecretReferenceButNeverVaultMaterial()
    {
        await using var temporary = TemporaryDatabase.Create();
        var secretReference = SecretRef.New();
        var purpose = new SecretUsePurpose(
            SecretUseKind.ConnectionAuthentication,
            "ssh-production");
        using var vault = new InMemorySecretVault();
        var sentinel = Encoding.UTF8.GetBytes("vault-canary-7f5bd2a5");
        using (var material = SecretMaterial.CopyFrom(sentinel))
        {
            var created = await vault.CreateAsync(
                new CreateSecretRequest(
                    secretReference,
                    "SSH password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.Connection, "ssh-production"),
                    purpose),
                material,
                CancellationToken.None);
            Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(created);
        }

        var connection = new ConnectionProfile(
            new ConnectionId("ssh-production"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production SSH",
            new ConnectionEndpoint.Ssh("host.example", 22, "operator"),
            new ConnectionAuthentication.Password(secretReference),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict,
            ["production"]);
        var repository = new SqliteDefinitionRepository<ConnectionProfile>(
            temporary.Database,
            TimeProvider.System);

        var saved = await repository.SaveAsync(connection, null, CancellationToken.None);
        var loaded = await repository.GetAsync(connection.Key, CancellationToken.None);

        Assert.True(saved.IsSuccess);
        Assert.True(loaded.IsSuccess);
        Assert.Equal(secretReference, Assert.IsType<ConnectionAuthentication.Password>(
            loaded.Value!.Value.Authentication).PasswordSecret);

        await using (var sqlite = await temporary.Database.OpenConnectionAsync(CancellationToken.None))
        {
            await using var checkpoint = sqlite.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var storageBytes = ReadStorageBytes(temporary.DatabasePath);
        Assert.Equal(-1, storageBytes.AsSpan().IndexOf(sentinel));
        var bundles = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var exported = await bundles.ExportAsync(CancellationToken.None);
        Assert.True(exported.IsSuccess);
        var exportText = string.Join(
            '\n',
            exported.Value!.Definitions.Select(document => document.PayloadJson));
        Assert.DoesNotContain(
            Encoding.UTF8.GetString(sentinel),
            exportText,
            StringComparison.Ordinal);
        Assert.Contains(secretReference.Value, exportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiProviderPersistsOnlyItsScopedSecretReference()
    {
        await using var temporary = TemporaryDatabase.Create();
        var profileId = new AiProviderProfileId("ai.openai");
        var secretReference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.AiProvider, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            profileId.Value);
        using var vault = new InMemorySecretVault();
        var sentinel = Encoding.UTF8.GetBytes("provider-canary-3e453ea1");
        using (var material = SecretMaterial.CopyFrom(sentinel))
        {
            var created = await vault.CreateAsync(
                new CreateSecretRequest(
                    secretReference,
                    "OpenAI API key",
                    SecretKind.ApiKey,
                    scope,
                    purpose),
                material,
                CancellationToken.None);
            Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(created);
        }

        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(secretReference),
            "gpt-5",
            order: 0);
        var repository = new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System);

        var saved = await repository.SaveAsync(profile, null, CancellationToken.None);
        var loaded = await repository.GetAsync(profile.Key, CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(
            secretReference,
            Assert.IsType<AiProviderAuthentication.ApiKey>(
                loaded.Value!.Value.Authentication).Secret);

        await using (var sqlite = await temporary.Database.OpenConnectionAsync(
                         CancellationToken.None))
        {
            await using var checkpoint = sqlite.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var storageBytes = ReadStorageBytes(temporary.DatabasePath);
        Assert.Equal(-1, storageBytes.AsSpan().IndexOf(sentinel));
        var bundles = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var exported = await bundles.ExportAsync(CancellationToken.None);
        Assert.True(exported.IsSuccess, exported.Error?.Message);
        var exportText = string.Join(
            '\n',
            exported.Value!.Definitions.Select(document => document.PayloadJson));
        Assert.DoesNotContain(
            Encoding.UTF8.GetString(sentinel),
            exportText,
            StringComparison.Ordinal);
        Assert.Contains(secretReference.Value, exportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServerEnvironmentPersistsOnlyItsScopedSecretReference()
    {
        await using var temporary = TemporaryDatabase.Create();
        var profileId = new McpServerProfileId("mcp.production");
        var secretReference = SecretRef.New();
        var scope = new SecretScope(SecretScopeKind.McpServer, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.McpServerEnvironment,
            profileId.Value);
        using var vault = new InMemorySecretVault();
        var sentinel = Encoding.UTF8.GetBytes("mcp-canary-c9eb66b4");
        using (var material = SecretMaterial.CopyFrom(sentinel))
        {
            var created = await vault.CreateAsync(
                new CreateSecretRequest(
                    secretReference,
                    "MCP token",
                    SecretKind.ApiKey,
                    scope,
                    purpose),
                material,
                CancellationToken.None);
            Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(created);
        }

        var profile = new McpServerProfile(
            profileId,
            McpServerProfile.CurrentSchemaVersion,
            "Production MCP",
            new McpServerTransport.Stdio(
                "/usr/local/bin/mcp-server",
                ["--stdio"],
                workingDirectory: null,
                [new McpServerEnvironmentVariable("MCP_TOKEN", secretReference)]),
            ["status.read"]);
        var repository = new SqliteDefinitionRepository<McpServerProfile>(
            temporary.Database,
            TimeProvider.System);

        var saved = await repository.SaveAsync(profile, null, CancellationToken.None);
        var loaded = await repository.GetAsync(profile.Key, CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(
            secretReference,
            Assert.Single(Assert.IsType<McpServerTransport.Stdio>(
                loaded.Value!.Value.Transport).Environment).Reference);

        await using (var sqlite = await temporary.Database.OpenConnectionAsync(
                         CancellationToken.None))
        {
            await using var checkpoint = sqlite.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var storageBytes = ReadStorageBytes(temporary.DatabasePath);
        Assert.Equal(-1, storageBytes.AsSpan().IndexOf(sentinel));
        var bundles = new SqliteDefinitionBundleStore(temporary.Database, TimeProvider.System);
        var exported = await bundles.ExportAsync(CancellationToken.None);
        Assert.True(exported.IsSuccess, exported.Error?.Message);
        var exportText = string.Join(
            '\n',
            exported.Value!.Definitions.Select(document => document.PayloadJson));
        Assert.DoesNotContain(
            Encoding.UTF8.GetString(sentinel),
            exportText,
            StringComparison.Ordinal);
        Assert.Contains(secretReference.Value, exportText, StringComparison.Ordinal);
    }

    private static byte[] ReadStorageBytes(string databasePath)
    {
        var paths = new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" };
        using var buffer = new MemoryStream();
        foreach (var path in paths.Where(File.Exists))
        {
            var bytes = File.ReadAllBytes(path);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }
}
