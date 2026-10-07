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
using ClipboardManager.Core.Updates;
using ClipboardManager.Dialogs;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;
using ClipboardManager.Shell;
using ClipboardManager.Updates;

namespace ClipboardManager.Hosting;

/// <summary>Composition root: creates the services, wires them together and runs startup and shutdown.</summary>
internal sealed class AppController : ISettingsHost, IDisposable
{
    private const string Category = "App";

    private readonly App _app;
    private readonly StartupOptions _options;
    private readonly FileLog _log;
    private readonly Dispatcher _dispatcher;
    private StartupWarmup _warmup = null!;
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
    private WelcomeWindow? _welcomeWindow;
    private readonly Queue<long> _popupRebuilds = new();
    private DispatcherTimer? _environmentPrewarm;
    private bool _discardMemoryEntriesOnPersist;
    private bool _installing;
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

        // At sign-in: below normal priority, no notices and no optional work until the user needs the app.
        _warmup = new StartupWarmup(_options.Autostart, notice => _tray.Notify(notice.Title, notice.Text, notice.Warning), _log);
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
        _worker.Changed += batch => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            // Mark a fresh capture as current before the index raises Changed, so an open popup
            // that refreshes on that event already sees the right "current" entry.
            if (batch.CapturedSequence is { } sequence && batch.Upserted.Count > 0)
            {
                _tracker.EntryIsCurrent(sequence, batch.Upserted[0].Id);
            }

            foreach (var removed in batch.Removed)
            {
                _tracker.EntryRemoved(removed);
            }

