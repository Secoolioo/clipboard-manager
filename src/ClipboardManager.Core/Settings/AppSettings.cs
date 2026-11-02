using System.Text.Json.Serialization;
using ClipboardManager.Core.History;

namespace ClipboardManager.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum LanguagePreference
{
    System,
    German,
    English,
}

/// <summary>
/// User settings. Autostart is intentionally absent: its single source of truth is the registry.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public static readonly int[] HistorySizePresets = [25, 50, 100, 250, 500, 1000, 2500, 5000];

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public LanguagePreference Language { get; init; } = LanguagePreference.System;

    public int MaxHistoryItems { get; init; } = 100;

    public bool MemoryOnly { get; init; }

    public HotkeyGesture Hotkey { get; init; } = HotkeyGesture.Default;

    public IReadOnlyList<string> ExcludedApps { get; init; } = [];

    public bool SkipDetectedSecrets { get; init; } = true;

    /// <summary>
    /// Screenshots and copied pictures are not saved. Stored as "skip" on purpose: the JSON source
    /// generator gives an init-only property that is missing from the file its default value, so
    /// settings files from before 0.11 keep images on.
    /// </summary>
    public bool SkipImages { get; init; }

    public bool HideFromScreenCapture { get; init; } = true;

    /// <summary>Persistent "pause until I resume" (timed pauses are not persisted).</summary>
    public bool MonitoringPaused { get; init; }

    public bool PopupCompact { get; init; }

    public bool PinnedExpanded { get; init; }

    /// <summary>The history window stays open (and on top) when another window is activated. Off: it closes, as before 0.12.</summary>
    public bool KeepPopupOpen { get; init; }

    /// <summary>Top-left of a history window the user dragged, in physical screen pixels; null: the default spot.</summary>
    public int? PopupLeft { get; init; }

    public int? PopupTop { get; init; }

    [JsonIgnore]
    public HistoryLimits Limits => HistoryLimits.For(MaxHistoryItems);

    /// <summary>Clamps and cleans values loaded from disk.</summary>
    public AppSettings Normalize()
    {
        // Beyond any real desktop (-32000 is where Windows parks minimized windows): use the default spot.
        // A range check, not Math.Abs: that throws for int.MinValue from a hand-edited file.
        const int MaxScreenCoordinate = 32_000;
        var hasPosition = PopupLeft is > -MaxScreenCoordinate and < MaxScreenCoordinate &&
            PopupTop is > -MaxScreenCoordinate and < MaxScreenCoordinate;
        var hotkey = Hotkey is null || Hotkey.Validate() != HotkeyValidation.Valid ? HotkeyGesture.Default : Hotkey;
        var excluded = (ExcludedApps ?? [])
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => Path.GetFileName(name.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
            Language = Enum.IsDefined(Language) ? Language : LanguagePreference.System,
            MaxHistoryItems = Math.Clamp(MaxHistoryItems, HistoryLimits.MinItems, HistoryLimits.MaxItemsUpperBound),
            Hotkey = hotkey,
            ExcludedApps = excluded,
            PopupLeft = hasPosition ? PopupLeft : null,
            PopupTop = hasPosition ? PopupTop : null,
        };
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
