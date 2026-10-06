using System.Runtime.InteropServices;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Interop;

namespace ClipboardManager.Clipboard;

/// <summary>
/// Puts plain text on the clipboard with our hidden window as owner. Only CF_UNICODETEXT, rendered
/// immediately (Windows synthesizes CF_TEXT/CF_LOCALE). Writes happen only on explicit user action,
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
    public async Task<uint?> WriteAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
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
                    var ok = WriteOpen(text);
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
}
