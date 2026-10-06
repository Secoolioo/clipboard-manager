using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests.Store;

[Trait("Category", "Store")]
public sealed class HistoryStoreTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private long _now = 1_700_000_000_000;

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open(bool memoryOnly = false, int maxItems = 100) =>
        HistoryStore.Open(_dir.Paths, memoryOnly, HistoryLimits.For(maxItems), FileLog.Null);

    private CaptureResult Capture(HistoryStore store, string text) =>
        store.Capture(text, ContentHash.Compute(text), _now += 1000);

    [Fact]
    public void Capture_inserts_new_text()
    {
        using var store = Open();
        var result = Capture(store, "docker compose up -d");

        Assert.Equal(CaptureOutcome.Inserted, result.Outcome);
        Assert.Equal("docker compose up -d", result.Entry!.SearchText);
        Assert.Equal(StoreMode.Persistent, store.Mode);
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void Capturing_the_newest_text_again_writes_nothing()
    {
        using var store = Open();
        Capture(store, "a");
        var again = Capture(store, "a");

        Assert.Equal(CaptureOutcome.Unchanged, again.Outcome);
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void Duplicate_moves_existing_entry_to_the_top_instead_of_adding_a_row()
    {
        using var store = Open();
        var first = Capture(store, "docker compose up -d").Entry!;
        Capture(store, "other");
        var again = Capture(store, "docker compose up -d");

        Assert.Equal(CaptureOutcome.Promoted, again.Outcome);
        Assert.Equal(first.Id, again.Entry!.Id);
        Assert.True(again.Entry.LastUsedAtMs > first.LastUsedAtMs);
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void Duplicates_are_exact_trailing_newline_is_a_different_entry()
    {
        using var store = Open();
        Capture(store, "rm -rf ./build");
        Capture(store, "rm -rf ./build\n");

        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void Retention_removes_oldest_unpinned_beyond_the_limit()
    {
        using var store = Open(maxItems: 10);
        var ids = Enumerable.Range(0, 12).Select(i => Capture(store, "item " + i).Entry!.Id).ToList();

        var remaining = store.LoadAll().Select(e => e.Id).ToHashSet();
        Assert.Equal(10, remaining.Count);
        Assert.DoesNotContain(ids[0], remaining);
        Assert.DoesNotContain(ids[1], remaining);
        Assert.Contains(ids[11], remaining);
    }

    [Fact]
    public void Pinned_entries_are_exempt_from_the_limit()
    {
        using var store = Open(maxItems: 10);
        var pinned = Capture(store, "pinned favourite").Entry!;
        store.SetPinned(pinned.Id, pinned: true, _now += 1000);

        for (var i = 0; i < 25; i++)
        {
            Capture(store, "item " + i);
        }

        var all = store.LoadAll();
        Assert.Contains(all, e => e.Id == pinned.Id && e.IsPinned);
        Assert.Equal(10, all.Count(e => !e.IsPinned));
    }

    [Fact]
    public void Size_budget_evicts_old_entries_even_below_the_count_limit()
    {
        using var store = HistoryStore.Open(_dir.Paths, memoryOnly: false, new HistoryLimits(100, MaxTotalChars: 30), FileLog.Null);
        Capture(store, new string('a', 20));
        Capture(store, new string('b', 20));

        var all = store.LoadAll();
        Assert.Single(all);
        Assert.StartsWith("b", all[0].SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void Lowering_the_limit_removes_entries_and_reports_them()
    {
        using var store = Open(maxItems: 100);
        for (var i = 0; i < 30; i++)
        {
            Capture(store, "item " + i);
        }

        Assert.Equal(20, store.CountExceeding(HistoryLimits.For(10)));
        var removed = store.SetLimits(HistoryLimits.For(10));

        Assert.Equal(20, removed.Count);
        Assert.Equal(10, store.LoadAll().Count);
    }

    [Fact]
    public void History_survives_reopening()
    {
        using (var store = Open())
        {
            Capture(store, "persistent text");
        }

        using var reopened = Open();
        Assert.Contains(reopened.LoadAll(), e => e.SearchText == "persistent text");
    }

    [Fact]
    public void Delete_returns_the_entry_for_undo_and_restore_brings_it_back()
    {
        using var store = Open();
        var entry = Capture(store, "undo me").Entry!;

        var deleted = store.Delete(entry.Id);
        Assert.NotNull(deleted);
        Assert.Empty(store.LoadAll());

        var (restored, _) = store.Restore(deleted!);
        Assert.Equal(entry.Id, restored!.Id);
        Assert.Equal("undo me", store.GetText(entry.Id));
    }

    [Fact]
    public void Clear_keeps_pins_unless_asked()
    {
        using var store = Open();
        var pinned = Capture(store, "keep").Entry!;
        store.SetPinned(pinned.Id, true, _now += 1000);
        Capture(store, "drop");

        Assert.Single(store.Clear(includePinned: false));
        Assert.Empty(store.Clear(includePinned: true));
    }

    [Fact]
    public void Long_text_is_stored_fully_but_searched_by_its_head()
    {
        using var store = Open();
        var text = new string('x', HistoryStore.SearchHeadLength + 100) + "tail";
        var entry = Capture(store, text).Entry!;

        Assert.Equal(HistoryStore.SearchHeadLength, entry.SearchText.Length);
        Assert.True(entry.IsPartiallySearchable);
        Assert.Equal(text, store.GetText(entry.Id));
    }

    [Fact]
    public void Unicode_and_multiline_text_round_trips_exactly()
    {
        using var store = Open();
        const string text = "Grüße 👋 مرحبا\r\n\tindent nbsp\n";
        var entry = Capture(store, text).Entry!;

        Assert.Equal(text, store.GetText(entry.Id));
        Assert.Equal(2, entry.LineCount);
    }
}
