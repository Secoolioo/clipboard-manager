using System.Globalization;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Settings;

namespace ClipboardManager.Localization;

/// <summary>
/// All UI text, German and English side by side so they cannot drift apart. Two languages do not
/// justify a resource pipeline; a third language would be the moment to move to .resx.
/// </summary>
public static partial class Strings
{
    private static bool _german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public static bool IsGerman => _german;

    public static CultureInfo Culture => _german ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

    public static void Apply(LanguagePreference preference)
    {
        _german = preference switch
        {
            LanguagePreference.German => true,
            LanguagePreference.English => false,
            _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "de" || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de",
        };
    }

    private static string T(string de, string en) => _german ? de : en;

    // App
    public static string AppName => "Clipboard Manager";

    // Popup
    public static string SearchPlaceholder => T("Verlauf durchsuchen…", "Search history…");

    public static string PopupAutomationName => T("Zwischenablageverlauf", "Clipboard history");

    public static string SearchHelp => T(
        "Tippen filtert. Pfeiltasten wählen, Eingabe kopiert, Umschalt+Entf löscht, Strg+P heftet an, Tab zeigt die Vorschau, Esc schließt.",
        "Type to filter. Arrow keys select, Enter copies, Shift+Delete removes, Ctrl+P pins, Tab toggles the preview, Esc closes.");

    public static string PinnedHeader => T("ANGEHEFTET", "PINNED");

    public static string HistoryHeader => T("VERLAUF", "HISTORY");

    public static string MorePinned(int count) => T($"+ {count} weitere", $"+ {count} more");

    public static string FewerPinned => T("Weniger anzeigen", "Show fewer");

    public static string CurrentBadge => T("AKTUELL", "CURRENT");

    public static string Loading => T("Lädt…", "Loading…");

    public static string EmptyTitle => T("Noch nichts kopiert", "Nothing copied yet");

    public static string EmptyHint => T("Kopiere etwas mit Strg+C – es erscheint hier.", "Copy something with Ctrl+C and it shows up here.");

    public static string NoResults => T("Keine Treffer", "No matches");

    public static string HintCopy => T("Kopieren", "Copy");

    public static string HintDelete => T("Löschen", "Delete");

    public static string HintPin => T("Anheften", "Pin");

    public static string HintPreview => T("Vorschau", "Preview");

    public static string HintClose => T("Schließen", "Close");

    public static string Deleted => T("Gelöscht – Strg+Z stellt wieder her", "Deleted – Ctrl+Z to undo");

    public static string CopyFailed => T("Zwischenablage gerade gesperrt – bitte erneut versuchen.", "The clipboard is locked right now – please try again.");

    public static string Truncated => T("Gekürzt – Eingabe kopiert den vollständigen Text.", "Truncated – Enter copies the full text.");

    public static string PartiallySearchable => T("nur Anfang durchsucht", "only the beginning is searchable");

    public static string Characters(int count) => _german ? $"{count:N0} Zeichen" : $"{count:N0} characters";

    public static string Lines(int count) => count == 1 ? T("1 Zeile", "1 line") : T($"{count:N0} Zeilen", $"{count:N0} lines");

    public static string LinesShort(int count) => T($"{count} Zeilen", $"{count} lines");

    public static string CopiedAt(string when) => T($"zuletzt verwendet {when}", $"last used {when}");

    public static string PinnedAt(string when) => T($"angeheftet {when}", $"pinned {when}");

    public static string MenuCopy => T("Kopieren", "Copy");

    public static string MenuPin => T("Anheften", "Pin");

    public static string MenuUnpin => T("Lösen", "Unpin");

    public static string MenuDelete => T("Löschen", "Delete");

    // Status
    public static string StatusActive => T("Aktiv", "Recording");

    public static string StatusPausedUntil(DateTimeOffset until) => T($"Pausiert bis {until.ToLocalTime():HH:mm}", $"Paused until {until.ToLocalTime():t}");

    public static string StatusPaused => T("Pausiert", "Paused");

