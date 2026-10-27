using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;

namespace ClipboardManager.Tests.Privacy;

/// <summary>"Deleted means deleted" and "memory-only never touches the disk", proven on the bytes.</summary>
[Trait("Category", "Privacy")]
public sealed class StorePrivacyTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private long _now = 1;

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open(bool memoryOnly = false) =>
        HistoryStore.Open(_dir.Paths, memoryOnly, HistoryLimits.For(100), FileLog.Null);

    private HistoryEntry Capture(HistoryStore store, string text) =>
        store.Capture(text, ContentHash.Compute(text), _now++).Entry!;

    [Fact]
    public void Deleted_entry_is_gone_from_every_file_while_the_app_still_runs()
    {
        var canary = Canary.Create();
        using var store = Open();
        Capture(store, "before");
        var entry = Capture(store, "secret " + canary);
        Capture(store, "after");
        Assert.True(_dir.AnyFileContains(canary));

        store.Delete(entry.Id);

        Assert.False(_dir.AnyFileContains(canary));
    }

    [Fact]
    public void Clearing_history_scrubs_all_files()
    {
        var canary = Canary.Create();
        using var store = Open();
        for (var i = 0; i < 20; i++)
        {
            Capture(store, $"{canary} {i}");
        }

        store.Clear(includePinned: false);

        Assert.False(_dir.AnyFileContains(canary));
    }

    [Fact]
    public void Memory_only_mode_never_writes_unpinned_text_to_disk()
    {
        var canary = Canary.Create();
        using (var store = Open(memoryOnly: true))
        {
            Assert.Equal(StoreMode.MemoryOnlyByChoice, store.Mode);
            Capture(store, canary);
            Assert.False(_dir.AnyFileContains(canary));
        }

        Assert.False(_dir.AnyFileContains(canary));
        using var reopened = Open(memoryOnly: true);
        Assert.Empty(reopened.LoadAll());
    }

    [Fact]
    public void Memory_only_mode_still_persists_pins()
    {
        long pinnedId;
        using (var store = Open(memoryOnly: true))
        {
            pinnedId = Capture(store, "pinned in memory mode").Id;
            store.SetPinned(pinnedId, true, _now++);
        }

        using var reopened = Open(memoryOnly: true);
        Assert.Contains(reopened.LoadAll(), e => e.Id == pinnedId && e.IsPinned);
    }

    [Fact]
    public void Switching_memory_only_on_scrubs_existing_unpinned_entries_from_disk()
    {
        var canary = Canary.Create();
        using var store = Open();
        Capture(store, canary);
        Assert.True(_dir.AnyFileContains(canary));

        store.SetMemoryOnly(true);

        Assert.False(_dir.AnyFileContains(canary));
        Assert.Contains(store.LoadAll(), e => e.SearchText == canary);
    }

    [Fact]
    public void Unpinning_in_memory_only_mode_removes_the_text_from_disk()
    {
        var canary = Canary.Create();
        using var store = Open(memoryOnly: true);
        var entry = Capture(store, canary);
        store.SetPinned(entry.Id, true, _now++);
        Assert.True(_dir.AnyFileContains(canary));

        store.SetPinned(entry.Id, false, _now++);

        Assert.False(_dir.AnyFileContains(canary));
    }

    [Fact]
    public void Retention_evictions_do_not_leave_text_in_temp_files()
    {
        var canary = Canary.Create();
        using var store = HistoryStore.Open(_dir.Paths, false, HistoryLimits.For(10), FileLog.Null);
        for (var i = 0; i < 40; i++)
        {
            Capture(store, $"{canary} {i}");
        }

        var temp = Path.GetTempPath();
        foreach (var file in Directory.EnumerateFiles(temp, "etilqs_*"))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(canary)) < 0, "SQLite temp file contains clipboard text");
        }
    }

    [Fact]
    public void Deleted_image_and_thumbnail_are_gone_from_every_file()
    {
        using var store = Open();
        var image = LargeImage();
        Capture(store, "before");
        var entry = store.CaptureImage(image, _now++).Entry!;
        Capture(store, "after");
        Assert.True(_dir.AnyFileContains(Pieces(image)));

        store.Delete(entry.Id);

        Assert.False(_dir.AnyFileContains(Pieces(image)));
    }

    [Fact]
    public void Clearing_history_scrubs_all_image_data()
    {
        using var store = Open();
        var images = Enumerable.Range(0, 5).Select(_ => LargeImage()).ToList();
        foreach (var image in images)
        {
            store.CaptureImage(image, _now++);
        }

        store.Clear(includePinned: false);

        Assert.All(images, image => Assert.False(_dir.AnyFileContains(Pieces(image))));
    }

    [Fact]
    public void Memory_only_mode_never_writes_unpinned_images_to_disk()
    {
        var image = LargeImage();
        using (var store = Open(memoryOnly: true))
        {
            store.CaptureImage(image, _now++);
            Assert.False(_dir.AnyFileContains(Pieces(image)));
        }

        Assert.False(_dir.AnyFileContains(Pieces(image)));
    }

    [Fact]
    public void Switching_memory_only_on_scrubs_existing_images_from_disk()
    {
        using var store = Open();
        var image = LargeImage();
        store.CaptureImage(image, _now++);
        Assert.True(_dir.AnyFileContains(Pieces(image)));

        store.SetMemoryOnly(true);

        Assert.False(_dir.AnyFileContains(Pieces(image)));
    }

    [Fact]
    public void Unpinning_an_image_in_memory_only_mode_removes_it_from_disk()
    {
        using var store = Open(memoryOnly: true);
        var image = LargeImage();
        var entry = store.CaptureImage(image, _now++).Entry!;
        store.SetPinned(entry.Id, true, _now++);
        Assert.True(_dir.AnyFileContains(Pieces(image)));

        store.SetPinned(entry.Id, false, _now++);

        Assert.False(_dir.AnyFileContains(Pieces(image)));
    }

    /// <summary>Large enough to spill into SQLite overflow pages, which is where pictures live.</summary>
    private static CapturedImage LargeImage() => TestImages.Fake(length: 64 * 1024);

    /// <summary>
    /// Short pieces from all over the image and the whole thumbnail: SQLite splits a large blob
    /// across pages, so a long needle could miss data that is there.
    /// </summary>
    private static byte[][] Pieces(CapturedImage image) =>
        [.. Enumerable.Range(0, 8).Select(i => image.Png.AsSpan(i * (image.Png.Length / 8), 48).ToArray()), image.Thumbnail];
}
