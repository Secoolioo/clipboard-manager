using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Monitoring;
using Microsoft.Extensions.Time.Testing;

namespace ClipboardManager.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class CapturePolicyTests
{
    // Built at runtime so the repository never contains strings that look like real credentials
    // (GitHub push protection and secret scanners would rightly complain).
    private static string GitHubToken() => "gh" + "p_" + new string('A', 18) + new string('7', 18);

    private static string AwsKeyId() => "AK" + "IA" + "ABCDEFGH12345678";

    private static string PemKey() => "-----BEGIN " + "OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----";

    private static string SlackToken() => "xo" + "xb-" + "1234567890-abcdefghij";

    private static string StripeKey() => "sk" + "_live_" + new string('a', 24);

    private static ClipboardSnapshot Text(string text, string? exe = "notepad.exe") => new(1, SkipReason.None, text, exe);

    [Fact]
    public void Plain_text_is_accepted()
    {
        Assert.Equal(SkipReason.None, CapturePolicy.Evaluate(Text("docker compose up -d"), CaptureSettings.Default));
    }

    [Fact]
    public void Reader_skip_reason_wins()
    {
        var snapshot = new ClipboardSnapshot(1, SkipReason.MarkedBySource, null, "keepassxc.exe");
        Assert.Equal(SkipReason.MarkedBySource, CapturePolicy.Evaluate(snapshot, CaptureSettings.Default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Blank_text_is_skipped_silently(string text)
    {
        var reason = CapturePolicy.Evaluate(Text(text), CaptureSettings.Default);
        Assert.Equal(SkipReason.Blank, reason);
        Assert.True(reason.IsSilent());
    }

    [Fact]
    public void Excluded_app_matches_case_insensitively()
    {
        var settings = new CaptureSettings(true, ["KeePass.exe"]);
        Assert.Equal(SkipReason.ExcludedApp, CapturePolicy.Evaluate(Text("pw", "keepass.EXE"), settings));
        Assert.Equal(SkipReason.None, CapturePolicy.Evaluate(Text("pw", "notepad.exe"), settings));
        Assert.Equal(SkipReason.None, CapturePolicy.Evaluate(Text("pw", null), settings));
    }

    [Fact]
    public void Oversized_text_is_skipped_not_truncated()
    {
        var text = new string('x', CapturePolicy.MaxCaptureChars + 1);
        Assert.Equal(SkipReason.TooLarge, CapturePolicy.Evaluate(Text(text), CaptureSettings.Default));
    }

    [Fact]
    public void Known_credential_formats_are_detected()
    {
        foreach (var secret in new[] { GitHubToken(), AwsKeyId(), PemKey(), SlackToken(), StripeKey(), "config:\n  token: " + GitHubToken() })
        {
            Assert.Equal(SkipReason.LooksLikeSecret, CapturePolicy.Evaluate(Text(secret), CaptureSettings.Default));
        }
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("3f786850e387550fdab836ed7e6dc881de23001b")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("ghp_tooShort")]
    [InlineData("The AKIA prefix alone is fine")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    public void Ordinary_text_is_not_mistaken_for_a_secret(string text)
    {
        Assert.False(SecretDetector.LooksLikeSecret(text));
    }

    [Fact]
    public void Secret_detection_can_be_switched_off()
    {
        var settings = new CaptureSettings(SkipDetectedSecrets: false, []);
        Assert.Equal(SkipReason.None, CapturePolicy.Evaluate(Text(GitHubToken()), settings));
    }

    [Fact]
    public void Pause_for_duration_ends_by_wall_clock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var state = new MonitoringState(time);

        state.PauseFor(TimeSpan.FromMinutes(5));
        Assert.Equal(MonitoringMode.PausedUntil, state.Mode);
        Assert.Equal(SkipReason.Paused, state.OnClipboardChanged());

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(MonitoringMode.Active, state.Mode);
        Assert.Equal(SkipReason.None, state.OnClipboardChanged());
    }

    [Fact]
    public void Ignore_next_copy_is_consumed_once()
    {
        var state = new MonitoringState(TimeProvider.System);
        state.IgnoreNextCopy();

        Assert.Equal(SkipReason.IgnoredOnce, state.OnClipboardChanged());
        Assert.Equal(SkipReason.None, state.OnClipboardChanged());
    }

    [Fact]
    public void Indefinite_pause_persists_until_resumed()
    {
        var state = new MonitoringState(TimeProvider.System, pausedIndefinitely: true);
        Assert.Equal(MonitoringMode.PausedIndefinitely, state.Mode);

        state.Resume();
        Assert.True(state.IsRecording);
    }

    [Fact]
    public void Current_clip_tracker_only_reports_matching_sequences()
    {
        var tracker = new CurrentClipTracker();
        tracker.EntryIsCurrent(10, entryId: 42);
        Assert.Equal(42, tracker.CurrentEntry(10));
        Assert.Null(tracker.CurrentEntry(11));

        tracker.ContentSkipped(11, SkipReason.LooksLikeSecret);
        Assert.Equal(SkipReason.LooksLikeSecret, tracker.CurrentSkip(11));
        Assert.Null(tracker.CurrentEntry(11));

        tracker.ContentSkipped(12, SkipReason.OwnWrite);
        Assert.Equal(SkipReason.None, tracker.CurrentSkip(12));
    }
}
