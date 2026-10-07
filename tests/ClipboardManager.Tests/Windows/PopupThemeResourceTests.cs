using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Windows;

/// <summary>
/// The popup's templates must resolve under the Fluent theme. Regression: a NoticeRow ("last copy not
/// saved", empty history, no results, loading) bound SolidColorBrush.Color to SystemAccentColor, which
/// Fluent defines as an alias (a ResourceReferenceExpression), not a Color; every realization threw
/// and the repeated UI exceptions ended the app. Real window, cloaked: nothing becomes visible.
/// </summary>
[Collection(Unit.LocalizationCollection.Name)]
[Trait("Category", "Windows")]
public sealed class PopupThemeResourceTests
{
    private static HistoryEntry Entry(long id, string text, long? pinned = null) =>
        new(id, text, text.Length, 1, id * 1000, id * 1000, pinned);

    public static TheoryData<string> Themes => ["System", "Light", "Dark"];

    [Theory]
    [MemberData(nameof(Themes))]
    public Task Every_row_kind_renders_under_the_fluent_theme(string theme) => StaRunner.Run(async () =>
    {
        Strings.Apply(LanguagePreference.English);
        var failures = new List<Exception>();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            failures.Add(e.Exception);
            e.Handled = true;
        };

        var model = new PopupViewModel();
        var window = new PopupWindow(model) { ShowActivated = false, IsPrewarming = true };
#pragma warning disable WPF0001 // Same Fluent dictionaries the app gets from Application.ThemeMode, without a process-wide Application.
        window.ThemeMode = new ThemeMode(theme);
#pragma warning restore WPF0001
        Dwm.Set(new WindowInteropHelper(window).EnsureHandle(), Dwm.DWMWA_CLOAK, 1);
        var pins = Enumerable.Range(20, 5).Select(i => Entry(i, "pin " + i, i)).ToList();
        var snapshot = new HistorySnapshot(pins, [Entry(3, "newest"), Entry(2, "previous"), Entry(1, "older")]);
        try
        {
            // Notice (skipped copy) + headers + pinned + "more" + history rows, like a real open.
            model.Load(snapshot, 3, SkipReason.LooksLikeSecret, isLoaded: true, pinnedExpanded: false);
            Assert.Contains(model.Rows, r => r is NoticeRow);
            Assert.Contains(model.Rows, r => r is HeaderRow);
            Assert.Contains(model.Rows, r => r is MoreRow);
            window.Show();
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // Open/close spam: the other notice kinds, ItemsSource resets while shown and hidden.
            for (var i = 0; i < 6; i++)
            {
                model.Reset();
                window.Hide();
                switch (i % 3)
                {
                    case 0:
                        model.Load(HistorySnapshot.Empty, null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
                        break;
                    case 1:
                        model.Load(snapshot, null, SkipReason.None, isLoaded: false, pinnedExpanded: true);
                        break;
                    default:
                        model.Load(snapshot, 3, SkipReason.NotText, isLoaded: true, pinnedExpanded: false);
                        model.Query = "no such text";
                        break;
                }

                Assert.Contains(model.Rows, r => r is NoticeRow);
                window.Show();
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            Assert.Empty(failures);
        }
        finally
        {
            window.Hide();
            window.AllowCloseForShutdown();
            window.Close();
        }
    });
}
