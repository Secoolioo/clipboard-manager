using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.Settings;

namespace ClipboardManager.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class SettingsTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsStore Store() => new(_dir.Paths.SettingsFile, FileLog.Null);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var (settings, state) = Store().Load();
        Assert.Equal(SettingsLoadState.Missing, state);
        Assert.Equal(100, settings.MaxHistoryItems);
        Assert.Equal(HotkeyGesture.Default, settings.Hotkey);
        Assert.True(settings.SkipDetectedSecrets);
        Assert.True(settings.HideFromScreenCapture);
    }

    [Fact]
    public void Round_trip_preserves_values()
    {
        var original = new AppSettings
        {
            Theme = ThemePreference.Dark,
            Language = LanguagePreference.English,
            MaxHistoryItems = 250,
            MemoryOnly = true,
            Hotkey = HotkeyGesture.Fallback,
            ExcludedApps = ["KeePassXC.exe"],
            PopupCompact = true,
        };

        Assert.True(Store().Save(original));
        var (loaded, state) = Store().Load();

        Assert.Equal(SettingsLoadState.Loaded, state);
        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(250, loaded.MaxHistoryItems);
        Assert.True(loaded.MemoryOnly);
        Assert.Equal(HotkeyGesture.Fallback, loaded.Hotkey);
        Assert.Equal(["KeePassXC.exe"], loaded.ExcludedApps);
        Assert.False(File.Exists(_dir.Paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_preserved_and_defaults_are_used()
    {
        File.WriteAllText(_dir.Paths.SettingsFile, "{ this is not json");

        var (settings, state) = Store().Load();

        Assert.Equal(SettingsLoadState.Corrupt, state);
        Assert.Equal(100, settings.MaxHistoryItems);
        Assert.True(File.Exists(_dir.Paths.SettingsFile + ".corrupt"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_lists_cleaned()
    {
        File.WriteAllText(
            _dir.Paths.SettingsFile,
            """{ "maxHistoryItems": 999999, "theme": "Dark", "excludedApps": ["  C:\\Tools\\KeePass.exe ", "keepass.exe", ""], "hotkey": { "modifiers": "None", "virtualKey": 86 } }""");

        var (settings, _) = Store().Load();

        Assert.Equal(5000, settings.MaxHistoryItems);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(["KeePass.exe"], settings.ExcludedApps);
        Assert.Equal(HotkeyGesture.Default, settings.Hotkey);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56, HotkeyValidation.Valid)]
    [InlineData(HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x56, HotkeyValidation.Valid)]
    [InlineData(HotkeyModifiers.Shift, 0x56, HotkeyValidation.MissingModifier)]
    [InlineData(HotkeyModifiers.None, 0x56, HotkeyValidation.MissingModifier)]
    [InlineData(HotkeyModifiers.Control, 0x43, HotkeyValidation.Reserved)]
    [InlineData(HotkeyModifiers.Control, 0x56, HotkeyValidation.Reserved)]
    [InlineData(HotkeyModifiers.Alt, 0x73, HotkeyValidation.Reserved)]
    [InlineData(HotkeyModifiers.Win, 0x4C, HotkeyValidation.Reserved)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x7B, HotkeyValidation.Reserved)]
    [InlineData(HotkeyModifiers.Control, 0x11, HotkeyValidation.NotAKey)]
    public void Hotkey_validation(HotkeyModifiers modifiers, int vk, HotkeyValidation expected)
    {
        Assert.Equal(expected, new HotkeyGesture(modifiers, vk).Validate());
    }

    [Fact]
    public void Ctrl_alt_chords_are_flagged_as_altgr()
    {
        Assert.True(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56).UsesAltGrChord);
        Assert.False(HotkeyGesture.Default.UsesAltGrChord);
    }
}
