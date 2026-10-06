using System.Windows;
using ClipboardManager.Core.Settings;
using ClipboardManager.Shell;

namespace ClipboardManager.Dialogs;

/// <summary>What the settings window may read and change. Implemented by the app controller.</summary>
internal interface ISettingsHost
{
    AppSettings Settings { get; }

    void UpdateSettings(Func<AppSettings, AppSettings> change);

    AutostartState AutostartState { get; }

    bool AutostartIsVolatile { get; }

    bool ExeInDownloads { get; }

    void SetAutostart(bool enabled);

    bool StartMenuShortcutExists { get; }

    void SetStartMenuShortcut(bool enabled);

    bool IsRecording { get; }

    void SetRecording(bool recording);

    HotkeyGesture? ActiveHotkey { get; }

    HotkeyStatus HotkeyStatus { get; }

    void SuspendHotkey();

    void ResumeHotkey();

    bool IsHotkeyAvailable(HotkeyGesture gesture);

    HotkeyStatus ApplyHotkey(HotkeyGesture gesture);

    Task<int> CountExceedingAsync(int maxItems);

    Task<int> CountMemoryOnlyEntriesAsync();

    void SetMemoryOnly(bool memoryOnly, bool discardMemoryEntries);

    Task ClearHistoryAsync(Window owner);

    IReadOnlyList<string> RecentSources { get; }

    DateTimeOffset? LastIgnored(string exe);

    string DataFolder { get; }

    string VersionText { get; }

    void OpenUrl(string url);

    void ShowLicenses(Window owner);

    void RemoveEverything(Window owner);

    void LanguageChanged();
}
