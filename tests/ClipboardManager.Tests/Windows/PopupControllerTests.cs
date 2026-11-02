using System.Windows.Interop;
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
/// The controller's link between the window and the settings (issue #12): the pin and the
/// "keep open" setting are one value, and a dragged spot is saved (and reset) in pixels. Real
/// window handle, never shown.
/// </summary>
[Collection(Unit.LocalizationCollection.Name)]
[Trait("Category", "Windows")]
public sealed class PopupControllerTests
{
    private sealed class Setup : IAsyncDisposable
    {
        private readonly TempDataDirectory _data = new();
        private readonly DbWorker _worker = new(FileLog.Null);
        private readonly ClipboardGate _gate = new();

        public Setup(AppSettings initial)
        {
            Strings.Apply(LanguagePreference.English);
            Store = new SettingsStore(_data.Paths.SettingsFile, FileLog.Null);
            Settings = new SettingsHolder(Store, initial);
            Model = new PopupViewModel();
            Window = new PopupWindow(Model);
            new WindowInteropHelper(Window).EnsureHandle();
            Controller = new PopupController(Window, new HistoryIndex(), new CurrentClipTracker(), new MonitoringState(TimeProvider.System), _worker,
                new ClipboardWriter(IntPtr.Zero, _gate, FileLog.Null), Settings, FileLog.Null);
        }

        public SettingsStore Store { get; }

        public SettingsHolder Settings { get; }

        public PopupViewModel Model { get; }

        public PopupWindow Window { get; }

        public PopupController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            Controller.Detach();
            Window.AllowCloseForShutdown();
            Window.Close();
            _gate.Dispose();
            await _worker.DisposeAsync();
            _data.Dispose();
        }
    }

    [Fact]
    public Task The_pin_and_the_keep_open_setting_are_one_value() => StaRunner.Run(async () =>
    {
        await using var setup = new Setup(new AppSettings { KeepPopupOpen = true });
        Assert.True(setup.Model.KeepOpen);
        Assert.True(setup.Controller.KeepsOpen);

        // The pin in the window: saved, also for the next start.
        setup.Model.KeepOpen = false;
        Assert.False(setup.Settings.Current.KeepPopupOpen);
        var (loaded, state) = setup.Store.Load();
        Assert.Equal(SettingsLoadState.Loaded, state);
        Assert.False(loaded.KeepPopupOpen);

        // The checkbox in the settings: the pin follows.
        setup.Settings.Update(static s => s with { KeepPopupOpen = true });
        Assert.True(setup.Model.KeepOpen);

        // A replaced controller (language change, rebuild after an error) no longer follows.
        setup.Controller.Detach();
        setup.Settings.Update(static s => s with { KeepPopupOpen = false });
        Assert.True(setup.Model.KeepOpen);
    });

    [Fact]
    public Task A_dragged_spot_is_saved_in_pixels_and_reset_to_the_default() => StaRunner.Run(async () =>
    {
        await using var setup = new Setup(new AppSettings());
        User32.SetWindowPos(setup.Window.Handle, IntPtr.Zero, 123, 77, 400, 300, User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);

        setup.Controller.WindowMoved();

        Assert.Equal(123, setup.Settings.Current.PopupLeft);
        Assert.Equal(77, setup.Settings.Current.PopupTop);
        var (saved, state) = setup.Store.Load();
        Assert.Equal(SettingsLoadState.Loaded, state);
        Assert.Equal((123, 77), (saved.PopupLeft, saved.PopupTop));

        setup.Controller.ResetPosition();

        Assert.Null(setup.Settings.Current.PopupLeft);
        Assert.Null(setup.Settings.Current.PopupTop);
        Assert.Null(setup.Store.Load().Settings.PopupLeft);

        // Nothing saved: a reset changes nothing (and does not throw while the window is closed).
        setup.Controller.ResetPosition();
        Assert.Null(setup.Settings.Current.PopupLeft);
    });
}
