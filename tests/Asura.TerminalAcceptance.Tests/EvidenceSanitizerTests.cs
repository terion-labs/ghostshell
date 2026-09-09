namespace Asura.TerminalAcceptance.Tests;

public sealed class EvidenceSanitizerTests
{
    [Fact]
    public void Sanitizer_removes_secret_material_identity_and_control_characters()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var raw = $"token=abc123 Bearer xyz.123 user@example.test 192.0.2.10 {home}/private\0\nfinished";

        var result = EvidenceSanitizer.SanitizeNote(raw);

        Assert.DoesNotContain("abc123", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz.123", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(home, result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', result.Value);
        Assert.True(result.RedactionsApplied >= 5);
        Assert.True(EvidenceSanitizer.IsSanitizedNote(result.Value));
    }

    [Fact]
    public void Sanitizer_redacts_url_credentials_and_private_key_material()
    {
        var raw = "https://alice:secret@example.test -----BEGIN PRIVATE KEY----- material -----END PRIVATE KEY-----";

        var result = EvidenceSanitizer.SanitizeNote(raw);

        Assert.Equal(
            "[URL_REDACTED] [PRIVATE_KEY_REDACTED]",
            result.Value);
        Assert.Equal(2, result.RedactionsApplied);
    }

    [Fact]
    public void Sanitizer_redacts_ipv6_authorization_and_absolute_paths()
    {
        var raw = "authorization=Bearer abc123 from 2001:db8::42 at /opt/release and C:\\release\\candidate";

        var result = EvidenceSanitizer.SanitizeNote(raw);

        Assert.DoesNotContain("abc123", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::42", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/release", result.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\release", result.Value, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.RedactionsApplied >= 4);
    }

    [Fact]
    public void Sanitizer_bounds_free_form_evidence()
    {
        var result = EvidenceSanitizer.SanitizeNote(new string('x', 3_000));

        Assert.EndsWith("[TRUNCATED]", result.Value, StringComparison.Ordinal);
        Assert.True(result.Value.Length <= EvidenceSanitizer.MaximumNoteLength + 12);
    }
}
