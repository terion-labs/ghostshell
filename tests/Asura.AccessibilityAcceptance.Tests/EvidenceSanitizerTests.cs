namespace Asura.AccessibilityAcceptance.Tests;

public sealed class EvidenceSanitizerTests
{
    [Fact]
    public void Sanitizer_removes_secrets_identity_paths_addresses_and_controls()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = $"token=abc123 Bearer xyz.123 user@example.test 192.0.2.10 2001:db8::42 {home}/private\0\nfinished";

        var result = EvidenceSanitizer.SanitizeNote(raw);

        Assert.DoesNotContain("abc123", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz.123", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::42", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(home, result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', result.Value);
        Assert.True(result.RedactionsApplied >= 6);
        Assert.True(EvidenceSanitizer.IsSanitizedNote(result.Value));
    }

    [Fact]
    public void Sanitizer_redacts_urls_private_keys_and_absolute_paths()
    {
        var raw = "https://alice:secret@example.test -----BEGIN PRIVATE KEY----- material -----END PRIVATE KEY----- /opt/release /Volumes/lab/private.txt /mnt/releases/key.txt /srv/asura C:\\release\\candidate";

        var result = EvidenceSanitizer.SanitizeNote(raw);

        Assert.DoesNotContain("secret", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/release", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/Volumes/lab/private.txt", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/mnt/releases/key.txt", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/srv/asura", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\release", result.Value, StringComparison.Ordinal);
        Assert.True(result.RedactionsApplied >= 7);
    }

    [Fact]
    public void Sanitizer_redacts_quoted_multiword_credentials()
    {
        var result = EvidenceSanitizer.SanitizeNote(
            "password=\"correct horse battery staple\" and token='multi word value'");

        Assert.DoesNotContain("correct horse", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("multi word", result.Value, StringComparison.Ordinal);
        Assert.Equal(2, result.RedactionsApplied);
    }

    [Fact]
    public void Sanitizer_bounds_free_form_evidence()
    {
        var result = EvidenceSanitizer.SanitizeNote(new string('x', 3_000));

        Assert.EndsWith("[TRUNCATED]", result.Value, StringComparison.Ordinal);
        Assert.True(result.Value.Length <= EvidenceSanitizer.MaximumNoteLength + 12);
    }

    [Theory]
    [InlineData("operator-01", true)]
    [InlineData("a11y.lab_02", true)]
    [InlineData("ab", false)]
    [InlineData("/Users/alice", false)]
    [InlineData("operator name", false)]
    public void Identifier_contract_is_strict(string value, bool expected)
    {
        Assert.Equal(expected, EvidenceSanitizer.IsValidIdentifier(value));
    }

    [Theory]
    [InlineData("host-0123456789abcdef", true)]
    [InlineData("host-0123456789ABCDEF", false)]
    [InlineData("alice-macbook", false)]
    public void Host_fingerprint_contract_is_non_identifying_and_strict(
        string value,
        bool expected)
    {
        Assert.Equal(expected, EvidenceSanitizer.IsHostFingerprint(value));
    }
}
