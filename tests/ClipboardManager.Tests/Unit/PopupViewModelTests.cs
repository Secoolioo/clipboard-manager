using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
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
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}
