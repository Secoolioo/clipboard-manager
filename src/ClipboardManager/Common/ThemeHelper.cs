using System.Windows;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using Microsoft.Win32;

namespace ClipboardManager.Common;

/// <summary>The one place that touches WPF's still-experimental Fluent ThemeMode API.</summary>
internal static class ThemeHelper
{
    public static void Apply(Application app, ThemePreference preference)
    {
#pragma warning disable WPF0001 // ThemeMode is experimental; isolated here so an API change touches one file.
        app.ThemeMode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }

    /// <summary>Windows "Make text bigger" (Settings > Accessibility > Text size), 1.0–2.25. WPF does not apply it by itself.</summary>
    public static double TextScaleFactor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int percent ? Math.Clamp(percent, 100, 225) / 100.0 : 1.0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return 1.0;
        }
    }

    /// <summary>Rounded corners on Windows 11 (ignored elsewhere).</summary>
    public static void RoundCorners(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Dwm.Set(hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_ROUND);
    }
}
