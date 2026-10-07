using System.Collections.Specialized;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Localization;
using ClipboardManager.Popup;

namespace ClipboardManager.Tests.Unit;

/// <summary>The popup list is updated in place, so typing keeps (recycles) the list's containers.</summary>
[Collection(LocalizationCollection.Name)]
[Trait("Category", "Unit")]
public sealed class RowCollectionTests
{
    private static List<PopupRow> Rows(int count) => Enumerable.Range(0, count).Select(i => (PopupRow)new MoreRow("row " + i)).ToList();

    private static List<NotifyCollectionChangedAction> Track(RowCollection collection)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    [Fact]
    public void Narrowing_removes_only_the_rows_that_are_gone()
    {
        var all = Rows(10);
        var collection = new RowCollection();
        collection.Update(all);
        var actions = Track(collection);

        var narrowed = all.Where((_, i) => i % 3 == 0).ToList();
        collection.Update(narrowed);

        Assert.Equal(narrowed, collection);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Remove, a));
        Assert.Equal(6, actions.Count);
    }

    [Fact]
    public void Widening_inserts_only_the_new_rows()
    {
        var all = Rows(10);
        var collection = new RowCollection();
        collection.Update(all.Take(3).ToList());
        var actions = Track(collection);

        collection.Update(all);

        Assert.Equal(all, collection);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Add, a));
        Assert.Equal(7, actions.Count);
    }

    [Fact]
    public void An_unchanged_list_raises_nothing()
    {
        var all = Rows(5);
        var collection = new RowCollection();
        collection.Update(all);
        var actions = Track(collection);

        collection.Update(all.ToList());

        Assert.Empty(actions);
    }

    [Fact]
    public void A_reordered_or_huge_change_is_one_reset()
    {
        var all = Rows(5);
        var collection = new RowCollection();
        collection.Update(all);
        var actions = Track(collection);

        var reordered = new List<PopupRow> { all[3], all[0], all[1], all[2], all[4] };
        collection.Update(reordered);
        Assert.Equal(reordered, collection);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);

        actions.Clear();
        var huge = Rows(3000);
        collection.Update(huge);
        Assert.Equal(huge, collection);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
    }

    [Fact]
    public void Typing_keeps_the_row_objects_of_unchanged_entries()
    {
        Strings.Apply(LanguagePreference.English);
        var model = new PopupViewModel();
        var entries = Enumerable.Range(1, 20).Select(i => new HistoryEntry(i, "entry " + i, 7, 1, i * 1000L, i * 1000L, null)).Reverse().ToList();
        model.Load(new HistorySnapshot([], entries), null, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        var before = model.Rows.OfType<EntryRow>().ToDictionary(r => r.Entry.Id);

        model.Query = "entry 1";

        var after = model.Rows.OfType<EntryRow>().ToList();
        Assert.NotEmpty(after);
        Assert.All(after, row => Assert.Same(before[row.Entry.Id], row));
        Assert.All(after, row => Assert.Equal(["entry", "1"], row.Terms));
        Assert.All(after.Select((row, i) => (row, i)), x => Assert.Equal(x.i + 1, x.row.Position));
        Assert.All(after, row => Assert.Equal(after.Count, row.SetSize));
    }
}
