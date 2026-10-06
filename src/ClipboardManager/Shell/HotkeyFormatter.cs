using System.Text;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using ClipboardManager.Localization;

namespace ClipboardManager.Shell;

internal static class HotkeyFormatter
{
    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>Localized display, e.g. "Strg+Umschalt+V" / "Ctrl+Shift+V", using the active keyboard layout's key name.</summary>
    public static string Format(HotkeyGesture? gesture)
    {
        if (gesture is null)
        {
            return string.Empty;
        }

        var german = Strings.IsGerman;
        var builder = new StringBuilder();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            builder.Append("Win+");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            builder.Append(german ? "Strg+" : "Ctrl+");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append(german ? "Umschalt+" : "Shift+");
        }

        builder.Append(KeyName(gesture.VirtualKey));
        return builder.ToString();
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        var scanCode = User32.MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
        var lParam = (int)(scanCode << 16);
        if (virtualKey is >= 0x21 and <= 0x2E)
        {
            lParam |= 1 << 24; // extended key: navigation block rather than numpad names
        }

        unsafe
        {
            var buffer = stackalloc char[64];
            var length = User32.GetKeyNameText(lParam, buffer, 64);
            return length > 0 ? new string(buffer, 0, length) : $"0x{virtualKey:X2}";
        }
    }
}
