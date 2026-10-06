using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Interop;

namespace ClipboardManager.Clipboard;

/// <summary>
/// Reads the clipboard on its own thread so delayed rendering (Excel, RDP, VMs can block up to
/// 30 s) never freezes the UI. "Latest wins": bursts of notifications collapse into one read of the
/// current state. All checks happen inside a single OpenClipboard session, so the content cannot
/// change between the privacy-marker check and the text read.
/// </summary>
internal sealed class ClipboardReader : IDisposable
{
    private const string Category = "Reader";
    private const int CoalesceMilliseconds = 50;
    private static readonly int[] OpenBackoffMilliseconds = [5, 10, 20, 40, 80, 160];

    private readonly IntPtr _ownWindow;
    private readonly FileLog _log;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly FloodBreaker _breaker = new();
    private volatile bool _stopping;
    private volatile string? _foregroundExe;
    private volatile CaptureSettings _settings = CaptureSettings.Default;
    private volatile uint _suppressThrough;
    private volatile SkipReason _suppressReason;
    private uint _lastProcessedSequence;

    public ClipboardReader(IntPtr ownWindow, ClipboardGate gate, FileLog log)
    {
        _ownWindow = ownWindow;
        Gate = gate;
        _log = log;

        // Normal priority on purpose: this thread holds the system-wide clipboard lock while it reads.
        _thread = new Thread(Run) { IsBackground = true, Name = "Clipboard reader" };
    }

    /// <summary>Raised on the reader thread with what was seen (text only when it may be stored).</summary>
    public event Action<ClipboardSnapshot>? SnapshotRead;

    public ClipboardGate Gate { get; }

    public CaptureSettings Settings
    {
        get => _settings;
        set => _settings = value ?? CaptureSettings.Default;
    }

    public void Start() => _thread.Start();

    /// <summary>Called on the UI thread for every WM_CLIPBOARDUPDATE.</summary>
    public void Signal(string? foregroundExe)
    {
        _foregroundExe = foregroundExe;
        _signal.Set();
    }

    /// <summary>
    /// The UI decided that clipboard content up to <paramref name="sequence"/> must not be stored
    /// (pause, "ignore next copy"). A read that is already pending honours it too, so "latest wins"
    /// can never store content whose notification was skipped.
    /// </summary>
    public void SuppressThrough(uint sequence, SkipReason reason)
    {
        _suppressReason = reason;
        _suppressThrough = sequence;
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // Still blocked in a delayed-render read: leave the event alive, the background thread dies with the process.
            _log.Warning(Category, "Reader thread did not stop in time");
            return;
        }

