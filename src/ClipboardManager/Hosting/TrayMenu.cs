using ClipboardManager.Core.Monitoring;
using ClipboardManager.Interop;
using ClipboardManager.Localization;

namespace ClipboardManager.Hosting;

internal enum TrayCommand
{
    None = 0,
    Open = 1,
    Pause5 = 10,
    Pause30 = 11,
    Pause60 = 12,
    PauseIndefinitely = 13,
    Resume = 14,
    IgnoreNext = 15,
    Clear = 20,
    Settings = 21,
    CheckForUpdates = 22,
    Exit = 30,
}

/// <summary>
/// Native popup menu: the rescue path when the hotkey is taken, so it uses the most robust
/// mechanism (correct DPI, keyboard and screen-reader behaviour). Win32 menus stay light in dark
/// mode; there is no documented API to change that.
/// </summary>
internal static class TrayMenu
{
    public static TrayCommand Show(IntPtr owner, int x, int y, MonitoringState monitoring, string hotkeyText)
    {
        var menu = User32.CreatePopupMenu();
        var pauseMenu = IntPtr.Zero;
        try
        {
            Add(menu, TrayCommand.Open, string.IsNullOrEmpty(hotkeyText) ? Strings.TrayOpen : $"{Strings.TrayOpen}\t{hotkeyText}");
            User32.SetMenuDefaultItem(menu, (uint)TrayCommand.Open, 0);
            Separator(menu);

            if (monitoring.IsRecording)
            {
                pauseMenu = User32.CreatePopupMenu();
                Add(pauseMenu, TrayCommand.Pause5, Strings.TrayPause5);
                Add(pauseMenu, TrayCommand.Pause30, Strings.TrayPause30);
                Add(pauseMenu, TrayCommand.Pause60, Strings.TrayPause60);
                Add(pauseMenu, TrayCommand.PauseIndefinitely, Strings.TrayPauseIndefinitely);
                User32.AppendMenu(menu, User32.MF_POPUP, (nuint)pauseMenu, Strings.TrayPause);
                Add(menu, TrayCommand.IgnoreNext, Strings.TrayIgnoreNext, monitoring.IgnoreNext ? User32.MF_CHECKED : 0);
            }
            else
            {
                Add(menu, TrayCommand.Resume, Strings.TrayResume(monitoring.PausedUntil));
            }

            Separator(menu);
            Add(menu, TrayCommand.Clear, Strings.TrayClear);
            Add(menu, TrayCommand.Settings, Strings.TraySettings);
            Add(menu, TrayCommand.CheckForUpdates, Strings.TrayCheckForUpdates);
            Separator(menu);
            Add(menu, TrayCommand.Exit, Strings.TrayExit);

            // Without this the menu does not close when clicking elsewhere (documented TrackPopupMenu requirement).
            User32.SetForegroundWindow(owner);
            var command = User32.TrackPopupMenuEx(menu, User32.TPM_RETURNCMD | User32.TPM_RIGHTBUTTON | User32.TPM_NONOTIFY | User32.TPM_BOTTOMALIGN, x, y, owner, IntPtr.Zero);
            User32.PostMessage(owner, User32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return (TrayCommand)command;
        }
        finally
        {
            // Destroying the parent also destroys the attached submenu.
            User32.DestroyMenu(menu);
        }
    }

    private static void Add(IntPtr menu, TrayCommand command, string text, uint flags = 0) =>
        User32.AppendMenu(menu, User32.MF_STRING | flags, (nuint)(int)command, text);

    private static void Separator(IntPtr menu) => User32.AppendMenu(menu, User32.MF_SEPARATOR, 0, null);
}
