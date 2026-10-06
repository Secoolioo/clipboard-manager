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
    private const long IgnoreBurstMilliseconds = 200;

    private readonly DispatcherTimer _pauseTimer;
    private bool _listening;
    private uint _lastNotifiedSequence;
    private long _ignoreBurstUntil;

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

        // Deliberately no initial capture: whatever was copied before the app ran (perhaps during
        // a pause or an "ignore next copy" of a previous run) is not recorded.
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
        var sequence = User32.GetClipboardSequenceNumber();
        if (sequence == _lastNotifiedSequence)
        {
            return; // repeated notification for the same change
        }

        _lastNotifiedSequence = sequence;

        // Neither our own write nor an emptied clipboard is "a copy": they must not use up a
        // pending "ignore next copy" (none of these calls needs the clipboard to be open).
        var owner = User32.GetClipboardOwner();
        if (sequence == _reader.Gate.OwnWriteSequence || (owner != IntPtr.Zero && owner == _host.Handle) || User32.CountClipboardFormats() == 0)
        {
            return;
        }

        // One copy often produces several notifications (OLE copy + flush); "ignore next copy"
        // covers that whole burst.
        var now = Environment.TickCount64;
        var skip = now < _ignoreBurstUntil ? SkipReason.IgnoredOnce : _monitoring.OnClipboardChanged();
        if (skip == SkipReason.IgnoredOnce && now >= _ignoreBurstUntil)
        {
            _ignoreBurstUntil = now + IgnoreBurstMilliseconds;
        }

        if (skip != SkipReason.None)
        {
            _reader.SuppressThrough(sequence, skip);
            _tracker.ContentSkipped(sequence, skip);
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

        var text = TextMetrics.NormalizeSurrogates(snapshot.Text!);
        var hash = ContentHash.Compute(text);
        _ = CaptureAsync(snapshot.Sequence, text, hash);
    }

    private async Task CaptureAsync(uint sequence, string text, byte[] hash)
    {
        // New and promoted entries carry the sequence in their change batch (applied together with
        // the index). An unchanged capture publishes no batch, so mark it current here.
        var result = await _worker.CaptureAsync(text, hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sequence).ConfigureAwait(false);
        if (result is { Outcome: CaptureOutcome.Unchanged, Entry: { } entry })
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
