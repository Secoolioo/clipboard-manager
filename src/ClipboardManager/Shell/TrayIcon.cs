using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Interop;
using Microsoft.Win32;

namespace ClipboardManager.Shell;

/// <summary>
/// Notification-area icon (NOTIFYICON_VERSION_4, identified by window + ID, not by GUID because a
/// GUID registration is bound to the EXE path and a portable EXE gets moved).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string Category = "Tray";
    private const uint IconId = 1;
    private static readonly int[] RetrySeconds = [2, 4, 8, 16, 30, 60];

    private readonly HostWindow _host;
    private readonly FileLog _log;
    private readonly DispatcherTimer _retry;
    private IntPtr _icon;
    private bool _added;
    private bool _paused;
    private string _tooltip = string.Empty;
    private int _retryIndex;

    public TrayIcon(HostWindow host, FileLog log)
    {
        _host = host;
        _log = log;
        _retry = new DispatcherTimer(DispatcherPriority.Background) { IsEnabled = false };
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            Add();
        };
        _host.TaskbarRecreated += (_, _) =>
        {
            // Explorer restarted, or the primary display's DPI changed: re-add at the right size.
            _added = false;
            RefreshIcon();
            Add();
        };
    }

    public void Show(string tooltip, bool paused)
    {
        _tooltip = tooltip;
        _paused = paused;
        RefreshIcon();
        Add();
    }

    public void Update(string tooltip, bool paused)
    {
        var iconChanged = paused != _paused;
        _tooltip = tooltip;
        _paused = paused;
        if (iconChanged)
        {
            RefreshIcon();
        }

        if (_added)
        {
            var data = Data(Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
            if (!Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data))
            {
                _added = false;
                Add();
            }
        }
    }

    /// <summary>Taskbar theme or DPI changed.</summary>
    public void RefreshIcon()
    {
        var old = _icon;
        var size = User32.GetSystemMetricsForDpi(User32.SM_CXSMICON, User32.GetDpiForSystem());
        var color = TaskbarUsesLightTheme() ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        _icon = User32.IconFromPng(BrandIcon.RenderPng(BrandIcon.CreateGlyph(color, _paused), size), size);
        if (_added)
        {
            var data = Data(Shell32.NIF_ICON);
            Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
        }

        if (old != IntPtr.Zero)
        {
            User32.DestroyIcon(old);
        }
    }

    /// <summary>A Windows notification (rendered as a toast). Never put clipboard content in here.</summary>
    public void Notify(string title, string text, bool warning = false)
    {
        if (!_added)
        {
            return;
        }

        var data = Data(Shell32.NIF_INFO);
        data.dwInfoFlags = (warning ? Shell32.NIIF_WARNING : Shell32.NIIF_INFO) | Shell32.NIIF_RESPECT_QUIET_TIME;
        unsafe
        {
            Shell32.Copy(title, data.szInfoTitle, 64);
            Shell32.Copy(text, data.szInfo, 256);
        }

        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        _retry.Stop();
        if (_added)
        {
            var data = Data(0);
            Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            User32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    internal static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Add()
    {
        if (_added)
        {
            return;
        }

        var data = Data(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        if (Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref data) || Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, ref data))
        {
            data.uVersion = Shell32.NOTIFYICON_VERSION_4;
            Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref data);
            _added = true;
            _retryIndex = 0;
            return;
        }

        // Explorer is often still busy right after login; retry with one-shot timers, then rely on TaskbarCreated.
        if (_retryIndex < RetrySeconds.Length)
        {
            _log.Info(Category, "Notification area not ready, retrying");
            _retry.Interval = TimeSpan.FromSeconds(RetrySeconds[_retryIndex++]);
            _retry.Start();
        }
        else
        {
            _log.Warning(Category, "Could not add the notification icon; waiting for TaskbarCreated");
        }
    }

    private Shell32.NOTIFYICONDATAW Data(uint flags)
    {
        var data = new Shell32.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Shell32.NOTIFYICONDATAW>(),
            hWnd = _host.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = HostWindow.TrayCallbackMessage,
            hIcon = _icon,
        };
        unsafe
        {
            Shell32.Copy(_tooltip, data.szTip, 128);
        }

        return data;
    }
}
