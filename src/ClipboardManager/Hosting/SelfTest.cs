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
/// winsqlite3, window creation, icon rendering). Uses a throw-away data folder, shows nothing
/// (the window is cloaked) and only touches the clipboard with "--selftest-clipboard" (CI).
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

        gate.OwnWriteSequence = 0;
        using var reader = new ClipboardReader(IntPtr.Zero, gate, options.Log);
        var read = new TaskCompletionSource<ClipboardSnapshot>();
        reader.SnapshotRead += snapshot => read.TrySetResult(snapshot);
        reader.Start();
        reader.Signal(null);
        var finished = await Task.WhenAny(read.Task, Task.Delay(5000)).ConfigureAwait(true);
        return finished == read.Task && read.Task.Result.Text == marker;
    }
}
