using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SshKnownHostStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"asura-shared-known-hosts-{Guid.NewGuid():N}");

    [Fact]
    public void CandidateFingerprintIsDerivedFromCanonicalPublicKeyBytes()
    {
        var bytes = Enumerable.Repeat((byte)7, 32).ToArray();
        var candidate = new SshHostKeyCandidate("ssh-ed25519", Convert.ToBase64String(bytes));
        var expected = $"SHA256:{Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=')}";

        Assert.Equal(expected, candidate.Identity.Sha256Fingerprint);
        Assert.Equal(Convert.ToBase64String(bytes), candidate.PublicKeyBase64);
    }

    [Fact]
    public void AcceptNewPersistsRawKeyForStrictVerificationAfterRestart()
    {
        var connectionId = new ConnectionId("persistent-sftp");
        var candidate = Candidate(1);
        var firstProcess = new SshKnownHostStore(_directory);

        var unknown = firstProcess.Verify(
            connectionId,
            SshHostKeyPolicy.Strict,
            candidate);
        var accepted = firstProcess.Verify(
            connectionId,
            SshHostKeyPolicy.AcceptNew,
            candidate);
        var restartedProcess = new SshKnownHostStore(_directory);
        var verified = restartedProcess.Verify(
            connectionId,
            SshHostKeyPolicy.Strict,
            candidate);
        var changed = restartedProcess.Verify(
            connectionId,
            SshHostKeyPolicy.Strict,
            Candidate(2));

        Assert.Equal(SshHostKeyVerification.Unknown, unknown);
        Assert.Equal(SshHostKeyVerification.Trusted, accepted);
        Assert.Equal(SshHostKeyVerification.Trusted, verified);
        Assert.Equal(SshHostKeyVerification.Changed, changed);
        var binding = restartedProcess.Binding(connectionId);
        var persisted = File.ReadAllText(binding.FilePath);
        Assert.Contains(candidate.Identity.Algorithm, persisted, StringComparison.Ordinal);
        Assert.Contains(candidate.PublicKeyBase64, persisted, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(binding.FilePath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_directory));
        }
    }

    [Fact]
    public async Task ConcurrentFirstTrustAcrossStoreInstancesNeverReplacesWinner()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var connectionId = new ConnectionId($"concurrent-sftp-{iteration}");
            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim();
            var first = VerifyAfterBarrierAsync(new SshKnownHostStore(_directory), Candidate(1));
            var second = VerifyAfterBarrierAsync(new SshKnownHostStore(_directory), Candidate(2));
            ready.Wait();
            start.Set();

            var results = await Task.WhenAll(first, second);

            Assert.True(
                results.Count(item => item == SshHostKeyVerification.Trusted) == 1
                && results.Count(item => item == SshHostKeyVerification.Changed) == 1,
                $"Concurrent trust results: {string.Join(", ", results)}");

            Task<SshHostKeyVerification> VerifyAfterBarrierAsync(
                SshKnownHostStore store,
                SshHostKeyCandidate candidate) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                return store.Verify(connectionId, SshHostKeyPolicy.AcceptNew, candidate);
            });
        }
    }

    [Fact]
    public async Task ExplicitReplacementChangesTheExactPersistedPublicKey()
    {
        var connectionId = new ConnectionId("replace-sftp");
        var original = Candidate(1);
        var replacement = Candidate(2);
        var store = new SshKnownHostStore(_directory);
        Assert.Equal(
            SshHostKeyVerification.Trusted,
            store.Verify(connectionId, SshHostKeyPolicy.AcceptNew, original));
        var expected = await store.ReadAsync(connectionId, CancellationToken.None);

        var result = await store.WriteAsync(
            connectionId,
            replacement,
            expected,
            CancellationToken.None);
        var restarted = new SshKnownHostStore(_directory);

        Assert.Equal(SshKnownHostWriteResult.Stored, result);
        Assert.Equal(
            SshHostKeyVerification.Trusted,
            restarted.Verify(connectionId, SshHostKeyPolicy.Strict, replacement));
        Assert.Equal(
            SshHostKeyVerification.Changed,
            restarted.Verify(connectionId, SshHostKeyPolicy.Strict, original));
    }

    [Fact]
    public void MalformedStoreFailsClosedWithoutAcceptNewOverwrite()
    {
        var connectionId = new ConnectionId("malformed-sftp");
        var store = new SshKnownHostStore(_directory);
        var binding = store.Binding(connectionId);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(binding.FilePath, "not a known-host binding\n");

        var result = store.Verify(
            connectionId,
            SshHostKeyPolicy.AcceptNew,
            Candidate(1));

        Assert.Equal(SshHostKeyVerification.StoreInvalid, result);
        Assert.Equal("not a known-host binding\n", File.ReadAllText(binding.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SshHostKeyCandidate Candidate(byte marker) =>
        new("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(marker, 32).ToArray()));
}
