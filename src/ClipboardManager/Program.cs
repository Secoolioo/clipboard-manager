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
            if (!options.SelfTest)
            {
                switch (SingleInstance.Acquire())
                {
                    case InstanceRole.Secondary:
                        // A manual start opens the running instance's popup; an autostart duplicate just leaves.
                        if (!options.Autostart)
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
            return app.Run();
        }
        catch (Exception ex)
        {
            // Plain Win32 message box: WPF itself may be what failed to load.
            options?.Log.Error("Startup", "Fatal startup failure", ex);
            User32.MessageBox(IntPtr.Zero, Strings.StartupFailed, Strings.AppName, User32.MB_ICONERROR);
            return 1;
        }
        finally
        {
            SingleInstance.Release();
        }
    }
}
