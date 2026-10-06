using System.Windows.Interop;
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

        public Task CopyAsync(EntryRow row) => Task.CompletedTask;

        public Task TogglePinAsync(EntryRow row) => Task.CompletedTask;

        public Task DeleteAsync(EntryRow row) => Task.CompletedTask;

        public Task UndoAsync() => Task.CompletedTask;

        public void Close(bool restoreFocus) => CloseCalls++;

        public void OpenSettings()
        {
        }

        public void Resume()
        {
        }
    }

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
