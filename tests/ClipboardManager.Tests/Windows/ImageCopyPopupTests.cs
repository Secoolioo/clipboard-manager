using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Monitoring;
using ClipboardManager.Core.Settings;
using ClipboardManager.Hosting;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Windows;

/// <summary>
/// Copying an image (no CF_UNICODETEXT) must leave the app alive: the capture pipeline records
/// "not text" for the clipboard sequence and the popup shows that hint row under the Fluent theme.
/// Regression for the v0.9.0 crash, driven through the real <see cref="PopupController"/> warm-up
/// (cloaked, never activated). The clipboard itself is never opened; only its sequence number is read.
/// </summary>
[Collection(Unit.LocalizationCollection.Name)]
[Trait("Category", "Windows")]
public sealed class ImageCopyPopupTests
{
    private static HistoryEntry Entry(long id, string text) => new(id, text, text.Length, 1, id * 1000, id * 1000, null);

    public static TheoryData<string> Themes => ["System", "Light", "Dark"];

    [Theory]
    [MemberData(nameof(Themes))]
    public Task Image_on_the_clipboard_shows_the_not_text_hint_without_ui_errors(string theme) => StaRunner.Run(async () =>
    {
        Strings.Apply(LanguagePreference.English);
        var failures = new List<Exception>();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            failures.Add(e.Exception);
            e.Handled = true;
        };

        using var data = new TempDataDirectory();
        var worker = new DbWorker(FileLog.Null);
        await using var workerScope = worker.ConfigureAwait(true);
        using var gate = new ClipboardGate();
        var index = new HistoryIndex();
        index.Apply(new HistoryChangeBatch(1, HistoryChangeBatch.NoEntries, HistoryChangeBatch.NoIds, [Entry(1, "older"), Entry(2, "previous"), Entry(3, "newest")]));
        var tracker = new CurrentClipTracker();
        var settings = new SettingsHolder(new SettingsStore(data.Paths.SettingsFile, FileLog.Null), new AppSettings());
        var model = new PopupViewModel();
        var window = new PopupWindow(model);
#pragma warning disable WPF0001 // Same Fluent dictionaries the app gets from Application.ThemeMode, without a process-wide Application.
        window.ThemeMode = new ThemeMode(theme);
#pragma warning restore WPF0001
        var controller = new PopupController(window, index, tracker, new MonitoringState(TimeProvider.System), worker, new ClipboardWriter(IntPtr.Zero, gate, FileLog.Null), settings, FileLog.Null);
        var failed = 0;
        controller.Failed += (_, _) => failed++;

        // What the reader reports when the clipboard holds an image, and what the policy makes of it.
        var reason = CapturePolicy.Evaluate(new ClipboardSnapshot(1, SkipReason.NotText, null, "mspaint.exe"), CaptureSettings.Default);
        Assert.Equal(SkipReason.NotText, reason);

        try
        {
            // Cold warm-up first (waits for ContentRendered), then the warm path, several times.
            var verified = 0;
            for (var attempt = 0; attempt < 10 && verified < 3; attempt++)
            {
                var sequence = User32.GetClipboardSequenceNumber();

                // The newest text was current before the image replaced it; the image's sequence is "not text".
                tracker.EntryIsCurrent(sequence - 1, 3);
                tracker.ContentSkipped(sequence, reason);

                controller.Prewarm();
                if (User32.GetClipboardSequenceNumber() != sequence)
                {
                    // Something else changed the clipboard meanwhile; the snapshot is not the one set up here.
                    await WaitForWarmUpAsync(controller);
                    continue;
                }

                Assert.True(controller.IsPrewarming);
                var notice = Assert.Single(model.Rows.OfType<NoticeRow>());
                Assert.Equal(Strings.Skipped(SkipReason.NotText), notice.Text);
                Assert.DoesNotContain(model.Rows.OfType<EntryRow>(), r => r.IsCurrent);
                Assert.Equal(3, Assert.IsType<EntryRow>(model.Selected).Entry.Id);

                window.UpdateLayout();
                Assert.IsType<ListBoxItem>(window.List.ItemContainerGenerator.ContainerFromItem(notice));

                await WaitForWarmUpAsync(controller);
                Assert.False(window.IsVisible);
                verified++;
            }

            Assert.Equal(3, verified);
            Assert.Equal(0, failed);
            Assert.Empty(failures);
        }
        finally
        {
            controller.Detach();
            window.AllowCloseForShutdown();
            window.Close();
        }
    });

    /// <summary>
    /// If a row template ever throws again, the popup must be taken out of service at once instead of
    /// failing on every layout pass (which used to end the app after a few seconds).
    /// </summary>
    [Fact]
    public Task A_broken_row_template_takes_the_popup_out_of_service() => StaRunner.Run(async () =>
    {
        Strings.Apply(LanguagePreference.English);
        using var data = new TempDataDirectory();
        var worker = new DbWorker(FileLog.Null);
        await using var workerScope = worker.ConfigureAwait(true);
        using var gate = new ClipboardGate();
        var index = new HistoryIndex(); // still loading: the popup shows a hint row
        var tracker = new CurrentClipTracker();
        var settings = new SettingsHolder(new SettingsStore(data.Paths.SettingsFile, FileLog.Null), new AppSettings());
        var model = new PopupViewModel();
        var window = new PopupWindow(model);
#pragma warning disable WPF0001 // Same Fluent dictionaries the app gets from Application.ThemeMode, without a process-wide Application.
        window.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        // A hint-row template that throws on every layout pass, like the v0.9.0 one did.
        window.Resources[new DataTemplateKey(typeof(NoticeRow))] = new DataTemplate(typeof(NoticeRow)) { VisualTree = new FrameworkElementFactory(typeof(ThrowingElement)) };
        var controller = new PopupController(window, index, tracker, new MonitoringState(TimeProvider.System), worker, new ClipboardWriter(IntPtr.Zero, gate, FileLog.Null), settings, FileLog.Null);
        var failed = 0;
        controller.Failed += (_, _) => failed++;
        var unhandled = 0;
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            // What App.OnDispatcherUnhandledException does with an error from a deferred layout pass.
            unhandled++;
            controller.FailFromOutside(e.Exception);
            e.Handled = true;
        };

        try
        {
            controller.Prewarm();
            await Task.Delay(1500);

            Assert.Equal(1, failed);
            Assert.False(window.IsVisible);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.True(unhandled < 10, $"{unhandled} UI exceptions after the popup was taken out of service");
        }
        finally
        {
            controller.Detach();
            if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                window.AllowCloseForShutdown();
                window.Close();
            }
        }
    });

    private static async Task WaitForWarmUpAsync(PopupController controller)
    {
        for (var i = 0; i < 500 && controller.IsPrewarming; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(controller.IsPrewarming, "the warm-up did not finish");
    }

    private sealed class ThrowingElement : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => throw new InvalidOperationException("broken row template");
    }
}
