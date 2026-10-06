using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Clipboard;
using ClipboardManager.Common;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Monitoring;
using ClipboardManager.Core.Settings;
using ClipboardManager.Dialogs;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;
using ClipboardManager.Shell;

namespace ClipboardManager.Hosting;

/// <summary>Composition root: creates the services, wires them together and runs startup and shutdown.</summary>
internal sealed class AppController : ISettingsHost, IDisposable
{
    private const string Category = "App";

    private readonly App _app;
    private readonly StartupOptions _options;
    private readonly FileLog _log;
    private readonly Dispatcher _dispatcher;
    private SettingsHolder _settings = null!;
    private MonitoringState _monitoring = null!;
    private HistoryIndex _index = null!;
    private CurrentClipTracker _tracker = null!;
    private DbWorker _worker = null!;
    private HostWindow _host = null!;
    private ClipboardGate _gate = null!;
    private ClipboardWriter _writer = null!;
    private CaptureCoordinator _capture = null!;
    private HotkeyRegistration _hotkey = null!;
    private TrayIcon _tray = null!;
    private Autostart _autostart = null!;
    private StartMenuShortcut _startMenu = null!;
    private PopupWindow _popupWindow = null!;
    private PopupController _popup = null!;
    private SettingsWindow? _settingsWindow;
    private bool _stopped;

    public AppController(App app, StartupOptions options)
    {
        _app = app;
        _options = options;
        _log = options.Log;
        _dispatcher = app.Dispatcher;
    }

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClipboardManager.exe");

    public void Start()
    {
        var started = Stopwatch.GetTimestamp();
        var (settings, settingsState) = new SettingsStore(_options.Paths.SettingsFile, _log).Load();
        _settings = new SettingsHolder(new SettingsStore(_options.Paths.SettingsFile, _log), settings);
        Strings.Apply(settings.Language);
        ThemeHelper.Apply(_app, settings.Theme);

        // Measured: the hardware D3D path costs ~90 MB private memory for this small, mostly static UI;
        // software rendering keeps the resident app near 35 MB and opens just as fast.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _monitoring = new MonitoringState(TimeProvider.System, settings.MonitoringPaused);
        _index = new HistoryIndex();
        _tracker = new CurrentClipTracker();
        _worker = new DbWorker(_log);
        _worker.Changed += batch => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _index.Apply(batch));

        // Hotkey, tray and listener first, so the app is reachable as early as possible after login.
        _host = new HostWindow();
        _gate = new ClipboardGate();
        _writer = new ClipboardWriter(_host.Handle, _gate, _log);
        var reader = new ClipboardReader(_host.Handle, _gate, _log) { Settings = CaptureSettingsOf(settings) };
        _capture = new CaptureCoordinator(_host, reader, _worker, _monitoring, _tracker, _log);
        _hotkey = new HotkeyRegistration(_host.Handle);
        var hotkeyStatus = _hotkey.Apply(settings.Hotkey);
        _tray = new TrayIcon(_host, _log);
        _tray.Show(TrayTooltip(), paused: !_monitoring.IsRecording);
        _autostart = new Autostart(ExePath, _log);
        _startMenu = new StartMenuShortcut(ExePath, _log);

        _host.HotkeyPressed += (_, id) =>
        {
            if (id == HotkeyRegistration.HotkeyId)
            {
                _popup.Toggle(PopupSource.Hotkey);
            }
        };
        _host.ActivateRequested += (_, _) => _popup.Show(PopupSource.SecondInstance);
        _host.TrayActivity += OnTray;
        _host.SettingChanged += OnSettingChanged;
        _host.EnvironmentChanged += (_, _) => SchedulePrewarm();
        _host.SessionEnding += (_, _) => ShutdownForSession();
        _monitoring.Changed += (_, _) => UpdateStatus();
        _settings.Changed += OnSettingsChanged;

        _ = OpenStoreAsync(settings);
        _capture.Start();

        CreatePopup();
        SchedulePrewarm();
        UpdateStatus();

        _log.Info(Category, $"Started in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms (process age {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");

        if (settingsState == SettingsLoadState.Corrupt)
        {
            _log.Warning(Category, "Settings were reset to defaults (the old file was kept as settings.json.corrupt)");
        }

