using System.Diagnostics;
using ClipboardManager.Hosting;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Shell;

[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace ClipboardManager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions? options = null;
        try
        {
            options = StartupOptions.Parse(args);
            if (options.FinishUpdateFrom is { } starter)
            {
                // The downloaded EXE installing itself; it never takes the mutex or loads the UI.
                return Updates.SelfUpdate.FinishInstall(starter, options.Log);
            }

            if (!options.SelfTest)
            {
                if (options.UpdatedFrom is { } previous)
                {
                    // Started by the in-app updater: the replaced version holds the mutex until it has exited.
                    SingleInstance.WaitForExit(previous, TimeSpan.FromSeconds(20));
                }

                var role = SingleInstance.Acquire();
                for (var attempt = 0; options.UpdatedFrom is not null && role == InstanceRole.Secondary && attempt < 40; attempt++)
                {
                    // The previous version can take a little longer to let go (store flush, a slow
                    // clipboard read). Leaving now would end with no instance running at all.
                    Thread.Sleep(250);
                    role = SingleInstance.Acquire();
                }

                switch (role)
                {
                    case InstanceRole.Secondary:
                        // A manual start opens the running instance's popup; an autostart or update duplicate just leaves.
                        if (options.IsManualStart)
                        {
                            SingleInstance.ActivateExisting(TimeSpan.FromSeconds(3));
                        }

                        return 0;
                    case InstanceRole.Inaccessible:
                        if (!options.Autostart)
                        {
                            User32.MessageBox(IntPtr.Zero, Strings.AlreadyRunningElevated, Strings.AppName, User32.MB_ICONINFORMATION);
                        }

                        return 0;
                }
            }

            // Crash reports must not include the heap: it holds clipboard history.
            _ = Kernel32.WerSetFlags(Kernel32.WER_FAULT_REPORTING_FLAG_NOHEAP);

            var app = new App(options);
            app.InitializeComponent();
            var exitCode = app.Run();
            if (!options.SelfTest && (app.FatalError ?? app.StartupError) is not null)
            {
                // The dispatcher has stopped: the message box cannot re-enter WPF any more.
                ShowError(app.FatalError is not null ? Strings.RuntimeFailed(LogFile(options)) : Strings.StartupFailed(LogFile(options)), options);
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            options?.Log.Error("Startup", "Fatal startup failure", ex);
            if (options?.SelfTest == true)
            {
                // Never block an unattended self-test with a dialog; report through the result file.
                var output = Environment.GetEnvironmentVariable(SelfTest.OutputVariable);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    File.WriteAllText(output, "FAIL startup: " + Core.Diagnostics.FileLog.Describe(ex) + Environment.NewLine);
                }

                return 1;
            }

            StopDispatcher();
            ShowError(Strings.StartupFailed(LogFile(options)), options);
            return 1;
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    private static string LogFile(StartupOptions? options) =>
        Path.Combine((options?.Paths ?? Core.AppPaths.Default()).LogDirectory, "app.log");

    /// <summary>Plain Win32 message box (WPF itself may be what failed) that offers to show the log.</summary>
    private static void ShowError(string text, StartupOptions? options)
    {
        // The message says "start it again": a start while it is still open must not see a running instance.
        SingleInstance.Release();
        if (User32.MessageBox(IntPtr.Zero, text, Strings.AppName, User32.MB_ICONERROR | User32.MB_YESNO) != User32.IDYES)
        {
            return;
        }

        try
        {
            // Full path: a bare "explorer.exe" would be searched in the current directory (e.g. Downloads) first.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var log = LogFile(options);
            var arguments = File.Exists(log) ? $"/select,\"{log}\"" : $"\"{Path.GetDirectoryName(log)}\"";
            Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            options?.Log.Warning("Startup", "Could not open the log folder", ex);
        }
    }

    /// <summary>Ends the WPF message loop before a modal dialog could pump queued UI work again.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StopDispatcher()
    {
        try
        {
            System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
        }
        catch (Exception)
        {
            // WPF may be what failed to load; the message box below works without it.
        }
    }
}
