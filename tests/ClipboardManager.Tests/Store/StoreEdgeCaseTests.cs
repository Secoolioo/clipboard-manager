using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests.Store;

/// <summary>Regression tests for defects found in review.</summary>
[Trait("Category", "Store")]
public sealed class StoreEdgeCaseTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private long _now = 1_700_000_000_000;

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open(int maxItems = 10, bool memoryOnly = false, long budget = HistoryLimits.DefaultMaxTotalChars) =>
        HistoryStore.Open(_dir.Paths, memoryOnly, new HistoryLimits(maxItems, budget), FileLog.Null);

    private HistoryEntry Capture(HistoryStore store, string text) =>
        store.Capture(text, ContentHash.Compute(text), _now += 1000).Entry!;

    [Fact]
    public void Unpinning_in_a_full_history_keeps_the_unpinned_entry()
    {
        using var store = Open(maxItems: 10);
        var old = Capture(store, "old favourite");
        store.SetPinned(old.Id, true, _now += 1000);
        for (var i = 0; i < 10; i++)
        {
            Capture(store, "filler " + i);
        }

        var (entry, removed) = store.SetPinned(old.Id, false, _now += 1000);

        Assert.NotNull(entry);
        Assert.False(entry!.IsPinned);
        Assert.DoesNotContain(old.Id, removed);
        Assert.Contains(store.LoadAll(), e => e.Id == old.Id);
    }

    [Fact]
    public void Retention_is_strictly_oldest_first_under_the_size_budget()
    {
        using var store = Open(maxItems: 100, budget: 100);
        var oldest = Capture(store, new string('o', 10));
        Capture(store, new string('m', 60));
        Capture(store, new string('n', 50));

        var ids = store.LoadAll().Select(e => e.Id).ToHashSet();

        // 50 + 60 > 100: the 60-char entry goes, and the older, smaller one must go with it.
        Assert.DoesNotContain(oldest.Id, ids);
        Assert.Single(ids);
    }

    [Fact]
    public void A_clock_jumping_backwards_does_not_reorder_or_evict_new_captures()
    {
        using var store = Open(maxItems: 2);
        Capture(store, "a");
        Capture(store, "b");
        _now -= 3_600_000; // clock set back one hour
        var fresh = Capture(store, "c");

        var all = store.LoadAll();
        Assert.Contains(all, e => e.Id == fresh.Id);
        Assert.Equal(fresh.Id, all.MaxBy(e => e.LastUsedAtMs)!.Id);
    }

    [Fact]
    public void Leaving_memory_only_mode_can_discard_the_in_memory_entries()
    {
        var canary = Canary.Create();
        using var store = Open(memoryOnly: true);
        Capture(store, canary);
        Assert.Equal(1, store.CountMemoryOnlyEntries());

        var remaining = store.SetMemoryOnly(false, discardMemoryEntries: true);

        Assert.Empty(remaining);
        Assert.False(_dir.AnyFileContains(canary));
    }

    [Fact]
    public void Leaving_memory_only_mode_can_keep_the_in_memory_entries()
    {
        using var store = Open(memoryOnly: true);
        var entry = Capture(store, "keep me");

        store.SetMemoryOnly(false, discardMemoryEntries: false);

        Assert.Equal(StoreMode.Persistent, store.Mode);
        Assert.Contains(store.LoadAll(), e => e.Id == entry.Id);
    }

    [Fact]
    public void Lowering_the_limit_scrubs_the_deleted_text_from_disk()
    {
        var canary = Canary.Create();
        using var store = Open(maxItems: 100);
        Capture(store, canary);
        for (var i = 0; i < 20; i++)
        {
            Capture(store, "newer " + i);
        }

        store.SetLimits(HistoryLimits.For(10));

        Assert.False(_dir.AnyFileContains(canary));
    }

    [Fact]
    public void Unpaired_surrogates_are_normalized_consistently()
    {
        var broken = "abc\uD800def";
        var normalized = TextMetrics.NormalizeSurrogates(broken);

        Assert.Equal("abc�def", normalized);
        Assert.Same("ok 👋", TextMetrics.NormalizeSurrogates("ok 👋"));

        using var store = Open();
        var entry = store.Capture(normalized, ContentHash.Compute(normalized), 1).Entry!;
        Assert.Equal(normalized, store.GetText(entry.Id));
    }
}
