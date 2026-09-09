using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class OpenSshKnownHostTrustSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"asura-openssh-known-hosts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Exact_default_port_key_is_trusted()
    {
        var candidate = Candidate(1);
        var source = await SourceAsync(
            $"host.example {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n");

        var trusted = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("host.example"),
            candidate,
            CancellationToken.None);

        Assert.True(trusted);
    }

    [Fact]
    public async Task Hashed_hostname_is_trusted()
    {
        var candidate = Candidate(2);
        var hashedHost = HashHost("host.example", [.. Enumerable.Repeat((byte)7, 20)]);
        var source = await SourceAsync(
            $"{hashedHost} {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n");

        var trusted = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("host.example"),
            candidate,
            CancellationToken.None);

        Assert.True(trusted);
    }

    [Fact]
    public async Task Non_default_port_requires_bracketed_host_and_port()
    {
        var candidate = Candidate(3);
        var source = await SourceAsync(
            $"[host.example]:2222 {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n");

        var matchingPort = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("host.example", port: 2222),
            candidate,
            CancellationToken.None);
        var defaultPort = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("host.example"),
            candidate,
            CancellationToken.None);

        Assert.True(matchingPort);
        Assert.False(defaultPort);
    }

    [Fact]
    public async Task Different_or_revoked_keys_are_not_trusted()
    {
        var candidate = Candidate(4);
        var different = Candidate(5);
        var source = await SourceAsync(
            $"""
            host.example {different.Identity.Algorithm} {different.PublicKeyBase64}
            host.example {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}
            @revoked host.example {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}

            """);

        var trusted = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("host.example"),
            candidate,
            CancellationToken.None);

        Assert.False(trusted);
    }

    [Fact]
    public async Task Negated_host_pattern_overrides_wildcard_match()
    {
        var candidate = Candidate(6);
        var source = await SourceAsync(
            $"!blocked.example,*.example {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n");

        var blocked = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("blocked.example"),
            candidate,
            CancellationToken.None);
        var allowed = await source.ContainsAsync(
            new ConnectionEndpoint.Ssh("allowed.example"),
            candidate,
            CancellationToken.None);

        Assert.False(blocked);
        Assert.True(allowed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<OpenSshKnownHostTrustSource> SourceAsync(string contents)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "known_hosts");
        await File.WriteAllTextAsync(path, contents);
        return new OpenSshKnownHostTrustSource([path]);
    }

    private static string HashHost(string host, byte[] salt)
    {
        var digest = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(digest)}";
    }

    private static SshHostKeyCandidate Candidate(byte marker) =>
        new("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(marker, 32).ToArray()));
}
