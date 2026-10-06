using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ClipboardManager.Common;
using ClipboardManager.Localization;
using ClipboardManager.Shell;

namespace ClipboardManager.Dialogs;

/// <summary>Shown once, so a double-click on a silent tray app visibly does something.</summary>
internal sealed partial class WelcomeWindow : Window
{
    public WelcomeWindow(string hotkeyText, string? hotkeyWarning, bool autostartOn, string? locationHint, bool startMenuOn)
    {
        InitializeComponent();
        Logo.Source = new DrawingImage(BrandIcon.CreateColor());
        TryText.Text = string.IsNullOrEmpty(hotkeyText) ? Strings.HotkeyMissing : Strings.WelcomeTry(hotkeyText);
        if (hotkeyWarning is not null)
        {
            HotkeyWarning.Text = hotkeyWarning;
            HotkeyWarning.Visibility = Visibility.Visible;
        }

        Autostart.IsChecked = autostartOn;
        if (locationHint is not null)
        {
            LocationHint.Text = locationHint;
            LocationHint.Visibility = Visibility.Visible;
        }

        StartMenu.IsChecked = startMenuOn;
        SourceInitialized += (_, _) => ThemeHelper.RoundCorners(this);
    }

    public bool AutostartChosen => Autostart.IsChecked == true;

    public bool StartMenuChosen => StartMenu.IsChecked == true;

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnTaskbarSettings(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true })?.Dispose();
}
