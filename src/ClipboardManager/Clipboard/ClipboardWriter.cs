using System.Runtime.InteropServices;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Interop;

namespace ClipboardManager.Clipboard;

/// <summary>
/// Puts plain text or an image on the clipboard with our hidden window as owner, rendered
/// immediately. Text is CF_UNICODETEXT (Windows synthesizes CF_TEXT/CF_LOCALE); an image is "PNG"
/// plus CF_DIB (Windows synthesizes CF_BITMAP/CF_DIBV5). Writes happen only on explicit user action,
/// never in reaction to a clipboard change, so the app cannot start a sync loop.
/// </summary>
internal sealed class ClipboardWriter
{
    private const string Category = "Writer";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(1);
    private const int OpenAttempts = 10;
    private const int OpenDelayMilliseconds = 100;

    private readonly IntPtr _owner;
    private readonly ClipboardGate _gate;
    private readonly FileLog _log;

    public ClipboardWriter(IntPtr owner, ClipboardGate gate, FileLog log)
    {
        _owner = owner;
        _gate = gate;
        _log = log;
    }

    /// <summary>Returns the clipboard sequence number of the write, or null on failure.</summary>
    public Task<uint?> WriteAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteCoreAsync(() => WriteOpen(text));
    }

    /// <summary>
    /// Puts a stored image back: "PNG" with exactly the stored bytes (keeps transparency) and a
    /// 24-bit CF_DIB on white for apps that only read bitmaps. Returns the sequence number or null.
    /// </summary>
    public async Task<uint?> WriteImageAsync(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        // Building the bitmap decodes the whole picture: off the UI thread.
        var dib = await Task.Run(() => ImageCodec.TryCreateDib(png)).ConfigureAwait(true);
        if (dib is null)
        {
            _log.Warning(Category, "Stored image could not be decoded");
            return null;
        }

        return await WriteCoreAsync(() => WriteOpenFormats([(ClipboardFormats.Png, png), (User32.CF_DIB, dib)])).ConfigureAwait(true);
    }

    private async Task<uint?> WriteCoreAsync(Func<bool> writeOpen)
    {
        if (!await _gate.TryEnterAsync(GateTimeout).ConfigureAwait(true))
        {
            _log.Warning(Category, "Clipboard busy (still reading delayed-rendered content)");
            return null;
        }

        try
        {
            for (var attempt = 0; attempt < OpenAttempts; attempt++)
            {
                if (User32.OpenClipboard(_owner))
                {
                    var ok = writeOpen();
                    User32.CloseClipboard();
                    if (!ok)
                    {
                        return null;
                    }

                    var sequence = User32.GetClipboardSequenceNumber();
                    _gate.OwnWriteSequence = sequence;
                    return sequence;
                }

                await Task.Delay(OpenDelayMilliseconds).ConfigureAwait(true);
            }

            _log.Warning(Category, "Clipboard stayed locked by another application");
            return null;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private bool WriteOpen(string text)
    {
        if (!User32.EmptyClipboard())
        {
            _log.Warning(Category, $"EmptyClipboard failed ({Marshal.GetLastPInvokeError()})");
            return false;
        }

        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var memory = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, bytes);
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        var pointer = Kernel32.GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            Kernel32.GlobalFree(memory);
            return false;
        }

        unsafe
        {
            var destination = new Span<char>((void*)pointer, text.Length + 1);
            text.AsSpan().CopyTo(destination);
            destination[text.Length] = '\0';
        }

        Kernel32.GlobalUnlock(memory);
        if (User32.SetClipboardData(User32.CF_UNICODETEXT, memory) == IntPtr.Zero)
        {
            // Ownership only passes to the system on success.
            Kernel32.GlobalFree(memory);
            _log.Warning(Category, $"SetClipboardData failed ({Marshal.GetLastPInvokeError()})");
            return false;
        }

        return true;
    }

    private bool WriteOpenFormats(IReadOnlyList<(uint Format, byte[] Data)> formats)
    {
        if (!User32.EmptyClipboard())
        {
            _log.Warning(Category, $"EmptyClipboard failed ({Marshal.GetLastPInvokeError()})");
            return false;
        }

        foreach (var (format, data) in formats)
        {
            var memory = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)data.Length);
            if (memory == IntPtr.Zero)
            {
                return false;
            }

            var pointer = Kernel32.GlobalLock(memory);
            if (pointer == IntPtr.Zero)
            {
                Kernel32.GlobalFree(memory);
                return false;
            }

            Marshal.Copy(data, 0, pointer, data.Length);
            Kernel32.GlobalUnlock(memory);
            if (User32.SetClipboardData(format, memory) == IntPtr.Zero)
            {
                // Ownership only passes to the system on success.
                Kernel32.GlobalFree(memory);
                _log.Warning(Category, $"SetClipboardData failed ({Marshal.GetLastPInvokeError()})");
                return false;
            }
        }

        return true;
    }
}
