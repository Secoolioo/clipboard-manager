using System.Windows.Interop;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Interop;

namespace ClipboardManager.Tests.Windows;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClipboardCollection
{
    public const string Name = "Real clipboard";
}

/// <summary>
/// Uses the real Windows clipboard, so it only runs with CM_INTEGRATION=1 (set in CI) and never
/// overwrites a developer's clipboard by accident.
/// </summary>
[Collection(ClipboardCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ClipboardIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CM_INTEGRATION") == "1";

    private static Task WithOwnerWindow(Func<IntPtr, Task> test) => StaRunner.Run(async () =>
    {
        using var source = new HwndSource(new HwndSourceParameters("cm-test-owner") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        await test(source.Handle);
    });

    private static async Task<ClipboardSnapshot> ReadAsync(ClipboardGate gate, CaptureSettings? settings = null, IntPtr ownWindow = default)
    {
        var snapshot = await TryReadAsync(gate, settings, ownWindow, TimeSpan.FromSeconds(5));
        Assert.True(snapshot is not null, "reader produced no snapshot");
        return snapshot!;
    }

    private static async Task<ClipboardSnapshot?> TryReadAsync(ClipboardGate gate, CaptureSettings? settings, IntPtr ownWindow, TimeSpan wait)
    {
        using var reader = new ClipboardReader(ownWindow, gate, FileLog.Null) { Settings = settings ?? CaptureSettings.Default };
        var read = new TaskCompletionSource<ClipboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.SnapshotRead += snapshot => read.TrySetResult(snapshot);
        reader.Start();
        reader.Signal(null);
        var done = await Task.WhenAny(read.Task, Task.Delay(wait));
        return done == read.Task ? await read.Task : null;
    }

    /// <summary>Puts text plus optional extra formats on the clipboard, the way other apps do.</summary>
    private static void Put(IntPtr owner, string? text, params (uint Format, byte[] Data)[] extra)
    {
        Assert.True(User32.OpenClipboard(owner));
        try
        {
            User32.EmptyClipboard();
            if (text is not null)
            {
                SetData(User32.CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            }

            foreach (var (format, data) in extra)
            {
                SetData(format, data);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }

        static void SetData(uint format, byte[] data)
        {
            var memory = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
            var pointer = Kernel32.GlobalLock(memory);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, pointer, data.Length);
            Kernel32.GlobalUnlock(memory);
            Assert.NotEqual(IntPtr.Zero, User32.SetClipboardData(format, memory));
        }
    }

    [Fact]
    public Task Unicode_text_round_trips_through_writer_and_reader() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        using var gate = new ClipboardGate();
        const string text = "Grüße 👋\r\n\tmulti-line مرحبا";

        Assert.NotNull(await new ClipboardWriter(owner, gate, FileLog.Null).WriteAsync(text));
        gate.OwnWriteSequence = 0;
        var snapshot = await ReadAsync(gate);

        Assert.Equal(SkipReason.None, snapshot.ReaderSkip);
        Assert.Equal(text, snapshot.Text);
    });

    [Fact]
    public Task Own_writes_are_not_read_back() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        using var gate = new ClipboardGate();
        await new ClipboardWriter(owner, gate, FileLog.Null).WriteAsync("ours");

        // Fast path: the sequence of our own write is skipped without even opening the clipboard.
        Assert.Null(await TryReadAsync(gate, null, owner, TimeSpan.FromSeconds(1)));

        // Second line of defence: we are the clipboard owner.
        gate.OwnWriteSequence = 0;
        var snapshot = await ReadAsync(gate, ownWindow: owner);
        Assert.Equal(SkipReason.OwnWrite, snapshot.ReaderSkip);
        Assert.Null(snapshot.Text);
    });

    [Fact]
    public Task Suppressed_sequences_are_never_read() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "copied while paused");
        var gate = new ClipboardGate();
        using var reader = new ClipboardReader(IntPtr.Zero, gate, FileLog.Null);
        var read = new TaskCompletionSource<ClipboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.SnapshotRead += snapshot => read.TrySetResult(snapshot);
        reader.SuppressThrough(User32.GetClipboardSequenceNumber(), SkipReason.Paused);
        reader.Start();
        reader.Signal(null);

        Assert.NotSame(read.Task, await Task.WhenAny(read.Task, Task.Delay(1000)));
    });

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1 })]
    [InlineData("Clipboard Viewer Ignore", new byte[] { 0x49, 0 })]
    [InlineData("CanIncludeInClipboardHistory", new byte[] { 0, 0, 0, 0 })]
    public Task Privacy_markers_prevent_reading_the_text(string formatName, byte[] data) => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "hunter2-password", (User32.RegisterClipboardFormat(formatName), data));

        var snapshot = await ReadAsync(new ClipboardGate());

        Assert.Equal(SkipReason.MarkedBySource, snapshot.ReaderSkip);
        Assert.Null(snapshot.Text);
    });

    [Fact]
    public Task Explicit_permission_marker_is_captured() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "allowed", (User32.RegisterClipboardFormat("CanIncludeInClipboardHistory"), [1, 0, 0, 0]));

        var snapshot = await ReadAsync(new ClipboardGate());

        Assert.Equal("allowed", snapshot.Text);
    });

    [Fact]
    public Task Cloud_only_opt_out_is_still_captured_like_windows_does() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "from rdp", (User32.RegisterClipboardFormat("CanUploadToCloudClipboard"), [0, 0, 0, 0]));

        var snapshot = await ReadAsync(new ClipboardGate());

        Assert.Equal("from rdp", snapshot.Text);
    });

    [Fact]
    public Task Non_text_content_is_reported_as_such() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, null, (User32.RegisterClipboardFormat("ClipboardManager.Tests.Binary"), [1, 2, 3]));

        var snapshot = await ReadAsync(new ClipboardGate());

        Assert.Equal(SkipReason.NotText, snapshot.ReaderSkip);
    });

    [Fact]
    public Task Oversized_text_is_skipped_before_it_is_copied() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, new string('x', CapturePolicy.MaxCaptureChars + 100));

        var snapshot = await ReadAsync(new ClipboardGate());

        Assert.Equal(SkipReason.TooLarge, snapshot.ReaderSkip);
        Assert.Null(snapshot.Text);
    });

    [Fact]
    public Task Excluded_app_is_decided_before_reading() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "secret from an excluded app");
        var self = Path.GetFileName(Environment.ProcessPath)!;

        var snapshot = await ReadAsync(new ClipboardGate(), new CaptureSettings(true, [self]));

        Assert.Equal(SkipReason.ExcludedApp, snapshot.ReaderSkip);
        Assert.Null(snapshot.Text);
    });

    [Fact]
    public Task Clipboard_held_by_someone_else_fails_gracefully() => WithOwnerWindow(async owner =>
    {
        Assert.SkipUnless(Enabled, "Set CM_INTEGRATION=1 to use the real clipboard.");
        Put(owner, "locked");

        // Only an open with a window locks the clipboard against OpenClipboard(NULL) of the
        // reader. Listeners of the OS (history service, rdpclip) may still be reading the content
        // just put there, so retry until this window really holds it. The dispatcher brings the
        // continuation back to this thread, which has to close the clipboard again.
        var held = false;
        for (var i = 0; i < 200 && !held; i++)
        {
            held = User32.OpenClipboard(owner);
            if (!held)
            {
                await Task.Delay(10);
            }
        }

        Assert.True(held, "test could not take the clipboard");
        try
        {
            var snapshot = await ReadAsync(new ClipboardGate());

            Assert.Equal(SkipReason.ReadFailed, snapshot.ReaderSkip);
        }
        finally
        {
            User32.CloseClipboard();
        }
    });
}
