using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipboardManager.Common;
using ClipboardManager.Core.Settings;
using ClipboardManager.Localization;
using ClipboardManager.Shell;

namespace ClipboardManager.Dialogs;

/// <summary>One scrollable page; every change applies immediately (destructive ones ask first).</summary>
internal sealed partial class SettingsWindow : Window
{
    public const string ProjectUrl = "https://github.com/Secoolioo/clipboard-manager";
    public const string SupportUrl = "https://github.com/Secoolioo/.github/blob/main/DONATE.md";

    private readonly ISettingsHost _host;
    private bool _loading = true;
    private bool _recordingHotkey;

    /// <summary>While a confirmation dialog is open, re-activation must not reset the controls.</summary>
    private bool _asking;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        Logo.Source = new DrawingImage(BrandIcon.CreateColor());
        VersionText.Text = Strings.Version(host.VersionText);
        SourceInitialized += (_, _) => ThemeHelper.RoundCorners(this);
        Activated += (_, _) =>
        {
            if (!_asking)
            {
                Refresh();
            }
        };
        Closed += (_, _) =>
        {
            if (_recordingHotkey)
            {
                _host.ResumeHotkey();
            }
        };

        LanguageBox.ItemsSource = new[] { Strings.LanguageSystem, "Deutsch", "English" };
        ThemeBox.ItemsSource = new[] { Strings.ThemeSystem, Strings.ThemeLight, Strings.ThemeDark };
        MaxEntriesBox.ItemsSource = AppSettings.HistorySizePresets.Select(p => p.ToString(Strings.Culture)).ToArray();
        Refresh();
        _loading = false;
    }

    private void Refresh()
    {
        var wasLoading = _loading;
        _loading = true;
        var settings = _host.Settings;

        var state = _host.AutostartState;
        AutostartBox.IsChecked = state == AutostartState.On;
        string? note = state switch
        {
            AutostartState.DisabledInWindows => Strings.AutostartDisabledInWindows,
            AutostartState.OtherLocation => Strings.AutostartOtherLocation,
            _ when _host.AutostartIsVolatile => Strings.AutostartVolatile,
            _ when _host.ExeInDownloads => Strings.AutostartDownloads,
            _ => null,
        };
        AutostartNote.Text = note ?? string.Empty;
        AutostartNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        AutostartBox.IsEnabled = !_host.AutostartIsVolatile || state == AutostartState.On;

        StartMenuBox.IsChecked = _host.StartMenuShortcutExists;
        RecordBox.IsChecked = _host.IsRecording;
        LanguageBox.SelectedIndex = (int)settings.Language;
        ThemeBox.SelectedIndex = (int)settings.Theme;
        MaxEntriesBox.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.HistorySizePresets, NearestPreset(settings.MaxHistoryItems)));
        MemoryOnlyBox.IsChecked = settings.MemoryOnly;
        SecretsBox.IsChecked = settings.SkipDetectedSecrets;
        CaptureBox.IsChecked = settings.HideFromScreenCapture;
        if (!_recordingHotkey)
        {
            ShowHotkey();
        }

        ExcludedList.ItemsSource = settings.ExcludedApps
            .Select(name => new ExcludedApp(name, Strings.LastIgnored(_host.LastIgnored(name) is { } when ? Strings.RelativeTime(when, DateTimeOffset.UtcNow) : null)))
            .ToList();
        AddAppBox.ItemsSource = _host.RecentSources.Where(s => !settings.ExcludedApps.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        _loading = wasLoading;
    }

    private static int NearestPreset(int value) =>
        AppSettings.HistorySizePresets.OrderBy(p => Math.Abs(p - value)).First();

    // ---- General ---------------------------------------------------------------------------

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        _host.SetAutostart(AutostartBox.IsChecked == true);
        Refresh();
    }

    private void OnStartMenuClick(object sender, RoutedEventArgs e)
    {
        _host.SetStartMenuShortcut(StartMenuBox.IsChecked == true);
        Refresh();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e) => _host.SetRecording(RecordBox.IsChecked == true);

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
        {
            return;
        }

        var language = (LanguagePreference)LanguageBox.SelectedIndex;
        _host.UpdateSettings(s => s with { Language = language });
        _host.LanguageChanged();
        LanguageNote.Visibility = Visibility.Visible;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeBox.SelectedIndex >= 0)
        {
            var theme = (ThemePreference)ThemeBox.SelectedIndex;
            _host.UpdateSettings(s => s with { Theme = theme });
        }
    }

    // ---- Hotkey ----------------------------------------------------------------------------

    private void ShowHotkey()
    {
        var configured = _host.Settings.Hotkey;
        var active = _host.ActiveHotkey;
        HotkeyBox.Text = HotkeyFormatter.Format(configured);
        HotkeyNote.Text = _host.HotkeyStatus switch
        {
            HotkeyStatus.RegisteredFallback => Strings.HotkeyFallbackActive(HotkeyFormatter.Format(active)),
            HotkeyStatus.Taken or HotkeyStatus.Unregistered => Strings.HotkeyNotActive,
            _ when configured == HotkeyGesture.Default => Strings.HotkeyConflictsCtrlShiftV,
            _ when configured.UsesAltGrChord => Strings.HotkeyAltGr,
            _ => string.Empty,
        };
    }

    private void OnHotkeyChange(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        _host.SuspendHotkey();
        HotkeyBox.Text = Strings.HotkeyPrompt;
        HotkeyNote.Text = string.Empty;
        HotkeyBox.Focus();
    }

    private void OnHotkeyReset(object sender, RoutedEventArgs e)
    {
        EndRecording();
        Apply(HotkeyGesture.Default);
    }

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingHotkey)
        {
            EndRecording();
            ShowHotkey();
        }
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndRecording();
            ShowHotkey();
            return;
        }

        var modifiers = CurrentModifiers();
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            HotkeyBox.Text = HotkeyFormatter.Format(new HotkeyGesture(modifiers, 0)).TrimEnd('0', 'x', '+') + "+…";
            HotkeyNote.Text = Strings.HotkeyOnlyModifiers;
            return;
        }

        var gesture = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
        switch (gesture.Validate())
        {
            case HotkeyValidation.MissingModifier:
                HotkeyNote.Text = Strings.HotkeyNeedsModifier;
                return;
            case HotkeyValidation.Reserved or HotkeyValidation.NotAKey:
                HotkeyNote.Text = Strings.HotkeyReserved;
                return;
        }

        if (!_host.IsHotkeyAvailable(gesture))
        {
            HotkeyBox.Text = HotkeyFormatter.Format(gesture);
            HotkeyNote.Text = Strings.HotkeyInUse;
            return;
        }

        EndRecording(resume: false);
        Apply(gesture);
    }

    private void Apply(HotkeyGesture gesture)
    {
        _host.UpdateSettings(s => s with { Hotkey = gesture });
        _host.ApplyHotkey(gesture);
        ShowHotkey();
    }

    private void EndRecording(bool resume = true)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        _recordingHotkey = false;
        if (resume)
        {
            _host.ResumeHotkey();
        }
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        var wpf = Keyboard.Modifiers;
        if (wpf.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (wpf.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (wpf.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    // ---- History ---------------------------------------------------------------------------

    private async void OnMaxEntriesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _asking || MaxEntriesBox.SelectedIndex < 0)
        {
            return;
        }

        var value = AppSettings.HistorySizePresets[MaxEntriesBox.SelectedIndex];
        _asking = true;
        try
        {
            if (value < _host.Settings.MaxHistoryItems)
            {
                var doomed = await _host.CountExceedingAsync(value).ConfigureAwait(true);
                if (doomed > 0 && ConfirmDialog.Ask(Strings.LimitTitle, Strings.LimitMessage(doomed), Strings.LimitConfirm, owner: this) is null)
                {
                    return;
                }
            }

            _host.UpdateSettings(s => s with { MaxHistoryItems = value });
        }
        finally
        {
            _asking = false;
            Refresh();
        }
    }

    private async void OnMemoryOnlyClick(object sender, RoutedEventArgs e)
    {
        var on = MemoryOnlyBox.IsChecked == true;
        if (on)
        {
            _host.SetMemoryOnly(true, discardMemoryEntries: false);
            return;
        }

        // Switching back to saving: the entries collected in memory only would reach the disk now,
        // so the user decides whether to keep or discard them.
        _asking = true;
        try
        {
            var count = await _host.CountMemoryOnlyEntriesAsync().ConfigureAwait(true);
            var discard = false;
            if (count > 0)
            {
                var answer = ConfirmDialog.Ask(Strings.MemoryOnlyOffTitle, Strings.MemoryOnlyOffMessage(count), Strings.MemoryOnlyOffConfirm, Strings.MemoryOnlyOffDiscard, this);
                if (answer is null)
                {
                    return;
                }

                discard = answer.Value;
            }

            _host.SetMemoryOnly(false, discard);
        }
        finally
        {
            _asking = false;
            Refresh();
        }
    }

    private async void OnClearHistory(object sender, RoutedEventArgs e) => await _host.ClearHistoryAsync(this).ConfigureAwait(true);

    // ---- Privacy ---------------------------------------------------------------------------

    private void OnSecretsClick(object sender, RoutedEventArgs e)
    {
        var on = SecretsBox.IsChecked == true;
        _host.UpdateSettings(s => s with { SkipDetectedSecrets = on });
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        var on = CaptureBox.IsChecked == true;
        _host.UpdateSettings(s => s with { HideFromScreenCapture = on });
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        var name = Path.GetFileName((AddAppBox.Text ?? string.Empty).Trim().Trim('"'));
        if (name.Length == 0)
        {
            return;
        }

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        _host.UpdateSettings(s => s with { ExcludedApps = [.. s.ExcludedApps, name] });
        AddAppBox.Text = string.Empty;
        Refresh();
    }

    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is ExcludedApp app)
        {
            _host.UpdateSettings(s => s with { ExcludedApps = s.ExcludedApps.Where(a => !string.Equals(a, app.Name, StringComparison.OrdinalIgnoreCase)).ToArray() });
            Refresh();
        }
    }

    // ---- About -----------------------------------------------------------------------------

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        // Full path: a bare "explorer.exe" would be searched in the current directory (e.g. Downloads) first.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"\"{_host.DataFolder}\"") { UseShellExecute = false })?.Dispose();
    }

    private void OnLicenses(object sender, RoutedEventArgs e) => _host.ShowLicenses(this);

    private void OnProjectPage(object sender, RoutedEventArgs e) => _host.OpenUrl(ProjectUrl);

    private void OnSupport(object sender, RoutedEventArgs e) => _host.OpenUrl(SupportUrl);

    private void OnRemoveEverything(object sender, RoutedEventArgs e) => _host.RemoveEverything(this);

    private sealed record ExcludedApp(string Name, string Status);
}
