using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClipboardManager.Interop;

namespace ClipboardManager.Shell;

internal readonly record struct TrayEvent(int Code, int AnchorX, int AnchorY);

/// <summary>
/// Hidden, never-shown top-level window that owns the clipboard listener, the global hotkey, the
/// tray icon and the activation message. It must be top-level (not HWND_MESSAGE): message-only
/// windows receive neither TaskbarCreated, WM_SETTINGCHANGE nor the session-end messages.
/// </summary>
internal sealed class HostWindow : IDisposable
{
    /// <summary>Unique title used by a second instance to find this one.</summary>
    public const string WindowTitle = "Secoolioo.ClipboardManager.Host.7c1f4e2a";

    public const int TrayCallbackMessage = User32.WM_APP + 1;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly HwndSource _source;
    private bool _disposed;

    public HostWindow()
    {
        var parameters = new HwndSourceParameters(WindowTitle)
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
            ParentWindow = IntPtr.Zero,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        // Explorer runs at medium integrity; if we ever run elevated these must still arrive.
        User32.ChangeWindowMessageFilterEx(Handle, TaskbarCreatedMessage, User32.MSGFLT_ALLOW, IntPtr.Zero);
        User32.ChangeWindowMessageFilterEx(Handle, ActivateMessage, User32.MSGFLT_ALLOW, IntPtr.Zero);
        Wtsapi32.WTSRegisterSessionNotification(Handle, Wtsapi32.NOTIFY_FOR_THIS_SESSION);
    }

    public static uint TaskbarCreatedMessage { get; } = User32.RegisterWindowMessage("TaskbarCreated");

    public static uint ActivateMessage { get; } = User32.RegisterWindowMessage("Secoolioo.ClipboardManager.Activate");

    public IntPtr Handle => _source.Handle;

    public event EventHandler? ClipboardUpdated;

    public event EventHandler<int>? HotkeyPressed;

    public event EventHandler<TrayEvent>? TrayActivity;

    public event EventHandler? TaskbarRecreated;

    public event EventHandler? ActivateRequested;

    /// <summary>WM_SETTINGCHANGE with its (possibly null) section name, e.g. "ImmersiveColorSet".</summary>
    public event EventHandler<string?>? SettingChanged;

    /// <summary>Display change, resume from sleep, session unlock or remote connect.</summary>
    public event EventHandler? EnvironmentChanged;

    /// <summary>The session is really ending (WM_ENDSESSION with TRUE). Handlers must finish quickly.</summary>
    public event EventHandler? SessionEnding;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Wtsapi32.WTSUnRegisterSessionNotification(Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case User32.WM_CLIPBOARDUPDATE:
                ClipboardUpdated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
            case User32.WM_HOTKEY:
                HotkeyPressed?.Invoke(this, wParam.ToInt32());
                handled = true;
                break;
            case TrayCallbackMessage:
                // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor (x, y) in screen pixels.
                var code = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                var anchor = wParam.ToInt64();
                TrayActivity?.Invoke(this, new TrayEvent(code, unchecked((short)(anchor & 0xFFFF)), unchecked((short)((anchor >> 16) & 0xFFFF))));
                handled = true;
                break;
            case User32.WM_SETTINGCHANGE:
                SettingChanged?.Invoke(this, lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam));
                break;
            case User32.WM_DISPLAYCHANGE:
                EnvironmentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case User32.WM_POWERBROADCAST when wParam.ToInt32() == User32.PBT_APMRESUMEAUTOMATIC:
                EnvironmentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case User32.WM_WTSSESSION_CHANGE when wParam.ToInt32() is User32.WTS_SESSION_UNLOCK or User32.WTS_REMOTE_CONNECT or User32.WTS_CONSOLE_CONNECT:
                EnvironmentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case User32.WM_ENDSESSION when wParam != IntPtr.Zero:
                SessionEnding?.Invoke(this, EventArgs.Empty);
                break;
            default:
                if (msg == (int)TaskbarCreatedMessage && msg != 0)
                {
                    TaskbarRecreated?.Invoke(this, EventArgs.Empty);
                }
                else if (msg == (int)ActivateMessage && msg != 0)
                {
                    ActivateRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                }

                break;
        }

        return IntPtr.Zero;
    }
}
