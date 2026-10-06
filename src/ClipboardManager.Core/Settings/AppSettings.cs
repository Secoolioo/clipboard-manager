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

    public bool HideFromScreenCapture { get; init; } = true;

    /// <summary>Persistent "pause until I resume" (timed pauses are not persisted).</summary>
    public bool MonitoringPaused { get; init; }

    public bool PopupCompact { get; init; }

    public bool PinnedExpanded { get; init; }

    [JsonIgnore]
    public HistoryLimits Limits => HistoryLimits.For(MaxHistoryItems);

    /// <summary>Clamps and cleans values loaded from disk.</summary>
    public AppSettings Normalize()
    {
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
