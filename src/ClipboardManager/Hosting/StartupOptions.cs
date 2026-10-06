using ClipboardManager.Core;
using ClipboardManager.Core.Diagnostics;

namespace ClipboardManager.Hosting;

internal sealed record StartupOptions(bool Autostart, bool SelfTest, bool SelfTestClipboard, AppPaths Paths, FileLog Log)
{
    public static StartupOptions Parse(string[] args)
    {
        var autostart = args.Contains(Shell.Autostart.AutostartArgument, StringComparer.OrdinalIgnoreCase);
        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        var selfTestClipboard = args.Contains("--selftest-clipboard", StringComparer.OrdinalIgnoreCase);

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

        return new StartupOptions(autostart, selfTest || selfTestClipboard, selfTestClipboard, paths, log);
    }
}