            _index.Apply(batch);
        });

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
                // Normal priority before the (possibly cold) first open; both are cheap and synchronous,
                // so the popup still takes the foreground before the next input.
                _warmup.UserEngaged();
                _popup.Toggle(PopupSource.Hotkey);
            }
        };
        _host.ActivateRequested += (_, _) =>
        {
            _warmup.UserEngaged();
            _popup.Show(PopupSource.SecondInstance);
        };
        _host.TrayActivity += OnTray;
        _host.SettingChanged += OnSettingChanged;
        _host.EnvironmentChanged += (_, _) => _warmup.Defer(OnEnvironmentChanged);
        _host.SessionEnding += (_, _) => ShutdownForSession();
        _monitoring.Changed += (_, _) => UpdateStatus();
        _settings.Changed += OnSettingsChanged;

        _ = OpenStoreAsync(settings);
        _capture.Start();

        CreatePopup();
        _warmup.Defer(SchedulePrewarm);
        UpdateStatus();

        // Reachable now (capture, hotkey, tray): only from here on may the quiet start slow down.
        _warmup.Begin();

        _log.Info(Category, $"Started in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms (process age {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");

        if (settingsState == SettingsLoadState.Corrupt)
        {
            _log.Warning(Category, "Settings were reset to defaults (the old file was kept as settings.json.corrupt)");
        }

        RunFirstStartTasks(hotkeyStatus);
        _warmup.Defer(() => _ = Task.Run(CleanStaleExtractionFolders));
        _warmup.Defer(() => _ = Task.Run(CleanUpdateLeftovers));
    }

    // ---- startup helpers -------------------------------------------------------------------

    private async Task OpenStoreAsync(AppSettings settings)
    {
        var status = await _worker.OpenAsync(_options.Paths, settings.MemoryOnly, settings.Limits).ConfigureAwait(true);
        if (status.Notice != StoreNotice.None)
        {
            Notify(Strings.StoreNotice(status.Notice), warning: true);
        }
    }

    /// <summary>All notices go through the warm-up, so none pops up during a quiet start.</summary>
    private void Notify(string text, bool warning = false) => _warmup.Notify(new TrayNotice(Strings.AppName, text, warning));

    private void CreatePopup()
    {
        var viewModel = new PopupViewModel { IsCompact = _settings.Current.PopupCompact };
        _popupWindow = new PopupWindow(viewModel);
        _popupWindow.SourceInitialized += (_, _) => ApplyDisplayAffinity();
        _popupWindow.ApplyScale(ThemeHelper.TextScaleFactor());
        _popup = new PopupController(_popupWindow, _index, _tracker, _monitoring, _worker, _writer, _settings, _log);
        _popup.SettingsRequested += (_, _) => OpenSettings();
        _popup.StatusRequested += (_, _) => UpdateStatus();

        // Never rebuild inside the failing call stack: the broken tree may throw again on teardown.
        _popup.Failed += (_, reopen) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => RecoverPopup(reopen));
    }

    /// <summary>
    /// Replaces a popup window that threw, so one bad state cannot keep failing (or end the app).
    /// Gives up after a few rebuilds in a short time; the popup then stays closed and the app keeps
    /// capturing in the background.
    /// </summary>
    private void RecoverPopup(PopupSource? reopen)
    {
        var now = Environment.TickCount64;
        while (_popupRebuilds.Count > 0 && now - _popupRebuilds.Peek() > 600_000)
        {
            _popupRebuilds.Dequeue();
        }

        if (_stopped || _popupRebuilds.Count >= 3)
        {
            return;
        }

        _popupRebuilds.Enqueue(now);
        _log.Warning(Category, "Rebuilding the popup after an error");
        var broken = _popupWindow;
        _popup.AllowShutdown();
        _popup.Detach();
        try
        {
            broken.Close();
        }
        catch (Exception ex)
        {
            _log.Warning(Category, "The broken popup did not close cleanly", ex);
        }

        CreatePopup();
        if (reopen is { } source)
        {
            _popup.Show(source);
        }
        else
        {
            SchedulePrewarm();
        }
    }

    private void SchedulePrewarm() =>
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (!_stopped && !_popup.IsOpen && !_popup.IsPrewarming)
            {
                _popup.Prewarm();
            }
        });

    /// <summary>Unlock, resume and display changes arrive as a burst; warm up once after it settled.</summary>
    private void OnEnvironmentChanged()
    {
        _environmentPrewarm ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.ApplicationIdle, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            SchedulePrewarm();
        }, _dispatcher);
        _environmentPrewarm.Stop();
        _environmentPrewarm.Start();
    }

    private void RunFirstStartTasks(HotkeyStatus hotkeyStatus)
    {
        var firstRun = !_autostart.FirstRunDone;
        _autostart.RegisterOnFirstRun();

        if (firstRun && _options.IsManualStart)
        {
            if (!StartMenuShortcut.Exists && !_autostart.IsVolatileLocation)
            {
                _startMenu.Create();
            }

            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ShowWelcome);
            return;
        }

        if (_options.IsManualStart)
        {
            _autostart.RepairIfBroken();

            // A manual start of an already-configured app means "show me the history".
            _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _popup.Show(PopupSource.SecondInstance));
        }
        else if (_options.UpdatedFrom is not null)
        {
            // The EXE kept its path, so the autostart entry needs no repair; just say what happened.
            _tray.Notify(Strings.AppName, Strings.UpdatedTo(VersionText));
        }

        NotifyHotkey(hotkeyStatus);
    }

    /// <summary>The previous EXE after an in-app update (it may still be closing), or a download a crash left behind.</summary>
    private void CleanUpdateLeftovers()
    {
        var swap = new ExeSwap(ExePath, _options.UpdatedFrom is null ? 1 : 10, TimeSpan.FromSeconds(1));
        swap.TryDiscardDownload();
        if (File.Exists(swap.OldPath) && !swap.TryDeleteOld())
        {
            _log.Info(Category, "The previous version's EXE could not be deleted yet");
        }
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
        _welcomeWindow = welcome;
        welcome.Closed += (_, _) =>
        {
            _welcomeWindow = null;
            if (_stopped)
            {
                // Closed by "Remove everything" or exit: never write autostart or shortcuts back.
                return;
            }

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
            Notify(Strings.HotkeyFallbackNotice(HotkeyFormatter.Format(_hotkey.Active)));
        }
        else if (status == HotkeyStatus.Taken)
        {
            Notify(Strings.HotkeyTakenNotice, warning: true);
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

    private string StatusText()
    {
        var status = _monitoring.Mode switch
        {
            MonitoringMode.PausedUntil => Strings.StatusPausedUntil(_monitoring.PausedUntil!.Value),
            MonitoringMode.PausedIndefinitely => Strings.StatusPaused,
            _ when _monitoring.IgnoreNext => Strings.StatusIgnoreNext,
            _ => Strings.StatusActive,
        };

        // A missing hotkey is shown as text everywhere, not only as a colour or glyph.
        return _hotkey?.Status is HotkeyStatus.Taken or HotkeyStatus.Unregistered ? status + " · " + Strings.HotkeyMissingShort : status;
    }

    private string TrayTooltip() => Strings.TrayTooltip(StatusText());

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
                _warmup.UserEngaged();
                if (!_popup.IsOpen && !_popup.RecentlyHidden)
                {
                    _popup.Show(PopupSource.Tray);
                }

                break;
            case User32.WM_CONTEXTMENU:
                _popup.Close(restoreFocus: false);
                Execute(TrayMenu.Show(_host.Handle, e.AnchorX, e.AnchorY, _monitoring, HotkeyFormatter.Format(_hotkey.Active)));

                // After the menu, so a held notice does not cover it (and none is shown after "Exit").
                if (!_stopped)
                {
                    _warmup.UserEngaged();
                }

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
            case TrayCommand.CheckForUpdates:
                OpenSettings(checkForUpdates: true);
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

    private void OpenSettings(bool checkForUpdates = false)
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
        if (checkForUpdates)
        {
            _settingsWindow.CheckForUpdates();
        }
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
            var discard = !next.MemoryOnly && _discardMemoryEntriesOnPersist;
            _discardMemoryEntriesOnPersist = false;
            _ = _worker.SetMemoryOnlyAsync(next.MemoryOnly, discard);
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

    public void ResumeHotkey()
    {
        _hotkey.Resume();
        UpdateStatus();
    }

    public Task<int> CountMemoryOnlyEntriesAsync() => _worker.CountMemoryOnlyEntriesAsync();

    public void SetMemoryOnly(bool memoryOnly, bool discardMemoryEntries)
    {
        _discardMemoryEntriesOnPersist = discardMemoryEntries;
        _settings.Update(s => s with { MemoryOnly = memoryOnly });
    }

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

    /// <summary>The informational version without the "+commit" build metadata.</summary>
    public string VersionText =>
        (typeof(AppController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public void OpenUrl(string url)
    {
        // Opens the user's browser (or a Settings page) on an explicit click. Policies can block
        // the Settings app or the handler may be missing; that must not count as an app error.
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warning(Category, "Could not open a link", ex);
        }
    }

    public async Task<UpdateOffer?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        // Off the UI thread: the first request resolves the system proxy (WPAD) synchronously.
        using var updates = CreateUpdateService();
        return await Task.Run(() => updates.CheckAsync(cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    public async Task DownloadUpdateAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using var updates = CreateUpdateService();
        await Task.Run(() => updates.DownloadAsync(offer, progress, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    public async Task InstallUpdateAsync()
    {
        // Settings and history live in the data folder, not next to the EXE: the new version just uses them.
        _installing = true;
        try
        {
            await SelfUpdate.RestartIntoNewVersionAsync(ExePath, _log).ConfigureAwait(true);
        }
        finally
        {
            _installing = false;
        }

        Exit();
    }

    /// <summary>One per click and disposed right after: no networking code is loaded before, and no connection outlives it.</summary>
    private UpdateService CreateUpdateService() =>
        new(ReleaseVersion.TryParse(VersionText, out var version) ? version : ReleaseVersion.Zero, ExePath, _log);

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
        if (_installing)
        {
            return;
        }

        if (ConfirmDialog.Ask(Strings.RemoveAllTitle, Strings.RemoveAllMessage, Strings.RemoveAllConfirm, owner: owner) is null)
        {
            return;
        }

        StopServices(TimeSpan.FromSeconds(3));
        _welcomeWindow?.Close();
        _autostart.RemoveAll();
        _startMenu.Remove();
        var swap = new ExeSwap(ExePath);
        swap.TryDiscardDownload();
        swap.TryDeleteOld();
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
        _popup.Detach();
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
        if (_installing)
        {
            // The EXE is being swapped; the app exits by itself in a moment. Leaving now could
            // end between the two renames and leave no EXE at the autostart path.
            _log.Info(Category, "Exit ignored while an update is installing");
            return;
        }

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
        _environmentPrewarm?.Stop();
        try
        {
            _warmup?.Dispose();
            _popup?.AllowShutdown();
            _settingsWindow?.Close();
            _capture?.Dispose();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _worker?.Shutdown(timeout);
            _host?.Dispose();

            // The gate is not disposed: a reader thread stuck in a delayed-render read may still release it.
        }
        catch (Exception ex)
        {
            _log.Error(Category, "Shutdown failed", ex);
        }
    }
}
