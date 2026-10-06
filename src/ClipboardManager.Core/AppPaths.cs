namespace ClipboardManager.Core;

/// <summary>
/// All files the app creates live below one directory, so "delete everything" is a single folder.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Overrides the data directory (tests, self-test, screenshots). Not a user setting.</summary>
    public const string DataDirectoryVariable = "CLIPBOARDMANAGER_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string HistoryDatabase => Path.Combine(DataDirectory, "history.db");

    public string QuarantineDatabase => Path.Combine(DataDirectory, "history.corrupt.db");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string SessionMarker => Path.Combine(DataDirectory, "session.active");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "Secoolioo", "ClipboardManager"));
    }

    public void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