        _signal.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                _signal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_stopping)
            {
                return;
            }

            Thread.Sleep(CoalesceMilliseconds);
            try
            {
                ReadCurrent();
            }
            catch (Exception ex)
            {
                // A broken clipboard entry must never take the app down.
                _log.Error(Category, "Unexpected failure while reading the clipboard", ex);
            }
        }
    }

    private void ReadCurrent()
    {
        var sequence = User32.GetClipboardSequenceNumber();
        if (sequence == _lastProcessedSequence || sequence == Gate.OwnWriteSequence)
        {
            return;
        }

        if (sequence <= _suppressThrough)
        {
            // Skipped by the UI (paused / ignored); do not even open the clipboard.
            _lastProcessedSequence = sequence;
            return;
        }

        if (_breaker.IsOpen(Environment.TickCount64))
        {
            return;
        }

        var snapshot = ReadSession(sequence);
        _lastProcessedSequence = snapshot.Sequence;
        if (snapshot.Text is not null && _breaker.Record(snapshot.Text, snapshot.SourceExe, Environment.TickCount64))
        {
            _log.Warning(Category, "Repeated identical clipboard updates; pausing reads for a few seconds");
        }

        SnapshotRead?.Invoke(snapshot);
    }

    private ClipboardSnapshot ReadSession(uint sequenceBefore)
    {
        Gate.Enter();
        try
        {
            if (!OpenWithBackoff())
            {
                _log.Debug(Category, "Clipboard stayed locked by another application");
                return new ClipboardSnapshot(sequenceBefore, SkipReason.ReadFailed, null, null);
            }

            try
            {
                return ReadOpenClipboard();
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            Gate.Exit();
        }
    }

    private static bool OpenWithBackoff()
    {
        if (User32.OpenClipboard(IntPtr.Zero))
        {
            return true;
        }

        foreach (var delay in OpenBackoffMilliseconds)
        {
            Thread.Sleep(delay);
            if (User32.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }
        }

        return false;
    }

    private ClipboardSnapshot ReadOpenClipboard()
    {
        var sequence = User32.GetClipboardSequenceNumber();
        if (sequence == Gate.OwnWriteSequence)
        {
            return new ClipboardSnapshot(sequence, SkipReason.OwnWrite, null, null);
        }

        if (sequence <= _suppressThrough)
        {
            return new ClipboardSnapshot(sequence, _suppressReason, null, null);
        }

        var owner = User32.GetClipboardOwner();
        if (owner == _ownWindow && owner != IntPtr.Zero)
        {
            return new ClipboardSnapshot(sequence, SkipReason.OwnWrite, null, null);
        }

        if (User32.CountClipboardFormats() == 0)
        {
            return new ClipboardSnapshot(sequence, SkipReason.Cleared, null, null);
        }

        if (IsMarkedPrivate())
        {
            return new ClipboardSnapshot(sequence, SkipReason.MarkedBySource, null, null);
        }

        // Decide on app exclusion before the text is ever copied into our process.
        var source = Kernel32.ProcessExeOfWindow(owner) ?? _foregroundExe;
        if (_settings.IsExcluded(source))
        {
            return new ClipboardSnapshot(sequence, SkipReason.ExcludedApp, null, source);
        }

        if (!User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT))
        {
            return new ClipboardSnapshot(sequence, SkipReason.NotText, null, source);
        }

        var handle = User32.GetClipboardData(User32.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return new ClipboardSnapshot(sequence, SkipReason.ReadFailed, null, source);
        }

        var bytes = Kernel32.GlobalSize(handle);
        if (bytes == 0)
        {
            return new ClipboardSnapshot(sequence, SkipReason.ReadFailed, null, source);
        }

        if (bytes / 2 > (nuint)CapturePolicy.MaxCaptureChars + 1)
        {
            return new ClipboardSnapshot(sequence, SkipReason.TooLarge, null, source);
        }

        var text = CopyText(handle, bytes);
        return text is null
            ? new ClipboardSnapshot(sequence, SkipReason.ReadFailed, null, source)
            : new ClipboardSnapshot(sequence, SkipReason.None, text, source);
    }

    private static bool IsMarkedPrivate()
    {
        if (User32.IsClipboardFormatAvailable(ClipboardFormats.ExcludeFromMonitoring) ||
            User32.IsClipboardFormatAvailable(ClipboardFormats.ViewerIgnore))
        {
            return true;
        }

        if (!User32.IsClipboardFormatAvailable(ClipboardFormats.CanIncludeInHistory))
        {
            return false;
        }

        var handle = User32.GetClipboardData(ClipboardFormats.CanIncludeInHistory);
        if (handle == IntPtr.Zero || Kernel32.GlobalSize(handle) < sizeof(uint))
        {
            // Present but unreadable: err on the side of privacy.
            return true;
        }

        var pointer = Kernel32.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            unsafe
            {
                return *(uint*)pointer == 0;
            }
        }
        finally
        {
            Kernel32.GlobalUnlock(handle);
        }
    }

    /// <summary>Copies at most GlobalSize bytes; never trusts the terminator (clipboard data is untrusted).</summary>
    private static string? CopyText(IntPtr handle, nuint bytes)
    {
        var pointer = Kernel32.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<char>((void*)pointer, (int)(bytes / 2));
                var end = span.IndexOf('\0');
                return new string(end < 0 ? span : span[..end]);
            }
        }
        finally
        {
            Kernel32.GlobalUnlock(handle);
        }
    }

    /// <summary>Stops reading for a while when a sync tool ping-pongs the same content.</summary>
    private sealed class FloodBreaker
    {
        private const int Threshold = 10;
        private const long WindowMs = 2000;
        private const long CooldownMs = 10_000;

        private int _hash;
        private int _count;
        private long _windowStart;
        private long _openUntil;

        public bool IsOpen(long now) => now < _openUntil;

        public bool Record(string text, string? source, long now)
        {
            var hash = HashCode.Combine(string.GetHashCode(text, StringComparison.Ordinal), source);
            if (hash != _hash || now - _windowStart > WindowMs)
            {
                _hash = hash;
                _count = 1;
                _windowStart = now;
                return false;
            }

            if (++_count < Threshold)
            {
                return false;
            }

            _openUntil = now + CooldownMs;
            _count = 0;
            return true;
        }
    }
}
