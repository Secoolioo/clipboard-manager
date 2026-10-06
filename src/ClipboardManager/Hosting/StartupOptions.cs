using ClipboardManager.Core;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Hosting;

internal sealed record StartupOptions(bool Autostart, bool SelfTest, bool SelfTestClipboard, AppPaths Paths, FileLog Log)
{
    /// <summary>Self-test only: stay idle this long after the checks (for CPU/memory measurements).</summary>
    public int IdleSeconds { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        var autostart = args.Contains(Shell.Autostart.AutostartArgument, StringComparer.OrdinalIgnoreCase);
        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        var selfTestClipboard = args.Contains("--selftest-clipboard", StringComparer.OrdinalIgnoreCase);
        var idleIndex = Array.FindIndex(args, a => string.Equals(a, "--idle", StringComparison.OrdinalIgnoreCase));
        var idle = idleIndex >= 0 && idleIndex + 1 < args.Length && int.TryParse(args[idleIndex + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? Math.Clamp(seconds, 0, 3600) : 0;

        var paths = selfTest || selfTestClipboard
            ? new AppPaths(Path.Combine(Path.GetTempPath(), "ClipboardManager-selftest-" + Environment.ProcessId))
            : AppPaths.Default();

        FileLog log;
        try
        {
            paths.EnsureCreated();
            log = new FileLog(paths.LogDirectory, FileLog.LevelFromEnvironment());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log = FileLog.Null;
        }

        return new StartupOptions(autostart, selfTest || selfTestClipboard, selfTestClipboard, paths, log) { IdleSeconds = idle };
    }
}
