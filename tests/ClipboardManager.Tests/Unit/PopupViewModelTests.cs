using System.Windows.Media;
using ClipboardManager.Clipboard;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Core.Text;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Unit;

[Collection(LocalizationCollection.Name)]
[Trait("Category", "Unit")]
public sealed class PopupViewModelTests
{
    public PopupViewModelTests() => Strings.Apply(LanguagePreference.English);

    private static HistoryEntry Entry(long id, string text, long? pinned = null) =>
        new(id, text, text.Length, TextMetrics.CountLines(text), id * 1000, id * 1000, pinned);

    private static PopupViewModel Model(HistorySnapshot snapshot, long? current = null, SkipReason skip = SkipReason.None, bool expanded = false)
    {
        var model = new PopupViewModel();
        model.Load(snapshot, current, skip, isLoaded: true, pinnedExpanded: expanded);
        return model;
    }

    [Fact]
    public void Default_selection_skips_the_entry_already_in_the_clipboard()
    {
        var snapshot = new HistorySnapshot([Entry(9, "pinned", 9)], [Entry(3, "newest"), Entry(2, "previous"), Entry(1, "older")]);

        var model = Model(snapshot, current: 3);

        Assert.Equal(2, ((EntryRow)model.Selected!).Entry.Id);
        Assert.True(model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 3).IsCurrent);
    }

    [Fact]
    public void Without_a_known_current_entry_the_newest_history_entry_is_selected()
    {
        var model = Model(new HistorySnapshot([Entry(9, "pinned", 9)], [Entry(3, "newest"), Entry(2, "previous")]));
        Assert.Equal(3, model.SelectedEntry!.Entry.Id);
    }

    [Fact]
    public void Searching_selects_the_first_history_match()
    {
        var model = Model(new HistorySnapshot([Entry(9, "docker pinned", 9)], [Entry(3, "git status"), Entry(2, "docker ps")]), current: 2);

        model.Query = "docker";

        Assert.Equal(2, model.SelectedEntry!.Entry.Id);
        Assert.Equal(2, model.Rows.OfType<EntryRow>().Count());
    }

    [Fact]
    public void Many_pins_collapse_to_three_with_a_more_row()
    {
        var pins = Enumerable.Range(10, 6).Select(i => Entry(i, "pin " + i, i)).ToList();
        var model = Model(new HistorySnapshot(pins, [Entry(1, "history")]));

        Assert.Equal(3, model.Rows.OfType<EntryRow>().Count(r => r.IsPinned));
        Assert.Single(model.Rows.OfType<MoreRow>());

        model.TogglePinnedExpanded();
        Assert.Equal(6, model.Rows.OfType<EntryRow>().Count(r => r.IsPinned));
    }

    [Fact]
    public void Skipped_copy_shows_a_content_free_notice()
    {
        var model = Model(new HistorySnapshot([], [Entry(1, "a")]), skip: SkipReason.LooksLikeSecret);

        var notice = Assert.Single(model.Rows.OfType<NoticeRow>());
        Assert.Contains("credential", notice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Movement_never_lands_on_headers()
    {
        var model = Model(new HistorySnapshot([Entry(9, "pinned", 9)], [Entry(2, "b"), Entry(1, "a")]));
        for (var i = 0; i < 10; i++)
        {
            model.MoveSelection(-1);
            Assert.True(model.Selected!.IsSelectable);
        }

        Assert.Equal(9, model.SelectedEntry!.Entry.Id);
    }

    [Fact]
    public void Delete_offers_undo_until_the_query_changes()
    {
        var entry = Entry(2, "to delete");
        var model = Model(new HistorySnapshot([], [entry, Entry(1, "other")]));

        model.ApplyDeletion(new DeletedEntry(entry, "to delete"));
        Assert.True(model.CanUndo);
        Assert.DoesNotContain(model.Rows.OfType<EntryRow>(), r => r.Entry.Id == 2);

        model.Query = "x";
        Assert.False(model.CanUndo);
    }

    [Fact]
    public void Empty_history_shows_a_hint()
    {
        var model = Model(HistorySnapshot.Empty);
        Assert.Single(model.Rows.OfType<NoticeRow>());
        Assert.Null(model.Selected);
    }

    [Fact]
    public void Preview_shows_metadata_and_marks_long_text_as_truncated()
    {
        var head = new string('x', HistoryStore.SearchHeadLength);
        var entry = new HistoryEntry(1, head, HistoryStore.SearchHeadLength + 10, 1, 1000, 1000, null);
        var model = Model(new HistorySnapshot([], [entry]));

        Assert.True(model.PreviewTruncated);
        Assert.Contains("characters", model.PreviewMeta, StringComparison.Ordinal);
    }

    private static HistoryEntry ImageEntry(long id, long? pinned = null) =>
        new(id, string.Empty, 0, 0, id * 1000, id * 1000, pinned, new EntryImage(640, 480, 150_000, TestImages.Captured(8, 6).Thumbnail));

    [Fact]
    public void An_image_row_shows_its_size_and_thumbnail()
    {
        var model = Model(new HistorySnapshot([ImageEntry(9, 9)], [ImageEntry(2), Entry(1, "text")]));

        var row = model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 2);
        Assert.True(row.IsImage);
        Assert.Equal(ContentKind.Image, row.Kind);
        Assert.Equal(EntryRow.GlyphImage, row.Glyph);
        Assert.Equal("Image · 640 × 480", row.Preview);
        Assert.Equal(string.Empty, row.LineInfo);
        Assert.Contains("Image, 640 by 480 pixels", row.AutomationName, StringComparison.Ordinal);
        Assert.NotNull(row.Thumbnail);
        Assert.Equal(EntryRow.GlyphPin, model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 9).Glyph);
    }

    [Fact]
    public void The_preview_of_an_image_shows_the_picture_and_its_size_instead_of_text()
    {
        var model = Model(new HistorySnapshot([], [ImageEntry(2), Entry(1, "text")]));

        Assert.Equal(2, model.SelectedEntry!.Entry.Id);
        Assert.Same(model.SelectedEntry!.Thumbnail, model.PreviewImage);
        Assert.Equal((640.0, 480.0), (model.PreviewImageWidth, model.PreviewImageHeight));
        Assert.Equal(string.Empty, model.PreviewText);
        Assert.False(model.PreviewTruncated);
        Assert.StartsWith("640 × 480 pixels · 146 KB", model.PreviewMeta, StringComparison.Ordinal);

        model.MoveSelection(1);

        Assert.Null(model.PreviewImage);
        Assert.Equal("text", model.PreviewText);
    }

    [Fact]
    public void Typing_the_word_for_image_finds_the_images()
    {
        var model = Model(new HistorySnapshot([], [ImageEntry(2), Entry(1, "text")]));

        model.Query = "image";

        Assert.Equal([2L], model.Rows.OfType<EntryRow>().Select(r => r.Entry.Id));
    }

    [Fact]
    public void No_picture_is_loaded_while_the_preview_is_hidden()
    {
        var loads = 0;
        var model = new PopupViewModel
        {
            IsCompact = true,
            ImageLoader = _ =>
            {
                loads++;
                return Task.FromResult<ImageSource?>(null);
            },
        };
        model.Load(new HistorySnapshot([], [ImageEntry(1)]), null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        Assert.Equal(0, loads);

        model.IsCompact = false;

        Assert.Equal(1, loads);
    }

    [Fact]
    public Task The_picture_is_loaded_one_at_a_time_for_the_latest_selection() => Windows.StaRunner.Run(async () =>
    {
        var requests = new List<(HistoryEntry Entry, TaskCompletionSource<ImageSource?> Done)>();
        var model = new PopupViewModel
        {
            ImageLoader = entry =>
            {
                var done = new TaskCompletionSource<ImageSource?>();
                requests.Add((entry, done));
                return done.Task;
            },
        };
        model.Load(new HistorySnapshot([], [ImageEntry(3), ImageEntry(2), ImageEntry(1)]), null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        Assert.NotNull(model.PreviewImage);
        Assert.Equal(3, Assert.Single(requests).Entry.Id);

        // Arrowing on while the first picture still loads queues nothing.
        model.MoveSelection(1);
        model.MoveSelection(1);
        Assert.Single(requests);

        var picture = ImageCodec.DecodeForDisplay(TestImages.Captured(64, 48).Png, 64, 48);
        requests[0].Done.SetResult(picture);
        await Settle();
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, requests[1].Entry.Id);
        Assert.NotSame(picture, model.PreviewImage);

        requests[1].Done.SetResult(picture);
        await Settle();
        Assert.Same(picture, model.PreviewImage);
        Assert.Equal(2, requests.Count);
    });

    [Fact]
    public void A_picture_skipped_by_the_warm_up_loads_when_the_popup_opens()
    {
        var warmingUp = true;
        var picture = ImageCodec.DecodeForDisplay(TestImages.Captured(16, 12).Png, 16, 12);
        var model = new PopupViewModel { ImageLoader = _ => Task.FromResult<ImageSource?>(warmingUp ? null : picture) };
        var snapshot = new HistorySnapshot([], [ImageEntry(1)]);
        model.Load(snapshot, null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        Assert.NotSame(picture, model.PreviewImage);

        // The real open reuses the warmed-up rows, so the selection does not change.
        warmingUp = false;
        model.Load(snapshot, null, SkipReason.None, isLoaded: true, pinnedExpanded: false);

        Assert.Same(picture, model.PreviewImage);
    }

    [Fact]
    public void Leaving_compact_mode_keeps_a_picture_that_is_already_shown()
    {
        var loads = 0;
        var picture = ImageCodec.DecodeForDisplay(TestImages.Captured(16, 12).Png, 16, 12);
        var model = new PopupViewModel
        {
            ImageLoader = _ =>
            {
                loads++;
                return Task.FromResult<ImageSource?>(picture);
            },
        };
        model.Load(new HistorySnapshot([], [ImageEntry(1)]), null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        Assert.Same(picture, model.PreviewImage);

        model.IsCompact = true;
        model.IsCompact = false;

        Assert.Same(picture, model.PreviewImage);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void A_window_kept_open_shows_new_copies_without_losing_search_selection_or_undo()
    {
        // The index hands out the same entry objects while an entry is unchanged.
        var (status, log, docker) = (Entry(3, "git status"), Entry(2, "git log"), Entry(1, "docker ps"));
        var model = Model(new HistorySnapshot([], [status, log, docker]), current: 3);
        model.Query = "git";
        model.Selected = model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 2);
        var kept = model.SelectedEntry;
        var deleted = new DeletedEntry(docker, "docker ps");
        model.ApplyDeletion(deleted);
        model.Selected = kept;

        // Copied meanwhile in another app: "git push" is new and current.
        model.RefreshSnapshot(new HistorySnapshot([], [Entry(4, "git push"), status, log]), 4, SkipReason.None, isLoaded: true);

        Assert.Equal("git", model.Query);
        Assert.Equal([4, 3, 2], model.Rows.OfType<EntryRow>().Select(r => r.Entry.Id));
        Assert.Same(kept, model.SelectedEntry);
        Assert.True(model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 4).IsCurrent);
        Assert.False(model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 3).IsCurrent);
        Assert.Same(deleted, model.TakeUndo());
    }

    [Fact]
    public void A_refresh_without_the_selected_entry_falls_back_to_the_default_selection()
    {
        var model = Model(new HistorySnapshot([], [Entry(2, "previous"), Entry(1, "older")]), current: 2);
        model.Selected = model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 1);

        // Entry 1 left the history (retention).
        model.RefreshSnapshot(new HistorySnapshot([], [Entry(3, "newest"), Entry(2, "previous")]), 3, SkipReason.None, isLoaded: true);

        Assert.Equal(2, model.SelectedEntry!.Entry.Id);
    }

    [Fact]
    public void A_refresh_reuses_the_rows_of_unchanged_entries()
    {
        var older = Entry(1, "older");
        var model = Model(new HistorySnapshot([], [older]));
        var row = model.Rows.OfType<EntryRow>().Single();

        model.RefreshSnapshot(new HistorySnapshot([], [Entry(2, "newest"), older]), null, SkipReason.None, isLoaded: true);

        Assert.Same(row, model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 1));
    }

    [Fact]
    public void A_copy_in_a_window_kept_open_moves_the_current_badge_at_once()
    {
        var model = Model(new HistorySnapshot([], [Entry(2, "previous"), Entry(1, "older")]), current: 2);

        model.MarkCurrent(1);

        Assert.True(model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 1).IsCurrent);
        Assert.False(model.Rows.OfType<EntryRow>().Single(r => r.Entry.Id == 2).IsCurrent);
    }

    [Fact]
    public void Keep_open_is_a_setting_and_survives_the_reset_on_close()
    {
        var model = Model(new HistorySnapshot([], [Entry(1, "a")]));
        model.KeepOpen = true;

        model.Reset();

        Assert.True(model.KeepOpen);
    }

    private static async Task Settle() =>
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}
