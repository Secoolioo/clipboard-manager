using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Common;
using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Updates;
using ClipboardManager.Localization;
using ClipboardManager.Shell;

namespace ClipboardManager.Dialogs;

/// <summary>One scrollable page; every change applies immediately (destructive ones ask first).</summary>
internal sealed partial class SettingsWindow : Window
{
    public const string ProjectUrl = "https://github.com/Secoolioo/clipboard-manager";
    public const string SupportUrl = "https://github.com/Secoolioo/.github/blob/main/DONATE.md";

    /// <summary>Settings &gt; Apps &gt; Startup, where Windows switches the same Run entry on and off.</summary>
    public const string StartupAppsUri = "ms-settings:startupapps";

    private readonly ISettingsHost _host;
    private bool _loading = true;
    private bool _recordingHotkey;

    /// <summary>While a confirmation dialog is open, re-activation must not reset the controls.</summary>
    private bool _asking;

    /// <summary>The running update check or download; cancelled when the window closes.</summary>
    private CancellationTokenSource? _update;
    private UpdateOffer? _offer;
    private UpdateStep _updateStep = UpdateStep.UpToDate;

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
            _update?.Cancel();
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

    // A change made there shows up here when this window is activated again (Refresh on Activated).
    private void OnStartupApps(object sender, RoutedEventArgs e) => _host.OpenUrl(StartupAppsUri);

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
        try
        {
            Process.Start(new ProcessStartInfo(explorer, $"\"{_host.DataFolder}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer blocked or missing (kiosk, policy): nothing to open.
        }
    }

    private void OnLicenses(object sender, RoutedEventArgs e) => _host.ShowLicenses(this);

    private void OnProjectPage(object sender, RoutedEventArgs e) => _host.OpenUrl(ProjectUrl);

    private void OnSupport(object sender, RoutedEventArgs e) => _host.OpenUrl(SupportUrl);

    private void OnRemoveEverything(object sender, RoutedEventArgs e) => _host.RemoveEverything(this);

    // ---- Updates ---------------------------------------------------------------------------

    /// <summary>Tray "Check for updates…": scrolls to the section and starts the check.</summary>
    public void CheckForUpdates() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            UpdatePanel.BringIntoView();
            CheckUpdatesButton.Focus();
            _ = RunUpdateCheckAsync();
        });

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await RunUpdateCheckAsync().ConfigureAwait(true);

    private async Task RunUpdateCheckAsync()
    {
        if (_update is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _update = cancellation;
        _offer = null;
        ShowUpdate(Strings.UpdateChecking, UpdateStep.Busy);
        try
        {
            _offer = await _host.CheckForUpdatesAsync(cancellation.Token).ConfigureAwait(true);
            if (_offer is null)
            {
                ShowUpdate(Strings.UpdateUpToDate(_host.VersionText), UpdateStep.UpToDate);
            }
            else
            {
                ShowUpdate(Strings.UpdateAvailable(_offer.Version.ToString()), UpdateStep.Offer);
            }
        }
        catch (UpdateException ex)
        {
            ShowUpdate(Strings.UpdateFailed(ex.Error), UpdateStep.Failed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The window was closed.
        }
        finally
        {
            _update = null;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_offer is not { } offer || _update is not null)
        {
            return;
        }

        // The update restarts the app: in memory-only mode that ends the unpinned history.
        if (_host.Settings.MemoryOnly)
        {
            var count = await _host.CountMemoryOnlyEntriesAsync().ConfigureAwait(true);
            if (count > 0 && ConfirmDialog.Ask(Strings.UpdateMemoryOnlyTitle, Strings.UpdateMemoryOnlyMessage(count), Strings.InstallUpdate, owner: this) is null)
            {
                return;
            }
        }

        using var cancellation = new CancellationTokenSource();
        _update = cancellation;
        ShowUpdate(Strings.UpdateDownloading(0), UpdateStep.Downloading);
        var progress = new Progress<int>(percent =>
        {
            // Reports are posted; a late one must not overwrite the next state.
            if (_updateStep == UpdateStep.Downloading && _update == cancellation)
            {
                ShowUpdate(Strings.UpdateDownloading(percent), UpdateStep.Downloading, percent);
            }
        });
        try
        {
            await _host.DownloadUpdateAsync(offer, progress, cancellation.Token).ConfigureAwait(true);

            // The window may have been closed to abort while the download finished.
            cancellation.Token.ThrowIfCancellationRequested();
            ShowUpdate(Strings.UpdateInstalling, UpdateStep.Busy);
            await _host.InstallUpdateAsync().ConfigureAwait(true);
        }
        catch (UpdateException ex)
        {
            ShowUpdate(Strings.UpdateFailed(ex.Error), UpdateStep.Failed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            ShowUpdate(Strings.UpdateAvailable(offer.Version.ToString()), UpdateStep.Offer);
        }
        finally
        {
            _update = null;
        }
    }

    private void OnCancelUpdate(object sender, RoutedEventArgs e) => _update?.Cancel();

    private void OnWhatsNew(object sender, RoutedEventArgs e) => _host.OpenUrl(_offer?.ReleasePage.AbsoluteUri ?? UpdateRules.ReleasesPage);

    private void OnReleasesPage(object sender, RoutedEventArgs e) => _host.OpenUrl(UpdateRules.ReleasesPage);

    private void ShowUpdate(string status, UpdateStep step, int percent = 0)
    {
        var announce = step != _updateStep || UpdateStatus.Visibility != Visibility.Visible;
        var moveFocus = UpdatePanel.IsKeyboardFocusWithin || Keyboard.FocusedElement is null or Window;
        _updateStep = step;
        UpdateStatus.Text = status;
        UpdateStatus.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = step is not (UpdateStep.Busy or UpdateStep.Downloading);
        InstallUpdateButton.Visibility = VisibleIf(step == UpdateStep.Offer);
        CancelUpdateButton.Visibility = VisibleIf(step == UpdateStep.Downloading);
        WhatsNewButton.Visibility = VisibleIf(step is UpdateStep.Offer or UpdateStep.Downloading);
        ReleasesPageButton.Visibility = VisibleIf(step == UpdateStep.Failed);
        UpdateActions.Visibility = VisibleIf(step is UpdateStep.Offer or UpdateStep.Downloading or UpdateStep.Failed);
        UpdateProgress.Visibility = VisibleIf(step == UpdateStep.Downloading);
        UpdateProgress.Value = percent;
        if (!announce)
        {
            return;
        }

        // Screen readers hear each new state once (not every percent); keyboard users land on the next action.
        var peer = UIElementAutomationPeer.FromElement(UpdateStatus) ?? UIElementAutomationPeer.CreatePeerForElement(UpdateStatus);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        Control target = step switch
        {
            UpdateStep.Offer => InstallUpdateButton,
            UpdateStep.Downloading => CancelUpdateButton,
            UpdateStep.Failed => ReleasesPageButton,
            _ => CheckUpdatesButton,
        };
        if (moveFocus && target.IsEnabled)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => target.Focus());
        }
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private enum UpdateStep
    {
        /// <summary>Checking or installing: nothing to click.</summary>
        Busy,
        UpToDate,
        Offer,
        Downloading,
        Failed,
    }

    private sealed record ExcludedApp(string Name, string Status);
}
