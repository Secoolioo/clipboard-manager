using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;

namespace ClipboardManager.Shell;

internal enum HotkeyStatus
{
    Registered,
    RegisteredFallback,
    Taken,
    Unregistered,
}

/// <summary>Owns the single global hotkey of the app.</summary>
internal sealed class HotkeyRegistration : IDisposable
{
    public const int HotkeyId = 0x4D43; // "MC"
    private const int ProbeId = 0x4D44;

    private readonly IntPtr _window;

    public HotkeyRegistration(IntPtr window)
    {
        _window = window;
    }

    public HotkeyGesture? Active { get; private set; }

    public HotkeyStatus Status { get; private set; } = HotkeyStatus.Unregistered;

    /// <summary>Registers the configured gesture; if the default is taken, tries the documented-free fallback.</summary>
    public HotkeyStatus Apply(HotkeyGesture configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        Unregister();
        if (TryRegister(HotkeyId, configured))
        {
            Active = configured;
            return Status = HotkeyStatus.Registered;
        }

        if (configured == HotkeyGesture.Default && TryRegister(HotkeyId, HotkeyGesture.Fallback))
        {
            Active = HotkeyGesture.Fallback;
            return Status = HotkeyStatus.RegisteredFallback;
        }

        return Status = HotkeyStatus.Taken;
    }

    /// <summary>Checks whether a gesture could be registered without disturbing the active one.</summary>
    public bool IsAvailable(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (gesture == Active)
        {
            return true;
        }

        if (!TryRegister(ProbeId, gesture))
        {
            return false;
        }

        User32.UnregisterHotKey(_window, ProbeId);
        return true;
    }

    /// <summary>Temporarily releases the hotkey (the recorder must be able to see the keystroke).</summary>
    public void Suspend()
    {
        if (Active is not null)
        {
            User32.UnregisterHotKey(_window, HotkeyId);
        }
    }

    public void Resume()
    {
        if (Active is not null && !TryRegister(HotkeyId, Active))
        {
            Status = HotkeyStatus.Taken;
            Active = null;
        }
    }

    public void Unregister()
    {
        if (Active is not null)
        {
            User32.UnregisterHotKey(_window, HotkeyId);
        }

        Active = null;
        Status = HotkeyStatus.Unregistered;
    }

    public void Dispose() => Unregister();

    private bool TryRegister(int id, HotkeyGesture gesture)
    {
        if (gesture.Validate() != HotkeyValidation.Valid)
        {
            return false;
        }

        return User32.RegisterHotKey(_window, id, (uint)gesture.Modifiers | User32.MOD_NOREPEAT, (uint)gesture.VirtualKey);
    }
}
