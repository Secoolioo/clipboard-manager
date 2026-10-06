using System.Globalization;
using System.Text;

namespace ClipboardManager.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Minimal size-bounded file log. Privacy rule: callers pass only technical, content-free
/// messages; exceptions are logged by type, HResult and stack trace, never by Message
/// (messages can echo clipboard input).
/// </summary>
public sealed class FileLog
{
    public const string LevelVariable = "CLIPBOARDMANAGER_LOG";
    public const long MaxFileBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string? _file;
    private readonly string? _backup;

    public FileLog(string? directory, LogLevel minimumLevel)
    {
        MinimumLevel = minimumLevel;
        if (directory is not null)
        {
            _file = Path.Combine(directory, "app.log");
            _backup = Path.Combine(directory, "app.1.log");
        }
    }

    /// <summary>A log that discards everything (used until the data directory exists, and in tests).</summary>
    public static FileLog Null { get; } = new(null, LogLevel.Error);

    public LogLevel MinimumLevel { get; }

    public static LogLevel LevelFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(LevelVariable);
        return value?.Trim().ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Warning,
        };
    }

    public bool IsEnabled(LogLevel level) => _file is not null && level >= MinimumLevel;

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);

    public void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public void Warning(string category, string message, Exception? exception = null) => Write(LogLevel.Warning, category, message, exception);

    public void Error(string category, string message, Exception? exception = null) => Write(LogLevel.Error, category, message, exception);

    /// <summary>Renders an exception without its Message (which may contain user data).</summary>
    public static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.Append(" ---> ");
            }

            builder.Append(current.GetType().FullName)
                .Append(" (0x")
                .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .Append(')');
            if (current.StackTrace is { } stack)
            {
                builder.AppendLine().Append(stack);
            }
        }

        return builder.ToString();
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var line = new StringBuilder(128)
            .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                _ => "ERR",
            })
            .Append(" [").Append(category).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.Append(" | ").Append(Describe(exception));
        }

        line.AppendLine();

        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_file!, line.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_file!);
        if (info.Exists && info.Length >= MaxFileBytes)
        {
            File.Move(_file!, _backup!, overwrite: true);
        }
        else if (!info.Exists)
        {
            Directory.CreateDirectory(info.DirectoryName!);
        }
    }
}
