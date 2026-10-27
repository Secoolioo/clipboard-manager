using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Interop;

namespace ClipboardManager.Clipboard;

/// <summary>
/// Reads the clipboard on its own thread so delayed rendering (Excel, RDP, VMs can block up to
/// 30 s) never freezes the UI. "Latest wins": bursts of notifications collapse into one read of the
/// current state. All checks happen inside a single OpenClipboard session, so the content cannot
/// change between the privacy-marker check and the read. Images are only copied inside the
/// session; decoding them happens after the clipboard was released.
/// </summary>
internal sealed class ClipboardReader : IDisposable
{
    private const string Category = "Reader";
    private const int CoalesceMilliseconds = 50;

    // From here on a normalized picture leaves enough native imaging memory behind to collect right away.
    private const long LargeImagePixels = 4L * 1024 * 1024;
    private static readonly int[] OpenBackoffMilliseconds = [5, 10, 20, 40, 80, 160];

    // "PNG" first: lossless with transparency, and about five times smaller to copy than the bitmap.
    private static readonly (uint Format, ClipboardImageFormat Kind)[] ImageFormats =
        [(ClipboardFormats.Png, ClipboardImageFormat.Png), (User32.CF_DIB, ClipboardImageFormat.Dib)];

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

    /// <summary>Raised on the reader thread with what was seen (text or an image only when it may be stored).</summary>
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

        var snapshot = ReadSession(sequence, out var image);
        _lastProcessedSequence = snapshot.Sequence;

        // Keyed on what was read, before a picture is decoded, so repeats count however long decoding takes.
        if (ContentKey(snapshot.Text, image) is { } key && _breaker.Record(key, snapshot.SourceExe, Environment.TickCount64))
        {
            _log.Warning(Category, "Repeated identical clipboard updates; pausing reads for a few seconds");
        }

        if (image is not null)
        {
            snapshot = WithImage(snapshot, image);
        }

        SnapshotRead?.Invoke(snapshot);
    }

    private static int? ContentKey(string? text, ClipboardImageData? image)
    {
        if (text is not null)
        {
            return string.GetHashCode(text, StringComparison.Ordinal);
        }

        if (image is null)
        {
            return null;
        }

        var hash = default(HashCode);
        hash.AddBytes(image.Data);
        return hash.ToHashCode();
    }

    /// <summary>Decodes a copied picture outside the clipboard session (the app that copied it may already paste it).</summary>
    private ClipboardSnapshot WithImage(ClipboardSnapshot snapshot, ClipboardImageData data)
    {
        SkipReason reason;
        CapturedImage? image;
        using (data)
        {
            reason = ImageCodec.Normalize(data, out image);
        }

        if (image is null || (long)image.Width * image.Height >= LargeImagePixels)
        {
            // WIC's native buffers are released by finalizers and the encoder's buffers sit on the large
            // object heap: give the memory of a large picture back now instead of whenever the GC runs.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        if (image is null)
        {
            _log.Debug(Category, $"Copied image not stored ({reason})");
            return snapshot with { ReaderSkip = reason };
        }

        return snapshot with { Image = image };
    }

    private ClipboardSnapshot ReadSession(uint sequenceBefore, out ClipboardImageData? image)
    {
        image = null;
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
                return ReadOpenClipboard(out image);
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

    private ClipboardSnapshot ReadOpenClipboard(out ClipboardImageData? image)
    {
        image = null;
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

        // Text wins when both are there (Excel cells, Word): the picture is a rendering of the same content.
        var hasText = User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT);
        var hasImage = User32.IsClipboardFormatAvailable(ClipboardFormats.Png) || User32.IsClipboardFormatAvailable(User32.CF_DIB);
        if (!hasText && (!hasImage || !_settings.CaptureImages))
        {
            // Files, other content, or a picture while images are off: let go of the clipboard right
            // away (the app that copied it may want to paste it at this very moment). Nothing is read.
            return new ClipboardSnapshot(sequence, hasImage ? SkipReason.ImagesOff : SkipReason.NotText, null, null);
        }

        // Decide on app exclusion before any content is ever copied into our process.
        var source = Kernel32.ProcessExeOfWindow(owner) ?? _foregroundExe;
        if (_settings.IsExcluded(source))
        {
            return new ClipboardSnapshot(sequence, SkipReason.ExcludedApp, null, source);
        }

        if (!hasText)
        {
            return ReadImage(sequence, source, out image);
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
        if (text is null)
        {
            return new ClipboardSnapshot(sequence, SkipReason.ReadFailed, null, source);
        }

        // Some apps put an empty text next to a picture: keep the picture then. Line breaks and tabs
        // alone (empty spreadsheet cells) stay a blank text, so no rendering of the cells is requested.
        return hasImage && _settings.CaptureImages && text.AsSpan().Trim(' ').IsEmpty
            ? ReadImage(sequence, source, out image)
            : new ClipboardSnapshot(sequence, SkipReason.None, text, source);
    }

    /// <summary>
    /// Copies the first readable image format. Only its header is checked here, so an oversized
    /// picture costs nothing; a "PNG" that is no PNG falls back to the bitmap.
    /// </summary>
    private static ClipboardSnapshot ReadImage(uint sequence, string? source, out ClipboardImageData? image)
    {
        image = null;
        foreach (var (format, kind) in ImageFormats)
        {
            if (!User32.IsClipboardFormatAvailable(format))
            {
                continue;
            }

            var handle = User32.GetClipboardData(format);
            var bytes = handle == IntPtr.Zero ? 0 : Kernel32.GlobalSize(handle);
            if (bytes == 0)
            {
                continue;
            }

            if (bytes > (nuint)ImageLimits.MaxClipboardBytes)
            {
                return new ClipboardSnapshot(sequence, SkipReason.TooLarge, null, source);
            }

            var pointer = Kernel32.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                SkipReason check;
                unsafe
                {
                    check = ImageCodec.CheckHeader(kind, new ReadOnlySpan<byte>((void*)pointer, (int)bytes));
                }

                if (check == SkipReason.TooLarge)
                {
                    return new ClipboardSnapshot(sequence, SkipReason.TooLarge, null, source);
                }

                if (check == SkipReason.None)
                {
                    image = ClipboardImageData.Copy(kind, pointer, (int)bytes);
                    return new ClipboardSnapshot(sequence, SkipReason.None, null, source);
                }
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }

        return new ClipboardSnapshot(sequence, SkipReason.NotText, null, source);
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

        // Identical reads less than this apart belong to one burst, also when reading a large picture is slow.
        private const long GapMs = 1000;
        private const long CooldownMs = 10_000;

        private int _hash;
        private int _count;
        private long _last;
        private long _openUntil;

        public bool IsOpen(long now) => now < _openUntil;

        public bool Record(int content, string? source, long now)
        {
            var hash = HashCode.Combine(content, source);
            var repeated = hash == _hash && now - _last <= GapMs;
            _hash = hash;
            _last = now;
            if (!repeated)
            {
                _count = 1;
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
