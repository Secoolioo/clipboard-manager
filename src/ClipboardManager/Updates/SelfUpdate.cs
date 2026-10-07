using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.Updates;

/// <summary>
/// Puts a verified download in place and starts it. The new process gets
/// <see cref="UpdatedFromArgument"/> with our process ID and waits for us to exit before it takes
/// the single-instance mutex, so the caller shuts down normally right after. Every future version
/// must keep honoring that argument.
/// </summary>
internal static class SelfUpdate
{
    public const string UpdatedFromArgument = "--updated-from";

    private const string Category = "Update";

    public static async Task RestartIntoNewVersionAsync(string exePath, FileLog log)
    {
        var swap = new ExeSwap(exePath);
        try
        {
            await Task.Run(swap.Apply).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error(Category, "Replacing the EXE failed; the previous version is kept", ex);
            if (File.Exists(swap.ExePath))
            {
                swap.TryDiscardDownload();
            }

            throw new UpdateException(ex is UnauthorizedAccessException ? UpdateError.NotWritable : UpdateError.InstallFailed, ex);
        }

        try
        {
            // No shell and no elevation: the same user starts the same path the autostart entry points to.
            var start = new ProcessStartInfo(swap.ExePath) { UseShellExecute = false };
            start.ArgumentList.Add(UpdatedFromArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The new version did not start");

            // The new process waits for this one; if it is gone already, it was blocked or crashed.
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
            log.Error(Category, "Starting the new version failed; restoring the previous one", ex);
            try
            {
                await Task.Run(swap.Revert).ConfigureAwait(true);
            }
            catch (Exception revertError) when (revertError is IOException or UnauthorizedAccessException)
            {
                log.Error(Category, "Restoring the previous version failed", revertError);
            }

            throw new UpdateException(UpdateError.InstallFailed, ex);
        }
    }
}
