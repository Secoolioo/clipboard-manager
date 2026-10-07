using ClipboardManager.Core.Settings;
using ClipboardManager.Localization;
using ClipboardManager.Shell;

namespace ClipboardManager.Tests.Unit;

[Collection(LocalizationCollection.Name)]
[Trait("Category", "Unit")]
public sealed class LocalizationTests
{
    [Fact]
    public void Hotkeys_are_shown_with_localized_modifier_names()
    {
        Strings.Apply(LanguagePreference.German);
        Assert.Equal("Strg+Umschalt+V", HotkeyFormatter.Format(HotkeyGesture.Default));
        Assert.Equal("Win+Alt+V", HotkeyFormatter.Format(HotkeyGesture.Fallback));

        Strings.Apply(LanguagePreference.English);
        Assert.Equal("Ctrl+Shift+V", HotkeyFormatter.Format(HotkeyGesture.Default));
    }

    [Fact]
    public void Relative_times_are_never_negative_and_localized()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Strings.Apply(LanguagePreference.German);
        Assert.Equal("gerade eben", Strings.RelativeTime(now.AddSeconds(5), now));
        Assert.Equal("vor 5 Min.", Strings.RelativeTime(now.AddMinutes(-5), now));

        Strings.Apply(LanguagePreference.English);
        Assert.Equal("5 min ago", Strings.RelativeTime(now.AddMinutes(-5), now));
        Assert.StartsWith("yesterday", Strings.RelativeTime(now.AddDays(-1), now), StringComparison.Ordinal);
    }

    [Fact]
    public void Plurals()
    {
        Strings.Apply(LanguagePreference.German);
        Assert.Equal("1 Zeile", Strings.Lines(1));
        Assert.Equal("3 Zeilen", Strings.Lines(3));
        Strings.Apply(LanguagePreference.English);
        Assert.Equal("1 line", Strings.Lines(1));
        Assert.Equal("3 lines", Strings.Lines(3));
    }

    [Fact]
    public void Every_skip_reason_with_a_message_has_text_in_both_languages()
    {
        foreach (var language in new[] { LanguagePreference.German, LanguagePreference.English })
        {
            Strings.Apply(language);
            foreach (var reason in Enum.GetValues<Core.Capture.SkipReason>())
            {
                Assert.Equal(Core.Capture.SkipReasons.IsSilent(reason), Strings.Skipped(reason).Length == 0);
            }
        }
    }

    [Fact]
    public void Every_update_error_has_text_in_both_languages()
    {
        var german = new HashSet<string>();
        var english = new HashSet<string>();
        foreach (var error in Enum.GetValues<Core.Updates.UpdateError>())
        {
            Strings.Apply(LanguagePreference.German);
            Assert.True(german.Add(Strings.UpdateFailed(error)) && Strings.UpdateFailed(error).Length > 0, error.ToString());
            Strings.Apply(LanguagePreference.English);
            Assert.True(english.Add(Strings.UpdateFailed(error)) && Strings.UpdateFailed(error).Length > 0, error.ToString());
        }

        Strings.Apply(LanguagePreference.German);
        Assert.Equal("Aktualisiert auf v0.10.0", Strings.UpdatedTo("0.10.0"));
        Assert.Equal("Wird heruntergeladen… 42 %", Strings.UpdateDownloading(42));
        Strings.Apply(LanguagePreference.English);
        Assert.Equal("Updated to v0.10.0", Strings.UpdatedTo("0.10.0"));
    }
}