    public static string StatusIgnoreNext => T("Nächste Kopie wird ignoriert", "Next copy will be ignored");

    public static string Resume => T("Fortsetzen", "Resume");

    public static string PauseBanner(DateTimeOffset? until) =>
        until is { } u
            ? T($"Aufzeichnung pausiert bis {u.ToLocalTime():HH:mm}", $"Recording paused until {u.ToLocalTime():t}")
            : T("Aufzeichnung pausiert", "Recording paused");

    public static string HotkeyMissing => T("Kein Hotkey – in den Einstellungen wählen", "No hotkey – choose one in Settings");

    public static string HotkeyMissingShort => T("kein Hotkey", "no hotkey");

    public static string Matches(int count) => count == 1 ? T("1 Treffer", "1 match") : T($"{count} Treffer", $"{count} matches");

    public static string MemoryOnlyOffTitle => T("Verlauf wieder speichern", "Save history again");

    public static string MemoryOnlyOffMessage(int count) => T(
        $"Ab jetzt wird der Verlauf wieder gespeichert. {count} Einträge liegen bisher nur im Arbeitsspeicher und würden dabei ebenfalls gespeichert.",
        $"From now on the history is saved again. {count} entries are currently held in memory only and would be saved as well.");

    public static string MemoryOnlyOffDiscard => T("Diese Einträge stattdessen verwerfen", "Discard those entries instead");

    public static string MemoryOnlyOffConfirm => T("Speichern einschalten", "Turn saving on");

    public static string Skipped(SkipReason reason) => reason switch
    {
        SkipReason.Paused => T("Letzte Kopie nicht gespeichert – Aufzeichnung pausiert", "Last copy not saved – recording paused"),
        SkipReason.IgnoredOnce => T("Letzte Kopie nicht gespeichert – wie gewünscht ignoriert", "Last copy not saved – ignored as requested"),
        SkipReason.MarkedBySource => T("Letzte Kopie nicht gespeichert – von der Quell-App als vertraulich markiert", "Last copy not saved – marked private by the source app"),
        SkipReason.ExcludedApp => T("Letzte Kopie nicht gespeichert – ausgeschlossene App", "Last copy not saved – excluded app"),
        SkipReason.LooksLikeSecret => T("Letzte Kopie nicht gespeichert – sieht aus wie Zugangsdaten", "Last copy not saved – looks like a credential"),
        SkipReason.TooLarge => T("Letzte Kopie nicht gespeichert – zu groß", "Last copy not saved – too large"),
        SkipReason.NotText => T("Letzte Kopie nicht gespeichert – kein Text (Bild oder Dateien)", "Last copy not saved – not text (image or files)"),
        SkipReason.ReadFailed => T("Letzte Kopie nicht gespeichert – Zwischenablage war gesperrt", "Last copy not saved – the clipboard was locked"),
        _ => string.Empty,
    };

