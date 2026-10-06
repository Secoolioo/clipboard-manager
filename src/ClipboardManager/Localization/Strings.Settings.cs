namespace ClipboardManager.Localization;

public static partial class Strings
{
    public static string SettingsTitle => T("Einstellungen", "Settings");

    public static string SectionGeneral => T("Allgemein", "General");

    public static string StartWithWindows => T("Mit Windows starten", "Start with Windows");

    public static string AutostartDisabledInWindows => T(
        "In den Windows-Autostart-Einstellungen deaktiviert. Einschalten aktiviert es dort wieder.",
        "Disabled in the Windows startup settings. Turning it on re-enables it there.");

    public static string AutostartOtherLocation => T(
        "Der Autostart zeigt auf eine andere Kopie der App. Einschalten stellt ihn auf diese Datei um.",
        "Autostart points to another copy of the app. Turning it on switches it to this file.");

    public static string AutostartVolatile => T(
        "Diese EXE liegt an einem temporären Ort (ZIP, Temp, USB oder Netzwerk). Lege sie in einen festen Ordner, damit der Autostart funktioniert.",
        "This EXE is in a temporary location (ZIP, Temp, USB or network). Move it to a permanent folder so autostart keeps working.");

    public static string AutostartDownloads => T(
        "Tipp: Lege die EXE in einen festen Ordner, z. B. %LOCALAPPDATA%\\Programs. Nach dem Verschieben einmal starten – der Autostart folgt.",
        "Tip: keep the EXE in a permanent folder such as %LOCALAPPDATA%\\Programs. Start it once after moving; autostart follows.");

    public static string ShowInStartMenu => T("Im Startmenü anzeigen", "Show in Start menu");

    public static string RecordClipboard => T("Zwischenablage aufzeichnen", "Record the clipboard");

    public static string Language => T("Sprache", "Language");

    public static string LanguageSystem => T("Wie Windows", "Same as Windows");

    public static string LanguageRestartNote => T("Gilt für neu geöffnete Fenster.", "Applies to newly opened windows.");

    public static string SectionHotkey => T("Tastenkürzel", "Shortcut");

    public static string HotkeyOpen => T("Verlauf öffnen", "Open history");

    public static string HotkeyChange => T("Ändern", "Change");

    public static string HotkeyReset => T("Standard", "Default");

    public static string HotkeyPrompt => T("Neue Kombination drücken… (Esc bricht ab)", "Press the new combination… (Esc cancels)");

    public static string HotkeyOnlyModifiers => T("Nur Umschalttasten erkannt – die Kombination ist vermutlich von einer anderen App belegt.", "Only modifier keys detected – the combination is probably taken by another app.");

    public static string HotkeyNeedsModifier => T("Bitte Strg, Alt oder Win mit einer Taste kombinieren.", "Combine Ctrl, Alt or Win with a key.");

    public static string HotkeyReserved => T("Diese Kombination ist für Windows oder Bearbeitung reserviert.", "This combination is reserved for Windows or editing.");

    public static string HotkeyInUse => T("Diese Kombination ist bereits von einer anderen App belegt.", "This combination is already used by another app.");

    public static string HotkeyAltGr => T("Hinweis: Strg+Alt entspricht AltGr – auf manchen Tastaturen blockiert das Zeichen wie @.", "Note: Ctrl+Alt equals AltGr – on some keyboards this blocks characters such as @.");

    public static string HotkeyConflictsCtrlShiftV => T(
        "Strg+Umschalt+V ist in Word, Teams, Outlook und Browsern „als Text einfügen“ und in Windows Terminal „Einfügen“. Diese Apps bekommen die Kombination dann nicht mehr.",
        "Ctrl+Shift+V is \"paste as text\" in Word, Teams, Outlook and browsers, and \"paste\" in Windows Terminal. Those apps no longer receive it.");

    public static string HotkeyFallbackActive(string used) => T($"Belegt – stattdessen aktiv: {used}", $"Taken – using {used} instead");

    public static string HotkeyNotActive => T("Nicht aktiv: von einer anderen App belegt.", "Not active: used by another app.");

    public static string SectionHistory => T("Verlauf", "History");

    public static string MaxEntries => T("Maximale Einträge (Angeheftete zählen nicht)", "Maximum entries (pinned do not count)");

    public static string MemoryOnly => T("Verlauf nur im Arbeitsspeicher halten", "Keep history in memory only");

