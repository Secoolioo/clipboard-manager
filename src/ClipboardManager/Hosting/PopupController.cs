using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Monitoring;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Hosting;

internal enum PopupSource
{
    Hotkey,
    Tray,
    SecondInstance,
}

/// <summary>Shows, places and closes the reusable popup and performs its actions.</summary>
internal sealed class PopupController : IPopupActions
{
    private const string Category = "Popup";
    private static readonly string[] ShellClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"];

    private readonly PopupWindow _window;
    private readonly PopupViewModel _viewModel;
    private readonly HistoryIndex _index;
    private readonly CurrentClipTracker _tracker;
    private readonly MonitoringState _monitoring;
    private readonly DbWorker _worker;
    private readonly ClipboardWriter _writer;
    private readonly SettingsHolder _settings;
    private readonly FileLog _log;
    private IntPtr _previousForeground;
    private long _snapshotVersion;
    private long _openedAt;
    private long _hiddenAt;
    private int _session;
    private bool _loadedAtOpen;
    private bool _refreshed;
    private bool _warm;
    private bool _busy;
    private bool _closing;

    public PopupController(
        PopupWindow window,
        HistoryIndex index,
        CurrentClipTracker tracker,
        MonitoringState monitoring,
        DbWorker worker,
        ClipboardWriter writer,
        SettingsHolder settings,
        FileLog log)
    {
        _window = window;
        _viewModel = window.ViewModel;
        _index = index;
        _tracker = tracker;
        _monitoring = monitoring;
        _worker = worker;
        _writer = writer;
        _settings = settings;
        _log = log;
        _window.Actions = this;
        _index.Changed += OnIndexChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PopupViewModel.IsCompact) && IsOpen)
            {
                Place(_window.Handle);
            }
        };
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? StatusRequested;

    /// <summary>
    /// The window threw while showing, warming up or closing and should be replaced. Carries the
    /// source of the open that failed, so the replacement can open instead.
    /// </summary>
    public event EventHandler<PopupSource?>? Failed;

    public bool IsOpen => _window.IsVisible && !_window.IsPrewarming;

    public bool IsPrewarming => _window.IsPrewarming;

    /// <summary>Hide-then-click on the tray icon must not reopen immediately.</summary>
    public bool RecentlyHidden => Environment.TickCount64 - _hiddenAt < 300;

    /// <summary>
    /// Builds the window's visual tree, layout and render resources once, invisibly (cloaked), so
    /// the first real open is as fast as every later one.
    /// </summary>
    public void Prewarm()
    {
        if (_window.IsVisible || _window.IsPrewarming)
        {
            // Open, or a warm-up is already running (unlock, resume and display changes arrive in bursts).
            return;
        }

        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        void Rendered(object? sender, EventArgs e)
        {
            _window.ContentRendered -= Rendered;
            Finish();
        }

        void Finish()
        {
            if (!_window.IsPrewarming)
            {
                // A real Show() took over while warming up; leave the visible popup alone.
                _warm = true;
                return;
            }

            try
            {
                _window.Hide();
                _viewModel.Reset();
                EndPrewarm(hwnd);
                _warm = true;
            }
            catch (Exception ex)
            {
                _window.ContentRendered -= Rendered;
                Fail(ex);
            }
        }

        try
        {
            Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 1);
            _window.IsPrewarming = true;
            _window.ShowActivated = false;
            LoadSnapshot();
            if (_warm)
            {
                _window.Show();
                _window.UpdateLayout();
                _window.Dispatcher.BeginInvoke(Finish, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else
            {
                _window.ContentRendered += Rendered;
                _window.Show();
            }
        }
        catch (Exception ex)
        {
            _window.ContentRendered -= Rendered;
            Fail(ex);
        }
    }

    private void EndPrewarm(IntPtr hwnd)
    {
        _window.ShowActivated = true;
        _window.IsPrewarming = false;
        Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 0);
    }

    /// <summary>
    /// A broken popup must never take the background app down or stay half-shown: hide it, log
    /// once and let the controller replace the window.
    /// </summary>
    private void Fail(Exception ex, PopupSource? reopen = null)
    {
        _log.Error(Category, "Popup failed", ex);
        try
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }

            EndPrewarm(new WindowInteropHelper(_window).Handle);
        }
        catch (Exception)
        {
            // The window is replaced anyway.
        }

        _hiddenAt = Environment.TickCount64;
        Failed?.Invoke(this, reopen);
    }

    public void Toggle(PopupSource source)
    {
        if (IsOpen)
        {
            Close(restoreFocus: true);
        }
        else
        {
            Show(source);
        }
    }

    /// <summary>Must run synchronously in the WM_HOTKEY handler: the foreground right ends with the next input.</summary>
    public void Show(PopupSource source)
    {
        var started = Stopwatch.GetTimestamp();
        if (_window.IsPrewarming)
        {
            _window.IsPrewarming = false;
            Dwm.Set(new WindowInteropHelper(_window).Handle, Dwm.DWMWA_CLOAK, 0);
            _window.ShowActivated = true;
        }

        var foreground = User32.GetForegroundWindow();
        _previousForeground = source == PopupSource.Hotkey && IsRestorable(foreground) ? foreground : IntPtr.Zero;

        _session++;
        _refreshed = false;
        StatusRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            // A popup that was never warmed up (quiet start, replaced after an error) has no
            // window yet; placing needs one, and display affinity must apply before the first frame.
            new WindowInteropHelper(_window).EnsureHandle();
            LoadSnapshot(initial: true);
            Place(foreground);
            _window.Show();
            _window.Activate();
            User32.SetForegroundWindow(_window.Handle);
            _window.MarkShown();
        }
        catch (Exception ex)
        {
            Fail(ex, source);
            return;
        }

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.Debug(Category, $"Popup shown in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms ({source})");
        }

        if (User32.GetForegroundWindow() != _window.Handle)
        {
            // Windows refused the foreground (foreground lock, another app's menu). The popup stays
            // visible and becomes active on the first click; the hotkey closes it again.
            _log.Info(Category, "Popup could not take the foreground");
        }
    }

    /// <summary>Unsubscribes from long-lived objects before the window is replaced (language change).</summary>
    public void Detach()
    {
        _index.Changed -= OnIndexChanged;
        _window.Actions = null;
    }

    public void Close(bool restoreFocus)
    {
        if (!_window.IsVisible || _closing)
        {
            // Giving the foreground back deactivates the popup, which calls Close again.
            return;
        }

        _closing = true;
        try
        {
            // Give the foreground back while we still own it; after Hide() Windows may refuse it.
            if (restoreFocus && _previousForeground != IntPtr.Zero && User32.IsWindow(_previousForeground))
            {
                User32.SetForegroundWindow(_previousForeground);
            }

            _previousForeground = IntPtr.Zero;
            _session++;
            PersistViewState();
            _viewModel.Reset();
            _window.Hide();
            _hiddenAt = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            // A topmost popup must always go away.
            Fail(ex);
        }
        finally
        {
            _closing = false;
        }
    }

    public async Task CopyAsync(EntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_busy)
        {
            return;
        }

        if (row.IsCurrent)
        {
            // Already in the clipboard: rewriting would only replace richer formats with plain text.
            Close(restoreFocus: true);
            return;
        }

        _busy = true;
        var session = _session;
        try
        {
            var text = row.Entry.IsPartiallySearchable ? await _worker.GetTextAsync(row.Entry.Id).ConfigureAwait(true) : row.Entry.SearchText;
            if (text is null || session != _session)
            {
                if (session == _session)
                {
                    _viewModel.InlineMessage = Strings.CopyFailed;
                }

                return;
            }

            var sequence = await _writer.WriteAsync(text).ConfigureAwait(true);
            if (sequence is null)
            {
                if (session == _session)
                {
                    _viewModel.InlineMessage = Strings.CopyFailed;
                }

                return;
            }

            _tracker.EntryIsCurrent(sequence.Value, row.Entry.Id);
            _ = _worker.PromoteAsync(row.Entry.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Only close the session this copy belongs to (the user may have closed and reopened meanwhile).
            if (session == _session)
            {
                Close(restoreFocus: true);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task TogglePinAsync(EntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var updated = await _worker.SetPinnedAsync(row.Entry.Id, !row.IsPinned, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(true);
        if (updated is not null && IsOpen)
        {
            _viewModel.ApplyUpsert(updated);
        }
    }

    public async Task DeleteAsync(EntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var deleted = await _worker.DeleteAsync(row.Entry.Id).ConfigureAwait(true);
        if (deleted is not null)
        {
            _tracker.EntryRemoved(deleted.Entry.Id);
            if (IsOpen)
            {
                _viewModel.ApplyDeletion(deleted);
            }
        }
    }

    public async Task UndoAsync()
    {
        if (_viewModel.TakeUndo() is not { } deleted)
        {
            return;
        }

        var restored = await _worker.RestoreAsync(deleted).ConfigureAwait(true);
        if (restored is not null && IsOpen)
        {
            _viewModel.ApplyUpsert(restored);
        }
    }

    public void OpenSettings()
    {
        Close(restoreFocus: false);
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        _monitoring.Resume();
        StatusRequested?.Invoke(this, EventArgs.Empty);
    }

    public void AllowShutdown() => _window.AllowCloseForShutdown();

    private void LoadSnapshot(bool initial = false)
    {
        var sequence = User32.GetClipboardSequenceNumber();
        if (initial)
        {
            _viewModel.IsCompact = _settings.Current.PopupCompact;
            _openedAt = Environment.TickCount64;
            _loadedAtOpen = _index.IsLoaded;
        }

        var expanded = initial ? _settings.Current.PinnedExpanded : _viewModel.PinnedExpanded;
        _viewModel.Load(_index.Snapshot(), _tracker.CurrentEntry(sequence), _tracker.CurrentSkip(sequence), _index.IsLoaded, expanded);
        _snapshotVersion = _index.Version;
    }

    /// <summary>
    /// The popup works on a snapshot. It is refreshed at most once, and only before the user did
    /// anything: when a capture that was in flight at opening lands (within 400 ms), or when the
    /// history finishes loading after the popup opened during startup.
    /// </summary>
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (!IsOpen || _refreshed || _window.HasUserInput || _index.Version == _snapshotVersion)
        {
            return;
        }

        var captureLanded = Environment.TickCount64 - _openedAt < 400;
        var loadFinished = !_loadedAtOpen && _index.IsLoaded;
        if (captureLanded || loadFinished)
        {
            _refreshed = true;
            LoadSnapshot();
        }
    }

    private void PersistViewState()
    {
        var compact = _viewModel.IsCompact;
        var expanded = _viewModel.PinnedExpanded;
        var current = _settings.Current;
        if (current.PopupCompact != compact || current.PinnedExpanded != expanded)
        {
            _settings.Update(s => s with { PopupCompact = compact, PinnedExpanded = expanded });
        }
    }

    /// <summary>Fixed, predictable spot: centred, upper third of the monitor with the foreground window. Set in pixels before Show.</summary>
    private void Place(IntPtr foreground)
    {
        var monitor = User32.MonitorFromWindow(foreground == IntPtr.Zero ? _window.Handle : foreground, User32.MONITOR_DEFAULTTOPRIMARY);
        var info = new User32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = Shcore.GetDpiForMonitor(monitor, Shcore.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 96u;
        var scale = dpi / 96.0 * _window.Scale;
        var logical = _window.LogicalSize;
        var work = info.rcWork;
        var width = Math.Min((int)Math.Round(logical.Width * scale), work.Width - 16);
        var height = Math.Min((int)Math.Round(logical.Height * scale), work.Height - 16);
        var x = work.Left + ((work.Width - width) / 2);
        var y = work.Top + Math.Max(8, (work.Height - height) / 3);
        User32.SetWindowPos(_window.Handle, User32.HWND_TOPMOST, x, y, width, height, User32.SWP_NOACTIVATE);
    }

    private bool IsRestorable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _window.Handle || !User32.IsWindow(hwnd))
        {
            return false;
        }

        var className = User32.ClassNameOf(hwnd);
        return Array.IndexOf(ShellClasses, className) < 0;
    }
}
