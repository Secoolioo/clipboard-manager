using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Tests.Store;

/// <summary>Image entries and the schema 1 → 2 migration. The store treats images as opaque blobs; the codec is tested on its own.</summary>
[Trait("Category", "Store")]
public sealed class ImageStoreTests : IDisposable
{
    /// <summary>The schema of 0.9 and 0.10, frozen here on purpose.</summary>
    private const string Version1Schema = """
        CREATE TABLE entries (
            id           INTEGER PRIMARY KEY,
            hash         BLOB    NOT NULL UNIQUE,
            char_count   INTEGER NOT NULL,
            line_count   INTEGER NOT NULL,
            created_at   INTEGER NOT NULL,
            last_used_at INTEGER NOT NULL,
            pinned_at    INTEGER NULL,
            search_head  TEXT    NULL,
            text         TEXT    NOT NULL
        );
        PRAGMA user_version = 1;
        """;

    private readonly TempDataDirectory _dir = new();
    private long _now = 1_700_000_000_000;

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open(bool memoryOnly = false, HistoryLimits? limits = null) =>
        HistoryStore.Open(_dir.Paths, memoryOnly, limits ?? HistoryLimits.For(100), FileLog.Null);

    private CaptureResult CaptureText(HistoryStore store, string text) => store.Capture(text, ContentHash.Compute(text), _now += 1000);

    private CaptureResult CaptureImage(HistoryStore store, CapturedImage image) => store.CaptureImage(image, _now += 1000);

    [Fact]
    public void An_image_is_stored_with_its_size_and_thumbnail()
    {
        var image = TestImages.Fake(width: 1920, height: 1080);
        long id;
        using (var store = Open())
        {
            var result = CaptureImage(store, image);
            Assert.Equal(CaptureOutcome.Inserted, result.Outcome);
            Assert.True(result.Entry!.IsImage);
            id = result.Entry.Id;
        }

        using var reopened = Open();
        var entry = Assert.Single(reopened.LoadAll());
        Assert.Equal(id, entry.Id);
        Assert.Equal(1920, entry.Image!.Width);
        Assert.Equal(1080, entry.Image.Height);
        Assert.Equal(image.Png.Length, entry.Image.ByteCount);
        Assert.Equal(image.Thumbnail, entry.Image.Thumbnail);
        Assert.Equal(string.Empty, entry.SearchText);
        Assert.False(entry.IsPartiallySearchable);
        Assert.Equal(image.Png, reopened.GetImage(id));
        Assert.Equal(string.Empty, reopened.GetText(id));
    }

    [Fact]
    public void Texts_have_no_image()
    {
        using var store = Open();
        var entry = CaptureText(store, "plain").Entry!;

        Assert.False(entry.IsImage);
        Assert.Null(store.GetImage(entry.Id));
    }