    public static string MemoryOnlyNote => T(
        "Nicht angeheftete Einträge werden nie gespeichert und sind nach dem Beenden weg. Angeheftete bleiben erhalten.",
        "Unpinned entries are never written to disk and are gone after exit. Pinned entries are kept.");

    public static string ClearHistoryButton => T("Verlauf löschen…", "Clear history…");

    public static string SectionPrivacy => T("Datenschutz", "Privacy");

    public static string SkipSecrets => T("Erkannte Zugangsdaten nicht speichern", "Don't save detected credentials");

    public static string SkipSecretsNote => T(
        "Erkennt eindeutige Formate wie private Schlüssel und Tokens (GitHub, AWS, Slack, Stripe, GitLab). Passwörter sind nicht erkennbar – dafür gibt es Pause und „Nächste Kopie ignorieren“.",
        "Recognizes unambiguous formats such as private keys and tokens (GitHub, AWS, Slack, Stripe, GitLab). Passwords cannot be detected – use pause or \"Ignore next copy\" for those.");

    public static string HideFromCapture => T("In Bildschirmfreigaben und Aufnahmen ausblenden", "Hide from screen sharing and recordings");

    public static string ExcludedApps => T("Ausgeschlossene Apps", "Excluded apps");

    public static string ExcludedAppsNote => T(
        "Kopien aus diesen Programmen werden nicht gespeichert (best effort: Windows nennt die Quell-App nicht immer). Passwortmanager wie KeePass, KeePassXC und Bitwarden markieren ihre Kopien selbst – die werden immer ignoriert.",
        "Copies from these programs are not saved (best effort: Windows does not always reveal the source app). Password managers such as KeePass, KeePassXC and Bitwarden mark their copies themselves – those are always ignored.");

    public static string AddApp => T("Hinzufügen", "Add");

    public static string RemoveApp => T("Entfernen", "Remove");

    public static string AppNamePlaceholder => T("z. B. KeePass.exe", "e.g. KeePass.exe");

    public static string RecentSources => T("Zuletzt gesehene Quellen", "Recently seen sources");

    public static string LastIgnored(string? when) => when is null ? T("noch nie ignoriert", "never ignored yet") : T($"zuletzt ignoriert {when}", $"last ignored {when}");

    public static string SectionAppearance => T("Darstellung", "Appearance");

    public static string Theme => T("Design", "Theme");

    public static string ThemeSystem => T("Wie Windows", "Same as Windows");

    public static string ThemeLight => T("Hell", "Light");

    public static string ThemeDark => T("Dunkel", "Dark");

    public static string SectionAbout => T("Info", "About");

    public static string Version(string version) => T($"Version {version}", $"Version {version}");

    public static string NoTelemetry => T("Keine Telemetrie, keine Konten, keine Netzwerkverbindungen. Alles bleibt auf diesem PC.", "No telemetry, no accounts, no network connections. Everything stays on this PC.");

    public static string License => T(
        "Freie Software unter der GNU GPL v3 oder später. Ohne jegliche Gewährleistung.",
        "Free software under the GNU GPL v3 or later. Comes with absolutely no warranty.");

    public static string OpenDataFolder => T("Datenordner öffnen", "Open data folder");

    public static string ShowLicenses => T("Lizenzen", "Licenses");

    public static string ProjectPage => T("Projektseite", "Project page");

    public static string SupportProject => T("Projekt unterstützen", "Support the project");

    public static string RemoveEverything => T("Alles entfernen…", "Remove everything…");

    // Welcome
    public static string WelcomeTitle => T("Clipboard Manager läuft", "Clipboard Manager is running");

    public static string WelcomeIntro => T("Die App arbeitet unauffällig im Hintergrund und merkt sich, was du kopierst.", "The app works quietly in the background and remembers what you copy.");

    public static string WelcomeTry(string hotkey) => T($"Kopiere etwas und drücke dann {hotkey}.", $"Copy something, then press {hotkey}.");

    public static string WelcomeTray => T(
        "Das Symbol liegt im Infobereich (Pfeil ^ neben der Uhr). Ziehe es auf die Taskleiste, damit es immer sichtbar ist.",
        "The icon lives in the notification area (the ^ arrow next to the clock). Drag it onto the taskbar to keep it visible.");

    public static string WelcomeTaskbarSettings => T("Taskleisten-Einstellungen", "Taskbar settings");

    public static string WelcomeDone => T("Fertig", "Done");
}
