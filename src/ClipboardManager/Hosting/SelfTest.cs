using System.Buffers.Binary;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClipboardManager.Clipboard;
using ClipboardManager.Common;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using ClipboardManager.Popup;
using ClipboardManager.Shell;

namespace ClipboardManager.Hosting;

/// <summary>
/// "--selftest": proves a published EXE really starts (single-file extraction, WPF, Fluent theme,
/// winsqlite3, window creation, icon rendering, image codecs). Uses a throw-away data folder, shows
/// nothing (the window is cloaked) and only touches the clipboard with "--selftest-clipboard" (CI).
/// </summary>
internal static class SelfTest
{
    public const string OutputVariable = "CLIPBOARDMANAGER_SELFTEST_OUT";

    public static async Task<int> RunAsync(Application app, StartupOptions options)
    {
        var report = new StringBuilder();
        var failed = 0;

        async Task Check(string name, Func<Task<bool>> check)
        {
            try
            {
                var ok = await check().ConfigureAwait(true);
                report.AppendLine((ok ? "PASS " : "FAIL ") + name);
                failed += ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                report.AppendLine("FAIL " + name + ": " + ex.GetType().Name);
                failed++;
            }
        }

        ThemeHelper.Apply(app, ThemePreference.System);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        await Check("sqlite (winsqlite3) open, capture, persist", async () =>
        {
            var worker = new DbWorker(options.Log);
            await using (worker.ConfigureAwait(true))
            {
                var status = await worker.OpenAsync(options.Paths, memoryOnly: false, HistoryLimits.For(100)).ConfigureAwait(true);
                var result = await worker.CaptureAsync("self-test", ContentHash.Compute("self-test"), 1).ConfigureAwait(true);
                return status.Mode == StoreMode.Persistent && result?.Outcome == CaptureOutcome.Inserted;
            }
        }).ConfigureAwait(true);

        using var host = new HostWindow();
        await Check("host window", () => Task.FromResult(host.Handle != IntPtr.Zero)).ConfigureAwait(true);

        await Check("hotkey registration api", () =>
        {
            // Probes an unusual combination and releases it immediately; whether it is free depends on the machine.
            using var hotkey = new HotkeyRegistration(host.Handle);
            var probe = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x78); // F9
            report.AppendLine("INFO probe hotkey available: " + hotkey.IsAvailable(probe));
            return Task.FromResult(true);
        }).ConfigureAwait(true);

        await Check("tray icon rendering", () =>
        {
            var icon = User32.IconFromPng(BrandIcon.RenderPng(BrandIcon.CreateGlyph(Colors.White, paused: true), 32), 32);
            var ok = icon != IntPtr.Zero;
            User32.DestroyIcon(icon);
            return Task.FromResult(ok);
        }).ConfigureAwait(true);

