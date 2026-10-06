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
    private bool _warm;
    private bool _busy;

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

    public bool IsOpen => _window.IsVisible && !_window.IsPrewarming;

    /// <summary>Hide-then-click on the tray icon must not reopen immediately.</summary>
    public bool RecentlyHidden => Environment.TickCount64 - _hiddenAt < 300;

    /// <summary>
    /// Builds the window's visual tree, layout and render resources once, invisibly (cloaked), so
    /// the first real open is as fast as every later one.
    /// </summary>
    public void Prewarm()
    {
        if (IsOpen)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 1);
        _window.IsPrewarming = true;
        _window.ShowActivated = false;
        LoadSnapshot();
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

            _window.Hide();
            _viewModel.Reset();
            _window.ShowActivated = true;
            _window.IsPrewarming = false;
            Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 0);
            _warm = true;
        }

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

        StatusRequested?.Invoke(this, EventArgs.Empty);
        LoadSnapshot();
        Place(foreground);
        _window.Show();
        _window.Activate();
        User32.SetForegroundWindow(_window.Handle);
        _window.MarkShown();

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.Debug(Category, $"Popup shown in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms ({source})");
        }

        if (User32.GetForegroundWindow() != _window.Handle)
        {
            // Activation was refused (foreground lock, a menu of another app): do not leave an inactive popup behind.
            _log.Info(Category, "Popup could not take the foreground");
        }
    }

    public void Close(bool restoreFocus)
    {
        if (!_window.IsVisible)
        {
            return;
        }

        // Give the foreground back while we still own it; after Hide() Windows may refuse it.
        if (restoreFocus && _previousForeground != IntPtr.Zero && User32.IsWindow(_previousForeground))
        {
            User32.SetForegroundWindow(_previousForeground);
        }

        _previousForeground = IntPtr.Zero;
        PersistViewState();
        _viewModel.Reset();
        _window.Hide();
        _hiddenAt = Environment.TickCount64;
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
        try
        {
            var text = row.Entry.IsPartiallySearchable ? await _worker.GetTextAsync(row.Entry.Id).ConfigureAwait(true) : row.Entry.SearchText;
            if (text is null)
            {
                _viewModel.InlineMessage = Strings.CopyFailed;
                return;
            }

            var sequence = await _writer.WriteAsync(text).ConfigureAwait(true);
            if (sequence is null)
            {
                _viewModel.InlineMessage = Strings.CopyFailed;
                return;
            }

            _tracker.EntryIsCurrent(sequence.Value, row.Entry.Id);
            Close(restoreFocus: true);
            _ = _worker.PromoteAsync(row.Entry.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

    private void LoadSnapshot()
    {
        var sequence = User32.GetClipboardSequenceNumber();
        _viewModel.IsCompact = _settings.Current.PopupCompact;
        _viewModel.Load(_index.Snapshot(), _tracker.CurrentEntry(sequence), _tracker.CurrentSkip(sequence), _index.IsLoaded, _settings.Current.PinnedExpanded);
        _snapshotVersion = _index.Version;
        _openedAt = Environment.TickCount64;
    }

    /// <summary>A capture that was still in flight when the popup opened may refresh it once, before the user acts.</summary>
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (IsOpen && Environment.TickCount64 - _openedAt < 400 && _viewModel.Query.Length == 0 && _index.Version != _snapshotVersion)
        {
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
