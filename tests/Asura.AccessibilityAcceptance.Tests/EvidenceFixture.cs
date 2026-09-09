namespace Asura.AccessibilityAcceptance.Tests;

internal static class EvidenceFixture
{
    public static AcceptanceEvidence Valid(
        TargetPlatform platform = TargetPlatform.Windows,
        AcceptanceStatus status = AcceptanceStatus.Pass)
    {
        var started = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var resultForAssertions = status;
        var checks = AcceptanceCatalog.All.Select((check, index) =>
        {
            var assertions = check.Assertions.Select(assertion =>
                new AssertionObservation(assertion.Id, resultForAssertions)).ToArray();
            return new CheckObservation(
                check.Id,
                check.Title,
                CheckObservation.ResolveResult(assertions),
                index switch
                {
                    0 or >= 3 and <= 10 => "operator-observed",
                    1 or 11 => "operator-observed+runner-boundary",
                    _ => "runner-observed-boundary",
                },
                assertions,
                "Concrete synthetic acceptance evidence.",
                0,
                started.AddSeconds(index + 1));
        }).ToArray();
        var build = platform switch
        {
            TargetPlatform.MacOS => new BuildIdentity(
                "rc-1", "macos-application-bundle", "Asura", "1.0", 10,
                new string('a', 64), 4, new string('b', 64), "sh.asura"),
            TargetPlatform.Windows => new BuildIdentity(
                "rc-1", "windows-package", "Asura.exe", "1.0", 10,
                new string('a', 64), 4, new string('b', 64), "Asura.exe"),
            _ => new BuildIdentity(
                "rc-1", "linux-x11-package", "Asura", "1.0", 10,
                new string('a', 64), 4, new string('b', 64), "Asura"),
        };
        var reader = AcceptanceEvidence.ScreenReaderFor(platform);
        return new AcceptanceEvidence(
            AcceptanceEvidence.CurrentSchemaVersion,
            AcceptanceEvidence.CurrentEvidenceKind,
            AcceptanceEvidence.CurrentRunnerVersion,
            AcceptanceEvidence.CurrentCatalogVersion,
            AcceptanceCatalog.Digest,
            platform,
            reader,
            new HostIdentity(
                "a11y-lab-01",
                "host-aaaaaaaaaaaaaaaa",
                "operator-01",
                "Test operating system",
                "Arm64",
                "Arm64",
                "Local desktop",
                true,
                new HostEnvironmentSignals(false, false, false, false, false, true, true),
                []),
            new AssistiveTechnologyIdentity(
                reader,
                reader switch
                {
                    ScreenReaderKind.VoiceOver => "Apple VoiceOver",
                    ScreenReaderKind.Narrator => "Microsoft Narrator",
                    _ => "GNOME Orca",
                },
                "1.0",
                reader switch
                {
                    ScreenReaderKind.VoiceOver =>
                        "running system application with bundle identifier com.apple.VoiceOver",
                    ScreenReaderKind.Narrator =>
                        "running executable verified as Windows System32 Narrator.exe",
                    _ => ScreenReaderProbe.OrcaIdentitySource,
                },
                "ACTIVE_VERIFIED",
                "ACTIVE_VERIFIED",
                platform == TargetPlatform.LinuxX11
                    ? "AT_SPI_SESSION_BUS_PRESENT"
                    : "NATIVE_PLATFORM_ACCESSIBILITY"),
            build,
            started,
            started.AddMinutes(1),
            AcceptanceEvidence.ResolveOverall(checks),
            status == AcceptanceStatus.Pass
                ? AcceptanceEvidence.CleanExitDisposition
                : "Package was not started because an acceptance boundary did not pass.",
            status switch
            {
                AcceptanceStatus.Pass => AcceptanceEvidence.PreferencesRestoredDisposition,
                AcceptanceStatus.Fail => AcceptanceEvidence.PreferencesNotRestoredDisposition,
                _ => AcceptanceEvidence.PreferencesUnconfirmedDisposition,
            },
            AcceptanceEvidence.StandardLimitations,
            checks);
    }
}