        await Check("popup window renders (cloaked)", async () =>
        {
            var window = new PopupWindow(new PopupViewModel()) { ShowActivated = false, IsPrewarming = true };
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 1);
            var rendered = new TaskCompletionSource();
            window.ContentRendered += (_, _) => rendered.TrySetResult();
            window.Show();
            var ok = await Task.WhenAny(rendered.Task, Task.Delay(10_000)).ConfigureAwait(true) == rendered.Task;

            // Warm open latency: Show → layout → render pass completed (the window stays cloaked).
            var samples = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                window.Hide();
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                window.Show();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            samples.Sort();
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"INFO popup warm open: median {samples[samples.Count / 2]:F1} ms, max {samples[^1]:F1} ms");
            window.AllowCloseForShutdown();
            window.Close();
            return ok;
        }).ConfigureAwait(true);

        if (options.SelfTestClipboard)
        {
            await Check("clipboard write and read round trip", () => ClipboardRoundTripAsync(host, options)).ConfigureAwait(true);
        }

        // Footprint after the UI has rendered once (what a resident app costs), for the performance log.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"INFO private bytes {process.PrivateMemorySize64 / 1048576.0:F1} MB, working set {process.WorkingSet64 / 1048576.0:F1} MB, process age {(DateTime.Now - process.StartTime).TotalMilliseconds:F0} ms");
        }

        if (options.IdleSeconds > 0)
        {
            // Idle probe: a rendered, hidden popup plus the host window, nothing else (no tray, no hotkey).
            var idleWindow = new PopupWindow(new PopupViewModel()) { ShowActivated = false, IsPrewarming = true };
            Dwm.Set(new WindowInteropHelper(idleWindow).EnsureHandle(), Dwm.DWMWA_CLOAK, 1);
            idleWindow.Show();
            idleWindow.Hide();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds)).ConfigureAwait(true);
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"INFO idle {options.IdleSeconds} s: CPU time {cpu.TotalMilliseconds:F1} ms, private bytes {process.PrivateMemorySize64 / 1048576.0:F1} MB");
            idleWindow.AllowCloseForShutdown();
            idleWindow.Close();
        }

        // Pictures last, so the footprint above stays comparable with earlier versions; what a 4K
        // picture leaves behind once its buffers are released is reported on its own.
        await Check("image codec (WIC) normalize, thumbnail, bitmap", () => Task.FromResult(ImageCodecRoundTrip(report))).ConfigureAwait(true);
        if (options.SelfTestClipboard)
        {
            await Check("clipboard image write and read round trip", () => ImageClipboardRoundTripAsync(host, options)).ConfigureAwait(true);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"INFO private bytes after a 4K picture {process.PrivateMemorySize64 / 1048576.0:F1} MB");
        }

        var output = Environment.GetEnvironmentVariable(OutputVariable);
        var text = report.ToString();
        options.Log.Info("SelfTest", text.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(output))
        {
            File.WriteAllText(output, text);
        }

        try
        {
            Directory.Delete(options.Paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task<bool> ClipboardRoundTripAsync(HostWindow host, StartupOptions options)
    {
        using var gate = new ClipboardGate();
        var writer = new ClipboardWriter(host.Handle, gate, options.Log);
        var marker = "self-test " + Guid.NewGuid().ToString("N") + " Grüße 👋";

        // Write from a separate owner so the reader does not treat it as our own write.
        var sequence = await writer.WriteAsync(marker).ConfigureAwait(true);
        if (sequence is null)
        {
            return false;
        }

        var read = await ReadBackAsync(gate, options).ConfigureAwait(true);
        return read?.Text == marker;
    }

    /// <summary>A picture with transparency goes out as "PNG" + CF_DIB and comes back as the same normalized PNG.</summary>
    private static async Task<bool> ImageClipboardRoundTripAsync(HostWindow host, StartupOptions options)
    {
        CapturedImage? original;
        using (var data = ClipboardImageData.FromSpan(ClipboardImageFormat.Dib, TestPattern(64, 32, withAlpha: true)))
        {
            if (ImageCodec.Normalize(data, out original) != SkipReason.None || original is null)
            {
                return false;
            }
        }

        using var gate = new ClipboardGate();
        var writer = new ClipboardWriter(host.Handle, gate, options.Log);
        if (await writer.WriteImageAsync(original.Png).ConfigureAwait(true) is null || !User32.IsClipboardFormatAvailable(User32.CF_DIB))
        {
            return false;
        }

        var read = await ReadBackAsync(gate, options).ConfigureAwait(true);
        return read?.Image is { } image && image.Png.AsSpan().SequenceEqual(original.Png);
    }

    private static async Task<ClipboardSnapshot?> ReadBackAsync(ClipboardGate gate, StartupOptions options)
    {
        gate.OwnWriteSequence = 0;
        using var reader = new ClipboardReader(IntPtr.Zero, gate, options.Log);
        var read = new TaskCompletionSource<ClipboardSnapshot>();
        reader.SnapshotRead += snapshot => read.TrySetResult(snapshot);
        reader.Start();
        reader.Signal(null);
        var finished = await Task.WhenAny(read.Task, Task.Delay(5000)).ConfigureAwait(true);
        return finished == read.Task ? read.Task.Result : null;
    }

    /// <summary>A 4K screenshot through the whole codec: proves WIC works in the published EXE and logs what it costs.</summary>
    private static bool ImageCodecRoundTrip(StringBuilder report)
    {
        const int width = 3840;
        const int height = 2160;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        SkipReason reason;
        CapturedImage? image;
        using (var data = ClipboardImageData.FromSpan(ClipboardImageFormat.Dib, TestPattern(width, height, withAlpha: false)))
        {
            reason = ImageCodec.Normalize(data, out image);
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (reason != SkipReason.None || image is null)
        {
            return false;
        }

        var decoded = ImageCodec.DecodeForDisplay(image.Png, image.Width, image.Height);
        var thumbnail = ImageCodec.DecodeForDisplay(image.Thumbnail, 0, 0);
        var bitmap = ImageCodec.TryCreateDib(image.Png);
        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"INFO image 4K normalize: {elapsed:F0} ms, PNG {image.Png.Length / 1048576.0:F1} MB, thumbnail {image.Thumbnail.Length / 1024.0:F1} KB");

        return image is { Width: width, Height: height } &&
            decoded is { PixelWidth: width, PixelHeight: height } &&
            thumbnail is { PixelWidth: <= 96, PixelHeight: <= 56 } &&
            bitmap is not null && bitmap.LongLength == Dib24Size(width, height);
    }

    private static long Dib24Size(int width, int height) => 40 + ((((width * 24) + 31) / 32 * 4) * (long)height);

    /// <summary>A bottom-up 32-bit DIB with a pattern that compresses like a real screenshot, not like a flat color.</summary>
    private static byte[] TestPattern(int width, int height, bool withAlpha)
    {
        var dib = new byte[40 + (width * height * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = 40 + (((y * width) + x) * 4);
                dib[i] = (byte)(x * 255 / width);
                dib[i + 1] = (byte)(y * 255 / height);
                dib[i + 2] = (byte)((x / 16) ^ (y / 16));
                dib[i + 3] = withAlpha ? (byte)(x * 255 / Math.Max(1, width - 1)) : (byte)0;
            }
        }

        return dib;
    }
}
