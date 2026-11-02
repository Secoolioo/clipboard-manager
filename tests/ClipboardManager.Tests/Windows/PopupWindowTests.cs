using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Windows;

/// <summary>Real window, but cloaked: nothing becomes visible on the desktop.</summary>
[Collection(Unit.LocalizationCollection.Name)]
[Trait("Category", "Windows")]
public sealed class PopupWindowTests
{
    private sealed class RecordingActions : IPopupActions
    {
        public int CloseCalls { get; private set; }

        public bool? LastRestoreFocus { get; private set; }

        public int ClickActivations { get; private set; }

        public Task CopyAsync(EntryRow row) => Task.CompletedTask;

        public Task TogglePinAsync(EntryRow row) => Task.CompletedTask;

        public Task DeleteAsync(EntryRow row) => Task.CompletedTask;

        public Task UndoAsync() => Task.CompletedTask;

        public void Close(bool restoreFocus)
        {
            CloseCalls++;
            LastRestoreFocus = restoreFocus;
        }

        public void OpenSettings()
        {
        }

        public void Resume()
        {
        }

        public void WindowMoved()
        {
        }

        public void ResetPosition()
        {
        }

        public void ActivatingByClick() => ClickActivations++;
    }

    private static async Task<(PopupWindow Window, PopupViewModel Model, RecordingActions Actions)> ShowCloakedAsync()
    {
        Strings.Apply(LanguagePreference.English);
        var model = new PopupViewModel();
        model.Load(new HistorySnapshot([], [new HistoryEntry(1, "a", 1, 1, 1, 1, null)]), null, Core.Capture.SkipReason.None, true, false);
        var actions = new RecordingActions();
        var window = new PopupWindow(model) { Actions = actions, ShowActivated = false, IsPrewarming = true };
        Dwm.Set(new WindowInteropHelper(window).EnsureHandle(), Dwm.DWMWA_CLOAK, 1);
        window.Show();
        await Idle(window);

        // Shown for real from here on (still cloaked and never activated).
        window.IsPrewarming = false;
        return (window, model, actions);
    }

    private static async Task Idle(PopupWindow window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void RaiseDeactivated(Window window) =>
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);

    private static void Discard(PopupWindow window)
    {
        window.Hide();
        window.AllowCloseForShutdown();
        window.Close();
    }

    [Fact]
    public Task Losing_the_focus_closes_the_window_unless_it_is_kept_open() => StaRunner.Run(async () =>
    {
        var (window, model, actions) = await ShowCloakedAsync();
        try
        {
            RaiseDeactivated(window);
            Assert.Equal(1, actions.CloseCalls);
            Assert.False(actions.LastRestoreFocus);

            model.KeepOpen = true;
            RaiseDeactivated(window);
            Assert.Equal(1, actions.CloseCalls);
        }
        finally
        {
            Discard(window);
        }
    });

    [Fact]
    public Task The_pin_and_the_close_button_follow_keep_open() => StaRunner.Run(async () =>
    {
        var (window, model, _) = await ShowCloakedAsync();
        try
        {
            Assert.False(window.KeepOpenButton.IsChecked);
            Assert.Equal(Visibility.Collapsed, window.CloseButton.Visibility);

            model.KeepOpen = true;
            await Idle(window);
            Assert.True(window.KeepOpenButton.IsChecked);
            Assert.Equal(Visibility.Visible, window.CloseButton.Visibility);

            // Clicking the pin writes back to the model (and from there to the settings).
            window.KeepOpenButton.IsChecked = false;
            Assert.False(model.KeepOpen);
            await Idle(window);
            Assert.Equal(Visibility.Collapsed, window.CloseButton.Visibility);
        }
        finally
        {
            Discard(window);
        }
    });

    [Fact]
    public Task A_click_into_the_inactive_kept_open_window_is_reported_before_it_activates() => StaRunner.Run(async () =>
    {
        var (window, model, actions) = await ShowCloakedAsync();
        try
        {
            // Not kept open: the window closes on deactivation anyway, nothing to remember.
            User32.PostMessage(window.Handle, User32.WM_MOUSEACTIVATE, window.Handle, IntPtr.Zero);
            await Idle(window);
            Assert.Equal(0, actions.ClickActivations);

            model.KeepOpen = true;
            User32.PostMessage(window.Handle, User32.WM_MOUSEACTIVATE, window.Handle, IntPtr.Zero);
            User32.PostMessage(window.Handle, User32.WM_POINTERACTIVATE, IntPtr.Zero, IntPtr.Zero);
            await Idle(window);
            Assert.Equal(2, actions.ClickActivations);
        }
        finally
        {
            Discard(window);
        }
    });

    [Fact]
    public Task Alt_F4_hides_instead_of_destroying_the_reusable_window() => StaRunner.Run(async () =>
    {
        Strings.Apply(LanguagePreference.English);
        var model = new PopupViewModel();
        model.Load(new HistorySnapshot([], [new HistoryEntry(1, "a", 1, 1, 1, 1, null)]), null, Core.Capture.SkipReason.None, true, false);
        var actions = new RecordingActions();
        var window = new PopupWindow(model) { Actions = actions, ShowActivated = false, IsPrewarming = true };
        Dwm.Set(new WindowInteropHelper(window).EnsureHandle(), Dwm.DWMWA_CLOAK, 1);
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        window.Close();

        Assert.Equal(1, actions.CloseCalls);
        Assert.True(window.IsLoaded);
        window.AllowCloseForShutdown();
        window.Close();
    });
}