        RunFirstStartTasks(hotkeyStatus);
        _ = Task.Run(CleanStaleExtractionFolders);
    }

    // ---- startup helpers -------------------------------------------------------------------

    private async Task OpenStoreAsync(AppSettings settings)
    {
        var status = await _worker.OpenAsync(_options.Paths, settings.MemoryOnly, settings.Limits).ConfigureAwait(true);
        if (status.Notice != StoreNotice.None)
        {
            _tray.Notify(Strings.AppName, Strings.StoreNotice(status.Notice), warning: true);
        }
    }

    private void CreatePopup()
    {
        var viewModel = new PopupViewModel { IsCompact = _settings.Current.PopupCompact };
        _popupWindow = new PopupWindow(viewModel);
        _popupWindow.SourceInitialized += (_, _) => ApplyDisplayAffinity();
        _popupWindow.ApplyScale(ThemeHelper.TextScaleFactor());
        _popup = new PopupController(_popupWindow, _index, _tracker, _monitoring, _worker, _writer, _settings, _log);
        _popup.SettingsRequested += (_, _) => OpenSettings();
        _popup.StatusRequested += (_, _) => UpdateStatus();
    }

    private void SchedulePrewarm() =>
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (!_stopped && !_popup.IsOpen)
            {
                _popup.Prewarm();
            }
        });

    private void RunFirstStartTasks(HotkeyStatus hotkeyStatus)
    {
        var firstRun = !_autostart.FirstRunDone;
        _autostart.RegisterOnFirstRun();

        if (firstRun && !_options.Autostart)
        {
            if (!StartMenuShortcut.Exists && !_autostart.IsVolatileLocation)
            {
                _startMenu.Create();
            }

            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ShowWelcome);
            return;
        }

        if (!_options.Autostart)
        {
            _autostart.RepairIfBroken();

            // A manual start of an already-configured app means "show me the history".
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _popup.Show(PopupSource.SecondInstance));
        }

        NotifyHotkey(hotkeyStatus);
    }

    private void ShowWelcome()
    {
        string? hotkeyWarning = _hotkey.Status switch
        {
            HotkeyStatus.RegisteredFallback => Strings.HotkeyFallbackNotice(HotkeyFormatter.Format(_hotkey.Active)),
            HotkeyStatus.Taken => Strings.HotkeyTakenNotice,
            _ => null,
        };
        string? locationHint = _autostart.IsVolatileLocation ? Strings.AutostartVolatile : _autostart.IsInDownloads ? Strings.AutostartDownloads : null;

        var welcome = new WelcomeWindow(HotkeyFormatter.Format(_hotkey.Active), hotkeyWarning, _autostart.State == AutostartState.On, locationHint, StartMenuShortcut.Exists);
        welcome.Closed += (_, _) =>
        {
            if (welcome.AutostartChosen != (_autostart.State == AutostartState.On) && !_autostart.IsVolatileLocation)
            {
                _autostart.SetEnabled(welcome.AutostartChosen);
            }

            if (welcome.StartMenuChosen != StartMenuShortcut.Exists)
            {
                SetStartMenuShortcut(welcome.StartMenuChosen);
            }
        };
        welcome.Show();
        welcome.Activate();
    }

    private void NotifyHotkey(HotkeyStatus status)
    {
        if (status == HotkeyStatus.RegisteredFallback)
        {
            _tray.Notify(Strings.AppName, Strings.HotkeyFallbackNotice(HotkeyFormatter.Format(_hotkey.Active)));
        }
        else if (status == HotkeyStatus.Taken)
        {
            _tray.Notify(Strings.AppName, Strings.HotkeyTakenNotice, warning: true);
        }
    }

    /// <summary>Single-file builds extract native WPF DLLs per version; remove folders of older versions.</summary>
    private void CleanStaleExtractionFolders()
    {
        try
        {
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(m.ModuleName, "wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase));
            var current = module?.FileName is { } file ? Path.GetDirectoryName(file) : null;
            var parent = current is null ? null : Directory.GetParent(current);
            if (current is null || parent is null || !string.Equals(parent.Name, "ClipboardManager", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parent.Parent?.Name, ".net", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var sibling in parent.EnumerateDirectories())
            {
                if (!string.Equals(sibling.FullName, current, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        sibling.Delete(recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // In use by another running version; try again next start.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _log.Debug(Category, "Extraction cleanup skipped");
        }
    }

    // ---- status ----------------------------------------------------------------------------

    private string StatusText() => _monitoring.Mode switch
    {
        MonitoringMode.PausedUntil => Strings.StatusPausedUntil(_monitoring.PausedUntil!.Value),
        MonitoringMode.PausedIndefinitely => Strings.StatusPaused,
        _ when _monitoring.IgnoreNext => Strings.StatusIgnoreNext,
        _ => Strings.StatusActive,
    };

    private string TrayTooltip()
    {
        var status = StatusText();
        if (_hotkey?.Status == HotkeyStatus.Taken)
        {
            status += " Â· " + Strings.HotkeyMissing;
        }

        return Strings.TrayTooltip(status);
    }

    private void UpdateStatus()
    {
        if (_stopped)
        {
            return;
        }

        var paused = !_monitoring.IsRecording;
        _tray.Update(TrayTooltip(), paused);
        _popupWindow?.ViewModel.SetStatus(StatusText(), paused, _monitoring.PausedUntil);
        if (_popupWindow is not null)
        {
            _popupWindow.ViewModel.HotkeyMissing = _hotkey.Status == HotkeyStatus.Taken;
            _popupWindow.SetStatusVisual(paused, _hotkey.Status == HotkeyStatus.Taken);
        }

        if (_settings.Current.MonitoringPaused != _monitoring.IsPausedIndefinitely)
        {
            var indefinite = _monitoring.IsPausedIndefinitely;
            _settings.Update(s => s with { MonitoringPaused = indefinite });
        }
    }

    // ---- tray ------------------------------------------------------------------------------

    private void OnTray(object? sender, TrayEvent e)
    {
        switch (e.Code)
        {
            case Shell32.NIN_SELECT or Shell32.NIN_KEYSELECT:
                if (!_popup.IsOpen && !_popup.RecentlyHidden)
                {
                    _popup.Show(PopupSource.Tray);
                }

                break;
            case User32.WM_CONTEXTMENU:
                _popup.Close(restoreFocus: false);
                Execute(TrayMenu.Show(_host.Handle, e.AnchorX, e.AnchorY, _monitoring, HotkeyFormatter.Format(_hotkey.Active)));
                break;
        }
    }

    private void Execute(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                _popup.Show(PopupSource.Tray);
                break;
            case TrayCommand.Pause5:
                _monitoring.PauseFor(TimeSpan.FromMinutes(5));
                break;
            case TrayCommand.Pause30:
                _monitoring.PauseFor(TimeSpan.FromMinutes(30));
                break;
            case TrayCommand.Pause60:
                _monitoring.PauseFor(TimeSpan.FromHours(1));
                break;
            case TrayCommand.PauseIndefinitely:
                _monitoring.PauseIndefinitely();
                break;
            case TrayCommand.Resume:
                _monitoring.Resume();
                break;
            case TrayCommand.IgnoreNext:
                _monitoring.IgnoreNextCopy();
                break;
            case TrayCommand.Clear:
                _ = ClearHistoryAsync(null);
                break;
            case TrayCommand.Settings:
                OpenSettings();
                break;
            case TrayCommand.Exit:
                Exit();
                break;
        }
    }

    private void OnSettingChanged(object? sender, string? section)
    {
        if (section is "ImmersiveColorSet" or null)
        {
            _tray.RefreshIcon();
        }

        _popupWindow.ApplyScale(ThemeHelper.TextScaleFactor());
    }

    // ---- settings --------------------------------------------------------------------------

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    private void OnSettingsChanged(AppSettings previous, AppSettings next)
    {
        if (previous.Theme != next.Theme)
        {
            ThemeHelper.Apply(_app, next.Theme);
        }

        if (previous.MaxHistoryItems != next.MaxHistoryItems)
        {
            _ = _worker.SetLimitsAsync(next.Limits);
        }

        if (previous.MemoryOnly != next.MemoryOnly)
        {
            _ = _worker.SetMemoryOnlyAsync(next.MemoryOnly);
        }

        if (previous.SkipDetectedSecrets != next.SkipDetectedSecrets || !previous.ExcludedApps.SequenceEqual(next.ExcludedApps, StringComparer.OrdinalIgnoreCase))
        {
            _capture.Settings = CaptureSettingsOf(next);
        }

        if (previous.HideFromScreenCapture != next.HideFromScreenCapture)
        {
            ApplyDisplayAffinity();
        }
    }

    private static CaptureSettings CaptureSettingsOf(AppSettings settings) =>
        new(settings.SkipDetectedSecrets, settings.ExcludedApps.ToArray());

    private void ApplyDisplayAffinity()
    {
        var hwnd = new WindowInteropHelper(_popupWindow).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_settings.Current.HideFromScreenCapture)
        {
            User32.SetWindowDisplayAffinity(hwnd, User32.WDA_NONE);
        }
        else if (!User32.SetWindowDisplayAffinity(hwnd, User32.WDA_EXCLUDEFROMCAPTURE))
        {
            // Before Windows 10 2004: shows as a black box instead of being invisible.
            User32.SetWindowDisplayAffinity(hwnd, User32.WDA_MONITOR);
        }
    }

    // ---- ISettingsHost ---------------------------------------------------------------------

    public AppSettings Settings => _settings.Current;

    public void UpdateSettings(Func<AppSettings, AppSettings> change) => _settings.Update(change);

    public AutostartState AutostartState => _autostart.State;

    public bool AutostartIsVolatile => _autostart.IsVolatileLocation;

    public bool ExeInDownloads => _autostart.IsInDownloads;

    public void SetAutostart(bool enabled) => _autostart.SetEnabled(enabled);

    public bool StartMenuShortcutExists => StartMenuShortcut.Exists;

    public void SetStartMenuShortcut(bool enabled)
    {
        if (enabled)
        {
            _startMenu.Create();
        }
        else
        {
            _startMenu.Remove();
        }
    }

    public bool IsRecording => _monitoring.IsRecording;

    public void SetRecording(bool recording)
    {
        if (recording)
        {
            _monitoring.Resume();
        }
        else
        {
            _monitoring.PauseIndefinitely();
        }
    }

    public HotkeyGesture? ActiveHotkey => _hotkey.Active;

    public HotkeyStatus HotkeyStatus => _hotkey.Status;

    public void SuspendHotkey() => _hotkey.Suspend();

    public void ResumeHotkey() => _hotkey.Resume();

    public bool IsHotkeyAvailable(HotkeyGesture gesture) => _hotkey.IsAvailable(gesture);

    public HotkeyStatus ApplyHotkey(HotkeyGesture gesture)
    {
        var status = _hotkey.Apply(gesture);
        UpdateStatus();
        return status;
    }

    public Task<int> CountExceedingAsync(int maxItems) => _worker.CountExceedingAsync(HistoryLimits.For(maxItems));

    public async Task ClearHistoryAsync(Window? owner)
    {
        var snapshot = _index.Snapshot();
        var includePinned = ConfirmDialog.Ask(
            Strings.ClearTitle,
            Strings.ClearMessage(snapshot.History.Count, snapshot.Pinned.Count),
            Strings.ClearConfirm,
            snapshot.Pinned.Count > 0 ? Strings.ClearAlsoPinned : null,
            owner);
        if (includePinned is null)
        {
            return;
        }

        await _worker.ClearAsync(includePinned.Value).ConfigureAwait(true);
    }

    Task ISettingsHost.ClearHistoryAsync(Window owner) => ClearHistoryAsync(owner);

    public IReadOnlyList<string> RecentSources => _capture.Sources.Recent;

    public DateTimeOffset? LastIgnored(string exe) => _capture.Sources.LastIgnored(exe);

    public string DataFolder => _options.Paths.DataDirectory;

    public string VersionText
    {
        get
        {
            var informational = typeof(AppController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 && informational.Length > plus + 8 ? informational[..(plus + 8)] : informational;
        }
    }

    public void OpenUrl(string url)
    {
        // Opens the user's browser on an explicit click; the app itself never connects anywhere.
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    public void ShowLicenses(Window owner)
    {
        var text = ReadResource("LICENSE.txt") + "\n\n" + ReadResource("THIRD-PARTY-NOTICES.md");
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
        };
        var window = new Window
        {
            Title = Strings.ShowLicenses,
            Owner = owner,
            Width = 760,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = box,
        };
        window.SetResourceReference(Window.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        window.Show();
    }

    public void RemoveEverything(Window owner)
    {
        if (ConfirmDialog.Ask(Strings.RemoveAllTitle, Strings.RemoveAllMessage, Strings.RemoveAllConfirm, owner: owner) is null)
        {
            return;
        }

        _autostart.RemoveAll();
        _startMenu.Remove();
        StopServices(TimeSpan.FromSeconds(3));
        try
        {
            Directory.Delete(_options.Paths.DataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Category, "Could not delete the data folder", ex);
            OpenUrl(_options.Paths.DataDirectory);
        }

        _app.Shutdown();
    }

    public void LanguageChanged()
    {
        // Window texts are fixed at load time; rebuild the popup in the new language.
        Strings.Apply(_settings.Current.Language);
        _popup.Close(restoreFocus: false);
        _popup.AllowShutdown();
        _popupWindow.Close();
        CreatePopup();
        SchedulePrewarm();
        UpdateStatus();
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(AppController).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---- shutdown --------------------------------------------------------------------------

    public void Exit()
    {
        StopServices(TimeSpan.FromSeconds(3));
        _app.Shutdown();
    }

    /// <summary>Session end: the process may be killed any moment after this returns.</summary>
    public void ShutdownForSession() => StopServices(TimeSpan.FromSeconds(1.5));

    public void Dispose() => StopServices(TimeSpan.FromSeconds(3));

    private void StopServices(TimeSpan timeout)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        try
        {
            _popup?.AllowShutdown();
            _settingsWindow?.Close();
            _capture?.Dispose();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _worker?.Shutdown(timeout);
            _host?.Dispose();
            _gate?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error(Category, "Shutdown failed", ex);
        }
    }
}