    [Fact]
    public void The_same_image_again_writes_nothing_or_moves_it_to_the_top()
    {
        using var store = Open();
        var image = TestImages.Fake();
        var first = CaptureImage(store, image).Entry!;

        Assert.Equal(CaptureOutcome.Unchanged, CaptureImage(store, image with { Png = image.Png.ToArray() }).Outcome);

        CaptureText(store, "in between");
        var again = CaptureImage(store, image with { Png = image.Png.ToArray() });
        Assert.Equal(CaptureOutcome.Promoted, again.Outcome);
        Assert.Equal(first.Id, again.Entry!.Id);
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void A_text_and_an_image_with_the_same_bytes_are_different_entries()
    {
        using var store = Open();
        const string text = "same bytes";
        var png = System.Text.Encoding.Unicode.GetBytes(text);
        CaptureText(store, text);

        Assert.Equal(CaptureOutcome.Inserted, CaptureImage(store, new CapturedImage(png, [1, 2, 3], 1, 1)).Outcome);
        Assert.NotEqual(ContentHash.Compute(text), ContentHash.ComputeImage(png));
    }

    [Fact]
    public void Deleting_and_restoring_an_image_keeps_its_id_and_data()
    {
        using var store = Open();
        var image = TestImages.Fake();
        CaptureText(store, "older");
        var entry = CaptureImage(store, image).Entry!;
        CaptureText(store, "newer");

        var deleted = store.Delete(entry.Id)!;
        Assert.Equal(image.Png, deleted.ImageData);
        Assert.Null(store.GetImage(entry.Id));

        var (restored, _) = store.Restore(deleted);
        Assert.Equal(entry.Id, restored!.Id);
        Assert.Equal(entry.LastUsedAtMs, restored.LastUsedAtMs);
        Assert.Equal(image.Thumbnail, restored.Image!.Thumbnail);
        Assert.Equal(image.Png, store.GetImage(entry.Id));

        // The hash was recomputed from the image: copying the picture again finds the restored entry.
        Assert.Equal(entry.Id, CaptureImage(store, image).Entry!.Id);
    }

    [Fact]
    public void Two_deleted_images_can_both_be_restored()
    {
        using var store = Open();
        var first = CaptureImage(store, TestImages.Fake()).Entry!;
        var second = CaptureImage(store, TestImages.Fake()).Entry!;
        var deletedFirst = store.Delete(first.Id)!;
        var deletedSecond = store.Delete(second.Id)!;

        Assert.NotNull(store.Restore(deletedFirst).Entry);
        Assert.NotNull(store.Restore(deletedSecond).Entry);
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void An_image_without_its_data_is_not_restored()
    {
        using var store = Open();
        var entry = CaptureImage(store, TestImages.Fake()).Entry!;
        var deleted = store.Delete(entry.Id)!;

        Assert.Null(store.Restore(deleted with { ImageData = null }).Entry);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void The_image_budget_removes_only_older_images()
    {
        using var store = Open(limits: HistoryLimits.For(100) with { MaxImageBytes = 10_000 });
        var oldText = CaptureText(store, "old text").Entry!;
        var oldImage = CaptureImage(store, TestImages.Fake(4000)).Entry!;
        var middleImage = CaptureImage(store, TestImages.Fake(4000)).Entry!;
        var newText = CaptureText(store, "new text").Entry!;
        var newImage = CaptureImage(store, TestImages.Fake(4000)).Entry!;

        var remaining = store.LoadAll().Select(e => e.Id).ToHashSet();
        Assert.Equal([oldText.Id, middleImage.Id, newText.Id, newImage.Id], remaining.Order());
        Assert.DoesNotContain(oldImage.Id, remaining);
    }

    [Fact]
    public void The_newest_image_survives_even_beyond_the_budget()
    {
        using var store = Open(limits: HistoryLimits.For(100) with { MaxImageBytes = 1000 });
        CaptureImage(store, TestImages.Fake(500));
        var big = CaptureImage(store, TestImages.Fake(5000)).Entry!;

        Assert.Equal([big.Id], store.LoadAll().Select(e => e.Id));
    }

    [Fact]
    public void The_text_budget_never_removes_images()
    {
        using var store = Open(limits: new HistoryLimits(100, 30));
        var image = CaptureImage(store, TestImages.Fake()).Entry!;
        CaptureText(store, new string('a', 20));
        var newest = CaptureText(store, new string('b', 20)).Entry!;

        Assert.Equal([image.Id, newest.Id], store.LoadAll().Select(e => e.Id).Order());
    }

    [Fact]
    public void Pinned_images_do_not_count_against_the_budget()
    {
        using var store = Open(limits: HistoryLimits.For(100) with { MaxImageBytes = 5000 });
        var pinned = CaptureImage(store, TestImages.Fake(4000)).Entry!;
        store.SetPinned(pinned.Id, pinned: true, _now += 1000);
        var older = CaptureImage(store, TestImages.Fake(4000)).Entry!;
        var newer = CaptureImage(store, TestImages.Fake(4000)).Entry!;

        var remaining = store.LoadAll().Select(e => e.Id).ToHashSet();
        Assert.Contains(pinned.Id, remaining);
        Assert.Contains(newer.Id, remaining);
        Assert.DoesNotContain(older.Id, remaining);
    }

    [Fact]
    public void The_count_limit_applies_to_texts_and_images_together()
    {
        using var store = Open(limits: HistoryLimits.For(10));
        var image = CaptureImage(store, TestImages.Fake(100)).Entry!;
        for (var i = 0; i < 10; i++)
        {
            CaptureText(store, "text " + i);
        }

        var remaining = store.LoadAll();
        Assert.Equal(10, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.Id == image.Id);
    }

    [Fact]
    public void Counting_matches_what_a_new_limit_removes()
    {
        using var store = Open();
        for (var i = 0; i < 6; i++)
        {
            CaptureImage(store, TestImages.Fake(1000));
            CaptureText(store, "text " + i);
        }

        var limits = HistoryLimits.For(10) with { MaxImageBytes = 2500 };
        var count = store.CountExceeding(limits);

        Assert.True(count > 2);
        Assert.Equal(count, store.SetLimits(limits).Count);
        Assert.Equal(2, store.LoadAll().Count(e => e.IsImage));
    }

    [Fact]
    public void A_pinned_image_moves_to_disk_with_its_data_in_memory_only_mode()
    {
        var image = TestImages.Fake();
        long id;
        using (var store = Open(memoryOnly: true))
        {
            id = CaptureImage(store, image).Entry!.Id;
            store.SetPinned(id, pinned: true, _now += 1000);
            Assert.Equal(image.Png, store.GetImage(id));
        }

        using var reopened = Open(memoryOnly: true);
        var entry = Assert.Single(reopened.LoadAll());
        Assert.True(entry.IsPinned);
        Assert.Equal(image.Thumbnail, entry.Image!.Thumbnail);
        Assert.Equal(image.Png, reopened.GetImage(id));
    }

    [Fact]
    public void Switching_memory_only_on_and_off_keeps_images()
    {
        using var store = Open();
        var image = TestImages.Fake();
        var id = CaptureImage(store, image).Entry!.Id;

        store.SetMemoryOnly(true);
        Assert.Equal(image.Png, store.GetImage(id));
        Assert.True(Assert.Single(store.LoadAll()).IsImage);

        store.SetMemoryOnly(false);
        Assert.Equal(image.Png, store.GetImage(id));
    }

    [Fact]
    public void A_version_1_database_is_migrated_and_keeps_every_entry()
    {
        var longText = new string('x', HistoryStore.SearchHeadLength + 100) + " tail";
        CreateVersion1Database((1, "plain", null), (2, "pinned", 1500), (3, longText, null));

        using (var store = Open())
        {
            Assert.Equal(StoreMode.Persistent, store.Mode);
            Assert.Equal(StoreNotice.None, store.Notice);
            var entries = store.LoadAll().OrderBy(e => e.Id).ToList();
            Assert.Equal([1L, 2L, 3L], entries.Select(e => e.Id));
            Assert.All(entries, e => Assert.False(e.IsImage));
            Assert.Equal("plain", entries[0].SearchText);
            Assert.True(entries[1].IsPinned);
            Assert.True(entries[2].IsPartiallySearchable);
            Assert.Equal(longText, store.GetText(3));

            // The hashes came along: the same text again is found instead of added.
            Assert.Equal(CaptureOutcome.Promoted, CaptureText(store, "plain").Outcome);
            Assert.Equal(CaptureOutcome.Inserted, CaptureImage(store, TestImages.Fake()).Outcome);
        }

        using var fresh = new TempDataDirectory();
        HistoryStore.Open(fresh.Paths, memoryOnly: false, HistoryLimits.For(100), FileLog.Null).Dispose();
        Assert.Equal((long)HistoryStore.SchemaVersion, Pragma(_dir.Paths.HistoryDatabase, "user_version"));
        Assert.Equal(Columns(fresh.Paths.HistoryDatabase), Columns(_dir.Paths.HistoryDatabase));
    }

    [Fact]
    public void A_migrated_database_opens_again_without_another_migration()
    {
        CreateVersion1Database((1, "plain", null));
        using (var store = Open())
        {
            CaptureImage(store, TestImages.Fake());
        }

        using var reopened = Open();
        Assert.Equal(StoreNotice.None, reopened.Notice);
        Assert.Equal(2, reopened.LoadAll().Count);
    }

    [Fact]
    public void The_migration_leaves_no_copy_of_unpinned_text_behind_in_memory_only_mode()
    {
        var canary = Canary.Create();
        CreateVersion1Database((1, "pinned", 500), (2, "secret " + canary, null));
        Assert.True(_dir.AnyFileContains(canary));

        using var store = Open(memoryOnly: true);

        // The rebuild copies every row once; secure_delete must have zeroed the old table's pages.
        Assert.False(_dir.AnyFileContains(canary));
        Assert.Equal(2, store.LoadAll().Count);
    }

    /// <summary>Writes a database the way 0.9 and 0.10 left it.</summary>
    private void CreateVersion1Database(params (long Id, string Text, long? PinnedAt)[] rows)
    {
        SqliteBootstrap.Initialize();
        using var raw = new SqliteConnection($"Data Source={_dir.Paths.HistoryDatabase};Pooling=False");
        raw.Open();
        using (var create = raw.CreateCommand())
        {
            create.CommandText = Version1Schema;
            create.ExecuteNonQuery();
        }

        foreach (var (id, text, pinnedAt) in rows)
        {
            using var insert = raw.CreateCommand();
            insert.CommandText = "INSERT INTO entries VALUES ($id, $hash, $chars, $lines, $created, $used, $pinned, $head, $text)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$hash", ContentHash.Compute(text));
            insert.Parameters.AddWithValue("$chars", text.Length);
            insert.Parameters.AddWithValue("$lines", TextMetrics.CountLines(text));
            insert.Parameters.AddWithValue("$created", id * 1000);
            insert.Parameters.AddWithValue("$used", id * 1000);
            insert.Parameters.AddWithValue("$pinned", pinnedAt is { } pinned ? pinned : DBNull.Value);
            insert.Parameters.AddWithValue("$head", text.Length > HistoryStore.SearchHeadLength ? text[..HistoryStore.SearchHeadLength] : DBNull.Value);
            insert.Parameters.AddWithValue("$text", text);
            insert.ExecuteNonQuery();
        }
    }

    private static long Pragma(string database, string name)
    {
        using var raw = new SqliteConnection($"Data Source={database};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = $"PRAGMA {name}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<string> Columns(string database)
    {
        using var raw = new SqliteConnection($"Data Source={database};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('entries') ORDER BY cid";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
