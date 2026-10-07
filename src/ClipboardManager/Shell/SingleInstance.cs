using System.Diagnostics;
using ClipboardManager.Interop;

namespace ClipboardManager.Shell;

internal enum InstanceRole
{
    First,
    Secondary,

    /// <summary>Another instance exists but we may not touch it (it runs elevated).</summary>
    Inaccessible,
}

/// <summary>One instance per user session (the clipboard is per session too).</summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\Secoolioo.ClipboardManager.{3B8C7E52-6A0F-4C1D-9E4B-2F7A1D5C8E90}";

    // Static: a local would be collectable after its last use, silently releasing the mutex.
    private static Mutex? _mutex;

    public static InstanceRole Acquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true }, out var createdNew);
            if (createdNew)
            {
                return InstanceRole.First;
            }

            try
            {
                if (_mutex.WaitOne(0))
                {
                    // The previous owner died without releasing it.
                    return InstanceRole.First;
                }
            }
            catch (AbandonedMutexException)
            {
                return InstanceRole.First;
            }

            _mutex.Dispose();
            _mutex = null;
            return InstanceRole.Secondary;
        }
        catch (UnauthorizedAccessException)
        {
            return InstanceRole.Inaccessible;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return InstanceRole.Inaccessible;
        }
    }

    /// <summary>Asks the running instance to open its popup. Gives it our right to take the foreground.</summary>
    public static bool ActivateExisting(TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            var window = User32.FindWindow(null, HostWindow.WindowTitle);
            if (window != IntPtr.Zero)
            {
                _ = User32.GetWindowThreadProcessId(window, out var pid);
                User32.AllowSetForegroundWindow(pid);
                return User32.PostMessage(window, HostWindow.ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// After an in-app update the previous version still holds the mutex while it shuts down. Waits
    /// (bounded) for it to exit, so this start becomes the first instance.
    /// </summary>
    public static void WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var previous = Process.GetProcessById(processId);
            using var self = Process.GetCurrentProcess();

            // Process IDs are reused: only wait for another copy of this app. The installing
            // download runs as "ClipboardManager.exe.new", whose process name is "ClipboardManager.exe".
            if (previous.ProcessName.StartsWith(self.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                self.ProcessName.StartsWith(previous.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                previous.WaitForExit(timeout);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
