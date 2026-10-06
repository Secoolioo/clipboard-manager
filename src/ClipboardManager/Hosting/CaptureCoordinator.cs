using System.Windows.Threading;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Monitoring;
using ClipboardManager.Interop;
using ClipboardManager.Shell;

namespace ClipboardManager.Hosting;

/// <summary>
/// Clipboard change → (pause check on the UI thread) → reader thread → policy → database worker.
/// When recording is paused the clipboard is not even opened.
/// </summary>
internal sealed class CaptureCoordinator : IDisposable
{
    private const string Category = "Capture";

    private readonly HostWindow _host;
    private readonly ClipboardReader _reader;
    private readonly DbWorker _worker;
    private readonly MonitoringState _monitoring;
    private readonly CurrentClipTracker _tracker;
    private readonly Dispatcher _dispatcher;
    private readonly FileLog _log;
    private readonly DispatcherTimer _pauseTimer;
    private bool _listening;

    public CaptureCoordinator(HostWindow host, ClipboardReader reader, DbWorker worker, MonitoringState monitoring, CurrentClipTracker tracker, FileLog log)
    {
        _host = host;
        _reader = reader;
        _worker = worker;
        _monitoring = monitoring;
        _tracker = tracker;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _pauseTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _pauseTimer.Tick += (_, _) =>
        {
            _pauseTimer.Stop();
            _monitoring.Refresh();
        };

        _host.ClipboardUpdated += OnClipboardUpdated;
        _host.EnvironmentChanged += (_, _) => _monitoring.Refresh();
        _reader.SnapshotRead += OnSnapshotRead;
        _monitoring.Changed += (_, _) => ArmPauseTimer();
    }

    public CaptureSettings Settings
    {
        get => _reader.Settings;
        set => _reader.Settings = value;
    }

    public void Start()
    {
        _reader.Start();
        _listening = User32.AddClipboardFormatListener(_host.Handle);
        if (!_listening)
        {
            _log.Error(Category, "AddClipboardFormatListener failed");
        }

        // Capture whatever is in the clipboard right now, so "current" is known from the start.
        OnClipboardUpdated(this, EventArgs.Empty);
        ArmPauseTimer();
    }

    public void Dispose()
    {
        _pauseTimer.Stop();
        if (_listening)
        {
            User32.RemoveClipboardFormatListener(_host.Handle);
            _listening = false;
        }

        _reader.Dispose();
    }

    private void OnClipboardUpdated(object? sender, EventArgs e)
    {
        var skip = _monitoring.OnClipboardChanged();
        if (skip != SkipReason.None)
        {
            _tracker.ContentSkipped(User32.GetClipboardSequenceNumber(), skip);
            return;
        }

        // The foreground window at change time is the fallback source when the owner window is unknown.
        _reader.Signal(Kernel32.ProcessExeOfWindow(User32.GetForegroundWindow()));
    }

    public SourceLog Sources { get; } = new();

    /// <summary>Runs on the reader thread.</summary>
    private void OnSnapshotRead(ClipboardSnapshot snapshot)
    {
        var reason = CapturePolicy.Evaluate(snapshot, _reader.Settings);
        Sources.Seen(snapshot.SourceExe);
        if (reason == SkipReason.ExcludedApp)
        {
            Sources.Ignored(snapshot.SourceExe);
        }

        if (reason != SkipReason.None)
        {
            _dispatcher.BeginInvoke(() => _tracker.ContentSkipped(snapshot.Sequence, reason));
            return;
        }

        var text = snapshot.Text!;
        var hash = ContentHash.Compute(text);
        _ = CaptureAsync(snapshot.Sequence, text, hash);
    }

    private async Task CaptureAsync(uint sequence, string text, byte[] hash)
    {
        var result = await _worker.CaptureAsync(text, hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        if (result.Entry is { } entry)
        {
            await _dispatcher.InvokeAsync(() => _tracker.EntryIsCurrent(sequence, entry.Id));
        }
    }

    /// <summary>One-shot timer to the end of a timed pause (no periodic ticking).</summary>
    private void ArmPauseTimer()
    {
        _pauseTimer.Stop();
        if (_monitoring.PausedUntil is { } until)
        {
            var due = until - DateTimeOffset.UtcNow;
            _pauseTimer.Interval = due > TimeSpan.Zero ? due + TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(250);
            _pauseTimer.Start();
        }
    }
}
