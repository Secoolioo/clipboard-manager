using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.Updates;
using ClipboardManager.Shell;

namespace ClipboardManager.Updates;

/// <summary>
/// Hands over to a verified download in two steps, so no running image is ever renamed (a
/// single-file app keeps reading its assemblies from its own path):
/// 1. The running version starts "*.new" with <see cref="FinishUpdateArgument"/> and its process ID,
///    then shuts down normally.
/// 2. "*.new" waits for it to exit, moves the previous EXE aside, copies itself to the EXE path and
///    starts that with <see cref="UpdatedFromArgument"/>; it never loads the UI.
/// The final process waits for step 2 to end and deletes the leftovers. Every future version must
/// keep honoring both arguments.
/// </summary>
internal static class SelfUpdate
{
    public const string FinishUpdateArgument = "--finish-update";
    public const string UpdatedFromArgument = "--updated-from";

    private const string Category = "Update";

    /// <summary>Step 1, in the running version: start the download and let it take over.</summary>
    public static async Task RestartIntoNewVersionAsync(string exePath, FileLog log)
    {
        var swap = new ExeSwap(exePath);
        try
        {
            // No shell and no elevation: the same user starts the verified file next to the EXE.
            using var process = Start(swap.NewPath, FinishUpdateArgument, Environment.ProcessId);

            // The download waits for this process; if it is gone already, it was blocked or crashed.
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            try
            {
                await process.WaitForExitAsync(probe.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                log.Info(Category, "Started the new version");
                return;
            }

            throw new InvalidOperationException("The new version exited immediately");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Nothing was replaced yet: this version simply keeps running.
            log.Error(Category, "Starting the new version failed; the current version is kept", ex);
            swap.TryDiscardDownload();
            throw new UpdateException(UpdateError.InstallFailed, ex);
        }
    }

    /// <summary>Step 2, in the downloaded EXE ("*.new"): put a copy of itself in place and start it.</summary>
    public static int FinishInstall(int previousProcessId, FileLog log)
    {
        var running = Environment.ProcessPath;
        if (running is null || !running.EndsWith(ExeSwap.DownloadSuffix, StringComparison.OrdinalIgnoreCase))
        {
            log.Error(Category, "The update helper does not run from a downloaded file");
            return 1;
        }

        var swap = new ExeSwap(running[..^ExeSwap.DownloadSuffix.Length]);
        SingleInstance.WaitForExit(previousProcessId, TimeSpan.FromSeconds(20));
        try
        {
            swap.Install();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The previous EXE is back at its path: start it again, quietly, as if Windows had.
            log.Error(Category, "Installing the new version failed; the previous version is restored", ex);
            return TryStart(swap.ExePath, Autostart.AutostartArgument, null, log) ? 1 : 2;
        }

        log.Info(Category, "Installed the new version");
        return TryStart(swap.ExePath, UpdatedFromArgument, Environment.ProcessId, log) ? 0 : 2;
    }

    private static Process Start(string exe, string argument, int? processId)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(argument);
        if (processId is { } id)
        {
            start.ArgumentList.Add(id.ToString(CultureInfo.InvariantCulture));
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The new version did not start");
    }

    private static bool TryStart(string exe, string argument, int? processId, FileLog log)
    {
        try
        {
            Start(exe, argument, processId).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log.Error(Category, "Starting the installed version failed", ex);
            return false;
        }
    }
}
