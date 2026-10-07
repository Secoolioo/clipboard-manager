using ClipboardManager.Core.Updates;

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

    public static string OpenStartupApps => T("Autostart-Apps in Windows verwalten", "Manage startup apps in Windows");

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

    public static string NoTelemetry => T(
        "Keine Telemetrie, keine Konten. Die App geht nur online, wenn du nach Updates suchst – und dann nur zu GitHub. Dein Verlauf bleibt auf diesem PC.",
        "No telemetry, no accounts. The app only goes online when you check for updates, and then only to GitHub. Your history stays on this PC.");

    public static string CheckForUpdates => T("Nach Updates suchen", "Check for updates");

    public static string UpdateChecking => T("Suche nach Updates…", "Checking for updates…");

    public static string UpdateUpToDate(string version) => T($"Du hast die neueste Version (v{version}).", $"You're up to date (v{version}).");

    public static string UpdateAvailable(string version) => T($"Version v{version} ist verfügbar.", $"Version v{version} is available.");

    public static string InstallUpdate => T("Update installieren", "Install update");

    public static string WhatsNew => T("Was ist neu?", "What's new");

    public static string UpdateDownloading(int percent) => T($"Wird heruntergeladen… {percent} %", $"Downloading… {percent}%");

    public static string UpdateProgress => T("Download-Fortschritt", "Download progress");

    public static string UpdateInstalling => T("Wird installiert – die App startet gleich neu…", "Installing – the app restarts in a moment…");

    public static string OpenReleasesPage => T("Releases-Seite öffnen", "Open releases page");

    public static string UpdateMemoryOnlyTitle => T("Verlauf nur im Arbeitsspeicher", "History in memory only");

    public static string UpdateMemoryOnlyMessage(int count) => T(
        $"Das Update startet die App neu. Im Modus „nur im Arbeitsspeicher“ gehen dabei {count} nicht angeheftete Einträge verloren; Angeheftetes bleibt.",
        $"The update restarts the app. In memory-only mode this discards {count} unpinned entries; pins are kept.");

    public static string UpdateNote => T(
        "Fragt nur auf Klick bei GitHub nach der neuesten Version, ohne Daten über dich oder deine Zwischenablage. Einstellungen und Verlauf bleiben beim Update erhalten.",
        "Asks GitHub for the latest version only when you click, without sending anything about you or your clipboard. Settings and history are kept when updating.");

    public static string UpdateFailed(UpdateError error) => error switch
    {
        UpdateError.Offline => T("GitHub ist nicht erreichbar. Prüfe die Internetverbindung und versuche es erneut.", "Could not reach GitHub. Check the internet connection and try again."),
        UpdateError.RateLimited => T("GitHub nimmt von dieser Adresse gerade keine Anfragen an. Versuche es in einer Stunde erneut.", "GitHub is not accepting requests from this address right now. Try again in an hour."),
        UpdateError.ServerError => T("GitHub hat unerwartet geantwortet. Versuche es später erneut.", "GitHub sent an unexpected answer. Try again later."),
        UpdateError.NoDownload => T("Diese Version enthält keine passende EXE für diesen PC.", "This release has no matching EXE for this PC."),
        UpdateError.VerificationFailed => T("Der Download konnte nicht überprüft werden und wurde verworfen.", "The download could not be verified and was discarded."),
        UpdateError.NotWritable => T(
            "Der Ordner der App ist ohne Administratorrechte nicht beschreibbar (z. B. „Programme“). Lade die neue Version über die Releases-Seite herunter.",
            "The app's folder is not writable without administrator rights (for example Program Files). Download the new version from the releases page."),
        UpdateError.Unsupported => T("Diese Kopie kann sich nicht selbst aktualisieren (kein Release-Build).", "This copy cannot update itself (not a release build)."),
        UpdateError.InstallFailed => T("Die neue Version konnte nicht installiert werden. Die bisherige bleibt erhalten.", "The new version could not be installed. The current version is kept."),
        _ => string.Empty,
    };

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
