using System.Text;
using System.Text.Json;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Core.Settings;

public enum SettingsLoadState
{
    /// <summary>No settings file yet.</summary>
    Missing,
    Loaded,

    /// <summary>The file was unreadable; it was kept as settings.json.corrupt and defaults are used.</summary>
    Corrupt,
}

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private const string Category = "Settings";
    private readonly string _file;
    private readonly FileLog _log;

    public SettingsStore(string file, FileLog log)
    {
        _file = file;
        _log = log;
    }

    public (AppSettings Settings, SettingsLoadState State) Load()
    {
        if (!File.Exists(_file))
        {
            return (new AppSettings(), SettingsLoadState.Missing);
        }

        try
        {
            var json = File.ReadAllText(_file, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                ?? throw new JsonException("empty settings");
            return (loaded.Normalize(), SettingsLoadState.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning(Category, "Settings file unreadable, using defaults", ex);
            try
            {
                File.Move(_file, _file + ".corrupt", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                _log.Warning(Category, "Could not preserve the unreadable settings file", moveError);
            }

            return (new AppSettings(), SettingsLoadState.Corrupt);
        }
    }

    /// <summary>Writes to a temp file and renames it over the old one, so a crash never leaves half a file.</summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temp = _file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var json = JsonSerializer.Serialize(settings.Normalize(), SettingsJsonContext.Default.AppSettings);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
            }

            File.Move(temp, _file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Category, "Saving settings failed", ex);
            return false;
        }
    }
}