    // Relative time
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToLocalTime();
        var delta = now - when;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return T("gerade eben", "just now");
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = (int)delta.TotalMinutes;
            return T($"vor {minutes} Min.", $"{minutes} min ago");
        }

        var today = now.ToLocalTime().Date;
        if (local.Date == today)
        {
            return local.ToString("t", Culture);
        }

        if (local.Date == today.AddDays(-1))
        {
            return T($"gestern {local:HH:mm}", $"yesterday {local.ToString("t", Culture)}");
        }

        return local.ToString(local.Year == today.Year ? (_german ? "d. MMM" : "MMM d") : "d", Culture);
    }

    public static string AbsoluteTime(DateTimeOffset when) => when.ToLocalTime().ToString("g", Culture);

    // Tray
    public static string TrayOpen => T("Verlauf öffnen", "Open history");

    public static string TrayPause => T("Pausieren", "Pause");

    public static string TrayPause5 => T("5 Minuten", "5 minutes");

    public static string TrayPause30 => T("30 Minuten", "30 minutes");

    public static string TrayPause60 => T("1 Stunde", "1 hour");

    public static string TrayPauseIndefinitely => T("Bis ich fortsetze", "Until I resume");

    public static string TrayResume(DateTimeOffset? until) =>
        until is { } u ? T($"Fortsetzen (pausiert bis {u.ToLocalTime():HH:mm})", $"Resume (paused until {u.ToLocalTime():t})") : T("Fortsetzen", "Resume");

    public static string TrayIgnoreNext => T("Nächste Kopie ignorieren", "Ignore next copy");

    public static string TrayClear => T("Verlauf löschen…", "Clear history…");

    public static string TraySettings => T("Einstellungen…", "Settings…");

    public static string TrayExit => T("Beenden", "Exit");

    public static string TrayTooltip(string status) => $"{AppName} – {status}";

    // Notifications
    public static string HotkeyFallbackNotice(string used) => T($"Strg+Umschalt+V ist belegt. Stattdessen gilt {used}.", $"Ctrl+Shift+V is taken. Using {used} instead.");

    public static string HotkeyTakenNotice => T("Der Hotkey ist von einer anderen App belegt. Öffne den Verlauf über das Tray-Symbol und wähle in den Einstellungen einen anderen.", "The hotkey is used by another app. Open the history from the tray icon and pick another one in Settings.");

    public static string StoreNotice(Core.History.StoreNotice notice) => notice switch
    {
        Core.History.StoreNotice.RecoveredFromCorruption => T("Die Verlaufsdatei war beschädigt und wurde neu angelegt. Eine Kopie liegt im Datenordner.", "The history file was damaged and has been recreated. A copy was kept in the data folder."),
        Core.History.StoreNotice.DatabaseInUse => T("Der Verlauf wird bereits in einer anderen Sitzung verwendet. Diese Sitzung speichert nichts.", "The history is in use by another session. This session will not save anything."),
        Core.History.StoreNotice.DatabaseFromNewerVersion => T("Der Verlauf stammt von einer neueren Version. Er bleibt unverändert; diese Version speichert nichts.", "The history was created by a newer version. It is left untouched; this version will not save anything."),
        Core.History.StoreNotice.DatabaseUnavailable => T("Der Verlauf kann nicht gespeichert werden (Datenordner nicht beschreibbar).", "The history cannot be saved (data folder not writable)."),
        _ => string.Empty,
    };

    // Dialogs
    public static string ClearTitle => T("Verlauf löschen", "Clear history");

    public static string ClearMessage(int unpinned, int pinned) =>
        T($"{unpinned} Einträge endgültig löschen? {pinned} angeheftete bleiben erhalten.", $"Permanently delete {unpinned} entries? {pinned} pinned entries are kept.");

    public static string ClearAlsoPinned => T("Auch angeheftete Einträge löschen", "Also delete pinned entries");

    public static string ClearConfirm => T("Löschen", "Delete");

    public static string Cancel => T("Abbrechen", "Cancel");

    public static string LimitTitle => T("Verlaufsgröße verringern", "Reduce history size");

    public static string LimitMessage(int count) => T($"{count} ältere Einträge werden endgültig gelöscht.", $"{count} older entries will be permanently deleted.");

    public static string LimitConfirm => T("Verringern", "Reduce");

    public static string RemoveAllTitle => T("Alles entfernen", "Remove everything");

    public static string RemoveAllMessage => T(
        "Entfernt Autostart, Startmenü-Eintrag, alle Einstellungen und den gesamten Verlauf (auch Angeheftetes) und beendet die App. Danach kannst du die EXE löschen.",
        "Removes autostart, the Start menu entry, all settings and the entire history (including pins) and exits. Afterwards you can delete the EXE.");

    public static string RemoveAllConfirm => T("Alles entfernen", "Remove everything");

    // Errors
    public static string StartupFailed => T("Clipboard Manager konnte nicht starten. Details stehen im Log im Datenordner.", "Clipboard Manager could not start. Details are in the log in the data folder.");

    public static string AlreadyRunningElevated => T("Clipboard Manager läuft bereits als Administrator. Beende diese Instanz zuerst.", "Clipboard Manager is already running as administrator. Exit that instance first.");
}
