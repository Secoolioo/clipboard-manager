namespace ClipboardManager.Core.Settings;

/// <summary>Same bit values as the Win32 MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

public enum HotkeyValidation
{
    Valid,
    MissingModifier,
    NotAKey,
    Reserved,
}

/// <summary>A global hotkey: modifiers plus a Win32 virtual-key code.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkF4 = 0x73;
    private const int VkF12 = 0x7B;
    private const int VkV = 0x56;

    /// <summary>Ctrl+Shift+V (the user's choice).</summary>
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VkV);

    /// <summary>Used automatically when the default is taken: the only Win+?+V combination Windows does not document as its own.</summary>
    public static HotkeyGesture Fallback { get; } = new(HotkeyModifiers.Win | HotkeyModifiers.Alt, VkV);

    /// <summary>Ctrl+Alt combinations double as AltGr on many layouts (e.g. Czech AltGr+V = @).</summary>
    public bool UsesAltGrChord => Modifiers.HasFlag(HotkeyModifiers.Control) && Modifiers.HasFlag(HotkeyModifiers.Alt);

    public HotkeyValidation Validate()
    {
        if (IsModifierKey(VirtualKey) || VirtualKey is <= 0 or > 0xFE)
        {
            return HotkeyValidation.NotAKey;
        }

        if ((Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            return HotkeyValidation.MissingModifier;
        }

        return IsReserved() ? HotkeyValidation.Reserved : HotkeyValidation.Valid;
    }

    internal static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) or 0x14 or 0x90 or 0x91;

    private bool IsReserved()
    {
        // F12 is reserved for debuggers by RegisterHotKey.
        if (VirtualKey == VkF12)
        {
            return true;
        }

        var mods = Modifiers;
        var letter = VirtualKey is >= 'A' and <= 'Z' ? (char)VirtualKey : '\0';

        // Plain Ctrl+<editing key> would hijack copy/paste & co. in every app.
        if (mods == HotkeyModifiers.Control && "ACVXZYSFPWTNO".Contains(letter, StringComparison.Ordinal) && letter != '\0')
        {
            return true;
        }

        if (mods == HotkeyModifiers.Alt && VirtualKey is VkF4 or VkTab or VkEscape)
        {
            return true;
        }

        if (mods == HotkeyModifiers.Control && VirtualKey == VkEscape)
        {
            return true;
        }

        // Win+<letter> belongs to the shell.
        return mods == HotkeyModifiers.Win && letter != '\0';
    }
}
