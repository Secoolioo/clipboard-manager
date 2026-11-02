using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Monitoring;
using ClipboardManager.Core.Settings;
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

    // Longest side of a picture decoded for the preview pane: sharp on high-DPI screens, cheap to decode.
    private const int PreviewMaxSide = 1280;
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
    private bool _failed;
    private bool _stale;

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
        _viewModel.ImageLoader = LoadPreviewImageAsync;
        _viewModel.KeepOpen = settings.Current.KeepPopupOpen;
        _index.Changed += OnIndexChanged;
        _settings.Changed += OnSettingsChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;

        // A kept-open window catches up on new copies once the user is no longer in it or pointing at it.
        _window.Deactivated += (_, _) => RefreshIfIdle();
        _window.MouseLeave += (_, _) => RefreshIfIdle();
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

    /// <summary>The window failed and was taken out of service; the controller replaces it.</summary>
    public bool HasFailed => _failed;

    /// <summary>The window stays open when another window is activated (the pin in its search row).</summary>
    public bool KeepsOpen => _viewModel.KeepOpen;

    /// <summary>Hide-then-click on the tray icon must not reopen immediately.</summary>
    public bool RecentlyHidden => Environment.TickCount64 - _hiddenAt < 300;

    /// <summary>
    /// Builds the window's visual tree, layout and render resources once, invisibly (cloaked), so
    /// the first real open is as fast as every later one.
    /// </summary>
    public void Prewarm()
    {
        if (_failed || _window.IsVisible || _window.IsPrewarming)
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
        _failed = true;
        try
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }

            EndPrewarm(new WindowInteropHelper(_window).Handle);

            // Closing ends the layout passes of the broken tree, which would otherwise throw again
            // and again; the controller puts a fresh window in its place.
            _window.AllowCloseForShutdown();
            _window.Close();
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
        if (IsOpen && _viewModel.KeepOpen && !_window.IsActive)
        {
            // Kept open behind another window: the hotkey brings it back; pressed again, it closes.
            BringToFront(source);
        }
        else if (IsOpen)
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
        if (_failed)
        {
            // Being replaced right now (see Failed).
            return;
        }

        if (IsOpen && _viewModel.KeepOpen)
        {
            // Opening a window that is kept open is bringing it to the front, never a new session.
            BringToFront(source);
            return;
        }

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
        _settings.Changed -= OnSettingsChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _window.Actions = null;
    }

    /// <summary>
    /// A kept-open window that lost the focus comes back as it was (search, selection), with the
    /// copies made meanwhile. After the next copy the focus goes back to the app the hotkey came from.
    /// </summary>
    private void BringToFront(PopupSource source)
    {
        // As in Show: from the desktop, the taskbar or the tray there is nothing to go back to, and an
        // app remembered earlier must not get the focus (and the Ctrl+V) instead.
        var foreground = User32.GetForegroundWindow();
        _previousForeground = source == PopupSource.Hotkey && IsRestorable(foreground) ? foreground : IntPtr.Zero;

        StatusRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            RefreshKeptOpen();
            _window.Activate();
            User32.SetForegroundWindow(_window.Handle);
            _window.FocusSearchBox();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>A click into the kept-open window while another app is active: that app gets the focus back after a copy.</summary>
    public void ActivatingByClick()
    {
        // Coming from the desktop or the taskbar: forget the app remembered earlier (see BringToFront).
        var foreground = User32.GetForegroundWindow();
        _previousForeground = IsRestorable(foreground) ? foreground : IntPtr.Zero;
    }

    /// <summary>After a copy in a kept-open window: back to the app the user came from, so Ctrl+V pastes there.</summary>
    private void ReturnFocus()
    {
        if (_previousForeground != IntPtr.Zero && User32.IsWindow(_previousForeground))
        {
            User32.SetForegroundWindow(_previousForeground);
        }
    }

    /// <summary>The user dragged the window: its top-left (in pixels) is where it opens next time, also after a restart.</summary>
    public void WindowMoved()
    {
        if (!User32.GetWindowRect(_window.Handle, out var rect))
        {
            return;
        }

        var (left, top) = (rect.Left, rect.Top);
        _settings.Update(s => s with { PopupLeft = left, PopupTop = top });
    }

    /// <summary>Back to the default spot (double-click on the drag area, or the button in the settings).</summary>
    public void ResetPosition()
    {
        if (_settings.Current.PopupLeft is null && _settings.Current.PopupTop is null)
        {
            Reposition();
            return;
        }

        // OnSettingsChanged moves an open window.
        _settings.Update(static s => s with { PopupLeft = null, PopupTop = null });
    }

    /// <summary>Text size or displays changed: an open window gets its new size, and stays on a screen.</summary>
    public void Reposition()
    {
        if (IsOpen)
        {
            Place(_window.Handle);
        }
    }

    public void Close(bool restoreFocus)
    {
        if (_failed || !_window.IsVisible || _closing)
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

        if (_tracker.CurrentEntry(User32.GetClipboardSequenceNumber()) == row.Entry.Id)
        {
            // Already in the clipboard: rewriting would only replace the source app's richer formats with ours.
            // (Asked now, not from the row: in a window that stays open, the clipboard changes meanwhile.)
            CopyDone();
            return;
        }

        _busy = true;
        var session = _session;
        try
        {
            byte[]? png = null;
            string? text = null;
            if (row.Entry.IsImage)
            {
                png = await _worker.GetImageAsync(row.Entry.Id).ConfigureAwait(true);
            }
            else
            {
                text = row.Entry.IsPartiallySearchable ? await _worker.GetTextAsync(row.Entry.Id).ConfigureAwait(true) : row.Entry.SearchText;
            }

            if ((text is null && png is null) || session != _session)
            {
                if (session == _session)
                {
                    _viewModel.InlineMessage = Strings.CopyFailed;
                }

                return;
            }

            var sequence = png is not null
                ? await _writer.WriteImageAsync(png).ConfigureAwait(true)
                : await _writer.WriteAsync(text!).ConfigureAwait(true);
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
                if (_viewModel.KeepOpen)
                {
                    _viewModel.MarkCurrent(row.Entry.Id);
                }

                CopyDone();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The entry is in the clipboard: close as always, or, kept open, say so and give the focus back.</summary>
    private void CopyDone()
    {
        if (!_viewModel.KeepOpen)
        {
            Close(restoreFocus: true);
            return;
        }

        _viewModel.InlineMessage = Strings.CopiedKeptOpen;
        ReturnFocus();
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

    /// <summary>The picture for the preview pane, decoded off the UI thread at no more than the pane can show.</summary>
    private async Task<ImageSource?> LoadPreviewImageAsync(HistoryEntry entry)
    {
        // A pre-warm only builds the window; it must not read and decode the newest image every time.
        if (_window.IsPrewarming || entry.Image is not { } image)
        {
            return null;
        }

        var png = await _worker.GetImageAsync(entry.Id).ConfigureAwait(true);
        if (png is null)
        {
            return null;
        }

        return await Task.Run(() => ImageCodec.DecodeForDisplay(png, image.Width, image.Height, PreviewMaxSide)).ConfigureAwait(true);
    }

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
    /// history finishes loading after the popup opened during startup. A window kept open also
    /// catches up on later copies (see <see cref="RefreshIfIdle"/>).
    /// </summary>
    private void OnIndexChanged(object? sender, EventArgs e)
    {
        if (!IsOpen || _index.Version == _snapshotVersion)
        {
            return;
        }

        if (!_refreshed && !_window.HasUserInput)
        {
            var captureLanded = Environment.TickCount64 - _openedAt < 400;
            var loadFinished = !_loadedAtOpen && _index.IsLoaded;
            if (captureLanded || loadFinished)
            {
                _refreshed = true;
                try
                {
                    LoadSnapshot();
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }

                return;
            }
        }

        // A window kept open also shows the copies made later, once the user is not in it.
        if (_viewModel.KeepOpen)
        {
            _stale = true;
            RefreshIfIdle();
        }
    }

    /// <summary>
    /// A kept-open window shows new copies, but never while the user is in it or pointing at it: the
    /// list must not shift under the cursor. It catches up on deactivation or when the mouse leaves.
    /// </summary>
    private void RefreshIfIdle()
    {
        if (!_stale || _closing || !IsOpen || !_viewModel.KeepOpen || _window.IsActive || _window.IsMouseOver)
        {
            return;
        }

        try
        {
            RefreshKeptOpen();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void RefreshKeptOpen()
    {
        _stale = false;
        var sequence = User32.GetClipboardSequenceNumber();
        _viewModel.RefreshSnapshot(_index.Snapshot(), _tracker.CurrentEntry(sequence), _tracker.CurrentSkip(sequence), _index.IsLoaded);
        _snapshotVersion = _index.Version;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PopupViewModel.IsCompact) when IsOpen:
                Place(_window.Handle);
                break;
            case nameof(PopupViewModel.KeepOpen):
                var keepOpen = _viewModel.KeepOpen;
                if (_settings.Current.KeepPopupOpen != keepOpen)
                {
                    _settings.Update(s => s with { KeepPopupOpen = keepOpen });
                }

                if (!keepOpen && IsOpen && !_window.IsActive)
                {
                    // Turned off (in the settings) while the window waited in the background: close it, as a normal one would have.
                    Close(restoreFocus: false);
                }

                break;
        }
    }

    private void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        _viewModel.KeepOpen = current.KeepPopupOpen;
        if (IsOpen && current.PopupLeft is null && previous.PopupLeft is not null)
        {
            Place(_window.Handle);
        }
    }

    /// <summary>
    /// A UI error surfaced outside the popup's own calls (e.g. from a deferred layout pass of its
    /// list): take the window out of service, so it cannot keep failing on every layout pass.
    /// </summary>
    public void FailFromOutside(Exception ex)
    {
        if (!_failed)
        {
            Fail(ex);
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

    /// <summary>
    /// Where the user dragged it last, if that is still on a screen (pulled inside its work area);
    /// otherwise the fixed, predictable default: centred, upper third of the monitor with the
    /// foreground window. Set in pixels before Show.
    /// </summary>
    private void Place(IntPtr foreground)
    {
        var logical = _window.LogicalSize;
        var settings = _settings.Current;
        var monitor = IntPtr.Zero;
        if (settings.PopupLeft is { } savedLeft && settings.PopupTop is { } savedTop)
        {
            var probe = PopupPlacement.Probe(savedLeft, savedTop, logical, _window.Scale);
            var rect = new User32.RECT { Left = probe.Left, Top = probe.Top, Right = probe.Right, Bottom = probe.Bottom };
            monitor = User32.MonitorFromRect(ref rect, User32.MONITOR_DEFAULTTONULL);
        }

        var remembered = monitor != IntPtr.Zero;
        if (!remembered)
        {
            // Never dragged, or that screen is gone.
            monitor = User32.MonitorFromWindow(foreground == IntPtr.Zero ? _window.Handle : foreground, User32.MONITOR_DEFAULTTOPRIMARY);
        }

        var info = new User32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = Shcore.GetDpiForMonitor(monitor, Shcore.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 96u;
        var work = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height);
        var place = remembered
            ? PopupPlacement.Remembered(settings.PopupLeft!.Value, settings.PopupTop!.Value, work, dpi, logical, _window.Scale)
            : PopupPlacement.Default(work, dpi, logical, _window.Scale);
        User32.SetWindowPos(_window.Handle, User32.HWND_TOPMOST, place.Left, place.Top, place.Width, place.Height, User32.SWP_NOACTIVATE);
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
